using System.Net.WebSockets;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;

namespace MqttRouting.TenantPlane;

internal sealed class MqttBrokerService : IHostedService
{
    private MqttServer? _server;
    private readonly TenantMessageStore _store;
    private readonly IOptions<TenantPlaneOptions> _options;
    private readonly ILogger<MqttBrokerService> _logger;

    public MqttBrokerService(
        TenantMessageStore store,
        IOptions<TenantPlaneOptions> options,
        ILogger<MqttBrokerService> logger)
    {
        _store = store;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointPort(_options.Value.BrokerPort)
            .Build();

        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        _server.ValidatingConnectionAsync += OnValidateConnection;
        _server.InterceptingPublishAsync += OnInterceptPublish;

        await _server.StartAsync();

        _logger.LogInformation("MQTT broker started on TCP port {Port} and WebSocket /mqtt (tenant: {Tenant})",
            _options.Value.BrokerPort, _options.Value.TenantName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is null) return;

        _server.ValidatingConnectionAsync -= OnValidateConnection;
        _server.InterceptingPublishAsync -= OnInterceptPublish;

        await _server.StopAsync();

        _logger.LogInformation("MQTT broker stopped (tenant: {Tenant})", _options.Value.TenantName);
    }

    /// <summary>
    /// Bridges a WebSocket connection to the local TCP MQTT broker via a loopback TCP connection.
    /// MQTT over WebSocket is raw MQTT frames carried as binary WebSocket messages.
    /// </summary>
    public async Task BridgeWebSocketAsync(WebSocket ws, CancellationToken ct)
    {
        // Connect a loopback TCP socket to the local broker
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _options.Value.BrokerPort, ct);
        using var stream = tcp.GetStream();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wsToTcp = PumpWsToTcpAsync(ws, stream, cts.Token);
        var tcpToWs = PumpTcpToWsAsync(stream, ws, cts.Token);

        await Task.WhenAny(wsToTcp, tcpToWs);
        cts.Cancel();

        try { await Task.WhenAll(wsToTcp, tcpToWs); } catch { /* shutdown */ }
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

    private static async Task PumpTcpToWsAsync(System.IO.Stream tcp, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var read = await tcp.ReadAsync(buffer, ct);
                if (read == 0) return; // remote closed
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(new ArraySegment<byte>(buffer, 0, read), WebSocketMessageType.Binary, true, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private Task OnValidateConnection(ValidatingConnectionEventArgs args)
    {
        _logger.LogInformation("MQTT client connecting: {ClientId} (tenant: {Tenant})",
            args.ClientId, _options.Value.TenantName);
        return Task.CompletedTask;
    }

    private Task OnInterceptPublish(InterceptingPublishEventArgs args)
    {
        var sequence = args.ApplicationMessage.Payload;
        var bytes = new byte[sequence.Length];
        var offset = 0;
        foreach (var segment in sequence)
        {
            segment.Span.CopyTo(bytes.AsSpan(offset));
            offset += segment.Length;
        }
        var payload = System.Text.Encoding.UTF8.GetString(bytes);

        _store.Add(new TenantMessage(
            Id: Guid.NewGuid().ToString("N")[..8],
            Topic: args.ApplicationMessage.Topic,
            Payload: payload,
            CreatedAt: DateTimeOffset.UtcNow));

        _logger.LogInformation("MQTT publish on {Tenant}: topic={Topic} client={ClientId}",
            _options.Value.TenantName, args.ApplicationMessage.Topic, args.ClientId);

        return Task.CompletedTask;
    }
}
