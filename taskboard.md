# Taskboard

## Done
- Scaffolding the Aspire solution
- Implementing the runtime services
- Adding Azure Terraform infrastructure
- Adding GitHub Actions deployment

## Next
- Decide whether to add Cloudflare DNS automation in Terraform
- Decide whether to replace the placeholder HTTP-based runtime with a real MQTT broker

## Notes
- Public edge TLS is handled by Cloudflare automatically when proxied
- Azure uses a Cloudflare Origin CA certificate bundled to PFX in Terraform
