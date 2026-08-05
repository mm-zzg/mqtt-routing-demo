namespace MqttRouting.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    public static IServiceCollection AddServiceDefaults(this IServiceCollection services)
    {
        services.AddHealthChecks();
        return services;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/alive", () => Results.Ok(new { status = "alive" }));
        return app;
    }
}
