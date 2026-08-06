namespace MqttRouting.TenantPlane;

internal sealed record TenantPlaneOptions
{
    public string TenantName { get; set; } = "tenant";
    public string BaseDomain { get; set; } = "example.com";
    public string? CustomDomain { get; set; }
    public int HttpPort { get; set; } = 8080;
    public int BrokerPort { get; set; } = 1883;
}
