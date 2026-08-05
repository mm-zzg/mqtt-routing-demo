variable "name_prefix" {
  type        = string
  description = "Prefix for Azure resource names."
  default     = "mqtt-routing"
}

variable "location" {
  type        = string
  description = "Azure region."
  default     = "eastus"
}

variable "base_domain" {
  type        = string
  description = "Public domain used for tenant hostnames."
  default     = "example.com"
}

variable "tenant_names" {
  type        = list(string)
  description = "Tenant identifiers that become subdomains."
  default     = ["tenant1", "tenant2"]
}

variable "image_tag" {
  type        = string
  description = "Container image tag pushed by GitHub Actions."
  default     = "latest"
}

variable "cloudflare_zone_id" {
  type        = string
  description = "Cloudflare zone ID for the public domain."
}

variable "cloudflare_proxy" {
  type        = bool
  description = "Whether Cloudflare should proxy the DNS records."
  default     = true
}
