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
Device ──TLS (8883)──► MqttGateway ──TCP+PPv2──► TenantPlane
       ──plain TCP (1883)──┘

        Web traffic          Ingress (HTTP only) ──HTTP──► TenantPlane
```

### Data flow
1. **Device** (or ClientSimulator) connects to **MqttGateway** via:
   - **Port 1883** — plain MQTT over TCP (for Cloudflare-proxied or L4-LB fronted traffic, with optional Proxy Protocol v2 for IP preservation).
   - **Port 8883** — MQTT over TLS (for direct mTLS connections; gateway terminates TLS and forwards the client certificate to the TenantPlane).
2. **Ingress** handles HTTP web traffic only: it routes requests to TenantPlanes by Host header.
3. **MqttGateway** receives the MQTT connection, optionally parses a Proxy Protocol v2 header (for L4 load balancers). On the TLS port it performs a TLS handshake using the Cloudflare Origin CA certificate (or self-signed cert in local dev), extracts the client certificate if presented, reads the MQTT CONNECT packet, parses the client ID, and determines the target tenant from the client ID prefix (e.g. `tenantA.` → tenantA). It then opens a TCP connection to the matching **TenantPlane**, sends a Proxy Protocol v2 header with the original client IP and an optional client-certificate TLV, forwards the buffered CONNECT bytes, and establishes a bidirectional byte pump (TCP ↔ TCP).
4. **TenantPlane** runs an embedded MQTT broker that accepts both TCP and WebSocket MQTT connections (via loopback bridge). It parses the Proxy Protocol v2 header and client-certificate TLV to learn the original client address and identity.

### TLS and Client Certificates
- **Production**: MqttGateway loads the Cloudflare Origin CA certificate (PFX) from the `MqttGateway__TlsCertBase64` and `MqttGateway__TlsCertPassword` configuration values. Clients may present mTLS certificates for authentication.
- **Local dev**: If no certificate is configured, the gateway auto-generates a self-signed certificate with SANs for `localhost` and `127.0.0.1`.
- **Client cert forwarding**: When a client presents a certificate during the TLS handshake, the gateway encodes the certificate thumbprint (SHA-256), subject, and issuer into a Proxy Protocol v2 TLV extension and forwards it to the TenantPlane. The TenantPlane makes this information available to its MQTT broker for connection validation.

### Proxy Protocol v2
The entire forwarding chain uses Proxy Protocol v2 to preserve the original client IP:
- If the MqttGateway sits behind an L4 load balancer, it parses the optional PPv2 header on the plain TCP port.
- When forwarding to a TenantPlane, the MqttGateway sends a PPv2 header carrying the original device IP and, if available, the client certificate TLV (type 0xE0).
- The TenantPlane parses the PPv2 header and TLV to learn both the true client address and the client certificate identity.

### Port assignments (local dev)
| Service           | Plain TCP | TLS    | HTTP   |
|-------------------|-----------|--------|--------|
| MqttGateway       | 1883      | 8883   | 18200  |
| TenantPlane (tA)  | 1886      | —      | 18080  |
| TenantPlane (tB)  | 1887      | —      | 18081  |
| Ingress (HTTP)    | —         | —      | 18000  |
| ClientSimulator   | —         | —      | 18110  |

### Default clients
The ClientSimulator automatically creates and starts two MQTT clients on startup:
- **TenantA Simulator** — client ID `tenantA.simulator`, publishes to `tenantA/simulator/heartbeat`, connects via MQTT-over-TCP to MqttGateway at localhost:1883
- **TenantB Simulator** — client ID `tenantB.simulator`, publishes to `tenantB/simulator/heartbeat`, connects via MQTT-over-TCP to MqttGateway at localhost:1883

