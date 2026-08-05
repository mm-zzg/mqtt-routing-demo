output "resource_group_name" {
  value = azurerm_resource_group.this.name
}

output "container_registry_login_server" {
  value = azurerm_container_registry.this.login_server
}

output "tenant_hosts" {
  value = local.tenant_hosts
}

output "cloudflare_dns_records" {
  value = {
    for tenant, host in local.tenant_hosts : tenant => {
      cname_target = azurerm_container_app.tenant[tenant].latest_revision_fqdn
      host         = host
    }
  }
}
