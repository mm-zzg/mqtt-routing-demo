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

## Next
- Keep AppHost local startup free of the dashboard HTTP/proxy dependency (already configured via empty env vars in launchSettings)

## Notes
- Public edge TLS is handled by Cloudflare automatically when proxied
- Azure uses a Cloudflare Origin CA certificate bundled to PFX in Terraform
- Cloudflare zone is now auto-discovered via data source; `cloudflare_zone_id` variable is optional
- SSL mode defaults to `strict` (requires valid origin certificate); zone settings are managed declaratively
- Client simulator now uses MQTTnet for real MQTT CONNECT/PUBLISH with TLS certificate support
- Client configs and certificates are persisted in SQLite across restarts
- AppHost .csproj includes `KillRunningAppHost` target that runs before Build/Clean/Rebuild to prevent file locks
