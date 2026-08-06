using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var baseDomain = builder.Configuration["BaseDomain"] ?? "example.com";

builder.AddProject("tenant1", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18080, targetPort: 18080)
    .WithEnvironment("TenantPlane__TenantName", "tenant1")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenant1.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18080")
    .WithEnvironment("TenantPlane__BrokerPort", "1883");

builder.AddProject("tenant2", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithHttpEndpoint(port: 18081, targetPort: 18081)
    .WithEnvironment("TenantPlane__TenantName", "tenant2")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenant2.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18081")
    .WithEnvironment("TenantPlane__BrokerPort", "1884");

builder.AddProject("ingress", @"..\MqttRouting.Ingress\MqttRouting.Ingress.csproj")
    .WithHttpEndpoint(port: 18000, targetPort: 18000)
    .WithEnvironment("Ingress__BaseDomain", baseDomain)
    .WithEnvironment("Ingress__RouteTable__0__Tenant", "tenant1")
    .WithEnvironment("Ingress__RouteTable__0__Host", $"tenant1.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__0__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__0__BackendPort", "18080")
    .WithEnvironment("Ingress__RouteTable__1__Tenant", "tenant2")
    .WithEnvironment("Ingress__RouteTable__1__Host", $"tenant2.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__1__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__1__BackendPort", "18081");

builder.AddProject("protocol-transfer", @"..\MqttRouting.ProtocolTransfer\MqttRouting.ProtocolTransfer.csproj")
    .WithHttpEndpoint(port: 18200, targetPort: 18200)
    .WithEnvironment("ProtocolTransfer__ListenPort", "1883")
    .WithEnvironment("ProtocolTransfer__IngressHost", "localhost")
    .WithEnvironment("ProtocolTransfer__IngressPort", "18000")
    .WithEnvironment("ProtocolTransfer__BaseDomain", baseDomain);

builder.AddProject("client-simulator", @"..\MqttRouting.ClientSimulator\MqttRouting.ClientSimulator.csproj")
    .WithHttpEndpoint(port: 18110, targetPort: 18110);

builder.Build().Run();
