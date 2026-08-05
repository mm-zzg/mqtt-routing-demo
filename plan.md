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

## Open questions
- Should Cloudflare DNS records also be managed in Terraform?
- Should the runtime stay as a lightweight proof-of-concept or be upgraded to a real MQTT broker?
