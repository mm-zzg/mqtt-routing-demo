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
- Client simulator UI project added for managing simulated clients and certificate inventory
- AppHost launch settings added for local startup and dashboard discovery
- AppHost local launch is configured to avoid the dashboard HTTP/proxy dependency during startup
- AppHost requires the generated executable to be closed before rebuild/start because the output is locked while the process is running
- Client simulator is still in-memory and needs to be upgraded to real MQTT transport and persistence

## Next implementation steps
1. Add Cloudflare DNS zone and record management to Terraform so public hostnames, validation, and proxying are fully declarative.
2. Upgrade the client simulator from in-memory simulation to real MQTT transport and persistence.
