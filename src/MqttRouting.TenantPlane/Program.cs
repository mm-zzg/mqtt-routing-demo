using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;
using MqttRouting.TenantPlane;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddOptions<TenantPlaneOptions>()
    .Bind(builder.Configuration.GetSection("TenantPlane"))
    .PostConfigure(options =>
    {
        options.TenantName = string.IsNullOrWhiteSpace(options.TenantName) ? "tenant" : options.TenantName;
        options.HttpPort = options.HttpPort <= 0 ? 8080 : options.HttpPort;
        options.InternalBrokerPort = options.InternalBrokerPort <= 0 ? 11883 : options.InternalBrokerPort;
        options.TcpListenerPorts = options.TcpListenerPorts
            .Where(p => p > 0 && p != options.InternalBrokerPort)
            .Distinct()
            .ToList();
        // No default TCP listener ports — must be configured explicitly.
        // This avoids port conflicts with MqttGateway (1883) and other services
        // in local debug mode.

        // TLS listener: generate a self-signed dev cert if a port is set but
        // no certificate was provided (local debug convenience).
        if (options.TlsListenerPort is > 0 && !options.HasTlsCert())
        {
            var domain = options.CustomDomain ?? $"{options.TenantName}.{options.BaseDomain}";
            var cert = GenerateSelfSignedCert(domain);
            options.TlsCertBase64 = Convert.ToBase64String(cert.Export(X509ContentType.Pkcs12));
            options.TlsCertPassword = null;
        }
    });
builder.Services.AddSingleton<TenantMessageStore>();
builder.Services.AddSingleton<MqttBrokerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBrokerService>());

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/", (IOptions<TenantPlaneOptions> options) =>
{
    var tenant = options.Value;
    return Results.Ok(new
    {
        tenant.TenantName,
        tenant.BaseDomain,
        tenant.HttpPort,
        InternalBrokerPort = tenant.InternalBrokerPort,
        TcpListenerPorts = tenant.TcpListenerPorts,
        TlsListenerPort = tenant.TlsListenerPort,
        IpcEndpoint = tenant.IpcEndpointPath ?? "(disabled)",
        CustomDomain = tenant.CustomDomain ?? $"{tenant.TenantName}.{tenant.BaseDomain}"
    });
});

app.MapGet("/messages", (TenantMessageStore store) => Results.Ok(store.All()));
app.MapPost("/messages", (TenantMessage message, TenantMessageStore store) =>
{
    store.Add(message);
    return Results.Accepted($"/messages/{message.Id}", message);
});

app.MapGet("/broker", (IOptions<TenantPlaneOptions> options) => Results.Ok(new
{
    name = options.Value.TenantName,
    internalBrokerPort = options.Value.InternalBrokerPort,
    tcpListenerPorts = options.Value.TcpListenerPorts,
    tlsListenerPort = options.Value.TlsListenerPort,
    ipcEndpoint = options.Value.IpcEndpointPath ?? "(disabled)",
    mode = "embedded"
}));

await app.RunAsync();

// ── Helpers ───────────────────────────────────────────────────────────

static X509Certificate2 GenerateSelfSignedCert(string subjectName)
{
    using var rsa = RSA.Create(2048);
    var req = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    // SAN for the custom domain
    var sanBuilder = new SubjectAlternativeNameBuilder();
    sanBuilder.AddDnsName(subjectName);
    req.CertificateExtensions.Add(sanBuilder.Build());

    // Basic constraints: not a CA
    req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

    // Extended key usage: server authentication
    req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        [new Oid("1.3.6.1.5.5.7.3.1")], false));

    var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    return cert;
}
