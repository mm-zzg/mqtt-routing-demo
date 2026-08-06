using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddOptions<ProtocolTransferOptions>()
    .Bind(builder.Configuration.GetSection("ProtocolTransfer"))
    .PostConfigure(options =>
    {
        options.ListenPort = options.ListenPort <= 0 ? 1883 : options.ListenPort;
        options.IngressHost = string.IsNullOrWhiteSpace(options.IngressHost) ? "localhost" : options.IngressHost;
        options.IngressPort = options.IngressPort <= 0 ? 18000 : options.IngressPort;
        options.BaseDomain = string.IsNullOrWhiteSpace(options.BaseDomain) ? "example.com" : options.BaseDomain;
    });
builder.Services.AddHostedService<MqttTcpGatewayService>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", (IOptions<ProtocolTransferOptions> options, MqttTcpGatewayService gateway) =>
    Results.Ok(new
    {
        listenPort = options.Value.ListenPort,
        ingress = $"{options.Value.IngressHost}:{options.Value.IngressPort}",
        activeConnections = gateway.GetActiveConnectionCount()
    }));
await app.RunAsync();

sealed record ProtocolTransferOptions
{
    public int ListenPort { get; set; } = 1883;
    public string IngressHost { get; set; } = "localhost";
    public int IngressPort { get; set; } = 18000;
    public string BaseDomain { get; set; } = "example.com";
}

sealed class MqttTcpGatewayService : BackgroundService
{
    private readonly ILogger<MqttTcpGatewayService> _logger;
    private readonly IOptions<ProtocolTransferOptions> _options;
    private int _activeConnections;

    public MqttTcpGatewayService(ILogger<MqttTcpGatewayService> logger, IOptions<ProtocolTransferOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    public int GetActiveConnectionCount() => _activeConnections;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, _options.Value.ListenPort);
        listener.Start();
        _logger.LogInformation("MQTT TCP gateway listening on port {Port} → Ingress ws://{IngressHost}:{IngressPort}/mqtt/{{tenant}}",
            _options.Value.ListenPort, _options.Value.IngressHost, _options.Value.IngressPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            _ = HandleClientAsync(client, stoppingToken);
        }

        listener.Stop();
    }

    private async Task HandleClientAsync(System.Net.Sockets.TcpClient tcpClient, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        var remoteEndpoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("TCP client connected from {Endpoint}", remoteEndpoint);

        try
        {
            using var stream = tcpClient.GetStream();

            // Peek the MQTT CONNECT packet to extract the client ID.
            // Supports MQTT 3.1, MQTT 3.1.1, and MQTT 5.0.
            //
            // CONNECT structure:
            //   Fixed header:   1 byte (0x10) + remaining length (1-4 bytes)
            //   Variable header: Protocol Name (length-prefixed string) + Protocol Level (1 byte) + Connect Flags (1 byte) + Keep Alive (2 bytes)
            //     - MQTT 3.1:     "MQIsdp" (6 chars) → 2 + 6 = 8 bytes
            //     - MQTT 3.1.1:   "MQTT" (4 chars) → 2 + 4 = 6 bytes
            //     - MQTT 5.0:     "MQTT" (4 chars) → 2 + 4 = 6 bytes, followed by Properties (length-prefixed)
            //   Payload:        ClientId length (2 bytes) + ClientId (UTF-8) + ...
            var connectBuffer = new byte[4096];
            var bytesRead = 0;

            // Read at least the fixed header + some variable header to start parsing.
            while (bytesRead < 32 && !ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead), ct);
                if (read == 0)
                {
                    _logger.LogWarning("Client {Endpoint} disconnected before sending CONNECT", remoteEndpoint);
                    return;
                }
                bytesRead += read;
            }

            // Verify packet type is CONNECT (0x10)
            if ((connectBuffer[0] & 0xF0) != 0x10)
            {
                _logger.LogWarning("Expected CONNECT from {Endpoint}, got packet type {Type}", remoteEndpoint, connectBuffer[0] >> 4);
                return;
            }

            // Parse remaining length (variable-length encoding)
            var remainingLength = 0;
            var multiplier = 1;
            var offset = 1;
            byte encodedByte;
            do
            {
                if (offset >= bytesRead)
                {
                    _logger.LogWarning("Truncated remaining length from {Endpoint}", remoteEndpoint);
                    return;
                }
                encodedByte = connectBuffer[offset++];
                remainingLength += (encodedByte & 127) * multiplier;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128)
                {
                    _logger.LogWarning("Malformed remaining length from {Endpoint}", remoteEndpoint);
                    return;
                }
            }
            while ((encodedByte & 128) != 0);

            // Variable header: starts with protocol name (length-prefixed UTF-8 string)
            if (offset + 2 > bytesRead)
            {
                _logger.LogWarning("Truncated protocol name length from {Endpoint}", remoteEndpoint);
                return;
            }
            var protoNameLen = (connectBuffer[offset] << 8) | connectBuffer[offset + 1];
            var protoNameStart = offset + 2;
            var protoNameEnd = protoNameStart + protoNameLen;

            // Ensure we have the full protocol name
            while (bytesRead < protoNameEnd + 4 && !ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead), ct);
                if (read == 0) break;
                bytesRead += read;
            }

            var protocolName = protoNameEnd <= bytesRead
                ? Encoding.UTF8.GetString(connectBuffer, protoNameStart, protoNameLen)
                : "";

            // After protocol name: Protocol Level (1 byte) + Connect Flags (1 byte) + Keep Alive (2 bytes) = 4 bytes
            // Then for MQTT 5.0: Properties Length (1-4 bytes) + Properties
            var afterProtoName = protoNameEnd + 4; // skip level + flags + keep-alive

            int payloadStart;
            if (string.Equals(protocolName, "MQTT", StringComparison.Ordinal) && protoNameLen == 4)
            {
                // Could be MQTT 3.1.1 (level 4) or MQTT 5.0 (level 5).
                // Check protocol level byte to distinguish.
                var protocolLevel = connectBuffer[protoNameEnd];
                if (protocolLevel == 5)
                {
                    // MQTT 5.0: skip Properties (length-prefixed)
                    if (afterProtoName >= bytesRead)
                    {
                        _logger.LogWarning("Truncated MQTT 5.0 CONNECT from {Endpoint}", remoteEndpoint);
                        return;
                    }

                    // Read properties length (variable-length encoding, same as remaining length)
                    var propsLength = 0;
                    var propsMultiplier = 1;
                    var propsOffset = afterProtoName;
                    byte propsByte;
                    do
                    {
                        if (propsOffset >= bytesRead)
                        {
                            // Need more data
                            while (bytesRead <= propsOffset && !ct.IsCancellationRequested)
                            {
                                var read = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead), ct);
                                if (read == 0) break;
                                bytesRead += read;
                            }
                            if (propsOffset >= bytesRead) break;
                        }
                        propsByte = connectBuffer[propsOffset++];
                        propsLength += (propsByte & 127) * propsMultiplier;
                        propsMultiplier *= 128;
                    }
                    while ((propsByte & 128) != 0);

                    // Ensure we have all properties bytes
                    var propsEnd = propsOffset + propsLength;
                    while (bytesRead < propsEnd + 2 && !ct.IsCancellationRequested)
                    {
                        var read = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead), ct);
                        if (read == 0) break;
                        bytesRead += read;
                    }

                    payloadStart = propsEnd;
                }
                else
                {
                    // MQTT 3.1.1 (level 4): no properties section
                    payloadStart = afterProtoName;
                }
            }
            else if (string.Equals(protocolName, "MQIsdp", StringComparison.Ordinal))
            {
                // MQTT 3.1 (level 3): no properties section
                payloadStart = afterProtoName;
            }
            else
            {
                _logger.LogWarning("Unknown MQTT protocol '{Proto}' from {Endpoint}", protocolName, remoteEndpoint);
                return;
            }

            // Read until we have the client ID length + client ID
            while (bytesRead < payloadStart + 2 && !ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead), ct);
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead < payloadStart + 2)
            {
                _logger.LogWarning("Incomplete CONNECT packet from {Endpoint}", remoteEndpoint);
                return;
            }

            var clientIdLength = (connectBuffer[payloadStart] << 8) | connectBuffer[payloadStart + 1];

            // Read until we have the full client ID
            while (bytesRead < payloadStart + 2 + clientIdLength && !ct.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead), ct);
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead < payloadStart + 2 + clientIdLength)
            {
                _logger.LogWarning("CONNECT packet too short for client ID from {Endpoint}", remoteEndpoint);
                return;
            }

            var clientId = Encoding.UTF8.GetString(connectBuffer, payloadStart + 2, clientIdLength);
            var tenant = ExtractTenant(clientId);

            if (tenant is null)
            {
                _logger.LogWarning("Rejecting client {ClientId} from {Endpoint}: no tenant prefix", clientId, remoteEndpoint);
                // Send CONNACK with "identifier rejected" and close.
                // MQTT 3.1/3.1.1: 0x20 0x02 0x00 0x02 (4 bytes, no session present, return code 2)
                // MQTT 5.0:        0x20 0x03 0x00 0x02 0x00 (5 bytes, no session, reason code 2, empty properties)
                var protocolLevel = connectBuffer[protoNameEnd];
                if (protocolLevel == 5)
                    await stream.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x02, 0x00 }, ct);
                else
                    await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x02 }, ct);
                return;
            }

            _logger.LogInformation("Routing client {ClientId} → tenant {Tenant} (from {Endpoint})",
                clientId, tenant, remoteEndpoint);

            // Connect to Ingress via WebSocket, using the tenant domain name as Host
            // so Ingress routes by domain (e.g. tenant1.example.com → tenant1 backend).
            using var ws = new ClientWebSocket();
            var tenantHost = $"{tenant}.{_options.Value.BaseDomain}";
            var wsUri = new Uri($"ws://{_options.Value.IngressHost}:{_options.Value.IngressPort}/mqtt");
            ws.Options.SetRequestHeader("Host", tenantHost);

            try
            {
                await ws.ConnectAsync(wsUri, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to Ingress at {Uri}", wsUri);
                var protocolLevel = connectBuffer[protoNameEnd];
                if (protocolLevel == 5)
                    await stream.WriteAsync(new byte[] { 0x20, 0x03, 0x00, 0x03, 0x00 }, ct); // MQTT5: reason code 3
                else
                    await stream.WriteAsync(new byte[] { 0x20, 0x02, 0x00, 0x03 }, ct); // MQTT 3.1/3.1.1: return code 3
                return;
            }

            // Forward the already-read CONNECT bytes to the backend first
            await ws.SendAsync(
                new ArraySegment<byte>(connectBuffer, 0, bytesRead),
                WebSocketMessageType.Binary, true, ct);

            // Now bidirectional pump
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tcpToWs = PumpTcpToWsAsync(stream, ws, cts.Token);
            var wsToTcp = PumpWsToTcpAsync(ws, stream, cts.Token);

            await Task.WhenAny(tcpToWs, wsToTcp);
            cts.Cancel();

            try { await Task.WhenAll(tcpToWs, wsToTcp); } catch { /* shutdown */ }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error handling TCP client from {Endpoint}", remoteEndpoint);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
            tcpClient.Dispose();
        }
    }

    private static string? ExtractTenant(string clientId)
    {
        var dot = clientId.IndexOf('.');
        if (dot <= 0) return null;
        var tenant = clientId[..dot];
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }

    private static async Task PumpTcpToWsAsync(System.IO.Stream tcp, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await tcp.ReadAsync(buffer, ct);
                if (read == 0) return;
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task PumpWsToTcpAsync(WebSocket ws, System.IO.Stream tcp, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    return;
                }
                if (result.Count > 0)
                    await tcp.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}
