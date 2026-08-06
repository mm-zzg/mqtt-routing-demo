# Azure MQTT Aspire plan

## Goal
Create a practical proof-of-concept for the diagram using .NET Aspire, Azure Container Apps, Terraform, and GitHub Actions.

## Milestones
1. Scaffold the Aspire solution and shared service defaults.
2. Implement the tenant services, optional local ingress, and protocol transfer worker.
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
ClientSimulator ──MQTT over TCP──► ProtocolTransfer ──MQTT over WebSocket──► Ingress ──MQTT over WebSocket──► TenantPlane
```

### Data flow
1. **ClientSimulator** connects to **ProtocolTransfer** using plain MQTT over TCP.
2. **ProtocolTransfer** acts as an MQTT gateway: it accepts TCP MQTT connections from clients, parses the MQTT CONNECT packet to extract the client ID, and determines the target tenant from the client ID prefix (e.g. `tenant1.` → tenant1). It then connects to **Ingress** via MQTT over WebSocket, setting the `Host` header to `{tenant}.{baseDomain}` (e.g. `tenant1.example.com`) so Ingress can route by domain name.
3. **Ingress** receives MQTT over WebSocket connections at `/mqtt` and routes them by the Host header (same domain-based routing as HTTP requests). It proxies the WebSocket connection to the matching **TenantPlane**'s `/mqtt` endpoint.
4. **TenantPlane** runs an embedded MQTT broker that accepts both TCP and WebSocket MQTT connections (via loopback bridge). It stores published messages in its `TenantMessageStore`.

### Routing rule
- MQTT connections whose client ID starts with `tenant1.` are routed to the TenantPlane instance for tenant1.
- MQTT connections whose client ID starts with `tenant2.` are routed to the TenantPlane instance for tenant2.
- Connections with an unrecognized prefix are rejected.
- ProtocolTransfer connects to Ingress using the tenant's domain name as the Host header (e.g. `tenant1.example.com`), so Ingress routes the WebSocket connection to the correct TenantPlane using the same domain-based routing as HTTP requests.

### Port assignments (local dev)
| Service           | TCP MQTT | WebSocket MQTT | HTTP |
|-------------------|----------|----------------|------|
| ClientSimulator   | —        | —              | 18110 |
| ProtocolTransfer  | 1883     | —              | 18200 |
| Ingress           | —        | 18000 (ws)     | 18000 |
| TenantPlane (t1)  | 1883     | 18080 (ws)     | 18080 |
| TenantPlane (t2)  | 1884     | 18081 (ws)     | 18081 |

