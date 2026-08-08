using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var baseDomain = builder.Configuration["BaseDomain"] ?? "example.com";

// TenantPlane A: internal broker on 11883 (localhost), external TCP listeners on 1886
builder.AddProject("tenantA", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(targetPort: 18080)
    .WithEndpoint(targetPort: 1886, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantA")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantA.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18080")
    .WithEnvironment("TenantPlane__InternalBrokerPort", "11883")
    .WithEnvironment("TenantPlane__TcpListenerPorts__0", "1886");

// TenantPlane B: internal broker on 11884 (localhost), external TCP listener on 1887
builder.AddProject("tenantB", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(targetPort: 18081)
    .WithEndpoint(targetPort: 1887, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantB")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantB.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18081")
    .WithEnvironment("TenantPlane__InternalBrokerPort", "11884")
    .WithEnvironment("TenantPlane__TcpListenerPorts__0", "1887");

// MqttGateway: MQTT-over-TLS only. Parses CONNECT, extracts tenant from
// client ID, bridges TLS → TenantPlane with PPv2 client cert forwarding.
builder.AddProject("mqtt-gateway", @"..\MqttRouting.MqttGateway\MqttRouting.MqttGateway.csproj")
    .WithHttpEndpoint(targetPort: 18200)
    .WithEndpoint(targetPort: 8883, scheme: "tcp", name: "mqtt-tls")
    .WithEnvironment("MqttGateway__BaseDomain", baseDomain)
    .WithEnvironment("MqttGateway__MqttTlsListenPort", "8883")
    .WithEnvironment("MqttGateway__RouteTable__0__Tenant", "tenantA")
    .WithEnvironment("MqttGateway__RouteTable__0__Host", "localhost")
    .WithEnvironment("MqttGateway__RouteTable__0__Port", "1886")
    .WithEnvironment("MqttGateway__RouteTable__1__Tenant", "tenantB")
    .WithEnvironment("MqttGateway__RouteTable__1__Host", "localhost")
    .WithEnvironment("MqttGateway__RouteTable__1__Port", "1887");

// Ingress: HTTP-only entry point. Routes web traffic by Host header to TenantPlanes.
builder.AddProject("ingress", @"..\MqttRouting.Ingress\MqttRouting.Ingress.csproj")
    .WithHttpEndpoint(targetPort: 18000)
    .WithEnvironment("Ingress__BaseDomain", baseDomain)
    .WithEnvironment("Ingress__RouteTable__0__Tenant", "tenantA")
    .WithEnvironment("Ingress__RouteTable__0__Host", $"tenantA.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__0__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__0__BackendPort", "18080")
    .WithEnvironment("Ingress__RouteTable__1__Tenant", "tenantB")
    .WithEnvironment("Ingress__RouteTable__1__Host", $"tenantB.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__1__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__1__BackendPort", "18081");

// ClientSimulator: connects to MqttGateway via MQTT-over-TLS
builder.AddProject("client-simulator", @"..\MqttRouting.ClientSimulator\MqttRouting.ClientSimulator.csproj")
    .WithHttpEndpoint(targetPort: 18110)
    .WithEnvironment("ClientSimulator__BrokerHost", "localhost")
    .WithEnvironment("ClientSimulator__BrokerPort", "8883");

builder.Build().Run();
