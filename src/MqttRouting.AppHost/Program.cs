using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var baseDomain = builder.Configuration["BaseDomain"] ?? "example.com";

builder.AddProject("tenantA", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18080, targetPort: 18080)
    .WithEndpoint(port: 1883, targetPort: 1883, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantA")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantA.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18080")
    .WithEnvironment("TenantPlane__BrokerPort", "1883");

builder.AddProject("tenantB", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18081, targetPort: 18081)
    .WithEndpoint(port: 1884, targetPort: 1884, scheme: "tcp", name: "mqtt")
    .WithEnvironment("TenantPlane__TenantName", "tenantB")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenantB.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18081")
    .WithEnvironment("TenantPlane__BrokerPort", "1884");

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

builder.AddProject("protocol-transfer", @"..\MqttRouting.ProtocolTransfer\MqttRouting.ProtocolTransfer.csproj")
    .WithHttpEndpoint(port: 18200, targetPort: 18200)
    .WithEndpoint(port: 1883, targetPort: 1883, scheme: "tcp", name: "mqtt")
    .WithEnvironment("ProtocolTransfer__ListenPort", "1883")
    .WithEnvironment("ProtocolTransfer__IngressHost", "localhost")
    .WithEnvironment("ProtocolTransfer__IngressPort", "18000")
    .WithEnvironment("ProtocolTransfer__BaseDomain", baseDomain);

builder.AddProject("client-simulator", @"..\MqttRouting.ClientSimulator\MqttRouting.ClientSimulator.csproj")
    .WithHttpEndpoint(port: 18110, targetPort: 18110)
    .WithEnvironment("ClientSimulator__BrokerHost", "localhost")
    .WithEnvironment("ClientSimulator__BrokerPort", "1883");

builder.Build().Run();