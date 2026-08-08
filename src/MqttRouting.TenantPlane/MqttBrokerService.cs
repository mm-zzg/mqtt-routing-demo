using System.IO.Pipes;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Server;
using MqttRouting.ServiceDefaults;

namespace MqttRouting.TenantPlane;

internal sealed class MqttBrokerService : IHostedService, IAsyncDisposable
{
    private MqttServer? _server;
    private readonly List<TcpListener> _tcpListeners = [];
    private readonly List<Task> _listenerTasks = [];
    private CancellationTokenSource? _listenerCts;
    private Task? _ipcListenerTask;
    private Task? _tlsListenerTask;
    private TcpListener? _tlsListener;
    private X509Certificate2? _tlsCertificate;

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
        _listenerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var internalPort = _options.Value.InternalBrokerPort;

        // ── Internal MQTTnet broker (localhost-only, not publicly exposed) ─
        var serverOptions = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(internalPort)
            .Build();

        _server = new MqttServerFactory().CreateMqttServer(serverOptions);
        _server.ValidatingConnectionAsync += OnValidateConnection;
        _server.InterceptingPublishAsync += OnInterceptPublish;

        await _server.StartAsync();

        // ── External TCP listeners → forward to internal broker ──────────
        foreach (var port in _options.Value.TcpListenerPorts)
        {
            if (port == internalPort)
            {
                _logger.LogWarning("Skipping TCP listener port {Port} (same as internal broker port)", port);
                continue;
            }

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _tcpListeners.Add(listener);

            var task = RunTcpListenerAsync(listener, port, _listenerCts.Token);
            _listenerTasks.Add(task);

            _logger.LogInformation("External TCP MQTT listener started on port {Port} → internal:{InternalPort} (tenant: {Tenant})",
                port, internalPort, _options.Value.TenantName);
        }

        // ── TLS listener (SNI-passthrough from MqttGateway) ──────────────
        var tlsPort = _options.Value.TlsListenerPort;
        if (tlsPort.HasValue && _options.Value.HasTlsCert())
        {
            _tlsCertificate = await LoadTlsCertificateAsync(cancellationToken);

            _tlsListener = new TcpListener(IPAddress.Any, tlsPort.Value);
            _tlsListener.Start();
            _tlsListenerTask = RunTlsListenerAsync(_tlsListener, tlsPort.Value, _listenerCts.Token);

            _logger.LogInformation("External TLS MQTT listener started on port {Port} → internal:{InternalPort} (tenant: {Tenant})",
                tlsPort.Value, internalPort, _options.Value.TenantName);
        }

        // ── IPC listener (named pipe / Unix domain socket) ───────────────
        var ipcPath = _options.Value.IpcEndpointPath;
        if (!string.IsNullOrWhiteSpace(ipcPath))
        {
            _ipcListenerTask = RunIpcListenerAsync(ipcPath, _listenerCts.Token);
        }

        _logger.LogInformation(
            "MQTT broker started — internal TCP:{InternalPort} (localhost), external TCP:[{TcpPorts}], TLS:{TlsPort}, IPC:{IpcPath} (tenant: {Tenant})",
            internalPort,
            string.Join(", ", _options.Value.TcpListenerPorts.Select(p => p.ToString())),
            tlsPort?.ToString() ?? "(disabled)",
            ipcPath ?? "(disabled)",
            _options.Value.TenantName);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_listenerCts is not null)
        {
            await _listenerCts.CancelAsync();
        }

        _tlsListener?.Stop();

        if (_tlsListenerTask is not null)
        {
            try { await _tlsListenerTask; } catch (OperationCanceledException) { }
        }

        if (_server is not null)
        {
            _server.ValidatingConnectionAsync -= OnValidateConnection;
            _server.InterceptingPublishAsync -= OnInterceptPublish;

            await _server.StopAsync();
        }

        foreach (var listener in _tcpListeners)
        {
            listener.Stop();
        }

        if (_listenerTasks.Count > 0)
        {
            await Task.WhenAll(_listenerTasks);
        }

        if (_ipcListenerTask is not null)
        {
            try { await _ipcListenerTask; } catch (OperationCanceledException) { }
        }

        _logger.LogInformation("MQTT broker stopped (tenant: {Tenant})", _options.Value.TenantName);
    }

    public async ValueTask DisposeAsync()
    {
        await ValueTask.CompletedTask;
        _listenerCts?.Dispose();
        _tlsListener?.Stop();
        _tlsCertificate?.Dispose();

        foreach (var listener in _tcpListeners)
        {
            listener.Stop();
        }

        if (_server is not null)
        {
            _server.Dispose();
            _server = null;
        }
    }

    // ── External TCP listener loop ──────────────────────────────────────

    /// <summary>
    /// Accepts TCP connections on an external port and bridges each to the internal
    /// localhost-only MQTTnet broker so the MQTT protocol is handled there.
    /// </summary>
    private async Task RunTcpListenerAsync(TcpListener listener, int port, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleTcpClientAsync(client, port, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TCP listener on port {Port} failed", port);
        }
    }

    /// <summary>
    /// Stores the client certificate info for the current connection being handled.
    /// Populated during PPv2 parsing and consumed by <see cref="OnValidateConnection"/>.
    /// </summary>
    private static readonly System.Threading.AsyncLocal<ProxyProtocol.X509CertInfo?> CurrentClientCert = new();

    private async Task HandleTcpClientAsync(TcpClient client, int port, CancellationToken ct)
    {
        var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        IPEndPoint? originalSource = null;
        ProxyProtocol.X509CertInfo? clientCert = null;
        try
        {
            // ── Parse Proxy Protocol v2 header (with optional client cert TLV) ──
            await using var clientStream = client.GetStream();
            try
            {
                var ppResult = await ProxyProtocol.ReadV2HeaderFullAsync(clientStream, ct);
                originalSource = ppResult.Source;
                clientCert = ppResult.ClientCert;
            }
            catch
            {
                // If PPv2 parsing fails, proceed without it
            }

            // Set AsyncLocal so OnValidateConnection can access the cert
            CurrentClientCert.Value = clientCert;

            var originalRemote = originalSource?.ToString() ?? remoteEp;
            if (clientCert is not null)
            {
                _logger.LogInformation(
                    "TCP bridge on port {Port}: accepted {Remote} (original {Original}) cert={CertSubject} thumbprint={Thumbprint}",
                    port, remoteEp, originalRemote,
                    clientCert.Subject,
                    Convert.ToHexString(clientCert.ThumbprintSha256));
            }
            else
            {
                _logger.LogInformation("TCP bridge on port {Port}: accepted {Remote} (original {Original})",
                    port, remoteEp, originalRemote);
            }

            using (client)
            using (var downstream = new TcpClient())
            {
                await downstream.ConnectAsync("127.0.0.1", _options.Value.InternalBrokerPort, ct);

                await using var brokerStream = downstream.GetStream();

                using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var a = BridgeStreamAsync(clientStream, brokerStream, bridgeCts.Token);
                var b = BridgeStreamAsync(brokerStream, clientStream, bridgeCts.Token);

                await Task.WhenAny(a, b);
                await bridgeCts.CancelAsync();

                try { await Task.WhenAll(a, b); } catch { /* shutdown */ }
            }

            _logger.LogDebug("TCP bridge on port {Port} closed for {Remote}", port, originalRemote);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TCP bridge on port {Port} failed for {Remote}", port, remoteEp);
        }
        finally
        {
            CurrentClientCert.Value = null;
        }
    }

    // ── IPC listener loop ───────────────────────────────────────────────

    /// <summary>
    /// Listens on a named pipe (Windows) or Unix domain socket path (Linux/macOS) and bridges
    /// each incoming connection to the internal MQTTnet broker via a loopback TCP connection.
    /// </summary>
    private async Task RunIpcListenerAsync(string pipeName, CancellationToken ct)
    {
        // Clean up stale socket file on Unix
        if (!OperatingSystem.IsWindows() && File.Exists(pipeName))
        {
            try { File.Delete(pipeName); } catch { /* best effort */ }
        }

        _logger.LogInformation("IPC MQTT listener starting on {Path} (tenant: {Tenant})",
            pipeName, _options.Value.TenantName);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipeServer = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                try
                {
                    await pipeServer.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await pipeServer.DisposeAsync();
                    break;
                }
                catch
                {
                    await pipeServer.DisposeAsync();
                    throw;
                }

                _ = HandleIpcClientAsync(pipeServer, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC listener on {Path} failed", pipeName);
        }

        _logger.LogInformation("IPC MQTT listener stopped on {Path} (tenant: {Tenant})",
            pipeName, _options.Value.TenantName);
    }

    private async Task HandleIpcClientAsync(NamedPipeServerStream pipeServer, CancellationToken ct)
    {
        try
        {
            using (pipeServer)
            using (var downstream = new TcpClient())
            {
                await downstream.ConnectAsync("127.0.0.1", _options.Value.InternalBrokerPort, ct);

                await using var brokerStream = downstream.GetStream();

                using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var a = BridgeStreamAsync(pipeServer, brokerStream, bridgeCts.Token);
                var b = BridgeStreamAsync(brokerStream, pipeServer, bridgeCts.Token);

                await Task.WhenAny(a, b);
                await bridgeCts.CancelAsync();

                try { await Task.WhenAll(a, b); } catch { /* shutdown */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC bridge client handling failed");
        }
    }

    // ── Shared bidirectional stream bridge ──────────────────────────────

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

    // ── TLS listener (SNI-passthrough from MqttGateway) ────────────────

    private async Task<X509Certificate2> LoadTlsCertificateAsync(CancellationToken ct)
    {
        var opts = _options.Value;
        if (!string.IsNullOrWhiteSpace(opts.TlsCertBase64))
        {
            var bytes = Convert.FromBase64String(opts.TlsCertBase64);
            return X509CertificateLoader.LoadPkcs12(bytes, opts.TlsCertPassword);
        }
        if (!string.IsNullOrWhiteSpace(opts.TlsCertPath) && File.Exists(opts.TlsCertPath))
        {
            var bytes = await File.ReadAllBytesAsync(opts.TlsCertPath, ct);
            return X509CertificateLoader.LoadPkcs12(bytes, opts.TlsCertPassword);
        }
        throw new InvalidOperationException("No TLS certificate configured for TenantPlane TLS listener.");
    }

    private async Task RunTlsListenerAsync(TcpListener listener, int port, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleTlsClientAsync(client, port, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TLS listener on port {Port} failed", port);
        }
    }

    /// <summary>
    /// Handles a TLS connection forwarded by the MqttGateway via SNI passthrough.
    /// Terminates TLS at the TenantPlane, extracts the client certificate, and
    /// bridges the decrypted MQTT stream to the internal broker.
    /// </summary>
    private async Task HandleTlsClientAsync(TcpClient client, int port, CancellationToken ct)
    {
        var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            using (client)
            {
                await using var rawStream = client.GetStream();
                await using var sslStream = new SslStream(rawStream, leaveInnerStreamOpen: false);

                var sslOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _tlsCertificate,
                    ClientCertificateRequired = false, // accept both with and without client cert
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };

                await sslStream.AuthenticateAsServerAsync(sslOptions, ct);

                // Extract client certificate from the TLS handshake
                var clientCert = sslStream.RemoteCertificate as X509Certificate2;
                ProxyProtocol.X509CertInfo? certInfo = null;
                if (clientCert is not null)
                {
                    certInfo = new ProxyProtocol.X509CertInfo(
                        clientCert.GetCertHash(HashAlgorithmName.SHA256),
                        clientCert.Subject,
                        clientCert.Issuer);
                }

                CurrentClientCert.Value = certInfo;

                if (certInfo is not null)
                {
                    _logger.LogInformation(
                        "TLS bridge on port {Port}: accepted {Remote} cert={CertSubject} thumbprint={Thumbprint}",
                        port, remoteEp, certInfo.Subject,
                        Convert.ToHexString(certInfo.ThumbprintSha256));
                }
                else
                {
                    _logger.LogInformation("TLS bridge on port {Port}: accepted {Remote} (no client cert)",
                        port, remoteEp);
                }

                using (var downstream = new TcpClient())
                {
                    await downstream.ConnectAsync("127.0.0.1", _options.Value.InternalBrokerPort, ct);
                    await using var brokerStream = downstream.GetStream();

                    using var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var a = BridgeStreamAsync(sslStream, brokerStream, bridgeCts.Token);
                    var b = BridgeStreamAsync(brokerStream, sslStream, bridgeCts.Token);

                    await Task.WhenAny(a, b);
                    await bridgeCts.CancelAsync();

                    try { await Task.WhenAll(a, b); } catch { /* shutdown */ }
                }

                _logger.LogDebug("TLS bridge on port {Port} closed for {Remote}", port, remoteEp);
            }
        }
        catch (AuthenticationException ex)
        {
            _logger.LogWarning(ex, "TLS handshake failed on port {Port} from {Remote}", port, remoteEp);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TLS bridge on port {Port} failed for {Remote}", port, remoteEp);
        }
        finally
        {
            CurrentClientCert.Value = null;
        }
    }

    // ── MQTTnet event handlers ──────────────────────────────────────────

    private Task OnValidateConnection(ValidatingConnectionEventArgs args)
    {
        var cert = CurrentClientCert.Value;
        if (cert is not null)
        {
            _logger.LogInformation(
                "MQTT client connecting: {ClientId} (tenant: {Tenant}) cert={CertSubject} thumbprint={Thumbprint}",
                args.ClientId, _options.Value.TenantName,
                cert.Subject,
                Convert.ToHexString(cert.ThumbprintSha256));
        }
        else
        {
            _logger.LogInformation("MQTT client connecting: {ClientId} (tenant: {Tenant})",
                args.ClientId, _options.Value.TenantName);
        }
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
