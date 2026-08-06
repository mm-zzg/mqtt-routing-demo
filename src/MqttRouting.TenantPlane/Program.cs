using Microsoft.Extensions.Options;
using MQTTnet.Server;
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
        options.BrokerPort = options.BrokerPort <= 0 ? 1883 : options.BrokerPort;
    });
builder.Services.AddSingleton<TenantMessageStore>();
builder.Services.AddSingleton<MqttBrokerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MqttBrokerService>());

var app = builder.Build();

app.UseWebSockets();
app.MapDefaultEndpoints();

// WebSocket MQTT endpoint — accepts raw MQTT-over-WebSocket connections and bridges to the local broker
app.Map("/mqtt", async (HttpContext ctx, MqttBrokerService broker) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync("mqtt");
    await broker.BridgeWebSocketAsync(ws, ctx.RequestAborted);
});

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
    mode = "embedded"
}));

await app.RunAsync();
