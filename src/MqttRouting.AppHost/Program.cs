using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
var baseDomain = builder.Configuration["BaseDomain"] ?? "example.com";

builder.AddProject("tenant1", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithEnvironment("TenantPlane__TenantName", "tenant1")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenant1.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18080")
    .WithEnvironment("TenantPlane__BrokerPort", "1883")
    .WithEnvironment("ASPNETCORE_URLS", "http://+:18080");

builder.AddProject("tenant2", @"..\MqttRouting.TenantPlane\MqttRouting.TenantPlane.csproj")
    .WithEnvironment("TenantPlane__TenantName", "tenant2")
    .WithEnvironment("TenantPlane__BaseDomain", baseDomain)
    .WithEnvironment("TenantPlane__CustomDomain", $"tenant2.{baseDomain}")
    .WithEnvironment("TenantPlane__HttpPort", "18081")
    .WithEnvironment("TenantPlane__BrokerPort", "1884")
    .WithEnvironment("ASPNETCORE_URLS", "http://+:18081");

builder.AddProject("ingress", @"..\MqttRouting.Ingress\MqttRouting.Ingress.csproj")
    .WithEnvironment("Ingress__BaseDomain", baseDomain)
    .WithEnvironment("Ingress__RouteTable__0__Host", $"tenant1.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__0__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__0__BackendPort", "18080")
    .WithEnvironment("Ingress__RouteTable__1__Host", $"tenant2.{baseDomain}")
    .WithEnvironment("Ingress__RouteTable__1__BackendHost", "localhost")
    .WithEnvironment("Ingress__RouteTable__1__BackendPort", "18081")
    .WithEnvironment("ASPNETCORE_URLS", "http://+:18000");

builder.AddProject("protocol-transfer", @"..\MqttRouting.ProtocolTransfer\MqttRouting.ProtocolTransfer.csproj")
    .WithEnvironment("ProtocolTransfer__Brokers__0__Name", "tenant1")
    .WithEnvironment("ProtocolTransfer__Brokers__0__Host", "localhost")
    .WithEnvironment("ProtocolTransfer__Brokers__0__Port", "18080")
    .WithEnvironment("ProtocolTransfer__Brokers__1__Name", "tenant2")
    .WithEnvironment("ProtocolTransfer__Brokers__1__Host", "localhost")
    .WithEnvironment("ProtocolTransfer__Brokers__1__Port", "18081");

builder.AddProject("client-simulator", @"..\MqttRouting.ClientSimulator\MqttRouting.ClientSimulator.csproj")
    .WithEnvironment("ASPNETCORE_URLS", "http://+:18110");

builder.Build().Run();
