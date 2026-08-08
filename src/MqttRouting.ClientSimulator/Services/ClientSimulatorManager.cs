using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using MQTTnet.Protocol;
using MqttRouting.ClientSimulator.Data;

namespace MqttRouting.ClientSimulator.Services;

public sealed class ClientSimulatorManager
{
    private readonly ConcurrentDictionary<string, ClientRuntime> _clients = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientSimulatorManager> _logger;

    public ClientSimulatorManager(IServiceScopeFactory scopeFactory, ILogger<ClientSimulatorManager> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<List<ClientCertificateRecord>> GetCertificatesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entities = await db.Certificates.OrderBy(c => c.Name).ToListAsync();
        return entities.Select(e => new ClientCertificateRecord(e.Id, e.Name, e.PfxBase64, e.Password, e.CreatedAt)).ToList();
    }

    public IReadOnlyList<ClientRuntimeSnapshot> GetClients() =>
        _clients.Values.Select(c => c.ToSnapshot()).OrderBy(c => c.Name).ToList();

    public async Task<string> AddCertificateAsync(CertificateInput input)
    {
        _ = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(input.PfxBase64), input.Password);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = new CertificateEntity
        {
            Name = input.Name.Trim(),
            PfxBase64 = input.PfxBase64.Trim(),
            Password = input.Password
        };
        db.Certificates.Add(entity);
        await db.SaveChangesAsync();

        return entity.Id;
    }

    public async Task<string> GenerateCertificateAsync(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN=MQTT Simulator Client - {name.Trim()}, O=Dev, OU=MQTT",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        var password = Guid.NewGuid().ToString("N")[..16];
        var pfxBytes = cert.Export(X509ContentType.Pfx, password);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entity = new CertificateEntity
        {
            Name = name.Trim(),
            PfxBase64 = Convert.ToBase64String(pfxBytes),
            Password = password
        };
        db.Certificates.Add(entity);
        await db.SaveChangesAsync();

        return entity.Id;
    }

    public async Task<string> AddClientAsync(ClientInput input)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!string.IsNullOrEmpty(input.CertificateId))
        {
            var certExists = await db.Certificates.AnyAsync(c => c.Id == input.CertificateId);
            if (!certExists)
                throw new InvalidOperationException("Selected certificate was not found.");
        }

        var entity = new ClientConfigEntity
        {
            Name = input.Name.Trim(),
            BrokerHost = input.BrokerHost.Trim(),
            BrokerPort = input.BrokerPort,
            Topic = input.Topic.Trim(),
            PublishIntervalSeconds = input.PublishIntervalSeconds,
            CertificateId = string.IsNullOrEmpty(input.CertificateId) ? null : input.CertificateId
        };
        db.ClientConfigs.Add(entity);
        await db.SaveChangesAsync();

        ClientCertificateRecord? certRecord = null;
        if (entity.CertificateId is not null)
        {
            var certEntity = await db.Certificates.FirstOrDefaultAsync(c => c.Id == entity.CertificateId);
            if (certEntity is not null)
                certRecord = new ClientCertificateRecord(certEntity.Id, certEntity.Name, certEntity.PfxBase64, certEntity.Password, certEntity.CreatedAt);
        }

        var runtime = new ClientRuntime(
            entity.Id,
            entity.Name,
            entity.BrokerHost,
            entity.BrokerPort,
            entity.Topic,
            entity.PublishIntervalSeconds,
            entity.CertificateId,
            certRecord,
            _logger);

        if (!_clients.TryAdd(entity.Id, runtime))
            throw new InvalidOperationException("Unable to add client runtime.");

        return entity.Id;
    }

    public async Task LoadPersistedClientsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configs = await db.ClientConfigs.Include(c => c.Certificate).ToListAsync();

        foreach (var config in configs)
        {
            var certRecord = config.Certificate is not null
                ? new ClientCertificateRecord(config.Certificate.Id, config.Certificate.Name,
                    config.Certificate.PfxBase64, config.Certificate.Password, config.Certificate.CreatedAt)
                : null;

            var runtime = new ClientRuntime(
                config.Id, config.Name, config.BrokerHost, config.BrokerPort,
                config.Topic, config.PublishIntervalSeconds, config.CertificateId,
                certRecord, _logger);

            _clients.TryAdd(config.Id, runtime);
        }

        _logger.LogInformation("Loaded {Count} persisted client configs.", configs.Count);
    }

    /// <summary>
    /// Ensures two default MQTT clients exist: one for tenantA and one for tenantB.
    /// Both connect to the MqttGateway TCP gateway.
    /// </summary>
    public async Task EnsureDefaultClientsAsync(string brokerHost, int brokerPort, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var defaults = new[]
        {
            new { Name = "TenantA Simulator", ClientId = "tenantA.simulator", Topic = "tenantA/simulator/heartbeat" },
            new { Name = "TenantB Simulator", ClientId = "tenantB.simulator", Topic = "tenantB/simulator/heartbeat" }
        };

        foreach (var d in defaults)
        {
            if (_clients.ContainsKey(d.ClientId))
                continue;

            var existing = await db.ClientConfigs.FindAsync(d.ClientId);
            if (existing is null)
            {
                existing = new ClientConfigEntity
                {
                    Id = d.ClientId,
                    Name = d.Name,
                    BrokerHost = brokerHost,
                    BrokerPort = brokerPort,
                    Topic = d.Topic,
                    PublishIntervalSeconds = 10
                };
                db.ClientConfigs.Add(existing);
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Created default client {Name} ({ClientId}) → {Host}:{Port}", d.Name, d.ClientId, brokerHost, brokerPort);
            }

            var runtime = new ClientRuntime(
                existing.Id, existing.Name, existing.BrokerHost, existing.BrokerPort,
                existing.Topic, existing.PublishIntervalSeconds, existing.CertificateId,
                null, _logger);

            _clients.TryAdd(existing.Id, runtime);
        }
    }

    public Task StartClientAsync(string clientId, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(clientId, out var runtime))
            throw new InvalidOperationException("Client was not found.");

        return runtime.StartAsync(cancellationToken);
    }

    public Task StopClientAsync(string clientId)
    {
        if (!_clients.TryGetValue(clientId, out var runtime))
            throw new InvalidOperationException("Client was not found.");

        return runtime.StopAsync();
    }

    public async Task RemoveClientAsync(string clientId)
    {
        if (!_clients.TryRemove(clientId, out var runtime))
            throw new InvalidOperationException("Client was not found.");

        await runtime.StopAsync();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.ClientConfigs.FindAsync(clientId);
        if (config is not null)
        {
            db.ClientConfigs.Remove(config);
            await db.SaveChangesAsync();
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var clientId in _clients.Keys)
        {
            if (_clients.TryGetValue(clientId, out var runtime))
                await runtime.StopAsync();
        }
    }
}

// ── Start-and-load hosted service ──────────────────────────────────

public sealed class ClientSimulatorHostedService : IHostedService
{
    private readonly ClientSimulatorManager _manager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ClientSimulatorHostedService> _logger;
    private readonly IConfiguration _configuration;

    public ClientSimulatorHostedService(
        ClientSimulatorManager manager,
        IServiceScopeFactory scopeFactory,
        ILogger<ClientSimulatorHostedService> logger,
        IConfiguration configuration)
    {
        _manager = manager;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        _logger.LogInformation("Database ensured.");

        await _manager.LoadPersistedClientsAsync();

        // Ensure two default clients exist and auto-start them
        var brokerHost = _configuration["ClientSimulator:BrokerHost"] ?? "localhost";
        var brokerPort = _configuration.GetValue<int>("ClientSimulator:BrokerPort");
        if (brokerPort <= 0) brokerPort = 1883;

        await _manager.EnsureDefaultClientsAsync(brokerHost, brokerPort, cancellationToken);

        foreach (var snapshot in _manager.GetClients())
        {
            try
            {
                await _manager.StartClientAsync(snapshot.Id, cancellationToken);
                _logger.LogInformation("Auto-started client {Name} ({Id})", snapshot.Name, snapshot.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-start client {Name} ({Id})", snapshot.Name, snapshot.Id);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => _manager.StopAllAsync();
}

// ── Input models ───────────────────────────────────────────────────

public sealed class CertificateInput
{
    [Required, MinLength(2)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string PfxBase64 { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public sealed class ClientInput
{
    [Required, MinLength(2)]
    public string Name { get; set; } = string.Empty;

    [Required, MinLength(2)]
    public string BrokerHost { get; set; } = "localhost";

    [Range(1, 65535)]
    public int BrokerPort { get; set; } = 1883;

    [Required, MinLength(1)]
    public string Topic { get; set; } = "simulator/heartbeat";

    [Range(1, 3600)]
    public int PublishIntervalSeconds { get; set; } = 10;

    public string? CertificateId { get; set; }
}

// ── Domain records ─────────────────────────────────────────────────

public sealed record ClientCertificateRecord(
    string Id,
    string Name,
    string PfxBase64,
    string Password,
    DateTimeOffset CreatedAt);

public enum ClientStatus
{
    Stopped,
    Starting,
    Running,
    Faulted
}

public sealed record ClientRuntimeSnapshot(
    string Id,
    string Name,
    string BrokerHost,
    int BrokerPort,
    string Topic,
    int PublishIntervalSeconds,
    string? CertificateId,
    ClientStatus Status,
    long PublishCount,
    DateTimeOffset? LastPublishedAt,
    string? LastError);

// ── MQTT-based client runtime ──────────────────────────────────────

internal sealed class ClientRuntime
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ClientCertificateRecord? _certificate;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string? _lastError;
    private long _publishCount;
    private DateTimeOffset? _lastPublishedAt;

    public ClientRuntime(
        string id,
        string name,
        string brokerHost,
        int brokerPort,
        string topic,
        int publishIntervalSeconds,
        string? certificateId,
        ClientCertificateRecord? certificate,
        ILogger logger)
    {
        Id = id;
        Name = name;
        BrokerHost = brokerHost;
        BrokerPort = brokerPort;
        Topic = topic;
        PublishIntervalSeconds = publishIntervalSeconds;
        CertificateId = certificateId;
        _certificate = certificate;
        _logger = logger;
    }

    public string Id { get; }
    public string Name { get; }
    public string BrokerHost { get; }
    public int BrokerPort { get; }
    public string Topic { get; }
    public int PublishIntervalSeconds { get; }
    public string? CertificateId { get; }
    public ClientStatus Status { get; private set; } = ClientStatus.Stopped;

    public ClientRuntimeSnapshot ToSnapshot() =>
        new(Id, Name, BrokerHost, BrokerPort, Topic, PublishIntervalSeconds,
            CertificateId, Status, _publishCount, _lastPublishedAt, _lastError);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status is ClientStatus.Starting or ClientStatus.Running)
                return;

            Status = ClientStatus.Starting;
            _lastError = null;
            _loopCts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCts.Token);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        Task? runningTask;
        await _gate.WaitAsync();
        try
        {
            if (_loopCts is null) { Status = ClientStatus.Stopped; return; }
            _loopCts.Cancel();
            runningTask = _loopTask;
        }
        finally { _gate.Release(); }

        if (runningTask is not null) await runningTask;

        await _gate.WaitAsync();
        try
        {
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
            Status = ClientStatus.Stopped;
        }
        finally { _gate.Release(); }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        var mqttFactory = new MqttClientFactory();
        using var mqttClient = mqttFactory.CreateMqttClient();

        mqttClient.ConnectedAsync += _ =>
        {
            _logger.LogInformation("MQTT client {ClientName} connected to {Host}:{Port}.", Name, BrokerHost, BrokerPort);
            return Task.CompletedTask;
        };

        mqttClient.DisconnectedAsync += e =>
        {
            var reason = e.Reason;
            _logger.LogWarning("MQTT client {ClientName} disconnected: {Reason}.", Name, reason);
            return Task.CompletedTask;
        };

        try
        {
            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithTcpServer(BrokerHost, BrokerPort)
                .WithClientId(Id)
                .WithCleanSession();

            if (_certificate is not null)
            {
                var certBytes = Convert.FromBase64String(_certificate.PfxBase64);
                var cert = X509CertificateLoader.LoadPkcs12(certBytes, _certificate.Password);
                var certs = new X509Certificate2Collection(cert);
                optionsBuilder.WithTlsOptions(o =>
                {
                    o.WithClientCertificates(certs);
                    o.WithSslProtocols(System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13);
                    o.UseTls();
                });
            }

            var options = optionsBuilder.Build();

            _logger.LogInformation("MQTT client {ClientName} connecting to {Host}:{Port}...", Name, BrokerHost, BrokerPort);
            await mqttClient.ConnectAsync(options, cancellationToken);

            Status = ClientStatus.Running;

            while (!cancellationToken.IsCancellationRequested)
            {
                var payload = $"Simulator heartbeat from {Name} at {DateTimeOffset.UtcNow:O}";
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(Topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(false)
                    .Build();

                await mqttClient.PublishAsync(message, cancellationToken);
                Interlocked.Increment(ref _publishCount);
                _lastPublishedAt = DateTimeOffset.UtcNow;
                _logger.LogInformation("MQTT client {ClientName} published to {Topic} (#{Count}).", Name, Topic, _publishCount);

                await Task.Delay(TimeSpan.FromSeconds(PublishIntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Status = ClientStatus.Stopped;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            Status = ClientStatus.Faulted;
            _logger.LogError(ex, "MQTT client {ClientName} failed: {Message}", Name, ex.Message);
        }
        finally
        {
            if (mqttClient.IsConnected)
            {
                var disconnectOptions = new MqttClientDisconnectOptions
                {
                    Reason = MqttClientDisconnectOptionsReason.NormalDisconnection
                };
                await mqttClient.DisconnectAsync(disconnectOptions);
            }
        }
    }
}
