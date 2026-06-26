###############################################################################
# FHIR Bulk Loader & Export — Terraform (azurerm ~> 3.x)
# Phase 1: All infrastructure EXCEPT Event Grid subscriptions.
# Event Grid subscriptions live in eventgrid.tf and are gated behind the
# null_resource that deploys the Function App package (so EG webhook
# handshake finds live endpoints, not 404s).
###############################################################################

terraform {
  required_version = ">= 1.5.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 3.100"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
    null = {
      source  = "hashicorp/null"
      version = "~> 3.2"
    }
  }
}

provider "azurerm" {
  features {
    key_vault {
      purge_soft_delete_on_destroy    = false
      recover_soft_deleted_key_vaults = true
    }
  }
  subscription_id = var.subscription_id
}

data "azurerm_client_config" "current" {}

resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

locals {
  suffix    = random_string.suffix.result
  base      = "${var.project_name}-${var.environment}"
  sa_name   = replace(lower("${var.project_name}${var.environment}${local.suffix}"), "-", "")
  kv_name   = "${local.base}-kv-${local.suffix}"
  func_name = "${local.base}-fn-${local.suffix}"
  rg_name   = var.resource_group_name != "" ? var.resource_group_name : "${local.base}-rg"
  tags = merge(var.tags, {
    Environment = var.environment
    Project     = var.project_name
    ManagedBy   = "Terraform"
  })
}

###############################################################################
# Resource Group
###############################################################################

resource "azurerm_resource_group" "main" {
  name     = local.rg_name
  location = var.location
  tags     = local.tags
}

###############################################################################
# Storage Account
###############################################################################

resource "azurerm_storage_account" "loader" {
  name                     = local.sa_name
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"

  blob_properties {
    versioning_enabled = true
    delete_retention_policy {
      days = 7
    }
    container_delete_retention_policy {
      days = 7
    }
  }

  tags = local.tags
}

resource "azurerm_storage_container" "containers" {
  for_each              = toset(["bundles", "ndjson", "zip", "export", "audit", "errors", "retry"])
  name                  = each.key
  storage_account_name  = azurerm_storage_account.loader.name
  container_access_type = "private"
}

resource "azurerm_storage_queue" "retry" {
  name                 = "fhir-retry-queue"
  storage_account_name = azurerm_storage_account.loader.name
}

resource "azurerm_storage_queue" "poison" {
  name                 = "fhir-poison-queue"
  storage_account_name = azurerm_storage_account.loader.name
}

###############################################################################
# Key Vault
###############################################################################

resource "azurerm_key_vault" "main" {
  name                       = local.kv_name
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  tenant_id                  = data.azurerm_client_config.current.tenant_id
  sku_name                   = "standard"
  purge_protection_enabled   = false
  soft_delete_retention_days = 7

  access_policy {
    tenant_id          = data.azurerm_client_config.current.tenant_id
    object_id          = data.azurerm_client_config.current.object_id
    secret_permissions = ["Get", "List", "Set", "Delete", "Purge", "Recover"]
  }

  tags = local.tags
}

resource "azurerm_key_vault_secret" "fhir_url" {
  name         = "FhirServiceUrl"
  value        = var.fhir_service_url
  key_vault_id = azurerm_key_vault.main.id
}

resource "azurerm_key_vault_secret" "storage_conn" {
  name         = "StorageConnectionString"
  value        = azurerm_storage_account.loader.primary_connection_string
  key_vault_id = azurerm_key_vault.main.id
}

resource "azurerm_key_vault_secret" "fhir_secret" {
  count        = var.fhir_client_secret != "" ? 1 : 0
  name         = "FhirClientSecret"
  value        = var.fhir_client_secret
  key_vault_id = azurerm_key_vault.main.id
}

###############################################################################
# Observability
###############################################################################

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${local.base}-law"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  tags                = local.tags
}

resource "azurerm_application_insights" "main" {
  name                = "${local.base}-ai"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
  tags                = local.tags
}

###############################################################################
# App Service Plan
###############################################################################

resource "azurerm_service_plan" "main" {
  name                = "${local.base}-asp"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  os_type             = "Windows"
  sku_name            = var.app_service_plan_sku
  tags                = local.tags
}

###############################################################################
# Function App
###############################################################################

resource "azurerm_windows_function_app" "loader" {
  name                       = local.func_name
  resource_group_name        = azurerm_resource_group.main.name
  location                   = azurerm_resource_group.main.location
  service_plan_id            = azurerm_service_plan.main.id
  storage_account_name       = azurerm_storage_account.loader.name
  storage_account_access_key = azurerm_storage_account.loader.primary_access_key

  identity {
    type = "SystemAssigned"
  }

  app_settings = {
    FUNCTIONS_WORKER_RUNTIME              = "dotnet"
    FUNCTIONS_EXTENSION_VERSION           = "~4"
    WEBSITE_RUN_FROM_PACKAGE              = "1"
    APPINSIGHTS_INSTRUMENTATIONKEY        = azurerm_application_insights.main.instrumentation_key
    APPLICATIONINSIGHTS_CONNECTION_STRING = azurerm_application_insights.main.connection_string

    "FS-URL"         = "@Microsoft.KeyVault(VaultName=${local.kv_name};SecretName=FhirServiceUrl)"
    "FS-TENANT-NAME" = var.fhir_tenant_id != "" ? var.fhir_tenant_id : data.azurerm_client_config.current.tenant_id
    "FS-CLIENT-ID"   = var.fhir_client_id
    "FS-SECRET"      = var.fhir_client_secret != "" ? "@Microsoft.KeyVault(VaultName=${local.kv_name};SecretName=FhirClientSecret)" : ""
    "FS-RESOURCE"    = var.fhir_resource != "" ? var.fhir_resource : var.fhir_service_url

    "FBI-STORAGEACCT"     = "@Microsoft.KeyVault(VaultName=${local.kv_name};SecretName=StorageConnectionString)"
    "FBI-MAXRETRIES"       = tostring(var.max_retries)
    "FBI-THROTTLE-DELAY"   = tostring(var.throttle_delay_ms)
    "FBI-MAXBUNDLESIZE"    = tostring(var.max_bundle_size)
    "FBI-PARALLELPATIENTS" = tostring(var.parallel_patients)

    AzureWebJobsStorage = azurerm_storage_account.loader.primary_connection_string
  }

  site_config {
    always_on = true
    application_stack {
      dotnet_version              = "v8.0"
      use_dotnet_isolated_runtime = false
    }
    cors {
      allowed_origins = ["https://portal.azure.com"]
    }
  }

  tags = local.tags
}

resource "azurerm_key_vault_access_policy" "func" {
  key_vault_id       = azurerm_key_vault.main.id
  tenant_id          = azurerm_windows_function_app.loader.identity[0].tenant_id
  object_id          = azurerm_windows_function_app.loader.identity[0].principal_id
  secret_permissions = ["Get", "List"]
}

###############################################################################
# Role Assignments
###############################################################################

resource "azurerm_role_assignment" "blob_contrib" {
  scope                = azurerm_storage_account.loader.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_windows_function_app.loader.identity[0].principal_id
}

resource "azurerm_role_assignment" "queue_contrib" {
  scope                = azurerm_storage_account.loader.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_windows_function_app.loader.identity[0].principal_id
}

resource "azurerm_role_assignment" "fhir_contrib" {
  count                = var.fhir_service_resource_id != "" ? 1 : 0
  scope                = var.fhir_service_resource_id
  role_definition_name = "FHIR Data Contributor"
  principal_id         = azurerm_windows_function_app.loader.identity[0].principal_id
}

###############################################################################
# Function App package deployment
# Builds, publishes, zips and deploys the .NET project BEFORE Event Grid
# subscriptions are created — Event Grid performs a live webhook handshake
# and will fail with 404 if the functions aren't yet deployed.
###############################################################################

resource "null_resource" "deploy_package" {
  # Re-run whenever the Function App is recreated or source changes
  triggers = {
    function_app_id  = azurerm_windows_function_app.loader.id
    source_hash      = var.source_code_hash
  }

  provisioner "local-exec" {
    working_dir = "${path.module}/.."
    command     = <<-CMD
      set -e
      echo "==> Building Function App..."
      dotnet publish src/FHIRBulkImport/FHIRBulkImport.csproj \
        --configuration Release \
        --output /tmp/fhir-loader-publish \
        --nologo \
        -p:GenerateRuntimeConfigurationFiles=true

      echo "==> Creating zip package..."
      rm -f /tmp/fhir-loader-package.zip
      cd /tmp/fhir-loader-publish && zip -r /tmp/fhir-loader-package.zip . && cd -

      echo "==> Deploying to Function App ${azurerm_windows_function_app.loader.name}..."
      az functionapp deployment source config-zip \
        --resource-group ${azurerm_resource_group.main.name} \
        --name ${azurerm_windows_function_app.loader.name} \
        --src /tmp/fhir-loader-package.zip \
        --output none

      echo "==> Waiting 60 seconds for Function App to initialise endpoints..."
      sleep 60

      echo "==> Package deployment complete."
    CMD
    interpreter = ["/bin/bash", "-c"]
  }

  depends_on = [
    azurerm_windows_function_app.loader,
    azurerm_key_vault_access_policy.func,
    azurerm_key_vault_secret.fhir_url,
    azurerm_key_vault_secret.storage_conn,
  ]
}

###############################################################################
# Event Grid System Topic
###############################################################################

resource "azurerm_eventgrid_system_topic" "storage" {
  name                   = "${local.base}-eg"
  resource_group_name    = azurerm_resource_group.main.name
  location               = azurerm_resource_group.main.location
  source_arm_resource_id = azurerm_storage_account.loader.id
  topic_type             = "Microsoft.Storage.StorageAccounts"
  tags                   = local.tags
}

###############################################################################
# Diagnostics
###############################################################################

resource "azurerm_monitor_diagnostic_setting" "func" {
  name                       = "func-diag"
  target_resource_id         = azurerm_windows_function_app.loader.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  enabled_log {
    category = "FunctionAppLogs"
  }

  metric {
    category = "AllMetrics"
    enabled  = true
  }
}

resource "azurerm_monitor_diagnostic_setting" "blob" {
  name                       = "blob-diag"
  target_resource_id         = "${azurerm_storage_account.loader.id}/blobServices/default"
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id

  enabled_log {
    category = "StorageRead"
  }

  enabled_log {
    category = "StorageWrite"
  }

  enabled_log {
    category = "StorageDelete"
  }

  metric {
    category = "Transaction"
    enabled  = true
  }
}

###############################################################################
# Alerts
###############################################################################

resource "azurerm_monitor_action_group" "alerts" {
  count               = var.alert_email != "" ? 1 : 0
  name                = "${local.base}-ag"
  resource_group_name = azurerm_resource_group.main.name
  short_name          = "fhiralerts"

  email_receiver {
    name                    = "ops"
    email_address           = var.alert_email
    use_common_alert_schema = true
  }
}

resource "azurerm_monitor_metric_alert" "errors" {
  count               = var.alert_email != "" ? 1 : 0
  name                = "${local.base}-fn-errors"
  resource_group_name = azurerm_resource_group.main.name
  scopes              = [azurerm_windows_function_app.loader.id]
  description         = "High error rate on FHIR Bulk Loader"
  severity            = 2
  frequency           = "PT5M"
  window_size         = "PT15M"

  criteria {
    metric_namespace = "Microsoft.Web/sites"
    metric_name      = "Http5xx"
    aggregation      = "Total"
    operator         = "GreaterThan"
    threshold        = 10
  }

  action {
    action_group_id = azurerm_monitor_action_group.alerts[0].id
  }
}
