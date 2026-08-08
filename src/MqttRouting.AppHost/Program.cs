using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var baseDomain = builder.Configuration["BaseDomain"] ?? "example.com";

// TenantPlane A: internal broker on 11883 (localhost), external TCP listeners on 1886
builder.AddProject("tenantA", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18080, targetPort: 18080)
    .WithEndpoint(port: 1886, targetPort: 1886, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantA")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantA.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18080")
    .WithEnvironment("TenantPlane__InternalBrokerPort", "11883")
    .WithEnvironment("TenantPlane__TcpListenerPorts__0", "1886");

// TenantPlane B: internal broker on 11884 (localhost), external TCP listener on 1887
builder.AddProject("tenantB", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18081, targetPort: 18081)
    .WithEndpoint(port: 1887, targetPort: 1887, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantB")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantB.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18081")
    .WithEnvironment("TenantPlane__InternalBrokerPort", "11884")
    .WithEnvironment("TenantPlane__TcpListenerPorts__0", "1887");

// MqttGateway: publicly exposed. Accepts MQTT-over-TCP from devices,
// parses CONNECT, extracts tenant from client ID, bridges TCP → TenantPlane.
// Supports optional Proxy Protocol v2 from upstream L4 load balancers.
builder.AddProject("mqtt-gateway", @"..\MqttRouting.MqttGateway\MqttRouting.MqttGateway.csproj")
    .WithHttpEndpoint(port: 18200, targetPort: 18200)
    .WithEndpoint(port: 1883, targetPort: 1883, scheme: "tcp", name: "mqtt-tcp")
    .WithEndpoint(port: 8883, targetPort: 8883, scheme: "tcp", name: "mqtt-tls")
    .WithEnvironment("MqttGateway__BaseDomain", baseDomain)
    .WithEnvironment("MqttGateway__MqttTcpListenPort", "1883")
    .WithEnvironment("MqttGateway__MqttTlsListenPort", "8883")
    .WithEnvironment("MqttGateway__RouteTable__0__Tenant", "tenantA")
    .WithEnvironment("MqttGateway__RouteTable__0__Host", "localhost")
    .WithEnvironment("MqttGateway__RouteTable__0__Port", "1886")
    .WithEnvironment("MqttGateway__RouteTable__1__Tenant", "tenantB")
    .WithEnvironment("MqttGateway__RouteTable__1__Host", "localhost")
    .WithEnvironment("MqttGateway__RouteTable__1__Port", "1887");

// Ingress: HTTP-only entry point. Routes web traffic by Host header to TenantPlanes.
builder.AddProject("ingress", @"..\MqttRouting.Ingress\MqttRouting.Ingress.csproj")
    .WithHttpEndpoint(port: 18000, targetPort: 18000)
    .WithEnvironment("Ingress__BaseDomain", baseDomain)
    .WithEnvironment("Ingress__RouteTable__0__Tenant", "tenantA")
    .WithEnvironment("Ingress__RouteTable__0__Host", $"tenantA.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__0__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__0__BackendPort", "18080")
    .WithEnvironment("Ingress__RouteTable__1__Tenant", "tenantB")
    .WithEnvironment("Ingress__RouteTable__1__Host", $"tenantB.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__1__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__1__BackendPort", "18081");

// ClientSimulator: connects directly to MqttGateway via MQTT-over-TCP
builder.AddProject("client-simulator", @"..\MqttRouting.ClientSimulator\MqttRouting.ClientSimulator.csproj")
    .WithHttpEndpoint(port: 18110, targetPort: 18110)
    .WithEnvironment("ClientSimulator__BrokerHost", "localhost")
    .WithEnvironment("ClientSimulator__BrokerPort", "1883");

builder.Build().Run();
