# Taskboard

## Done
- Scaffolding the Aspire solution
- Implementing the runtime services
- Adding Azure Terraform infrastructure
- Adding GitHub Actions deployment
- Added client simulator UI project for simulated MQTT client lifecycle and certificate inventory management
- Added AppHost launch settings for local startup
- Reviewed current implementation gaps and launch behavior

## Next
- Add Cloudflare DNS automation in Terraform
- Upgrade the client simulator to real MQTT transport and persistence
- Close the running AppHost process before rebuilds so the output executable is not locked
- Keep AppHost local startup free of the dashboard HTTP/proxy dependency

## Notes
- Public edge TLS is handled by Cloudflare automatically when proxied
- Azure uses a Cloudflare Origin CA certificate bundled to PFX in Terraform
