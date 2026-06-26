#!/usr/bin/env bash
###############################################################################
# import-existing.sh
# Imports all already-deployed Azure resources into Terraform state so that
# "terraform apply" finds them instead of trying to create duplicates.
#
# Usage:
#   bash scripts/import-existing.sh
#
# Prerequisites:
#   - az login already done
#   - terraform init already done
#   - Run from the repo root (not from inside terraform/)
###############################################################################
set -euo pipefail

###############################################################################
# ── EDIT THESE VALUES ────────────────────────────────────────────────────────
SUB="2c2a512d-74b0-4d00-9cb1-4a5a1996b02d"
RG="fhirldr-prod-rg"
###############################################################################

TF="terraform -chdir=terraform"

echo ""
echo "══════════════════════════════════════════════════════"
echo " FHIR Bulk Loader — import existing resources to state"
echo "══════════════════════════════════════════════════════"
echo ""

# ── Discover actual resource names ───────────────────────────────────────────

echo "▶  Discovering deployed resources in $RG …"

FUNCAPP=$(az functionapp list \
  --resource-group "$RG" \
  --query "[0].name" -o tsv 2>/dev/null || true)

STORAGE=$(az storage account list \
  --resource-group "$RG" \
  --query "[0].name" -o tsv 2>/dev/null || true)

KV=$(az keyvault list \
  --resource-group "$RG" \
  --query "[0].name" -o tsv 2>/dev/null || true)

ASP=$(az appservice plan list \
  --resource-group "$RG" \
  --query "[0].name" -o tsv 2>/dev/null || true)

AI=$(az resource list \
  --resource-group "$RG" \
  --resource-type "Microsoft.Insights/components" \
  --query "[0].name" -o tsv 2>/dev/null || true)

LAW=$(az resource list \
  --resource-group "$RG" \
  --resource-type "Microsoft.OperationalInsights/workspaces" \
  --query "[0].name" -o tsv 2>/dev/null || true)

EG_TOPIC=$(az eventgrid system-topic list \
  --resource-group "$RG" \
  --query "[0].name" -o tsv 2>/dev/null || true)

echo ""
echo "  Resource Group : $RG"
echo "  Function App   : $FUNCAPP"
echo "  Storage Account: $STORAGE"
echo "  Key Vault      : $KV"
echo "  App Svc Plan   : $ASP"
echo "  App Insights   : $AI"
echo "  Log Analytics  : $LAW"
echo "  EG System Topic: $EG_TOPIC"
echo ""

if [[ -z "$FUNCAPP" || -z "$STORAGE" ]]; then
  echo "ERROR: Could not find Function App or Storage Account in $RG."
  echo "       Run: az functionapp list --resource-group $RG --output table"
  exit 1
fi

# Extract suffix from function app name (last 6 chars after last -)
SUFFIX="${FUNCAPP##*-}"
echo "  Detected suffix: $SUFFIX"
echo ""

# ── Write terraform.tfvars with locked suffix ─────────────────────────────────

TFVARS="terraform/terraform.tfvars"
if [[ ! -f "$TFVARS" ]]; then
  cp terraform/terraform.tfvars.example "$TFVARS"
fi

# Upsert existing_suffix and resource_group_name
python3 - "$TFVARS" "$SUFFIX" "$RG" "$SUB" << 'PY'
import sys, re

tfvars, suffix, rg, sub = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
with open(tfvars) as f:
    content = f.read()

def upsert(content, key, value):
    pattern = rf'^{key}\s*=.*$'
    replacement = f'{key} = "{value}"'
    if re.search(pattern, content, re.MULTILINE):
        return re.sub(pattern, replacement, content, flags=re.MULTILINE)
    return content + f'\n{replacement}\n'

content = upsert(content, 'existing_suffix',     suffix)
content = upsert(content, 'resource_group_name', rg)
content = upsert(content, 'subscription_id',     sub)

with open(tfvars, 'w') as f:
    f.write(content)

print(f"  Updated {tfvars}")
PY

echo "▶  terraform.tfvars updated with locked suffix=$SUFFIX"
echo ""

# ── Import resources into Terraform state ────────────────────────────────────

BASE_ID="/subscriptions/$SUB/resourceGroups/$RG"

import_if_missing() {
  local label="$1" addr="$2" id="$3"
  if $TF state show "$addr" &>/dev/null 2>&1; then
    echo "  [skip]  $label already in state"
  else
    echo "  [import] $label"
    $TF import "$addr" "$id" 2>&1 | tail -1
  fi
}

echo "▶  Importing resources …"
echo ""

import_if_missing "random_string.suffix" \
  "random_string.suffix" "$SUFFIX"

import_if_missing "Resource Group" \
  "azurerm_resource_group.main" \
  "$BASE_ID"

import_if_missing "Storage Account" \
  "azurerm_storage_account.loader" \
  "$BASE_ID/providers/Microsoft.Storage/storageAccounts/$STORAGE"

import_if_missing "App Service Plan" \
  "azurerm_service_plan.main" \
  "$BASE_ID/providers/Microsoft.Web/serverFarms/$ASP"

import_if_missing "Function App" \
  "azurerm_windows_function_app.loader" \
  "$BASE_ID/providers/Microsoft.Web/sites/$FUNCAPP"

import_if_missing "Key Vault" \
  "azurerm_key_vault.main" \
  "$BASE_ID/providers/Microsoft.KeyVault/vaults/$KV"

import_if_missing "Application Insights" \
  "azurerm_application_insights.main" \
  "$BASE_ID/providers/Microsoft.Insights/components/$AI"

import_if_missing "Log Analytics Workspace" \
  "azurerm_log_analytics_workspace.main" \
  "$BASE_ID/providers/Microsoft.OperationalInsights/workspaces/$LAW"

import_if_missing "Event Grid System Topic" \
  "azurerm_eventgrid_system_topic.storage" \
  "$BASE_ID/providers/Microsoft.EventGrid/systemTopics/$EG_TOPIC"

# Storage containers
for CONTAINER in bundles ndjson zip export audit errors retry; do
  import_if_missing "Container: $CONTAINER" \
    "azurerm_storage_container.containers[\"$CONTAINER\"]" \
    "https://$STORAGE.blob.core.windows.net/$CONTAINER"
done

# Storage queues
import_if_missing "Queue: fhir-retry-queue" \
  "azurerm_storage_queue.retry" \
  "https://$STORAGE.queue.core.windows.net/fhir-retry-queue"

import_if_missing "Queue: fhir-poison-queue" \
  "azurerm_storage_queue.poison" \
  "https://$STORAGE.queue.core.windows.net/fhir-poison-queue"

# Event Grid subscriptions
for SUB_NAME in bundles ndjson zip; do
  EG_SUB_ID="$BASE_ID/providers/Microsoft.EventGrid/systemTopics/$EG_TOPIC/eventSubscriptions/fhir-${SUB_NAME}-sub"
  import_if_missing "EG subscription: $SUB_NAME" \
    "azurerm_eventgrid_system_topic_event_subscription.ingest[\"$SUB_NAME\"]" \
    "$EG_SUB_ID"
done

# Key Vault secrets
import_if_missing "KV secret: FhirServiceUrl" \
  "azurerm_key_vault_secret.fhir_url" \
  "https://$KV.vault.azure.net/secrets/FhirServiceUrl"

import_if_missing "KV secret: StorageConnectionString" \
  "azurerm_key_vault_secret.storage_conn" \
  "https://$KV.vault.azure.net/secrets/StorageConnectionString"

echo ""
echo "▶  Import complete. Running terraform plan to verify …"
echo ""
$TF plan -compact-warnings 2>&1 | tail -20

echo ""
echo "══════════════════════════════════════════════════════"
echo " Done. Check the plan output above."
echo " If it shows '0 to add, 0 to destroy' you are good."
echo " Run: terraform -chdir=terraform apply"
echo "══════════════════════════════════════════════════════"
