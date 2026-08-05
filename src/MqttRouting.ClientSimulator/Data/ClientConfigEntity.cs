namespace MqttRouting.ClientSimulator.Data;

public sealed class ClientConfigEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string BrokerHost { get; set; } = "localhost";
    public int BrokerPort { get; set; } = 1883;
    public string Topic { get; set; } = "simulator/heartbeat";
    public int PublishIntervalSeconds { get; set; } = 10;
    public string? CertificateId { get; set; }
    public CertificateEntity? Certificate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
