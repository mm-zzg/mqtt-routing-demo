# MQTT routing demo

This repo is a proof-of-concept for the architecture in the diagram:

- .NET Aspire AppHost for local orchestration
- tenant plane services for broker + HTTP listener
- local-only custom ingress for debugging
- Azure Container Apps native ingress for production traffic
- MQTT gateway (TCP → WebSocket bridge)
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

The Terraform layer provisions the Azure Container Apps environment, ACR, DNS zone, custom domains, and a Cloudflare Origin CA certificate bundled to PFX for Azure Container Apps.

## Public edge model

Cloudflare serves the public certificate automatically at the edge. Terraform only creates the Cloudflare Origin CA cert for the Azure origin and uploads the PFX to Container Apps.

## Local run

Open the AppHost project and run the Aspire solution to start the services together.

The client simulator is available as a web UI and lets you:
- add/remove client certificate entries (PFX base64 + password validation)
- create simulated MQTT client profiles
- start/stop/remove ongoing client sessions
- monitor runtime status, publish counts, last activity, and last errors
