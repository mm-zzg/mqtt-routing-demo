output "resource_group_name" {
  value = var.resource_group_name
}

output "container_registry_login_server" {
  value = try(azurerm_container_registry.this[0].login_server, null)
}

output "tenant_hosts" {
  value = local.active_tenant_hosts
}

output "cloudflare_zone_id" {
  value = local.zone_id
}

output "cloudflare_zone_name" {
  value = data.cloudflare_zone.this.name
}

output "cloudflare_dns_records" {
  value = {
    for tenant, host in local.active_tenant_hosts : tenant => {
      cname_target = azurerm_container_app.tenant[tenant].ingress[0].fqdn
      host         = host
    }
  }
}

output "container_app_environment_name" {
  description = "Azure Container Apps environment name that holds uploaded certificates."
  value       = try(azurerm_container_app_environment.this[0].name, null)
}

output "origin_certificate_name" {
  description = "Expected certificate name uploaded to the Container Apps environment."
  value       = "${var.name_prefix}-origin"
}
