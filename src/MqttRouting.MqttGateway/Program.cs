using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddOptions<MqttGatewayOptions>()
    .Bind(builder.Configuration.GetSection("MqttGateway"))
    .PostConfigure(options =>
    {
        options.BaseDomain = string.IsNullOrWhiteSpace(options.BaseDomain) ? "example.com" : options.BaseDomain;
        options.MqttTcpListenPort = options.MqttTcpListenPort <= 0 ? 1883 : options.MqttTcpListenPort;
    });

// TCP listener: accepts MQTT-over-TCP from Ingress, parses CONNECT,
// extracts tenant from client ID, bridges to TenantPlane via TCP.
builder.Services.AddHostedService<MqttTcpGatewayService>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", (IOptions<MqttGatewayOptions> options) =>
    Results.Ok(new
    {
        baseDomain = options.Value.BaseDomain,
        mqttTcpListenPort = options.Value.MqttTcpListenPort,
        routeTable = options.Value.RouteTable.Select(r => new { r.Tenant, r.Host, r.Port })
    }));

await app.RunAsync();

// ── TCP → TCP MQTT gateway ────────────────────────────────────────────

sealed class MqttTcpGatewayService : IHostedService
{
    private readonly MqttGatewayOptions _options;
    private readonly ILogger<MqttTcpGatewayService> _logger;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public MqttTcpGatewayService(IOptions<MqttGatewayOptions> options, ILogger<MqttTcpGatewayService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Any, _options.MqttTcpListenPort);
        _listener.Start();
        _logger.LogInformation("MQTT TCP gateway listening on :{Port} (TCP → TCP bridge)", _options.MqttTcpListenPort);

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
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        string? tenant = null;
        string? clientId = null;
        int protocolLevel = 0;
        int bytesRead = 0;

        try
        {
            // ── Read & parse MQTT CONNECT from TCP stream ──
            byte[] connectBuffer = new byte[4096];
            const int maxBufferSize = 256 * 1024;

            async ValueTask<bool> EnsureBufferAsync(int target)
            {
                while (connectBuffer.Length < target)
                {
                    if (target > maxBufferSize) return false;
                    var newSize = Math.Min(connectBuffer.Length * 2, maxBufferSize);
                    if (newSize < target) return false;
                    var newBuf = new byte[newSize];
                    Array.Copy(connectBuffer, newBuf, bytesRead);
                    connectBuffer = newBuf;
                }
                while (bytesRead < target)
                {
                    var n = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead, connectBuffer.Length - bytesRead), ct);
                    if (n == 0) return false;
                    bytesRead += n;
                }
                return true;
            }

            if (!await EnsureBufferAsync(32)) return;

            // Verify packet type is CONNECT (0x10)
            if ((connectBuffer[0] & 0xF0) != 0x10) return;

            // Parse remaining length
            var remainingLength = 0;
            var multiplier = 1;
            var offset = 1;
            byte encodedByte;
            do
            {
                if (offset >= bytesRead) return;
                encodedByte = connectBuffer[offset++];
                remainingLength += (encodedByte & 127) * multiplier;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128) return;
            }
            while ((encodedByte & 128) != 0);

            var totalConnectSize = offset + remainingLength;

            // Protocol name
            if (offset + 2 > bytesRead) return;
            var protoNameLen = (connectBuffer[offset] << 8) | connectBuffer[offset + 1];
            var protoNameStart = offset + 2;
            var protoNameEnd = protoNameStart + protoNameLen;
            if (!await EnsureBufferAsync(protoNameEnd + 4)) return;

            var protocolName = Encoding.UTF8.GetString(connectBuffer, protoNameStart, protoNameLen);
            var afterProtoName = protoNameEnd + 4;
            protocolLevel = connectBuffer[protoNameEnd];

            int payloadStart;
            if (string.Equals(protocolName, "MQTT", StringComparison.Ordinal) && protoNameLen == 4)
            {
                if (protocolLevel == 5)
                {
                    if (!await EnsureBufferAsync(afterProtoName + 4)) return;
                    var propsLength = 0;
                    var propsMultiplier = 1;
                    var propsOffset = afterProtoName;
                    byte propsByte;
                    do
                    {
                        propsByte = connectBuffer[propsOffset++];
                        propsLength += (propsByte & 127) * propsMultiplier;
                        propsMultiplier *= 128;
                        if (propsMultiplier > 128 * 128 * 128) return;
                    }
                    while ((propsByte & 128) != 0);
                    if (!await EnsureBufferAsync(propsOffset + propsLength + 2)) return;
                    payloadStart = propsOffset + propsLength;
                }
                else
                {
                    payloadStart = afterProtoName;
                }
            }
            else if (string.Equals(protocolName, "MQIsdp", StringComparison.Ordinal))
            {
                payloadStart = afterProtoName;
            }
            else return;

            if (!await EnsureBufferAsync(payloadStart + 2)) return;
            var clientIdLength = (connectBuffer[payloadStart] << 8) | connectBuffer[payloadStart + 1];
            if (!await EnsureBufferAsync(payloadStart + 2 + clientIdLength)) return;

            clientId = Encoding.UTF8.GetString(connectBuffer, payloadStart + 2, clientIdLength);
            tenant = ExtractTenant(clientId);

            if (tenant is null)
            {
                _logger.LogWarning("Rejecting {Endpoint}: no tenant prefix in clientId '{ClientId}'", remote, clientId);
                byte[] reject = protocolLevel == 5
                    ? new byte[] { 0x20, 0x03, 0x00, 0x02, 0x00 }
                    : new byte[] { 0x20, 0x02, 0x00, 0x02 };
                await stream.WriteAsync(reject, ct);
                return;
            }

            var backend = _options.RouteTable.FirstOrDefault(r =>
                string.Equals(r.Tenant, tenant, StringComparison.OrdinalIgnoreCase));
            if (backend is null)
            {
                _logger.LogWarning("No backend for tenant {Tenant} (client {ClientId})", tenant, clientId);
                byte[] reject = protocolLevel == 5
                    ? new byte[] { 0x20, 0x03, 0x00, 0x02, 0x00 }
                    : new byte[] { 0x20, 0x02, 0x00, 0x02 };
                await stream.WriteAsync(reject, ct);
                return;
            }

            _logger.LogInformation("Routing {ClientId} → tenant {Tenant} → {Host}:{Port}",
                clientId, tenant, backend.Host, backend.Port);

            // ── Connect to TenantPlane via TCP ──
            using var downstream = new TcpClient();
            try
            {
                await downstream.ConnectAsync(backend.Host, backend.Port, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to TenantPlane {Host}:{Port}", backend.Host, backend.Port);
                byte[] reject = protocolLevel == 5
                    ? new byte[] { 0x20, 0x03, 0x00, 0x03, 0x00 }
                    : new byte[] { 0x20, 0x02, 0x00, 0x03 };
                await stream.WriteAsync(reject, ct);
                return;
            }

            // ── Forward buffered CONNECT bytes to TenantPlane ──
            await using var downstreamStream = downstream.GetStream();

            if (totalConnectSize > 0)
            {
                await downstreamStream.WriteAsync(connectBuffer.AsMemory(0, totalConnectSize), ct);
            }

            // Forward any extra bytes beyond CONNECT
            if (bytesRead > totalConnectSize)
            {
                await downstreamStream.WriteAsync(connectBuffer.AsMemory(totalConnectSize, bytesRead - totalConnectSize), ct);
            }

            // ── Bidirectional pump: TCP ↔ TCP ──
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var upstreamToDownstream = BridgeStreamAsync(stream, downstreamStream, linkedCts.Token);
            var downstreamToUpstream = BridgeStreamAsync(downstreamStream, stream, linkedCts.Token);

            await Task.WhenAny(upstreamToDownstream, downstreamToUpstream);
            linkedCts.Cancel();

            try { await Task.WhenAll(upstreamToDownstream, downstreamToUpstream); } catch { /* shutdown */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT TCP gateway: error for {Endpoint}", remote);
        }

        if (clientId is not null)
            _logger.LogInformation("Client {ClientId} (tenant {Tenant}) disconnected", clientId, tenant ?? "?");
    }

    private static string? ExtractTenant(string clientId)
    {
        var dot = clientId.IndexOf('.');
        if (dot <= 0) return null;
        var tenant = clientId[..dot];
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }

    /// <summary>
    /// Copies data from <paramref name="source"/> to <paramref name="destination"/> until
    /// EOF or cancellation.
    /// </summary>
    private static async Task BridgeStreamAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while (!ct.IsCancellationRequested && (read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }
}

sealed record MqttGatewayOptions
{
    public string BaseDomain { get; set; } = "example.com";
    public int MqttTcpListenPort { get; set; } = 1883;
    public List<TenantBackend> RouteTable { get; set; } = new();
}

sealed record TenantBackend
{
    public string Tenant { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
}
