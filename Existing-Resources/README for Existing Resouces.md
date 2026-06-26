# FHIR Bulk Loader - Script to run after you have existing resources.

To import all existing resources into Terraform state and lock the suffix 

Three files changed. Here's exactly what to do:

1 — Replace the three files in your repo
terraform/main.tf          ← updated locals block
terraform/variables.tf     ← new existing_suffix variable
scripts/import-existing.sh ← new script (make executable)

2 — Run the import script once (this is the key step — it reads your actual deployed resource names, locks the suffix in terraform.tfvars, then imports every resource into Terraform state):
cd ~/fhir-bulk-loader
chmod +x scripts/import-existing.sh
bash scripts/import-existing.sh

The script auto-detects your actual Function App name and suffix, so you don't need to know it in advance. At the end it runs terraform plan — you want to see something close to 0 to add, 0 to destroy (there may be a few ~ to update for settings drift, which is fine).

3 — After the import, apply any pending changes
bash terraform -chdir=terraform apply

From this point on, terraform apply will update existing resources in-place rather than trying to recreate them, because the state file now knows they exist.
