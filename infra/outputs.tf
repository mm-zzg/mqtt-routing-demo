output "resource_group_name" {
  value = var.resource_group_name
}

output "container_registry_login_server" {
  value = azurerm_container_registry.this.login_server
}

output "tenant_hosts" {
  value = local.tenant_hosts
}

output "cloudflare_zone_id" {
  value = local.zone_id
}

output "cloudflare_zone_name" {
  value = data.cloudflare_zone.this.name
}

output "cloudflare_dns_records" {
  value = {
    for tenant, host in local.tenant_hosts : tenant => {
      cname_target = azurerm_container_app.tenant[tenant].latest_revision_fqdn
      host         = host
    }
  }
}
