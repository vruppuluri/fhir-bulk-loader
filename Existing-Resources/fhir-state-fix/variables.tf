###############################################################################
# FHIR Bulk Loader — Variables
###############################################################################

variable "subscription_id" {
  type        = string
  description = "Azure Subscription ID"
}

variable "project_name" {
  type        = string
  default     = "fhirldr"
  description = "Short name used in resource naming (alphanumeric + hyphens)"
}

variable "environment" {
  type        = string
  default     = "dev"
  description = "dev | staging | prod"
}

variable "location" {
  type        = string
  default     = "eastus"
  description = "Azure region for all resources"
}

variable "resource_group_name" {
  type        = string
  default     = ""
  description = "Resource group name. Leave empty to auto-generate."
}

# ── FHIR Service ──────────────────────────────────────────────────────────────

variable "fhir_service_url" {
  type        = string
  description = "FHIR service endpoint, e.g. https://<ws>-<svc>.fhir.azurehealthcareapis.com"
}

variable "fhir_service_resource_id" {
  type        = string
  default     = ""
  description = "ARM resource ID of FHIR service — used to assign FHIR Data Contributor role. Leave empty to skip."
}

variable "fhir_tenant_id" {
  type        = string
  default     = ""
  description = "Tenant ID for FHIR OAuth. Defaults to the deploying tenant."
}

variable "fhir_client_id" {
  type        = string
  default     = ""
  description = "App registration client ID for FHIR auth. Leave empty to use Managed Identity."
}

variable "fhir_client_secret" {
  type        = string
  default     = ""
  sensitive   = true
  description = "Client secret for FHIR auth. Stored in Key Vault. Leave empty to use Managed Identity."
}

variable "fhir_resource" {
  type        = string
  default     = ""
  description = "OAuth audience for FHIR token. Defaults to fhir_service_url."
}

# ── Compute ───────────────────────────────────────────────────────────────────

variable "app_service_plan_sku" {
  type        = string
  default     = "P2v3"
  description = "App Service Plan SKU. P2v3 recommended for production."
  validation {
    condition     = contains(["B1", "B2", "B3", "S1", "S2", "P1v2", "P2v2", "P1v3", "P2v3", "P3v3"], var.app_service_plan_sku)
    error_message = "Must be a valid App Service Plan SKU."
  }
}

# ── Event Grid ────────────────────────────────────────────────────────────────

variable "eg_max_events_per_batch" {
  type    = number
  default = 10
}

variable "eg_preferred_batch_size_kb" {
  type    = number
  default = 64
}

# ── Loader Tuning ─────────────────────────────────────────────────────────────

variable "max_retries" {
  type        = number
  default     = 3
  description = "Max retry attempts on 429 / 503 responses"
}

variable "throttle_delay_ms" {
  type        = number
  default     = 500
  description = "Base delay ms for exponential back-off"
}

variable "max_bundle_size" {
  type        = number
  default     = 500
  description = "Max resources per FHIR transaction bundle"
}

variable "parallel_patients" {
  type        = number
  default     = 10
  description = "Concurrent patients during bulk export"
}

# ── Observability ─────────────────────────────────────────────────────────────

variable "log_retention_days" {
  type    = number
  default = 30
}

variable "alert_email" {
  type        = string
  default     = ""
  description = "Ops alert email address. Leave empty to disable alerts."
}

# ── Deployment ────────────────────────────────────────────────────────────────

variable "existing_suffix" {
  type        = string
  default     = ""
  description = <<-EOT
    Pin the 6-character random suffix to match already-deployed resources.
    Set this to the suffix of your existing Function App name (the last 6 chars).
    Example: if your Function App is "fhirldr-prod-fn-nmgfhq", set this to "nmgfhq".
    Leave empty on a brand-new deployment — Terraform will generate one.
  EOT
}

variable "source_code_hash" {
  type        = string
  default     = "initial"
  description = "Change this value to force re-deployment of the function package (e.g. a git commit SHA)."
}

# ── Tags ──────────────────────────────────────────────────────────────────────

variable "tags" {
  type    = map(string)
  default = {}
}
