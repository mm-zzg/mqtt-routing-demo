namespace MqttRouting.TenantPlane;

internal sealed record TenantPlaneOptions
{
    public string TenantName { get; set; } = "tenant";
    public string BaseDomain { get; set; } = "example.com";
    public string? CustomDomain { get; set; }
    public int HttpPort { get; set; } = 8080;

    /// <summary>
    /// Internal MQTTnet broker port (localhost-only, not exposed externally).
    /// External TCP listeners forward connections to this port.
    /// </summary>
    public int InternalBrokerPort { get; set; } = 11883;

    /// <summary>
    /// External TCP ports that accept MQTT connections and forward them
    /// to the internal broker. Must be configured explicitly — no default.
    /// </summary>
    public List<int> TcpListenerPorts { get; set; } = [];

    /// <summary>
    /// File-system path for a Unix domain socket / named pipe IPC endpoint.
    /// When set, an IPC listener is started alongside the TCP listeners.
    /// </summary>
    public string? IpcEndpointPath { get; set; }
}
