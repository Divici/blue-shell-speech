#!/usr/bin/env bash
# Provisions the compute + data platform for Blue Shell Speech.
# Idempotent — safe to re-run. Requires `az login`.
#
# Pairs with provision-ai.sh, which covers Azure OpenAI and Azure Speech.
#
# Targets ONE subscription at a time. Development and production are separate
# subscriptions (DECISIONS.md D025) — the dev subscription is structurally incapable of
# holding PHI, because PHI only ever exists in production. Set SUBSCRIPTION explicitly.

set -euo pipefail

SUBSCRIPTION="${SUBSCRIPTION:-}"
RG="${RG:-blueShellRG}"
LOCATION="${LOCATION:-eastus}"
ENVIRONMENT="${ENVIRONMENT:-dev}"

ACA_ENV="${ACA_ENV:-blueshell-${ENVIRONMENT}-env}"
LOG_WORKSPACE="${LOG_WORKSPACE:-blueshell-${ENVIRONMENT}-logs}"
STORAGE="${STORAGE:-blueshell${ENVIRONMENT}storage}"
SQL_SERVER="${SQL_SERVER:-blueshell-${ENVIRONMENT}-sql}"
SQL_DB="${SQL_DB:-BlueShell}"

say() { printf '\n==> %s\n' "$1"; }

if [[ -n "$SUBSCRIPTION" ]]; then
  az account set --subscription "$SUBSCRIPTION"
fi
say "Subscription: $(az account show --query name -o tsv)"

say "Registering resource providers (no-op once done)"
for ns in Microsoft.App Microsoft.OperationalInsights Microsoft.Sql Microsoft.Storage; do
  az provider register --namespace "$ns" --wait --only-show-errors || true
done

say "Resource group ${RG} (${LOCATION})"
az group create --name "$RG" --location "$LOCATION" --output none

# ---------------------------------------------------------------------------
# Log Analytics — required by Container Apps for its log destination.
# Free tier covers 5 GB/month ingestion, far beyond a solo practice.
# No PHI reaches here: structured logs carry IDs and correlation IDs only
# (docs/SECURITY.md).
# ---------------------------------------------------------------------------
say "Log Analytics workspace ${LOG_WORKSPACE}"
az monitor log-analytics workspace create \
  --resource-group "$RG" --workspace-name "$LOG_WORKSPACE" \
  --location "$LOCATION" --output none

WORKSPACE_ID=$(az monitor log-analytics workspace show \
  --resource-group "$RG" --workspace-name "$LOG_WORKSPACE" \
  --query customerId -o tsv)
WORKSPACE_KEY=$(az monitor log-analytics workspace get-shared-keys \
  --resource-group "$RG" --workspace-name "$LOG_WORKSPACE" \
  --query primarySharedKey -o tsv)

# ---------------------------------------------------------------------------
# Container Apps environment.
#
# No container registry is provisioned. Images live in GitHub Container Registry,
# which is free for public repositories — Azure Container Registry Basic is ~$5/month
# for the same job, which is real money against a $0 target (DECISIONS.md D026).
# ---------------------------------------------------------------------------
say "Container Apps environment ${ACA_ENV}"
az containerapp env create \
  --name "$ACA_ENV" --resource-group "$RG" --location "$LOCATION" \
  --logs-workspace-id "$WORKSPACE_ID" --logs-workspace-key "$WORKSPACE_KEY" \
  --output none

# ---------------------------------------------------------------------------
# Storage: session audio and public resource documents in SEPARATE containers.
# Clinical audio and parent handouts must never share access rules
# (docs/THREAT_MODEL.md boundary 4).
# ---------------------------------------------------------------------------
say "Storage account ${STORAGE}"
az storage account create \
  --name "$STORAGE" --resource-group "$RG" --location "$LOCATION" \
  --sku Standard_LRS --kind StorageV2 \
  --min-tls-version TLS1_2 --allow-blob-public-access false \
  --output none

for container in session-audio public-resources; do
  az storage container create \
    --name "$container" --account-name "$STORAGE" \
    --auth-mode login --output none 2>/dev/null || true
done

say "Done. Current state:"
az resource list --resource-group "$RG" \
  --query "[].{name:name, type:type, location:location}" --output table

cat <<'NOTE'

Next:
  - SQL Server + free-offer database (separate step; the free offer must be selected
    at database creation and cannot be applied afterwards).
  - Federated credentials for GitHub OIDC (infra/provision-github-oidc.sh).

Nothing here reads or stores a key. Container Apps authenticates to storage and SQL
with managed identity; run this once the apps exist:

  az containerapp identity assign --name <app> -g <rg> --system-assigned
NOTE
