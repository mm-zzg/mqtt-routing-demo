# Taskboard

## Done
- Scaffolding the Aspire solution
- Implementing the runtime services
- Adding Azure Terraform infrastructure
- Adding GitHub Actions deployment
- Added client simulator UI project for simulated MQTT client lifecycle and certificate inventory management
- Added AppHost launch settings for local startup

## Next
- Decide whether to add Cloudflare DNS automation in Terraform
- Decide whether to replace the placeholder HTTP-based runtime and simulated client transport with real MQTT protocol operations

## Notes
- Public edge TLS is handled by Cloudflare automatically when proxied
- Azure uses a Cloudflare Origin CA certificate bundled to PFX in Terraform
