using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddOptions<ProtocolTransferOptions>()
    .Bind(builder.Configuration.GetSection("ProtocolTransfer"))
    .PostConfigure(options =>
    {
        options.Brokers ??= new List<BrokerConnection>();
    });
builder.Services.AddHostedService<MqttTransferWorker>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", (IOptions<ProtocolTransferOptions> options) => Results.Ok(options.Value));
await app.RunAsync();

sealed record ProtocolTransferOptions
{
    public List<BrokerConnection>? Brokers { get; set; }
}

sealed record BrokerConnection
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1883;
    public string ClientIdPrefix { get; set; } = "bridge";
}

sealed class MqttTransferWorker : BackgroundService
{
    private readonly ILogger<MqttTransferWorker> _logger;
    private readonly IOptions<ProtocolTransferOptions> _options;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSeen = new();

    public MqttTransferWorker(ILogger<MqttTransferWorker> logger, IOptions<ProtocolTransferOptions> options)
    {
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var brokers = _options.Value.Brokers ?? [];
        using var httpClient = new HttpClient();

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var broker in brokers)
            {
                var url = $"http://{broker.Host}:{broker.Port}/health";
                try
                {
                    using var response = await httpClient.GetAsync(url, stoppingToken);
                    _lastSeen[broker.Name] = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Checked {Broker} at {Url}: {Status}", broker.Name, url, response.StatusCode);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Unable to reach broker endpoint for {Broker}", broker.Name);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}
