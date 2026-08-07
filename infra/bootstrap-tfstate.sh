#!/usr/bin/env bash
# Idempotently provisions the Azure Storage account used as Terraform's
# remote state backend. Safe to run multiple times (also runs in CI).
#
# Override defaults with TF_STATE_RG / TF_STATE_SA / TF_STATE_CONTAINER /
# LOCATION environment variables.
set -euo pipefail

STATE_RG="${TF_STATE_RG:-mqtt-routing-tfstate-rg}"
LOCATION="${LOCATION:-eastus}"
SUB_ID="$(az account show --query id -o tsv)"
# Storage account names: 3-24 chars, lowercase alphanumeric, globally unique.
STATE_SA="${TF_STATE_SA:-mqtttf${SUB_ID:0:8}}"
STATE_CONTAINER="${TF_STATE_CONTAINER:-tfstate}"

echo "Ensuring resource group '$STATE_RG' in '$LOCATION' ..."
az group create --name "$STATE_RG" --location "$LOCATION" --output none

if ! az storage account show --name "$STATE_SA" --resource-group "$STATE_RG" --output none 2>/dev/null; then
  echo "Creating storage account '$STATE_SA' ..."
  az storage account create \
    --name "$STATE_SA" \
    --resource-group "$STATE_RG" \
    --location "$LOCATION" \
    --sku Standard_LRS \
    --min-tls-version TLS1_2 \
    --allow-blob-public-access false \
    --output none
else
  echo "Storage account '$STATE_SA' already exists."
fi

echo "Ensuring blob container '$STATE_CONTAINER' ..."
if ! az storage container exists --name "$STATE_CONTAINER" --account-name "$STATE_SA" --auth-mode login --query exists -o tsv 2>/dev/null | grep -q true; then
  az storage container create --name "$STATE_CONTAINER" --account-name "$STATE_SA" --auth-mode login --output none 2>/dev/null \
    || az storage container create --name "$STATE_CONTAINER" --account-name "$STATE_SA" --output none
fi

echo
echo "Terraform backend configuration:"
echo "  resource_group_name  = $STATE_RG"
echo "  storage_account_name = $STATE_SA"
echo "  container_name       = $STATE_CONTAINER"
echo "  key                  = mqtt-routing.tfstate"

# When running in GitHub Actions, export values for subsequent steps.
if [ -n "${GITHUB_ENV:-}" ]; then
  {
    echo "TF_STATE_RG=$STATE_RG"
    echo "TF_STATE_SA=$STATE_SA"
    echo "TF_STATE_CONTAINER=$STATE_CONTAINER"
  } >> "$GITHUB_ENV"
fi
