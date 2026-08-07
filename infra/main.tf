data "cloudflare_zone" "this" {
  filter = {
    name = var.base_domain
  }
}

data "azurerm_resource_group" "this" {
  name = var.resource_group_name
}

locals {
  log_analytics_name   = "${var.name_prefix}-law"
  acr_name             = replace("${var.name_prefix}${substr(replace(replace(var.base_domain, ".", ""), "-", ""), 0, 12)}", "-", "")
  env_name             = "${var.name_prefix}-aca"
  gateway_app_name     = "${var.name_prefix}-gateway"
  ingress_app_name     = "${var.name_prefix}-ingress"
  tenant_apps          = { for tenant in var.tenant_names : tenant => "${var.name_prefix}-${tenant}" }
  tenant_hosts         = { for tenant in var.tenant_names : tenant => "${tenant}.${var.base_domain}" }
  wildcard_host        = "*.${var.base_domain}"
  zone_id              = var.cloudflare_zone_id != "" ? var.cloudflare_zone_id : data.cloudflare_zone.this.id
  images = {
    gateway      = "${azurerm_container_registry.this.login_server}/mqtt-gateway:${var.image_tag}"
    ingress     = "${azurerm_container_registry.this.login_server}/ingress:${var.image_tag}"
    tenant_plane = "${azurerm_container_registry.this.login_server}/tenant-plane:${var.image_tag}"
  }
}

resource "tls_private_key" "origin" {
  algorithm = "RSA"
  rsa_bits  = 2048
}

resource "tls_cert_request" "origin" {
  private_key_pem = tls_private_key.origin.private_key_pem

  subject {
    common_name = local.wildcard_host
  }

  dns_names = [local.wildcard_host]
}

resource "cloudflare_origin_ca_certificate" "origin" {
  csr                = tls_cert_request.origin.cert_request_pem
  hostnames          = [local.wildcard_host]
  request_type       = "origin-rsa"
  requested_validity = 5475
}

resource "random_password" "origin_pfx" {
  length  = 32
  special = false
}

# Build a PKCS#12 bundle via openssl at apply time and expose its base64
# content through a state-stored local. The pki_bundle provider can't carry
# a private key with passwordless, and its password_wo can't be evaluated at
# plan time, so we run openssl ourselves and store the bytes on the
# terraform_data resource itself.
resource "terraform_data" "origin_pfx" {
  triggers_replace = {
    cert     = cloudflare_origin_ca_certificate.origin.certificate
    key      = tls_private_key.origin.private_key_pem
    password = random_password.origin_pfx.result
  }

  provisioner "local-exec" {
    command = <<-EOT
      set -euo pipefail
      cat > .origin.crt <<'CERT'
      ${cloudflare_origin_ca_certificate.origin.certificate}
      CERT
      cat > .origin.key <<'KEY'
      ${tls_private_key.origin.private_key_pem}
      KEY
      openssl pkcs12 -export \
        -inkey .origin.key \
        -in .origin.crt \
        -name origin \
        -password pass:${random_password.origin_pfx.result} \
        -out .origin.pfx
    EOT
  }
}

# Use data.local_file to read the PFX at apply time. The data source is
# evaluated during the apply graph because terraform_data's local-exec has
# already written the file by the time data sources are read for resources
# created in the same apply.
data "local_file" "origin_pfx" {
  filename   = "${path.module}/.origin.pfx"
  depends_on = [terraform_data.origin_pfx]
}

resource "azurerm_log_analytics_workspace" "this" {
  name                = local.log_analytics_name
  location            = data.azurerm_resource_group.this.location
  resource_group_name = data.azurerm_resource_group.this.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_container_app_environment" "this" {
  name                       = local.env_name
  location                   = data.azurerm_resource_group.this.location
  resource_group_name        = data.azurerm_resource_group.this.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id
}

resource "azurerm_container_registry" "this" {
  name                = local.acr_name
  resource_group_name = data.azurerm_resource_group.this.name
  location            = data.azurerm_resource_group.this.location
  sku                 = "Basic"
  admin_enabled       = false
}

resource "azurerm_container_app" "tenant" {
  for_each                     = local.tenant_apps
  name                         = each.value
  resource_group_name          = data.azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  registry {
    server   = azurerm_container_registry.this.login_server
    identity = "System"
  }

  template {
    container {
      name   = each.key
      image  = "${azurerm_container_registry.this.login_server}/tenant-plane:${var.image_tag}"
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

resource "azurerm_role_assignment" "tenant_pull" {
  for_each             = azurerm_container_app.tenant
  scope                = azurerm_container_registry.this.id
  role_definition_name = "AcrPull"
  principal_id         = each.value.identity[0].principal_id
}

resource "azurerm_container_app" "mqtt_gateway" {
  name                         = local.gateway_app_name
  resource_group_name          = data.azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  registry {
    server   = azurerm_container_registry.this.login_server
    identity = "System"
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
        value = azurerm_container_app.tenant[var.tenant_names[0]].latest_revision_fqdn
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
        value = azurerm_container_app.tenant[var.tenant_names[1]].latest_revision_fqdn
      }

      env {
        name  = "MqttGateway__RouteTable__1__Port"
        value = "8080"
      }
    }
  }
}

resource "azurerm_role_assignment" "gateway_pull" {
  scope                = azurerm_container_registry.this.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_container_app.mqtt_gateway.identity[0].principal_id
}

# Ingress: public-facing entry point for devices.
# MQTT-over-TCP exposed on port 1883; HTTP proxy on port 8080 (internal health).
resource "azurerm_container_app" "ingress" {
  name                         = local.ingress_app_name
  resource_group_name          = data.azurerm_resource_group.this.name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  registry {
    server   = azurerm_container_registry.this.login_server
    identity = "System"
  }

  template {
    container {
      name   = "ingress"
      image  = local.images.ingress
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "Ingress__BaseDomain"
        value = var.base_domain
      }

      env {
        name  = "Ingress__MqttTcpListenPort"
        value = "1883"
      }

      env {
        name  = "Ingress__MqttGatewayHost"
        value = azurerm_container_app.mqtt_gateway.latest_revision_fqdn
      }

      env {
        name  = "Ingress__MqttGatewayPort"
        value = "1883"
      }

      env {
        name  = "Ingress__RouteTable__0__Tenant"
        value = var.tenant_names[0]
      }

      env {
        name  = "Ingress__RouteTable__0__Host"
        value = local.tenant_hosts[var.tenant_names[0]]
      }

      env {
        name  = "Ingress__RouteTable__0__BackendHost"
        value = azurerm_container_app.tenant[var.tenant_names[0]].latest_revision_fqdn
      }

      env {
        name  = "Ingress__RouteTable__0__BackendPort"
        value = "8080"
      }

      env {
        name  = "Ingress__RouteTable__1__Tenant"
        value = var.tenant_names[1]
      }

      env {
        name  = "Ingress__RouteTable__1__Host"
        value = local.tenant_hosts[var.tenant_names[1]]
      }

      env {
        name  = "Ingress__RouteTable__1__BackendHost"
        value = azurerm_container_app.tenant[var.tenant_names[1]].latest_revision_fqdn
      }

      env {
        name  = "Ingress__RouteTable__1__BackendPort"
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

resource "azurerm_role_assignment" "ingress_pull" {
  scope                = azurerm_container_registry.this.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_container_app.ingress.identity[0].principal_id
}

resource "azurerm_dns_zone" "this" {
  name                = var.base_domain
  resource_group_name = data.azurerm_resource_group.this.name
}

resource "cloudflare_dns_record" "tenant" {
  for_each = local.tenant_hosts

  zone_id = local.zone_id
  name    = each.key
  type    = "CNAME"
  content = azurerm_container_app.tenant[each.key].latest_revision_fqdn
  ttl     = 1
  proxied = var.cloudflare_proxy
}

resource "cloudflare_dns_record" "tenant_verification" {
  for_each = local.tenant_hosts

  zone_id = local.zone_id
  name    = "asuid.${each.key}"
  type    = "TXT"
  content = azurerm_container_app.tenant[each.key].custom_domain_verification_id
  ttl     = 300
  proxied = false
}

# --- Cloudflare zone settings ---

resource "cloudflare_zone_setting" "ssl" {
  zone_id    = local.zone_id
  setting_id = "ssl"
  value      = var.cloudflare_ssl_mode
}

resource "cloudflare_zone_setting" "always_use_https" {
  zone_id    = local.zone_id
  setting_id = "always_use_https"
  value      = "on"
}

resource "cloudflare_zone_setting" "min_tls_version" {
  zone_id    = local.zone_id
  setting_id = "min_tls_version"
  value      = "1.2"
}

resource "cloudflare_zone_setting" "http3" {
  zone_id    = local.zone_id
  setting_id = "http3"
  value      = "on"
}

resource "azurerm_dns_cname_record" "tenant" {
  for_each            = local.tenant_hosts
  name                = each.key
  zone_name           = azurerm_dns_zone.this.name
  resource_group_name = data.azurerm_resource_group.this.name
  ttl                 = 300
  record              = azurerm_container_app.tenant[each.key].latest_revision_fqdn
}

resource "azurerm_dns_txt_record" "tenant_verification" {
  for_each            = local.tenant_hosts
  name                = "asuid.${each.key}"
  zone_name           = azurerm_dns_zone.this.name
  resource_group_name = data.azurerm_resource_group.this.name
  ttl                 = 300

  record {
    value = azurerm_container_app.tenant[each.key].custom_domain_verification_id
  }
}

resource "azurerm_container_app_environment_certificate" "origin" {
  name                         = "${var.name_prefix}-origin"
  container_app_environment_id = azurerm_container_app_environment.this.id
  certificate_blob_base64      = base64encode(data.local_file.origin_pfx.content)
  certificate_password         = random_password.origin_pfx.result
  depends_on                   = [data.local_file.origin_pfx]
}

resource "azurerm_container_app_custom_domain" "tenant" {
  for_each                                = local.tenant_hosts
  name                                    = each.value
  container_app_id                        = azurerm_container_app.tenant[each.key].id
  container_app_environment_certificate_id = azurerm_container_app_environment_certificate.origin.id
  certificate_binding_type                = "SniEnabled"
}
