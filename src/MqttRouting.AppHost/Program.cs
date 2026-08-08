using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var baseDomain = builder.Configuration["BaseDomain"] ?? "example.com";

// TenantPlane A: internal broker on 11883 (localhost), external TCP listeners on 1883
builder.AddProject("tenantA", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18080, targetPort: 18080)
    .WithEndpoint(port: 1883, targetPort: 1883, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantA")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantA.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18080")
    .WithEnvironment("TenantPlane__InternalBrokerPort", "11883")
    .WithEnvironment("TenantPlane__TcpListenerPorts__0", "1883");

// TenantPlane B: internal broker on 11884 (localhost), external TCP listener on 1884
builder.AddProject("tenantB", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18081, targetPort: 18081)
    .WithEndpoint(port: 1884, targetPort: 1884, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantB")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantB.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18081")
    .WithEnvironment("TenantPlane__InternalBrokerPort", "11884")
    .WithEnvironment("TenantPlane__TcpListenerPorts__0", "1884");

// MqttGateway: receives MQTT-over-TCP from Ingress, parses CONNECT,
// extracts tenant from client ID, bridges TCP → TCP to TenantPlane.
builder.AddProject("mqtt-gateway", @"..\MqttRouting.MqttGateway\MqttRouting.MqttGateway.csproj")
    .WithHttpEndpoint(port: 18200, targetPort: 18200)
    .WithEndpoint(port: 1885, targetPort: 1885, scheme: "tcp", name: "mqtt-tcp")
    .WithEnvironment("MqttGateway__BaseDomain", baseDomain)
    .WithEnvironment("MqttGateway__MqttTcpListenPort", "1885")
    .WithEnvironment("MqttGateway__RouteTable__0__Tenant", "tenantA")
    .WithEnvironment("MqttGateway__RouteTable__0__Host", "localhost")
    .WithEnvironment("MqttGateway__RouteTable__0__Port", "1883")
    .WithEnvironment("MqttGateway__RouteTable__1__Tenant", "tenantB")
    .WithEnvironment("MqttGateway__RouteTable__1__Host", "localhost")
    .WithEnvironment("MqttGateway__RouteTable__1__Port", "1884");

// Ingress: public-facing entry point. HTTP for web traffic, TCP for MQTT.
// Devices connect with MQTT-over-TCP; Ingress proxies raw TCP to MqttGateway.
builder.AddProject("ingress", @"..\MqttRouting.Ingress\MqttRouting.Ingress.csproj")
    .WithHttpEndpoint(port: 18000, targetPort: 18000)
    .WithEndpoint(port: 1883, targetPort: 1883, scheme: "tcp", name: "mqtt-tcp")
    .WithEnvironment("Ingress__BaseDomain", baseDomain)
    .WithEnvironment("Ingress__MqttTcpListenPort", "1883")
    .WithEnvironment("Ingress__MqttGatewayHost", "localhost")
    .WithEnvironment("Ingress__MqttGatewayPort", "1885")
    .WithEnvironment("Ingress__RouteTable__0__Tenant", "tenantA")
    .WithEnvironment("Ingress__RouteTable__0__Host", $"tenantA.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__0__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__0__BackendPort", "18080")
    .WithEnvironment("Ingress__RouteTable__1__Tenant", "tenantB")
    .WithEnvironment("Ingress__RouteTable__1__Host", $"tenantB.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__1__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__1__BackendPort", "18081");

// ClientSimulator: connects to Ingress via MQTT-over-TCP
builder.AddProject("client-simulator", @"..\MqttRouting.ClientSimulator\MqttRouting.ClientSimulator.csproj")
    .WithHttpEndpoint(port: 18110, targetPort: 18110)
    .WithEnvironment("ClientSimulator__BrokerHost", "localhost")
    .WithEnvironment("ClientSimulator__BrokerPort", "1883");

builder.Build().Run();
