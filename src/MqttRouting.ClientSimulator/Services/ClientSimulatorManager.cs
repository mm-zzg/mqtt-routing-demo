using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace MqttRouting.ClientSimulator.Services;

public sealed class ClientSimulatorManager
{
    private readonly ConcurrentDictionary<string, ClientCertificateRecord> _certificates = new();
    private readonly ConcurrentDictionary<string, ClientRuntime> _clients = new();
    private readonly ILogger<ClientSimulatorManager> _logger;

    public ClientSimulatorManager(ILogger<ClientSimulatorManager> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<ClientCertificateRecord> GetCertificates() =>
        _certificates.Values.OrderBy(c => c.Name).ToList();

    public IReadOnlyList<ClientRuntimeSnapshot> GetClients() =>
        _clients.Values.Select(c => c.ToSnapshot()).OrderBy(c => c.Name).ToList();

    public string AddCertificate(CertificateInput input)
    {
        _ = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(input.PfxBase64), input.Password);
        var id = Guid.NewGuid().ToString("N");
        var certificate = new ClientCertificateRecord(id, input.Name.Trim(), input.PfxBase64.Trim(), input.Password, DateTimeOffset.UtcNow);
        if (!_certificates.TryAdd(id, certificate))
        {
            throw new InvalidOperationException("Unable to add certificate.");
        }

        return id;
    }

    public string AddClient(ClientInput input)
    {
        if (!string.IsNullOrEmpty(input.CertificateId) && !_certificates.ContainsKey(input.CertificateId))
        {
            throw new InvalidOperationException("Selected certificate was not found.");
        }

        var id = Guid.NewGuid().ToString("N");
        var runtime = new ClientRuntime(
            id,
            input.Name.Trim(),
            input.BrokerHost.Trim(),
            input.BrokerPort,
            input.Topic.Trim(),
            input.PublishIntervalSeconds,
            input.CertificateId,
            _logger);

        if (!_clients.TryAdd(id, runtime))
        {
            throw new InvalidOperationException("Unable to add client.");
        }

        return id;
    }

    public Task StartClientAsync(string clientId, CancellationToken cancellationToken)
    {
        if (!_clients.TryGetValue(clientId, out var runtime))
        {
            throw new InvalidOperationException("Client was not found.");
        }

        return runtime.StartAsync(cancellationToken);
    }

    public Task StopClientAsync(string clientId)
    {
        if (!_clients.TryGetValue(clientId, out var runtime))
        {
            throw new InvalidOperationException("Client was not found.");
        }

        return runtime.StopAsync();
    }

    public async Task RemoveClientAsync(string clientId)
    {
        if (!_clients.TryRemove(clientId, out var runtime))
        {
            throw new InvalidOperationException("Client was not found.");
        }

        await runtime.StopAsync();
    }

    public async Task StopAllAsync()
    {
        foreach (var clientId in _clients.Keys)
        {
            if (_clients.TryGetValue(clientId, out var runtime))
            {
                await runtime.StopAsync();
            }
        }
    }
}

public sealed class ClientSimulatorHostedService : IHostedService
{
    private readonly ClientSimulatorManager _manager;

    public ClientSimulatorHostedService(ClientSimulatorManager manager)
    {
        _manager = manager;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => _manager.StopAllAsync();
}

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

internal sealed class ClientRuntime
{
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
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
        ILogger logger)
    {
        Id = id;
        Name = name;
        BrokerHost = brokerHost;
        BrokerPort = brokerPort;
        Topic = topic;
        PublishIntervalSeconds = publishIntervalSeconds;
        CertificateId = certificateId;
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
        new(
            Id,
            Name,
            BrokerHost,
            BrokerPort,
            Topic,
            PublishIntervalSeconds,
            CertificateId,
            Status,
            _publishCount,
            _lastPublishedAt,
            _lastError);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Status is ClientStatus.Starting or ClientStatus.Running)
            {
                return;
            }

            Status = ClientStatus.Starting;
            _lastError = null;
            _loopCts = new CancellationTokenSource();
            _loopTask = RunLoopAsync(_loopCts.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        Task? runningTask;

        await _gate.WaitAsync();
        try
        {
            if (_loopCts is null)
            {
                Status = ClientStatus.Stopped;
                return;
            }

            _loopCts.Cancel();
            runningTask = _loopTask;
        }
        finally
        {
            _gate.Release();
        }

        if (runningTask is not null)
        {
            await runningTask;
        }

        await _gate.WaitAsync();
        try
        {
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;
            Status = ClientStatus.Stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        Status = ClientStatus.Running;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await SimulatePublishAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(PublishIntervalSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Status = ClientStatus.Stopped;
        }
        catch (SocketException ex)
        {
            _lastError = ex.Message;
            Status = ClientStatus.Faulted;
            _logger.LogError(ex, "Simulator client {ClientName} failed with socket error.", Name);
        }
        catch (IOException ex)
        {
            _lastError = ex.Message;
            Status = ClientStatus.Faulted;
            _logger.LogError(ex, "Simulator client {ClientName} failed while simulating publish.", Name);
        }
    }

    private async Task SimulatePublishAsync(CancellationToken cancellationToken)
    {
        using var tcpClient = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
        await tcpClient.ConnectAsync(BrokerHost, BrokerPort, timeoutCts.Token);
        Interlocked.Increment(ref _publishCount);
        _lastPublishedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("Simulator client {ClientName} reached {Host}:{Port} for topic {Topic}.", Name, BrokerHost, BrokerPort, Topic);
    }
}
