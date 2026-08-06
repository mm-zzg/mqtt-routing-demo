# Taskboard

## Done
- Scaffolding the Aspire solution
- Implementing the runtime services
- Adding Azure Terraform infrastructure
- Adding GitHub Actions deployment
- Added client simulator UI project for simulated MQTT client lifecycle and certificate inventory management
- Added AppHost launch settings for local startup
- Reviewed current implementation gaps and launch behavior
- Implemented Cloudflare DNS zone data source, zone settings (SSL mode, always HTTPS, min TLS, HTTP3), and auto-discovery of zone ID from domain name
- Upgraded client simulator to real MQTT transport via MQTTnet and SQLite persistence via EF Core
- Added pre-build target to kill running AppHost executable before rebuild/clean
- Converted Client Simulator UI from Razor Pages to Blazor (Server-side rendering) with interactive components for certificate management, client config CRUD, and real-time client status
- Fixed Aspire endpoint configuration in AppHost by replacing `ASPNETCORE_URLS=http://+:port` with explicit `WithHttpEndpoint(port, targetPort)` calls
- Fixed ingress routing by moving self-info endpoint from `/` to `/info` so root requests fall through to `MapFallback` proxy
- Ensured AppHost local startup free of proxy dependency: fixed `HTTP_PROXY` env var (had trailing space), added missing `ClientSimulator` project reference to AppHost .csproj
- Enabled Aspire dashboard OTLP/gRPC telemetry endpoints in AppHost launchSettings
- Added WebSocket proxy support to ingress: detects `IsWebSocketRequest`, accepts client upgrade, connects `ClientWebSocket` to backend, bidirectional pump with graceful shutdown
- Added embedded MQTT broker (MQTTnet.Server) to TenantPlane on BrokerPort with `InterceptingPublishAsync` capturing messages to TenantMessageStore
- Upgraded ProtocolTransfer from health-check stub to real MQTT bridge: subscribes to `#` on each tenant broker, forwards messages to other brokers with `x-bridge-source` user property to prevent loops
- Updated AppHost to point ProtocolTransfer at MQTT broker ports (1883/1884) instead of HTTP ports
- Implemented new MQTT routing topology: ClientSimulator →(TCP MQTT)→ ProtocolTransfer →(WS MQTT)→ Ingress →(WS MQTT)→ TenantPlane
- TenantPlane: added WebSocket MQTT endpoint at `/mqtt` via loopback TCP bridge to embedded broker
- ProtocolTransfer: rewritten as MQTT TCP gateway — accepts TCP MQTT on port 1883, parses CONNECT to extract client ID, routes to Ingress `ws://ingress:18000/mqtt/{tenant}` based on client ID prefix (`tenant1.` / `tenant2.`)
- Ingress: added `/mqtt/{tenant}` WebSocket route that proxies to TenantPlane's `/mqtt` endpoint; route table now includes `Tenant` field
- Updated AppHost configuration: ProtocolTransfer ListenPort=1883, IngressHost/IngressPort point to Ingress:18000

## Next

## Notes
- Public edge TLS is handled by Cloudflare automatically when proxied
- Azure uses a Cloudflare Origin CA certificate bundled to PFX in Terraform
- Cloudflare zone is now auto-discovered via data source; `cloudflare_zone_id` variable is optional
- SSL mode defaults to `strict` (requires valid origin certificate); zone settings are managed declaratively
- Client simulator now uses MQTTnet for real MQTT CONNECT/PUBLISH with TLS certificate support
- Client configs and certificates are persisted in SQLite across restarts
- AppHost .csproj includes `KillRunningAppHost` target that runs before Build/Clean/Rebuild to prevent file locks
