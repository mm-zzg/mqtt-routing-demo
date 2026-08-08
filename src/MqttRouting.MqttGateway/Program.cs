using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        options.MqttTlsListenPort = options.MqttTlsListenPort <= 0 ? 8883 : options.MqttTlsListenPort;
    });

// TCP listeners: plain TCP (optional PPv2 for L4 LB) + TLS (with client cert forwarding)
builder.Services.AddHostedService<MqttTcpGatewayService>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", (IOptions<MqttGatewayOptions> options) =>
    Results.Ok(new
    {
        baseDomain = options.Value.BaseDomain,
        mqttTcpListenPort = options.Value.MqttTcpListenPort,
        mqttTlsListenPort = options.Value.MqttTlsListenPort,
        useTls = options.Value.HasTlsCert(),
        routeTable = options.Value.RouteTable.Select(r => new { r.Tenant, r.Host, r.Port })
    }));

await app.RunAsync();

// ═══════════════════════════════════════════════════════════════════════════
// MQTT TCP/TLS Gateway Service
// ═══════════════════════════════════════════════════════════════════════════

sealed class MqttTcpGatewayService : IHostedService
{
    private readonly MqttGatewayOptions _options;
    private readonly ILogger<MqttTcpGatewayService> _logger;
    private TcpListener? _plainListener;
    private TcpListener? _tlsListener;
    private X509Certificate2? _serverCert;
    private CancellationTokenSource? _cts;

    public MqttTcpGatewayService(IOptions<MqttGatewayOptions> options, ILogger<MqttTcpGatewayService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _serverCert = LoadCertificate();

        // Plain TCP listener (port 1883 by default)
        _plainListener = new TcpListener(IPAddress.Any, _options.MqttTcpListenPort);
        _plainListener.Start();
        _logger.LogInformation("MQTT plain TCP listener on :{Port}", _options.MqttTcpListenPort);

        // TLS listener (port 8883 by default)
        _tlsListener = new TcpListener(IPAddress.Any, _options.MqttTlsListenPort);
        _tlsListener.Start();
        _logger.LogInformation("MQTT TLS listener on :{Port} (cert subject: {Subject})",
            _options.MqttTlsListenPort, _serverCert?.Subject ?? "(self-signed)");

        _ = AcceptLoopAsync(_plainListener, useTls: false, _cts.Token);
        _ = AcceptLoopAsync(_tlsListener, useTls: true, _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        _plainListener?.Stop();
        _tlsListener?.Stop();
        _serverCert?.Dispose();
        return Task.CompletedTask;
    }

    // ── Certificate loading ────────────────────────────────────────────

    private X509Certificate2 LoadCertificate()
    {
        // 1. Base64-encoded PFX (production: from ACA secret / Terraform)
        if (!string.IsNullOrWhiteSpace(_options.TlsCertBase64))
        {
            try
            {
                var certBytes = Convert.FromBase64String(_options.TlsCertBase64);
                var password = string.IsNullOrWhiteSpace(_options.TlsCertPassword)
                    ? (string?)null
                    : _options.TlsCertPassword;
                var cert = password is null
                    ? X509CertificateLoader.LoadCertificate(certBytes)
                    : X509CertificateLoader.LoadPkcs12(certBytes, password);
                _logger.LogInformation("Loaded TLS certificate from base64 PFX (subject: {Subject})", cert.Subject);
                return cert;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load TLS cert from base64; falling back to self-signed");
            }
        }

        // 2. PFX file path
        if (!string.IsNullOrWhiteSpace(_options.TlsCertPath) && File.Exists(_options.TlsCertPath))
        {
            try
            {
                var cert = string.IsNullOrWhiteSpace(_options.TlsCertPassword)
                    ? X509CertificateLoader.LoadCertificateFromFile(_options.TlsCertPath)
                    : X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(_options.TlsCertPath), _options.TlsCertPassword);
                _logger.LogInformation("Loaded TLS certificate from file: {Path} (subject: {Subject})",
                    _options.TlsCertPath, cert.Subject);
                return cert;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load TLS cert from file {Path}; falling back to self-signed",
                    _options.TlsCertPath);
            }
        }

        // 3. Self-signed certificate (local debug)
        _logger.LogWarning("No TLS certificate configured; generating self-signed certificate for local debug");
        return GenerateSelfSignedCertificate();
    }

    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=MQTT Gateway Local Dev, O=Dev, OU=MQTT",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        // Export and re-import as PFX so it's usable as a server cert with private key
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadCertificate(pfxBytes);
    }

    // ── Accept loops ───────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, bool useTls, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, useTls, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    // ── Per-connection handler ─────────────────────────────────────────

    private async Task HandleClientAsync(TcpClient client, bool useTls, CancellationToken ct)
    {
        using var __ = client;
        var rawStream = client.GetStream();
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        X509Certificate2? clientCert = null;
        IPEndPoint? originalSource = null;
        Stream stream = rawStream;

        try
        {
            // ── TLS handshake (TLS port only) ──
            if (useTls && _serverCert is not null)
            {
                var sslStream = new SslStream(rawStream, false,
                    (sender, certificate, chain, errors) => true, // accept any client cert
                    null);
                stream = sslStream;

                await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, ct);

                if (sslStream.RemoteCertificate is X509Certificate2 remoteCert)
                {
                    clientCert = remoteCert;
                    _logger.LogInformation("MQTT TLS gateway: client {Remote} presented cert {Subject} ({Thumbprint})",
                        remote, clientCert.Subject,
                        Convert.ToHexString(clientCert.GetCertHash(HashAlgorithmName.SHA256)));
                }
                else
                {
                    _logger.LogInformation("MQTT TLS gateway: client {Remote} connected (no client cert)", remote);
                }

                // Use the SSL-decrypted stream for subsequent reads
            }
            else
            {
                // ── Parse optional Proxy Protocol v2 header (from upstream L4 LB on plain TCP) ──
                try
                {
                    (originalSource, _) = await ProxyProtocol.ReadV2HeaderAsync(rawStream, ct);
                }
                catch
                {
                    // If PPv2 parsing fails, proceed without it
                }
            }
        }
        catch (AuthenticationException ex)
        {
            _logger.LogWarning(ex, "TLS handshake failed for {Remote}", remote);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Connection setup failed for {Remote}", remote);
            return;
        }

        string? tenant = null;
        string? clientId = null;
        int protocolLevel = 0;
        int bytesRead = 0;

        try
        {
            var displayRemote = originalSource?.ToString() ?? remote;
            _logger.LogInformation("MQTT gateway: accepted {Remote} (useTLS={UseTls}, clientCert={HasCert})",
                displayRemote, useTls, clientCert is not null);

            // ── Read & parse MQTT CONNECT from stream ──
            byte[] connectBuffer = new byte[4096];
            const int maxBufferSize = 256 * 1024;

            async ValueTask<bool> EnsureBufferAsync(int target)
            {
                while (connectBuffer.Length < target)
                {
                    if (target > maxBufferSize) return false;
                    Array.Resize(ref connectBuffer, connectBuffer.Length * 2);
                }

                while (bytesRead < target)
                {
                    int n = await stream.ReadAsync(connectBuffer.AsMemory(bytesRead, target - bytesRead), ct);
                    if (n == 0) return false;
                    bytesRead += n;
                }
                return true;
            }

            if (!await EnsureBufferAsync(2)) return;
            if ((connectBuffer[0] >> 4) != 1) return; // CONNECT type

            int remaining = 0;
            int multiplier = 1;
            int offset = 1;
            byte b;
            do
            {
                if (!await EnsureBufferAsync(offset + 2)) return;
                b = connectBuffer[offset++];
                remaining += (b & 127) * multiplier;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128) return;
            }
            while ((b & 128) != 0);

            int totalConnectSize = offset + remaining;
            if (!await EnsureBufferAsync(totalConnectSize)) return;

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
                _logger.LogWarning("Rejecting {Endpoint}: no tenant prefix in clientId '{ClientId}'", displayRemote, clientId);
                byte[] reject = protocolLevel == 5
                    ? new byte[] { 0x20, 0x03, 0x00, 0x02, 0x00 }
                    : new byte[] { 0x20, 0x02, 0x00, 0x02 };
                await rawStream.WriteAsync(reject, ct);
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
                await rawStream.WriteAsync(reject, ct);
                return;
            }

            _logger.LogInformation("Routing {ClientId} → tenant {Tenant} → {Host}:{Port} (cert={HasCert})",
                clientId, tenant, backend.Host, backend.Port, clientCert is not null);

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
                await rawStream.WriteAsync(reject, ct);
                return;
            }

            // ── Send Proxy Protocol v2 header (with client cert TLV if present) ──
            await using var downstreamStream = downstream.GetStream();
            var tenantLocalEp = downstream.Client.LocalEndPoint ?? new IPEndPoint(IPAddress.None, 0);
            var srcEp = originalSource
                ?? (client.Client.RemoteEndPoint as IPEndPoint)
                ?? new IPEndPoint(IPAddress.None, 0);
            var proxyHeader = ProxyProtocol.BuildV2Header(srcEp, tenantLocalEp, clientCert);
            await downstreamStream.WriteAsync(proxyHeader, ct);

            // ── Forward buffered CONNECT bytes to TenantPlane ──
            if (totalConnectSize > 0)
            {
                await downstreamStream.WriteAsync(connectBuffer.AsMemory(0, totalConnectSize), ct);
            }

            // Forward any extra bytes beyond CONNECT
            if (bytesRead > totalConnectSize)
            {
                await downstreamStream.WriteAsync(connectBuffer.AsMemory(totalConnectSize, bytesRead - totalConnectSize), ct);
            }

            // ── Bidirectional pump: client ↔ TenantPlane ──
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var upToDown = BridgeStreamAsync(stream, downstreamStream, linkedCts.Token);
            var downToUp = BridgeStreamAsync(downstreamStream, stream, linkedCts.Token);

            await Task.WhenAny(upToDown, downToUp);
            linkedCts.Cancel();

            try { await Task.WhenAll(upToDown, downToUp); } catch { /* shutdown */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT gateway: error for {Remote}", remote);
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

// ═══════════════════════════════════════════════════════════════════════════
// Configuration
// ═══════════════════════════════════════════════════════════════════════════

sealed record MqttGatewayOptions
{
    public string BaseDomain { get; set; } = "example.com";
    public int MqttTcpListenPort { get; set; } = 1883;
    public int MqttTlsListenPort { get; set; } = 8883;

    // TLS certificate
    public string? TlsCertBase64 { get; set; }          // PFX as base64 (from ACA secrets)
    public string? TlsCertPath { get; set; }            // PFX file path
    public string? TlsCertPassword { get; set; }

    public List<TenantBackend> RouteTable { get; set; } = new();

    public bool HasTlsCert() =>
        !string.IsNullOrWhiteSpace(TlsCertBase64)
        || (!string.IsNullOrWhiteSpace(TlsCertPath) && File.Exists(TlsCertPath));
}

sealed record TenantBackend
{
    public string Tenant { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
}
