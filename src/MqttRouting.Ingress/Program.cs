using System.Net.WebSockets;
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
    });

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.MapDefaultEndpoints();
app.MapGet("/info", (IOptions<IngressOptions> options) => Results.Ok(options.Value));

// MQTT-over-WebSocket route: /mqtt
// Routed by Host header (e.g. tenant1.example.com → tenant1 backend), same as HTTP requests.
app.Map("/mqtt", async (HttpContext ctx, IOptions<IngressOptions> options) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var host = ctx.Request.Host.Host;
    var route = options.Value.RouteTable.FirstOrDefault(r =>
        string.Equals(r.Host, host, StringComparison.OrdinalIgnoreCase));

    if (route is null)
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        await ctx.Response.WriteAsJsonAsync(new { error = "No route matched host", host });
        return;
    }

    using var clientSocket = await ctx.WebSockets.AcceptWebSocketAsync("mqtt");

    using var backendSocket = new ClientWebSocket();
    var wsUri = new Uri($"ws://{route.BackendHost}:{route.BackendPort}/mqtt");

    try
    {
        await backendSocket.ConnectAsync(wsUri, ctx.RequestAborted);
    }
    catch
    {
        if (clientSocket.State == WebSocketState.Open)
            await clientSocket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Backend unreachable", CancellationToken.None);
        return;
    }

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    var clientToBackend = PumpAsync(clientSocket, backendSocket, cts.Token);
    var backendToClient = PumpAsync(backendSocket, clientSocket, cts.Token);

    await Task.WhenAny(clientToBackend, backendToClient);
    cts.Cancel();

    try { await Task.WhenAll(clientToBackend, backendToClient); } catch { /* shut down */ }
});

// HTTP fallback proxy (existing behavior, routes by Host header)
app.MapFallback(ProxyHttpAsync);

static async Task PumpAsync(WebSocket source, WebSocket target, CancellationToken ct)
{
    var buffer = new byte[8192];
    try
    {
        while (source.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await source.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await target.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                return;
            }

            if (target.State == WebSocketState.Open)
                await target.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
}

async Task ProxyHttpAsync(HttpContext context, IHttpClientFactory httpClientFactory, IOptions<IngressOptions> options)
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
    var targetUri = new Uri($"http://{tenant.BackendHost}:{tenant.BackendPort}{context.Request.Path}{context.Request.QueryString}");
    using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

    if (context.Request.ContentLength > 0 || context.Request.Body.CanRead)
    {
        requestMessage.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrEmpty(context.Request.ContentType))
        {
            requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    using var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
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

sealed record IngressOptions
{
    public string BaseDomain { get; set; } = "example.com";
    public List<RouteEntry> RouteTable { get; set; } = new();
}

sealed record RouteEntry
{
    public string Tenant { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string BackendScheme { get; set; } = "http";
    public string BackendHost { get; set; } = string.Empty;
    public int BackendPort { get; set; } = 8080;
}
