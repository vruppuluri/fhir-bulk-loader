output "resource_group_name" {
  value       = azurerm_resource_group.main.name
  description = "Resource group containing all FHIR Bulk Loader resources"
}

output "storage_account_name" {
  value       = azurerm_storage_account.loader.name
  description = "Storage account — upload files here to trigger imports"
}

output "function_app_name" {
  value       = azurerm_windows_function_app.loader.name
  description = "Azure Function App name"
}

output "function_app_hostname" {
  value       = azurerm_windows_function_app.loader.default_hostname
  description = "Function App default hostname"
}

output "managed_identity_principal_id" {
  value       = azurerm_windows_function_app.loader.identity[0].principal_id
  description = "Managed Identity principal ID (assign FHIR Data Contributor if not using fhir_service_resource_id)"
}

output "key_vault_name" {
  value       = azurerm_key_vault.main.name
  description = "Key Vault holding FHIR credentials"
}

output "application_insights_name" {
  value       = azurerm_application_insights.main.name
  description = "Application Insights resource for logs and traces"
}

output "alt_export_endpoint" {
  value       = "https://${azurerm_windows_function_app.loader.default_hostname}/api/$alt-export"
  description = "HTTP POST endpoint for triggering patient-centric bulk export"
}

output "blob_containers" {
  value = {
    for k, v in azurerm_storage_container.containers : k => v.name
  }
  description = "Map of storage container names"
}
