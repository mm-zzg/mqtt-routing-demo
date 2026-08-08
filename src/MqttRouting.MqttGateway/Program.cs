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
        options.MqttTlsListenPort = options.MqttTlsListenPort <= 0 ? 8883 : options.MqttTlsListenPort;
    });

// TLS-only listener (with client cert forwarding)
builder.Services.AddHostedService<MqttTlsGatewayService>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", (IOptions<MqttGatewayOptions> options) =>
    Results.Ok(new
    {
        baseDomain = options.Value.BaseDomain,
        mqttTlsListenPort = options.Value.MqttTlsListenPort,
        useTls = options.Value.HasTlsCert(),
        routeTable = options.Value.RouteTable.Select(r => new { r.Tenant, r.Host, r.Port, r.TlsPort })
    }));

await app.RunAsync();

// ═══════════════════════════════════════════════════════════════════════════
// MQTT TLS Gateway Service (TLS-only, no plain TCP)
// ═══════════════════════════════════════════════════════════════════════════

sealed class MqttTlsGatewayService : IHostedService
{
    private readonly MqttGatewayOptions _options;
    private readonly ILogger<MqttTlsGatewayService> _logger;
    private TcpListener? _tlsListener;
    private X509Certificate2? _serverCert;
    private CancellationTokenSource? _cts;

    public MqttTlsGatewayService(IOptions<MqttGatewayOptions> options, ILogger<MqttTlsGatewayService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _serverCert = LoadCertificate();

        _tlsListener = new TcpListener(IPAddress.Any, _options.MqttTlsListenPort);
        _tlsListener.Start();
        _logger.LogInformation("MQTT TLS listener on :{Port} (cert subject: {Subject})",
            _options.MqttTlsListenPort, _serverCert?.Subject ?? "(self-signed)");

        _ = AcceptLoopAsync(_tlsListener, _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
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
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    // ── Accept loop ────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = HandleClientAsync(client, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    // ── Per-connection handler (TLS only) ──────────────────────────────

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var __ = client;
        var rawStream = client.GetStream();
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        X509Certificate2? clientCert = null;
        string? sniHostName = null;
        Stream stream = rawStream;

        try
        {
            var sslStream = new SslStream(rawStream, false,
                (sender, certificate, chain, errors) => true, // accept any client cert
                null);
            stream = sslStream;

            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _serverCert!,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);

            // SNI hostname sent by the client (null if client did not send SNI)
            sniHostName = sslStream.TargetHostName;

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

        // ── Dispatch to the appropriate routing path ──
        if (!string.IsNullOrEmpty(sniHostName))
        {
            // SNI path: passthrough, no CONNECT parsing, no PPv2.
            // Routing is based on the subdomain of the SNI hostname.
            _logger.LogInformation("MQTT gateway: SNI routing {Remote} SNI={SniHostName}",
                remote, sniHostName);
            await HandleSniRoutingAsync(stream, remote, sniHostName, ct);
        }
        else
        {
            // No-SNI path: decode MQTT CONNECT, extract tenant from
            // username prefix, reverse-proxy with Proxy Protocol v2.
            _logger.LogInformation("MQTT gateway: MQTT-connect routing {Remote} (no SNI, clientCert={HasCert})",
                remote, clientCert is not null);
            await HandleMqttRoutingAsync(stream, rawStream, client.Client.RemoteEndPoint as IPEndPoint,
                remote, clientCert, ct);
        }
    }

    // ───────────────────────────────────────────────────────────────────
    // SNI routing: passthrough (no MQTT CONNECT inspection, no PPv2)
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleSniRoutingAsync(
        Stream decryptedStream, string remote, string sniHostName,
        CancellationToken ct)
    {
        var tenant = ExtractTenantFromHost(sniHostName, _options.BaseDomain);
        if (tenant is null)
        {
            _logger.LogWarning("SNI routing: rejecting {Remote}, SNI={Sni}, no tenant in subdomain",
                remote, sniHostName);
            return;
        }

        var backend = _options.RouteTable.FirstOrDefault(r =>
            string.Equals(r.Tenant, tenant, StringComparison.OrdinalIgnoreCase));
        if (backend is null)
        {
            _logger.LogWarning("SNI routing: no backend for tenant {Tenant} (SNI={Sni})", tenant, sniHostName);
            return;
        }

        _logger.LogInformation("SNI passthrough: {SniHostName} → tenant {Tenant} → {Host}:{Port}",
            sniHostName, tenant, backend.Host, backend.Port);

        try
        {
            using var downstream = new TcpClient();
            await downstream.ConnectAsync(backend.Host, backend.Port, ct);
            await using var downstreamStream = downstream.GetStream();

            // Raw passthrough: no CONNECT inspection, no PPv2.
            // All bytes are forwarded as-is after TLS termination.
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var upToDown = BridgeStreamAsync(decryptedStream, downstreamStream, linkedCts.Token);
            var downToUp = BridgeStreamAsync(downstreamStream, decryptedStream, linkedCts.Token);

            await Task.WhenAny(upToDown, downToUp);
            linkedCts.Cancel();
            try { await Task.WhenAll(upToDown, downToUp); } catch { /* shutdown */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SNI passthrough: error for {Remote} (SNI={Sni})", remote, sniHostName);
        }

        _logger.LogInformation("SNI passthrough: disconnected {Remote} (tenant {Tenant}, SNI={Sni})",
            remote, tenant ?? "?", sniHostName);
    }

    // ───────────────────────────────────────────────────────────────────
    // No-SNI routing: decode MQTT CONNECT → username prefix → PPv2
    // ───────────────────────────────────────────────────────────────────

    private async Task HandleMqttRoutingAsync(
        Stream decryptedStream, Stream rawStream, IPEndPoint? clientEp,
        string remote, X509Certificate2? clientCert,
        CancellationToken ct)
    {
        string? tenant = null;
        string? clientId = null;
        int protocolLevel = 0;
        int bytesRead = 0;

        try
        {
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
                    int n = await decryptedStream.ReadAsync(connectBuffer.AsMemory(bytesRead, target - bytesRead), ct);
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
            var connectFlags = connectBuffer[protoNameEnd + 1];

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

            // ── Parse payload: clientId, then optionally username ──
            if (!await EnsureBufferAsync(payloadStart + 2)) return;
            var clientIdLength = (connectBuffer[payloadStart] << 8) | connectBuffer[payloadStart + 1];
            if (!await EnsureBufferAsync(payloadStart + 2 + clientIdLength)) return;
            clientId = Encoding.UTF8.GetString(connectBuffer, payloadStart + 2, clientIdLength);

            // Extract tenant from username prefix (e.g. "tenantA.device1" → "tenantA")
            string? username = null;
            int nextFieldPos = payloadStart + 2 + clientIdLength;

            // Skip will topic + will message if will flag is set (bit 2)
            if ((connectFlags & 0x04) != 0)
            {
                if (!await EnsureBufferAsync(nextFieldPos + 2)) return;
                var willTopicLen = (connectBuffer[nextFieldPos] << 8) | connectBuffer[nextFieldPos + 1];
                nextFieldPos += 2 + willTopicLen;

                // MQTT 5: will message length; MQTT 3: will message is binary
                if (protocolLevel == 5)
                {
                    await EnsureBufferAsync(nextFieldPos + 2);
                    var willMsgLen = (connectBuffer[nextFieldPos] << 8) | connectBuffer[nextFieldPos + 1];
                    nextFieldPos += 2 + willMsgLen;
                }
                else
                {
                    if (!await EnsureBufferAsync(nextFieldPos + 2)) return;
                    var willMsgLen = (connectBuffer[nextFieldPos] << 8) | connectBuffer[nextFieldPos + 1];
                    nextFieldPos += 2 + willMsgLen;
                }
            }

            // Read username if username flag is set (bit 7)
            if ((connectFlags & 0x80) != 0)
            {
                if (!await EnsureBufferAsync(nextFieldPos + 2)) return;
                var usernameLen = (connectBuffer[nextFieldPos] << 8) | connectBuffer[nextFieldPos + 1];
                if (!await EnsureBufferAsync(nextFieldPos + 2 + usernameLen)) return;
                username = Encoding.UTF8.GetString(connectBuffer, nextFieldPos + 2, usernameLen);
            }

            tenant = ExtractTenant(username);
            if (tenant is null)
            {
                _logger.LogWarning(
                    "MQTT routing: rejecting {Remote}, no tenant prefix in username '{Username}' (clientId='{ClientId}')",
                    remote, username ?? "(none)", clientId);
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
                _logger.LogWarning("MQTT routing: no backend for tenant {Tenant} (username={Username})",
                    tenant, username);
                byte[] reject = protocolLevel == 5
                    ? new byte[] { 0x20, 0x03, 0x00, 0x02, 0x00 }
                    : new byte[] { 0x20, 0x02, 0x00, 0x02 };
                await rawStream.WriteAsync(reject, ct);
                return;
            }

            _logger.LogInformation("MQTT routing: {ClientId} (user={Username}) → tenant {Tenant} → {Host}:{Port}",
                clientId, username, tenant, backend.Host, backend.Port);

            // ── Connect to TenantPlane via TCP ──
            using var downstream = new TcpClient();
            try
            {
                await downstream.ConnectAsync(backend.Host, backend.Port, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MQTT routing: failed to connect to {Host}:{Port}", backend.Host, backend.Port);
                byte[] reject = protocolLevel == 5
                    ? new byte[] { 0x20, 0x03, 0x00, 0x03, 0x00 }
                    : new byte[] { 0x20, 0x02, 0x00, 0x03 };
                await rawStream.WriteAsync(reject, ct);
                return;
            }

            // ── Send Proxy Protocol v2 header (with client cert TLV if present) ──
            await using var downstreamStream = downstream.GetStream();
            var tenantLocalEp = downstream.Client.LocalEndPoint ?? new IPEndPoint(IPAddress.None, 0);
            var srcEp = clientEp ?? new IPEndPoint(IPAddress.None, 0);
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
            var upToDown = BridgeStreamAsync(decryptedStream, downstreamStream, linkedCts.Token);
            var downToUp = BridgeStreamAsync(downstreamStream, decryptedStream, linkedCts.Token);

            await Task.WhenAny(upToDown, downToUp);
            linkedCts.Cancel();

            try { await Task.WhenAll(upToDown, downToUp); } catch { /* shutdown */ }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MQTT routing: error for {Remote}", remote);
        }

        if (clientId is not null)
            _logger.LogInformation("MQTT routing: {ClientId} (tenant {Tenant}) disconnected", clientId, tenant ?? "?");
    }

    // ── Tenant extraction helpers ──────────────────────────────────────

    /// <summary>
    /// Extracts the tenant from the username prefix (e.g. "tenantA.device1" → "tenantA").
    /// </summary>
    private static string? ExtractTenant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var dot = value.IndexOf('.');
        if (dot <= 0) return null;
        var tenant = value[..dot];
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }

    /// <summary>
    /// Extracts the tenant from the SNI hostname subdomain
    /// (e.g. "tenantA.example.com" → "tenantA").
    /// </summary>
    private static string? ExtractTenantFromHost(string hostName, string baseDomain)
    {
        if (string.IsNullOrWhiteSpace(hostName)) return null;
        // Match: <tenant>.<baseDomain>  or  <tenant>.<baseDomain>:<port>
        var suffix = "." + baseDomain.Trim('.');
        var host = hostName;
        var portIdx = host.IndexOf(':');
        if (portIdx >= 0) host = host[..portIdx];

        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var tenant = host[..^suffix.Length];
        return string.IsNullOrWhiteSpace(tenant) ? null : tenant;
    }

    // ── Stream helper ──────────────────────────────────────────────────

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

    /// <summary>Plain TCP port for no-SNI (PPv2) connections.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>TLS port for SNI-passthrough connections. Falls back to <see cref="Port"/> if not set.</summary>
    public int TlsPort { get; set; } = 0;
}
