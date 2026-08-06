# Azure MQTT Aspire plan

## Goal
Create a practical proof-of-concept for the diagram using .NET Aspire, Azure Container Apps, Terraform, and GitHub Actions.

## Milestones
1. Scaffold the Aspire solution and shared service defaults.
2. Implement the tenant services, optional local ingress, and MQTT gateway worker.
3. Add Terraform for Azure resources, DNS, and deployment inputs using native Container Apps ingress.
4. Add Terraform-native certificate bundling with the `nijave/pki` provider for Cloudflare origin certs and Azure Container Apps.
5. Add GitHub Actions for build and deploy automation.
6. Validate the solution builds and the deployment files are internally consistent.

## Current status
- Core solution scaffolded
- Terraform deployment path added
- Cloudflare origin certificate flow wired
- Public edge TLS delegated to Cloudflare when proxied
- Client simulator UI project added, upgraded with real MQTT transport (MQTTnet) and SQLite persistence (EF Core) with TLS certificate support; UI framework is Blazor (Server-side rendering)
- AppHost launch settings added for local startup with dashboard telemetry disabled
- Pre-build target added to AppHost .csproj to kill locked executable before rebuild
- Cloudflare DNS zone management is fully declarative: zone auto-discovered via data source, DNS records, and SSL/security settings managed in Terraform

## Next implementation steps
1. Validate end-to-end deployment with real Cloudflare + Azure infrastructure.
2. Add monitoring and observability (Application Insights, structured logging).

## MQTT Routing Architecture

### Topology
```
Device ──MQTT over TCP──► Ingress ──MQTT over TCP──► MqttGateway ──MQTT over WebSocket──► TenantPlane
```

### Data flow
1. **Device** (or ClientSimulator) connects to **Ingress** via plain MQTT over TCP.
2. **Ingress** acts as the public entry point: it listens on both TCP (for MQTT) and HTTP (for web traffic). MQTT-over-TCP connections are transparently proxied via raw TCP forwarding to **MqttGateway**. HTTP requests are routed to TenantPlanes by Host header.
3. **MqttGateway** receives the MQTT-over-TCP connection from Ingress, reads the initial MQTT CONNECT packet, parses the client ID, and determines the target tenant from the client ID prefix (e.g. `tenantA.` → tenantA). It then opens a **WebSocket** connection to the matching **TenantPlane**'s `/mqtt` endpoint, forwards the buffered CONNECT bytes, and establishes a bidirectional byte pump (TCP ↔ WebSocket).
4. **TenantPlane** runs an embedded MQTT broker that accepts both TCP and WebSocket MQTT connections (via loopback bridge). It stores published messages in its `TenantMessageStore`.

### Routing rule
- MQTT connections whose client ID starts with `tenantA.` are routed to the TenantPlane instance for tenantA.
- MQTT connections whose client ID starts with `tenantB.` are routed to the TenantPlane instance for tenantB.
- Connections with an unrecognized prefix are rejected with CONNACK identifier rejected.
- MqttGateway uses a route table (tenant name → backend host:port) to connect to the correct TenantPlane via WebSocket.

### Port assignments (local dev)
| Service           | TCP MQTT | HTTP   |
|-------------------|----------|--------|
| Ingress           | 1883     | 18000  |
| MqttGateway       | 1885     | 18200  |
| TenantPlane (tA)  | 1883     | 18080  |
| TenantPlane (tB)  | 1884     | 18081  |
| ClientSimulator   | —        | 18110  |

### Default clients
The ClientSimulator automatically creates and starts two MQTT clients on startup:
- **TenantA Simulator** — client ID `tenantA.simulator`, publishes to `tenantA/simulator/heartbeat`, connects via MQTT-over-TCP to Ingress at localhost:1883
- **TenantB Simulator** — client ID `tenantB.simulator`, publishes to `tenantB/simulator/heartbeat`, connects via MQTT-over-TCP to Ingress at localhost:1883

