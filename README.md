# FHIR Bulk Loader & Export

An **Azure Function App** solution for high-speed ingestion and patient-centric export of FHIR R4 data into **Azure Health Data Services**.  Deploy it to your tenant with **Terraform** and ship it to GitHub with one script.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│  Azure Storage Account (loader)                                      │
│                                                                      │
│  ┌──────────┐  ┌────────┐  ┌─────┐  ┌────────┐ ┌──────┐ ┌───────┐ │
│  │ /bundles │  │/ndjson │  │/zip │  │/export │ │/audit│ │/errors│ │
│  └────┬─────┘  └───┬────┘  └──┬──┘  └────────┘ └──────┘ └───────┘ │
└───────┼────────────┼──────────┼──────────────────────────────────── ┘
        │            │          │   BlobCreated events
        ▼            ▼          ▼
┌───────────────────────────────────────────────┐
│  Event Grid System Topic                       │
│  (3 subscriptions — one per container)         │
└──────────────────────────────────────────────-┘
        │            │          │
        ▼            ▼          ▼
┌───────────────────────────────────────────────┐
│  Azure Function App (Windows / .NET 6)         │
│                                                │
│  ImportBundleEventGrid  — splits & POSTs JSON  │
│  ImportNDJSONEventGrid  — streams NDJSON lines │
│  ImportZIPEventGrid     — decompresses & fans  │
│  RetryProcessor         — queue-based retry    │
│  AltExportTrigger  ─────────────────────────── │──► POST /$alt-export
│  ExportOrchestrator     — Durable orchestrator │
│  GatherPatientIds       — pages FHIR search    │
│  ExportPatientResources — parallel fan-out     │
│  WriteExportManifest    — _completed_run.xjson │
└──────────────┬────────────────────────────────-┘
               │ HTTPS + Bearer token (MSI or client creds)
               ▼
┌─────────────────────────────────────────────────┐
│  Azure Health Data Services — FHIR R4 Service   │
└─────────────────────────────────────────────────┘
```

---

## Repository Layout

```
fhir-bulk-loader/
├── src/FHIRBulkImport/
│   ├── FHIRBulkImport.csproj         # .NET 6 Azure Functions v4 project
│   ├── host.json                      # concurrency, timeout settings
│   ├── local.settings.json            # local dev config (not deployed)
│   ├── FHIRUtils.cs                   # FHIR HTTP client, token cache, retry policy
│   ├── StorageUtils.cs                # Blob, Queue, Audit, Error helpers
│   ├── ImportBundleEventGrid.cs       # FHIR Bundle importer
│   ├── ImportNDJSONEventGrid.cs       # NDJSON importer
│   ├── ImportZIPEventGrid.cs          # ZIP importer
│   ├── RetryProcessor.cs              # Queue-triggered retry
│   └── ExportOrchestrator.cs          # Durable export orchestration
├── terraform/
│   ├── main.tf                        # All Azure resources
│   ├── variables.tf                   # Input variable definitions
│   ├── outputs.tf                     # Useful output values
│   └── terraform.tfvars.example       # Copy → terraform.tfvars
├── .github/workflows/
│   └── deploy.yml                     # Build → Plan → Apply → Deploy
├── scripts/
│   └── push-to-github.sh              # One-shot GitHub repo creation
└── docs/
    └── export-query-examples.json     # Sample $alt-export payloads
```

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Azure Subscription | Contributor role |
| Azure Health Data Services FHIR Service | Already deployed |
| Azure Storage Account | Already deployed (used by FHIR service) |
| Terraform ≥ 1.5 | `brew install terraform` or [tfenv](https://github.com/tfutils/tfenv) |
| .NET 6 SDK | `brew install dotnet` / [download](https://dotnet.microsoft.com/download) |
| Azure CLI | `az login` before running Terraform |
| GitHub CLI (optional) | `brew install gh && gh auth login` |

---

## Quick Start

### 1 — Clone / initialise locally

```bash
git clone https://github.com/vruppuluri/fhir-bulk-loader
cd fhir-bulk-loader
```

### 2 — Configure Terraform

```bash
cp terraform/terraform.tfvars.example terraform/terraform.tfvars
# Edit terraform/terraform.tfvars with your values
```

Minimum required values:

```hcl
subscription_id          = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
fhir_service_url         = "https://<workspace>-<svc>.fhir.azurehealthcareapis.com"
fhir_service_resource_id = "/subscriptions/.../fhirservices/<svc>"
```

### 3 — Deploy infrastructure

```bash
cd terraform
az login
terraform init
terraform plan -out=plan1  # review changes
terraform apply plan1
```

Terraform outputs the Function App name, storage account, and the export endpoint.

### 4 — Build & deploy the Function App

```bash
cd src/FHIRBulkImport
dotnet publish -c Release -o ../../publish
cd ../../publish && zip -r ../function-app.zip .
cd ..

# Replace <funcapp> with the terraform output value
az functionapp deployment source config-zip \
  --resource-group <rg-name> \
  --name <funcapp-name> \
  --src function-app.zip
```

Or just push to `main` and let GitHub Actions handle it (see § CI/CD below).

---

## Importing FHIR Data

Upload files directly to the storage account containers.  
Event Grid fires automatically on blob creation.

| Container | Accepts | Function triggered |
|---|---|---|
| `bundles` | FHIR Bundle JSON (`.json`) | `ImportBundleEventGrid` |
| `ndjson`  | NDJSON / JSONL (`.ndjson`, `.jsonl`) | `ImportNDJSONEventGrid` |
| `zip`     | ZIP archive of any of the above | `ImportZIPEventGrid` |

**Azure Storage Explorer** or **azcopy** are the fastest options for large loads:

```bash
azcopy copy './synthea-output/*.json' \
  "https://<storage>.blob.core.windows.net/bundles?<SAS>" \
  --recursive
```

---

## Patient-centric Bulk Export (`$alt-export`)

### Step 1 — Build a query definition

```json
{
  "query": "Patient?birthdate=lt1970-01-01&_count=50",
  "patientReferenceField": "id",
  "include": [
    "Patient?_id=$IDS&_count=50",
    "Encounter?patient=$IDS&_count=50",
    "Condition?patient=$IDS&_count=50",
    "Observation?patient=$IDS&_count=50"
  ]
}
```

`$IDS` is replaced with each patient's logical ID at runtime.

### Step 2 — Get your function key

```bash
az functionapp keys list \
  --resource-group <rg> \
  --name <funcapp> \
  --query "functionKeys.default" -o tsv
```

### Step 3 — Trigger the export

```bash
curl -X POST \
  "https://<funcapp>.azurewebsites.net/api/\$alt-export?code=<key>" \
  -H "Content-Type: application/json" \
  -d @query.json
```

Response:

```json
{
  "id": "<instanceId>",
  "statusQueryGetUri": "https://...",
  "terminatePostUri": "https://..."
}
```

### Step 4 — Poll status

```bash
curl "<statusQueryGetUri>"
```

Results are written to `export/<instanceId>/*.xndjson` and a manifest at `export/<instanceId>/_completed_run.xjson`.

---

## Audit & Error Logs

| Container | Content |
|---|---|
| `audit/YYYY/MM/DD/<correlationId>.json` | Per-file import summary (resources, errors, status) |
| `errors/YYYY/MM/DD/<correlationId>-error.json` | Raw FHIR error response |
| `retry/<instanceId>/` | Poisoned messages after max retries |

Application Insights captures all function logs with full distributed tracing.

---

## Tuning

| App Setting | Default | Description |
|---|---|---|
| `FBI-MAXBUNDLESIZE` | 500 | Resources per FHIR transaction bundle |
| `FBI-MAXRETRIES` | 3 | Retry attempts on 429/503 |
| `FBI-THROTTLE-DELAY` | 500 | Base delay ms (exponential back-off) |
| `FBI-PARALLELPATIENTS` | 10 | Concurrent patients in export fan-out |
| `app_service_plan_sku` | P2v3 | Scale up for higher concurrency |

---

## CI/CD — GitHub Actions

### Required GitHub Secrets

| Secret | Value |
|---|---|
| `AZURE_CREDENTIALS` | `az ad sp create-for-rbac --sdk-auth` output |
| `ARM_CLIENT_ID` | Service principal app ID |
| `ARM_CLIENT_SECRET` | Service principal secret |
| `ARM_SUBSCRIPTION_ID` | Azure subscription ID |
| `ARM_TENANT_ID` | Azure tenant ID |
| `FHIR_SERVICE_URL` | Your FHIR service URL |
| `FHIR_RESOURCE_ID` | ARM resource ID of FHIR service |
| `ALERT_EMAIL` | Ops email for alerts (optional) |

### Create the service principal

```bash
az ad sp create-for-rbac \
  --name "fhir-bulk-loader-cicd" \
  --role Contributor \
  --scopes /subscriptions/<sub-id> \
  --sdk-auth
```

Copy the JSON output as `AZURE_CREDENTIALS`.

### Workflow summary

| Trigger | Job |
|---|---|
| Pull request to `main` | Build + `terraform plan` |
| Push to `main` | Build + `terraform apply` + deploy zip |

---

## Push to GitHub

From the project root (first time only):

```bash
# Option A — GitHub CLI
gh auth login
bash scripts/push-to-github.sh

# Option B — PAT
export GITHUB_TOKEN=ghp_xxxxxxxxxxxx
bash scripts/push-to-github.sh
```

---

## Security Notes

- **Managed Identity is preferred** — leave `fhir_client_id` and `fhir_client_secret` blank in `terraform.tfvars`.  
  Terraform assigns `FHIR Data Contributor` to the Function App's system identity automatically.
- All secrets are stored in **Key Vault** and referenced via `@Microsoft.KeyVault(...)` app settings.
- `terraform.tfvars` is in `.gitignore` — never commit it.
- The storage account enforces **TLS 1.2** and **private access only**.

---

## License

MIT — same as the upstream [microsoft/fhir-loader](https://github.com/microsoft/fhir-loader).
