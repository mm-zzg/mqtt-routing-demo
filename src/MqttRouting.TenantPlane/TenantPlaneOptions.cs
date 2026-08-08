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

    // ── TLS listener (for SNI-passthrough connections from MqttGateway) ──

    /// <summary>
    /// External TLS port that accepts MQTT-over-TLS connections forwarded
    /// by the MqttGateway via SNI passthrough. When set, a TLS listener is
    /// started that terminates TLS and bridges to the internal broker.
    /// Client certificates are extracted from the TLS handshake.
    /// </summary>
    public int? TlsListenerPort { get; set; }

    /// <summary>Base64-encoded PFX certificate for the TLS listener.</summary>
    public string? TlsCertBase64 { get; set; }

    /// <summary>File-system path to a PFX certificate for the TLS listener.</summary>
    public string? TlsCertPath { get; set; }

    /// <summary>Password for the PFX certificate.</summary>
    public string? TlsCertPassword { get; set; }

    public bool HasTlsCert() =>
        !string.IsNullOrWhiteSpace(TlsCertBase64)
        || (!string.IsNullOrWhiteSpace(TlsCertPath) && File.Exists(TlsCertPath));
}
