using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddOptions<TenantPlaneOptions>()
    .Bind(builder.Configuration.GetSection("TenantPlane"))
    .PostConfigure(options =>
    {
        options.TenantName = string.IsNullOrWhiteSpace(options.TenantName) ? "tenant" : options.TenantName;
        options.HttpPort = options.HttpPort <= 0 ? 8080 : options.HttpPort;
        options.BrokerPort = options.BrokerPort <= 0 ? 1883 : options.BrokerPort;
    });
builder.Services.AddSingleton<TenantMessageStore>();

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
        tenant.BrokerPort,
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
    port = options.Value.BrokerPort,
    mode = "simulated"
}));

await app.RunAsync();

sealed record TenantPlaneOptions
{
    public string TenantName { get; set; } = "tenant";
    public string BaseDomain { get; set; } = "example.com";
    public string? CustomDomain { get; set; }
    public int HttpPort { get; set; } = 8080;
    public int BrokerPort { get; set; } = 1883;
}

sealed record TenantMessage(string Id, string Topic, string Payload, DateTimeOffset CreatedAt);

sealed class TenantMessageStore
{
    private readonly ConcurrentQueue<TenantMessage> _messages = new();

    public void Add(TenantMessage message) => _messages.Enqueue(message);

    public IReadOnlyCollection<TenantMessage> All() => _messages.ToArray();
}
