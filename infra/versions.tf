terraform {
  required_version = ">= 1.11.0"

  backend "azurerm" {
    resource_group_name  = "tfstate-rg"
    storage_account_name = "mqtttfroutingdemo"
    container_name       = "tfstate"
    key                  = "mqtt-routing.terraform.tfstate"
  }

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    cloudflare = {
      source  = "cloudflare/cloudflare"
      version = "~> 5.0"
    }
    pki = {
      source  = "nijave/pki"
      version = "~> 1.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.0"
    }
    tls = {
      source  = "hashicorp/tls"
      version = "~> 4.0"
    }
    local = {
      source  = "hashicorp/local"
      version = "~> 2.0"
    }
  }
}

provider "cloudflare" {}

provider "azurerm" {
  features {}
}
