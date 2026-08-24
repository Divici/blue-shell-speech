#!/usr/bin/env bash
# Creates the two container apps. Idempotent. Requires `az login`.
#
# Bootstrapped with a placeholder image: the apps must exist before CI can update them,
# but CI is what publishes the real images. Every deploy afterwards is
# `az containerapp update --image`.

set -euo pipefail

RG="${RG:-blueShellRG}"
LOCATION="${LOCATION:-eastus}"
ENVIRONMENT="${ENVIRONMENT:-dev}"
ACA_ENV="${ACA_ENV:-blueshell-${ENVIRONMENT}-env}"
WEB_APP="${WEB_APP:-blueshell-${ENVIRONMENT}-web}"
API_APP="${API_APP:-blueshell-${ENVIRONMENT}-api}"
PLACEHOLDER="mcr.microsoft.com/k8se/quickstart:latest"

say() { printf '\n==> %s\n' "$1"; }

exists() {
  az containerapp show --name "$1" --resource-group "$RG" --output none 2>/dev/null
}

# ---------------------------------------------------------------------------
# api — INTERNAL ingress.
#
# This is the security boundary, not an optimisation (docs/THREAT_MODEL.md boundary 2).
# There is no public route to the API at all: the only path to clinical data is through
# `web`, which means the authentication check cannot be bypassed by knowing a URL.
#
# minReplicas 0 — scale to zero. Cold start is the cost (DECISIONS.md D001) and is a
# measured deliverable, not an assumption.
# ---------------------------------------------------------------------------
if exists "$API_APP"; then
  say "api ${API_APP} already exists — leaving image alone (CI owns it)"
else
  say "api ${API_APP} (internal ingress, scale-to-zero)"
  az containerapp create \
    --name "$API_APP" --resource-group "$RG" --environment "$ACA_ENV" \
    --image "$PLACEHOLDER" \
    --ingress internal --target-port 80 --transport auto \
    --min-replicas 0 --max-replicas 3 \
    --cpu 0.25 --memory 0.5Gi \
    --system-assigned \
    --output none
fi

# ---------------------------------------------------------------------------
# web — EXTERNAL ingress. The only public surface.
# ---------------------------------------------------------------------------
if exists "$WEB_APP"; then
  say "web ${WEB_APP} already exists — leaving image alone (CI owns it)"
else
  say "web ${WEB_APP} (external ingress, scale-to-zero)"
  az containerapp create \
    --name "$WEB_APP" --resource-group "$RG" --environment "$ACA_ENV" \
    --image "$PLACEHOLDER" \
    --ingress external --target-port 80 --transport auto \
    --min-replicas 0 --max-replicas 3 \
    --cpu 0.25 --memory 0.5Gi \
    --system-assigned \
    --output none
fi

say "Granting the api identity access to storage"
API_PRINCIPAL=$(az containerapp show --name "$API_APP" --resource-group "$RG" \
  --query identity.principalId -o tsv | tr -d '\r\n')
SUBSCRIPTION_ID=$(az account show --query id -o tsv | tr -d '\r\n')
STORAGE="${STORAGE:-blueshell${ENVIRONMENT}storage}"

# Storage Blob Data Contributor — read/write blobs, no ability to manage the account.
# `az role assignment create` fails with MissingSubscription on this tenant; REST works.
BLOB_ROLE="ba92f5b4-2d11-453d-a403-e96b0029c9fe"
SCOPE="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}/providers/Microsoft.Storage/storageAccounts/${STORAGE}"

if [[ -n "$API_PRINCIPAL" ]]; then
  EXISTING=$(az role assignment list --subscription "$SUBSCRIPTION_ID" --all \
    --query "[?principalId=='${API_PRINCIPAL}'] | length(@)" -o tsv | tr -d '\r\n')
  if [[ "$EXISTING" == "0" ]]; then
    GUID=$(powershell -NoProfile -Command "[guid]::NewGuid().ToString()" 2>/dev/null | tr -d '\r\n')
    az rest --method put \
      --url "https://management.azure.com${SCOPE}/providers/Microsoft.Authorization/roleAssignments/${GUID}?api-version=2022-04-01" \
      --body "{\"properties\":{\"roleDefinitionId\":\"/subscriptions/${SUBSCRIPTION_ID}/providers/Microsoft.Authorization/roleDefinitions/${BLOB_ROLE}\",\"principalId\":\"${API_PRINCIPAL}\",\"principalType\":\"ServicePrincipal\"}}" \
      --output none
    echo "    assigned Storage Blob Data Contributor"
  else
    echo "    already assigned"
  fi
fi

say "Done."
az containerapp list --resource-group "$RG" \
  --query "[].{app:name, ingress:properties.configuration.ingress.external, fqdn:properties.configuration.ingress.fqdn, min:properties.template.scale.minReplicas}" \
  --output table

cat <<'NOTE'

`ingress.external = false` on the api is the point. Verify it independently:

  curl -sS https://<api-fqdn>/health/live     # must fail to resolve or connect

NOTE
