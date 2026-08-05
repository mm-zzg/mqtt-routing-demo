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
- Client simulator UI project added, upgraded with real MQTT transport (MQTTnet) and SQLite persistence (EF Core) with TLS certificate support
- AppHost launch settings added for local startup with dashboard telemetry disabled
- Pre-build target added to AppHost .csproj to kill locked executable before rebuild
- Cloudflare DNS zone management is fully declarative: zone auto-discovered via data source, DNS records, and SSL/security settings managed in Terraform

## Next implementation steps
1. Validate end-to-end deployment with real Cloudflare + Azure infrastructure.
2. Add monitoring and observability (Application Insights, structured logging).
