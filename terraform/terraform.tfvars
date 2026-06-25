# Copy this file to terraform.tfvars and fill in your values.
# DO NOT commit terraform.tfvars to source control.

subscription_id          = "2c2a512d-74b0-4d00-9cb1-4a5a1996b02d"
project_name             = "fhirldr"
environment              = "prod"
location                 = "westus2"


fhir_service_url         = "https://viuahdsws01-viuahdsws01.fhir.azurehealthcareapis.com"
fhir_service_resource_id = "/subscriptions/2c2a512d-74b0-4d00-9cb1-4a5a1996b02d/resourceGroups/viuhealth01/providers/Microsoft.HealthcareApis/workspaces/viuahdsws01/fhirservices/viuahdsws01"

# Leave client_id/secret empty to use Managed Identity (recommended)
fhir_client_id           = ""
fhir_client_secret       = ""

app_service_plan_sku     = "P2v3"
max_retries              = 3
throttle_delay_ms        = 500
max_bundle_size          = 500
parallel_patients        = 10
log_retention_days       = 30
alert_email              = "uppuluri_v@yahoo.com"
