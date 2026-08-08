# MQTT routing demo

This repo is a proof-of-concept for the architecture in the diagram:

- .NET Aspire AppHost for local orchestration
- tenant plane services for broker + HTTP listener
- local-only custom ingress simulator for Azure Container Apps ingress; it is not deployed to Azure
- Azure Container Apps native ingress for production traffic (HTTP)
- MQTT gateway (public-facing, TCP → TCP bridge with tenant routing)
- client simulator UI for managing ongoing simulated MQTT clients and client certificates
- Terraform for Azure Container Apps, DNS, and certificates
- GitHub Actions for build and deploy automation

## Configuration

- `base_domain` controls the public domain prefix used by Terraform
- tenant hostnames are derived from that domain
- the custom ingress service is only used in local development
- Terraform now generates a Cloudflare Origin CA cert, bundles it to PFX, and uploads it to Azure Container Apps
- if you later put Cloudflare in front, move the host records to Cloudflare DNS and proxy them there

## Azure deployment

The Terraform layer provisions the Azure Container Apps environment, a custom VNet/subnet for Container Apps infrastructure, ACR, DNS zone, custom domains, and a Cloudflare Origin CA certificate bundled to PFX for Azure Container Apps. The `MqttRouting.Ingress` project stays local-only and is not part of the Azure deployment.

## Deployment permissions

The GitHub Actions service principal used by `AZURE_CLIENT_ID` must have permission to create role assignments at the resource-group or subscription scope before Terraform can create the `AcrPull` binding for the user-assigned identity. In practice, grant that principal `User Access Administrator` or `Owner` on the deployment scope once, then keep the Terraform-managed `AcrPull` assignment in place.

## Public edge model

Cloudflare serves the public certificate automatically at the edge. Terraform only creates the Cloudflare Origin CA cert for the Azure origin and uploads the PFX to Container Apps.

## Local run

Open the AppHost project and run the Aspire solution to start the services together.

The client simulator is available as a web UI and lets you:
- add/remove client certificate entries (PFX base64 + password validation)
- create simulated MQTT client profiles
- start/stop/remove ongoing client sessions
- monitor runtime status, publish counts, last activity, and last errors
