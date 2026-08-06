using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddHttpClient("proxy")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });
builder.Services.AddOptions<IngressOptions>()
    .Bind(builder.Configuration.GetSection("Ingress"))
    .PostConfigure(options =>
    {
        options.BaseDomain = string.IsNullOrWhiteSpace(options.BaseDomain) ? "example.com" : options.BaseDomain;
        options.MqttGatewayHost = string.IsNullOrWhiteSpace(options.MqttGatewayHost) ? "localhost" : options.MqttGatewayHost;
        options.MqttGatewayPort = options.MqttGatewayPort <= 0 ? 1883 : options.MqttGatewayPort;
        options.MqttTcpListenPort = options.MqttTcpListenPort <= 0 ? 1883 : options.MqttTcpListenPort;
    });

// TCP proxy: devices connect via MQTT-over-TCP, Ingress forwards raw TCP to MqttGateway
builder.Services.AddHostedService<MqttTcpProxyService>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/info", (IOptions<IngressOptions> options) =>
    Results.Ok(new
    {
        options.Value.BaseDomain,
        mqttTcpListenPort = options.Value.MqttTcpListenPort,
        mqttGateway = $"{options.Value.MqttGatewayHost}:{options.Value.MqttGatewayPort}",
        routeTable = options.Value.RouteTable.Select(r => new { r.Tenant, r.Host })
    }));

// HTTP fallback proxy (routes by Host header to TenantPlanes)
app.MapFallback(ProxyHttpAsync);

await app.RunAsync();

async Task ProxyHttpAsync(HttpContext context, IHttpClientFactory httpClientFactory,
    IOptions<IngressOptions> options)
{
    var host = context.Request.Host.Host;
    var tenant = options.Value.RouteTable.FirstOrDefault(route =>
        string.Equals(route.Host, host, StringComparison.OrdinalIgnoreCase));

    if (tenant is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = "No route matched host", host });
        return;
    }

    var client = httpClientFactory.CreateClient("proxy");
    var targetUri = new Uri(
        $"{tenant.BackendScheme ?? "http"}://{tenant.BackendHost}:{tenant.BackendPort}{context.Request.Path}{context.Request.QueryString}");
    using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

    requestMessage.Headers.Host = host;

    if (context.Request.ContentLength > 0 || context.Request.Body.CanRead)
    {
        requestMessage.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrEmpty(context.Request.ContentType))
        {
            requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type",
                context.Request.ContentType);
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            continue;

        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    using var response = await client.SendAsync(requestMessage,
        HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;

    foreach (var header in response.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    foreach (var header in response.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    context.Response.Headers.Remove("transfer-encoding");
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

// ── TCP proxy: accepts MQTT-over-TCP from devices, forwards raw TCP to MqttGateway ──

sealed class MqttTcpProxyService : IHostedService
{
    private readonly IngressOptions _options;
    private readonly ILogger<MqttTcpProxyService> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public MqttTcpProxyService(IOptions<IngressOptions> options, ILogger<MqttTcpProxyService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _options.MqttTcpListenPort);
        _listener.Start();
        _logger.LogInformation("MQTT TCP proxy listening on :{Port}, forwarding to {Host}:{BackendPort}",
            _options.MqttTcpListenPort, _options.MqttGatewayHost, _options.MqttGatewayPort);

        _ = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _listener?.Stop();
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                _ = ProxyAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { /* listener stopped */ }
    }

    private async Task ProxyAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        TcpClient? backend = null;

        try
        {
            backend = new TcpClient();
            await backend.ConnectAsync(_options.MqttGatewayHost, _options.MqttGatewayPort, ct);
            _logger.LogInformation("MQTT TCP proxy: device connected, upstream to {Host}:{Port}",
                _options.MqttGatewayHost, _options.MqttGatewayPort);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var clientStream = client.GetStream();
            var backendStream = backend.GetStream();

            var clientToBackend = PumpAsync(clientStream, backendStream, linkedCts.Token);
            var backendToClient = PumpAsync(backendStream, clientStream, linkedCts.Token);

            await Task.WhenAny(clientToBackend, backendToClient);
            linkedCts.Cancel();

            try { await Task.WhenAll(clientToBackend, backendToClient); } catch { /* shutdown */ }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "MQTT TCP proxy: connection error");
        }
        finally
        {
            backend?.Dispose();
        }
    }

    private static async Task PumpAsync(NetworkStream source, NetworkStream target, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await source.ReadAsync(buffer, ct);
                if (bytesRead == 0) return;
                await target.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                await target.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}

sealed record IngressOptions
{
    public string BaseDomain { get; set; } = "example.com";

    // TCP MQTT listen port (devices connect here with plain MQTT-over-TCP)
    public int MqttTcpListenPort { get; set; } = 1883;

    // MqttGateway TCP backend (MQTT-over-TCP)
    public string MqttGatewayHost { get; set; } = "localhost";
    public int MqttGatewayPort { get; set; } = 1883;

    // TenantPlane route table (HTTP fallback)
    public List<RouteEntry> RouteTable { get; set; } = new();
}

sealed record RouteEntry
{
    public string Tenant { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string? BackendScheme { get; set; } = "http";
    public string BackendHost { get; set; } = string.Empty;
    public int BackendPort { get; set; } = 8080;
}
