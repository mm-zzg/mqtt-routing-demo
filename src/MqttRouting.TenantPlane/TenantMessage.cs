namespace MqttRouting.TenantPlane;

internal sealed record TenantMessage(string Id, string Topic, string Payload, DateTimeOffset CreatedAt);
