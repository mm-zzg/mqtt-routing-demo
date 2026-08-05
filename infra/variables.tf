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
  description = "Cloudflare zone ID for the public domain. Leave empty to auto-discover via the domain name."
  default     = ""
}

variable "cloudflare_account_id" {
  type        = string
  description = "Cloudflare account ID. Optional, used only when the zone data source needs scoping."
  default     = ""
}

variable "cloudflare_proxy" {
  type        = bool
  description = "Whether Cloudflare should proxy the DNS records."
  default     = true
}

variable "cloudflare_ssl_mode" {
  type        = string
  description = "Cloudflare SSL/TLS encryption mode (off, flexible, full, strict)."
  default     = "strict"
}
