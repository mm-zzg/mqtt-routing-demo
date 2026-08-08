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
        if (options.TcpListenerPorts.Count == 0)
        {
            options.TcpListenerPorts = [1883, 1884];
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
    ipcEndpoint = options.Value.IpcEndpointPath ?? "(disabled)",
    mode = "embedded"
}));

await app.RunAsync();
