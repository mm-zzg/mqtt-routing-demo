using Microsoft.Extensions.Options;
using MqttRouting.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddServiceDefaults();
builder.Services.AddHttpClient("proxy");
builder.Services.AddOptions<IngressOptions>()
    .Bind(builder.Configuration.GetSection("Ingress"))
    .PostConfigure(options =>
    {
        options.BaseDomain = string.IsNullOrWhiteSpace(options.BaseDomain) ? "example.com" : options.BaseDomain;
    });

var app = builder.Build();

app.MapDefaultEndpoints();

app.MapGet("/__routes", (IOptions<IngressOptions> options) => Results.Ok(options.Value));
app.MapFallback(ProxyAsync);

await app.RunAsync();

async Task ProxyAsync(HttpContext context, IHttpClientFactory httpClientFactory, IOptions<IngressOptions> options)
{
    var host = context.Request.Host.Host;
    var tenant = options.Value.RouteTable.FirstOrDefault(route =>
        string.Equals(route.Host, host, StringComparison.OrdinalIgnoreCase));

    if (tenant is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { error = "No route matched host", host });
        return;
    }

    var client = httpClientFactory.CreateClient("proxy");
    var targetUri = new Uri($"{tenant.BackendScheme}://{tenant.BackendHost}:{tenant.BackendPort}{context.Request.Path}{context.Request.QueryString}");
    using var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

    if (context.Request.ContentLength > 0 || context.Request.Body.CanRead)
    {
        requestMessage.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrEmpty(context.Request.ContentType))
        {
            requestMessage.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }
    }

    foreach (var header in context.Request.Headers)
    {
        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            requestMessage.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    using var response = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;

    foreach (var header in response.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    foreach (var header in response.Content.Headers)
    {
        context.Response.Headers[header.Key] = header.Value.ToArray();
    }

    context.Response.Headers.Remove("transfer-encoding");
    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
}

sealed record IngressOptions
{
    public string BaseDomain { get; set; } = "example.com";
    public List<RouteEntry> RouteTable { get; set; } = new();
}

sealed record RouteEntry
{
    public string Host { get; set; } = string.Empty;
    public string BackendScheme { get; set; } = "http";
    public string BackendHost { get; set; } = string.Empty;
    public int BackendPort { get; set; } = 8080;
}
