data "cloudflare_zone" "this" {
  filter = {
    name = var.base_domain
  }
}

resource "azurerm_resource_group" "this" {
  count    = var.deploy_resources ? 1 : 0
  name     = var.resource_group_name
  location = var.location
}

locals {
  log_analytics_name   = "${var.name_prefix}-law"
  acr_name             = replace("${var.name_prefix}${substr(replace(replace(var.base_domain, ".", ""), "-", ""), 0, 12)}", "-", "")
  container_apps_uai_name = "${var.name_prefix}-aca-pull"
  env_name             = "${var.name_prefix}-aca"
  vnet_name            = "${var.name_prefix}-aca-vnet"
  infra_subnet_name    = "${var.name_prefix}-aca-infra"
  gateway_app_name     = "${var.name_prefix}-gateway"
  tenant_apps          = { for tenant in var.tenant_names : tenant => "${var.name_prefix}-${tenant}" }
  tenant_hosts         = { for tenant in var.tenant_names : tenant => "${tenant}.${var.base_domain}" }
  active_tenant_apps   = var.deploy_resources ? local.tenant_apps : {}
  active_tenant_hosts  = var.deploy_resources ? local.tenant_hosts : {}
  wildcard_host        = "*.${var.base_domain}"
  zone_id              = var.cloudflare_zone_id != "" ? var.cloudflare_zone_id : data.cloudflare_zone.this.id
  images = {
    gateway      = var.deploy_resources ? "${azurerm_container_registry.this[0].login_server}/mqtt-gateway:${var.image_tag}" : null
    tenant_plane = var.deploy_resources ? "${azurerm_container_registry.this[0].login_server}/tenant-plane:${var.image_tag}" : null
  }
}

resource "tls_private_key" "origin" {
  count     = var.deploy_resources ? 1 : 0
  algorithm = "RSA"
  rsa_bits  = 2048
}

resource "tls_cert_request" "origin" {
  count           = var.deploy_resources ? 1 : 0
  private_key_pem = tls_private_key.origin[0].private_key_pem

  subject {
    common_name = local.wildcard_host
  }

  dns_names = [local.wildcard_host]
}

resource "cloudflare_origin_ca_certificate" "origin" {
  count              = var.deploy_resources ? 1 : 0
  csr                = tls_cert_request.origin[0].cert_request_pem
  hostnames          = [local.wildcard_host]
  request_type       = "origin-rsa"
  requested_validity = 5475
}

resource "random_password" "origin_pfx" {
  count   = var.deploy_resources ? 1 : 0
  length  = 32
  special = false
}

# Build a PKCS#12 bundle with openssl and upload it to the Container App
# environment via `az containerapp env certificate upload`. This sidesteps
# Terraform's plan/apply timing (data sources are read at plan time, before
# any local-exec runs) and the pki_bundle provider's password_wo validation
# issues.
resource "terraform_data" "origin_pfx_upload" {
  count = var.deploy_resources ? 1 : 0

  triggers_replace = {
    cert     = cloudflare_origin_ca_certificate.origin[0].certificate
    key      = tls_private_key.origin[0].private_key_pem
    password = random_password.origin_pfx[0].result
  }

  provisioner "local-exec" {
    interpreter = ["/usr/bin/env", "bash", "-c"]
    environment = {
      ORIGIN_PFX_PASSWORD = random_password.origin_pfx[0].result
    }
    command = <<-EOT
      set -euo pipefail

      cat > .origin.crt <<'CERT'
      ${cloudflare_origin_ca_certificate.origin[0].certificate}
      CERT
      cat > .origin.key <<'KEY'
      ${tls_private_key.origin[0].private_key_pem}
      KEY
      openssl pkcs12 -export \
        -inkey .origin.key \
        -in .origin.crt \
        -name origin \
        -password pass:"$ORIGIN_PFX_PASSWORD" \
        -out .origin.pfx

      # Upload (or update) the certificate in the Container App environment.
      az containerapp env certificate upload \
        --resource-group "${azurerm_resource_group.this[0].name}" \
        --name "${local.env_name}" \
        --certificate-file .origin.pfx \
        --certificate-name "${var.name_prefix}-origin" \
        --password "$ORIGIN_PFX_PASSWORD" \
        --output none
    EOT
  }

  depends_on = [azurerm_container_app_environment.this]
}

resource "terraform_data" "origin_host_bindings" {
  count = var.deploy_resources ? 1 : 0

  triggers_replace = {
    cert  = cloudflare_origin_ca_certificate.origin[0].certificate
    hosts = jsonencode(local.active_tenant_hosts)
  }

  provisioner "local-exec" {
    interpreter = ["/usr/bin/env", "bash", "-c"]
    command = <<-EOT
      set -euo pipefail

      bind_hostname() {
        local app_name="$1"
        local host_name="$2"
        local attempts=12
        local wait_seconds=10

        for i in $(seq 1 "$attempts"); do
          if az containerapp hostname bind \
            --resource-group "${azurerm_resource_group.this[0].name}" \
            --name "$app_name" \
            --hostname "$host_name" \
            --environment "${local.env_name}" \
            --certificate "${var.name_prefix}-origin" \
            --output none; then
            echo "Bound $host_name to $app_name"
            return 0
          fi

          if [ "$i" -lt "$attempts" ]; then
            echo "Bind failed for $host_name (attempt $i/$attempts). Retrying in $wait_seconds s..."
            sleep "$wait_seconds"
          fi
        done

        echo "Failed to bind $host_name to $app_name after $attempts attempts"
        return 1
      }

      # Bind each tenant hostname to its Container App using the uploaded cert.
      %{for tenant, app in local.active_tenant_apps~}
      bind_hostname "${app}" "${tenant}.${var.base_domain}"
      %{endfor~}
    EOT
  }

  depends_on = [
    terraform_data.origin_pfx_upload,
    azurerm_container_app.tenant,
    cloudflare_dns_record.tenant,
    cloudflare_dns_record.tenant_verification,
    azurerm_dns_cname_record.tenant,
    azurerm_dns_txt_record.tenant_verification
  ]
}

resource "azurerm_log_analytics_workspace" "this" {
  count               = var.deploy_resources ? 1 : 0
  name                = local.log_analytics_name
  location            = azurerm_resource_group.this[0].location
  resource_group_name = azurerm_resource_group.this[0].name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_virtual_network" "container_apps" {
  count               = var.deploy_resources ? 1 : 0
  name                = local.vnet_name
  location            = azurerm_resource_group.this[0].location
  resource_group_name = azurerm_resource_group.this[0].name
  address_space       = [var.container_apps_vnet_cidr]
}

resource "azurerm_subnet" "container_apps_infra" {
  count                = var.deploy_resources ? 1 : 0
  name                 = local.infra_subnet_name
  resource_group_name  = azurerm_resource_group.this[0].name
  virtual_network_name = azurerm_virtual_network.container_apps[0].name
  address_prefixes     = [var.container_apps_infra_subnet_cidr]

  delegation {
    name = "containerapps-delegation"

    service_delegation {
      name = "Microsoft.App/environments"
      actions = [
        "Microsoft.Network/virtualNetworks/subnets/join/action"
      ]
    }
  }
}

resource "azurerm_container_app_environment" "this" {
  count                      = var.deploy_resources ? 1 : 0
  name                       = local.env_name
  location                   = azurerm_resource_group.this[0].location
  resource_group_name        = azurerm_resource_group.this[0].name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this[0].id
  infrastructure_subnet_id   = azurerm_subnet.container_apps_infra[0].id
}

resource "azurerm_container_registry" "this" {
  count               = var.deploy_resources ? 1 : 0
  name                = local.acr_name
  resource_group_name = azurerm_resource_group.this[0].name
  location            = azurerm_resource_group.this[0].location
  sku                 = "Basic"
  admin_enabled       = false
}

resource "azurerm_user_assigned_identity" "container_apps" {
  count               = var.deploy_resources ? 1 : 0
  name                = local.container_apps_uai_name
  resource_group_name = azurerm_resource_group.this[0].name
  location            = azurerm_resource_group.this[0].location
}

resource "azurerm_role_assignment" "container_apps_pull" {
  count                = var.deploy_resources ? 1 : 0
  scope                = azurerm_container_registry.this[0].id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.container_apps[0].principal_id
}

resource "azurerm_container_app" "tenant" {
  for_each                     = local.active_tenant_apps
  name                         = each.value
  resource_group_name          = azurerm_resource_group.this[0].name
  container_app_environment_id = azurerm_container_app_environment.this[0].id
  revision_mode                = "Single"
  depends_on                   = [azurerm_role_assignment.container_apps_pull]

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.container_apps[0].id]
  }

  registry {
    server   = azurerm_container_registry.this[0].login_server
    identity = azurerm_user_assigned_identity.container_apps[0].id
  }

  template {
    container {
      name   = each.key
      image  = "${azurerm_container_registry.this[0].login_server}/tenant-plane:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "TenantPlane__TenantName"
        value = each.key
      }

      env {
        name  = "TenantPlane__BaseDomain"
        value = var.base_domain
      }

      env {
        name  = "TenantPlane__CustomDomain"
        value = local.tenant_hosts[each.key]
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

resource "azurerm_container_app" "mqtt_gateway" {
  count                        = var.deploy_resources ? 1 : 0
  name                         = local.gateway_app_name
  resource_group_name          = azurerm_resource_group.this[0].name
  container_app_environment_id = azurerm_container_app_environment.this[0].id
  revision_mode                = "Single"
  depends_on                   = [azurerm_role_assignment.container_apps_pull]

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.container_apps[0].id]
  }

  registry {
    server   = azurerm_container_registry.this[0].login_server
    identity = azurerm_user_assigned_identity.container_apps[0].id
  }

  template {
    container {
      name   = "mqtt-gateway"
      image  = local.images.gateway
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "MqttGateway__BaseDomain"
        value = var.base_domain
      }

      env {
        name  = "MqttGateway__MqttTcpListenPort"
        value = "1883"
      }

      env {
        name  = "MqttGateway__RouteTable__0__Tenant"
        value = var.tenant_names[0]
      }

      env {
        name  = "MqttGateway__RouteTable__0__Host"
        value = azurerm_container_app.tenant[var.tenant_names[0]].ingress[0].fqdn
      }

      env {
        name  = "MqttGateway__RouteTable__0__Port"
        value = "8080"
      }

      env {
        name  = "MqttGateway__RouteTable__1__Tenant"
        value = var.tenant_names[1]
      }

      env {
        name  = "MqttGateway__RouteTable__1__Host"
        value = azurerm_container_app.tenant[var.tenant_names[1]].ingress[0].fqdn
      }

      env {
        name  = "MqttGateway__RouteTable__1__Port"
        value = "8080"
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 1883
    transport        = "tcp"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }
}

resource "azurerm_dns_zone" "this" {
  count               = var.deploy_resources ? 1 : 0
  name                = var.base_domain
  resource_group_name = azurerm_resource_group.this[0].name
}

resource "cloudflare_dns_record" "tenant" {
  for_each = local.active_tenant_hosts

  zone_id = local.zone_id
  name    = each.key
  type    = "CNAME"
  content = azurerm_container_app.tenant[each.key].ingress[0].fqdn
  ttl     = 1
  proxied = var.cloudflare_proxy
}

resource "cloudflare_dns_record" "tenant_verification" {
  for_each = local.active_tenant_hosts

  zone_id = local.zone_id
  name    = "asuid.${each.key}"
  type    = "TXT"
  content = azurerm_container_app.tenant[each.key].custom_domain_verification_id
  ttl     = 300
  proxied = false
}

# --- Cloudflare zone settings ---

resource "cloudflare_zone_setting" "ssl" {
  count      = var.deploy_resources ? 1 : 0
  zone_id    = local.zone_id
  setting_id = "ssl"
  value      = var.cloudflare_ssl_mode
}

resource "cloudflare_zone_setting" "always_use_https" {
  count      = var.deploy_resources ? 1 : 0
  zone_id    = local.zone_id
  setting_id = "always_use_https"
  value      = "on"
}

resource "cloudflare_zone_setting" "min_tls_version" {
  count      = var.deploy_resources ? 1 : 0
  zone_id    = local.zone_id
  setting_id = "min_tls_version"
  value      = "1.2"
}

resource "cloudflare_zone_setting" "http3" {
  count      = var.deploy_resources ? 1 : 0
  zone_id    = local.zone_id
  setting_id = "http3"
  value      = "on"
}

resource "azurerm_dns_cname_record" "tenant" {
  for_each            = local.active_tenant_hosts
  name                = each.key
  zone_name           = azurerm_dns_zone.this[0].name
  resource_group_name = azurerm_resource_group.this[0].name
  ttl                 = 300
  record              = azurerm_container_app.tenant[each.key].ingress[0].fqdn
}

resource "azurerm_dns_txt_record" "tenant_verification" {
  for_each            = local.active_tenant_hosts
  name                = "asuid.${each.key}"
  zone_name           = azurerm_dns_zone.this[0].name
  resource_group_name = azurerm_resource_group.this[0].name
  ttl                 = 300

  record {
    value = azurerm_container_app.tenant[each.key].custom_domain_verification_id
  }
}

# Note: the cert is uploaded and custom domains are bound via the
# terraform_data.origin_pfx_upload and terraform_data.origin_host_bindings
# resources above (using `az containerapp` CLI) because the
# pki_bundle provider's password_wo mechanism cannot evaluate managed-resource
# values at plan time, and data sources can't be read after local-exec runs.

# Stubs preserve the resource addresses Terraform would expect; the actual
# lifecycle is managed via `az` CLI in the local-exec above.
# (azurerm_container_app_environment_certificate.origin and
#  azurerm_container_app_custom_domain.tenant are no longer declared here)
