#!/usr/bin/env bash
# GitHub Actions → Azure authentication via OIDC federated identity.
# Idempotent. Requires `az login` and `gh auth login`.
#
# WHY OIDC AND NOT A SERVICE-PRINCIPAL SECRET:
#
# A client secret in GitHub is a long-lived credential that can deploy to Azure. It has to
# be rotated, it appears in every fork discussion about repository access, and if it leaks
# it is valid until someone notices. OIDC issues a token per workflow run, scoped to this
# repository and this branch, valid for minutes.
#
# This repository is PUBLIC (docs/THREAT_MODEL.md boundary 8), which raises the stakes on
# every one of those points.

set -euo pipefail

RG="${RG:-blueShellRG}"
LOCATION="${LOCATION:-eastus}"
ENVIRONMENT="${ENVIRONMENT:-dev}"
IDENTITY="${IDENTITY:-blueshell-${ENVIRONMENT}-deploy}"
GH_REPO="${GH_REPO:-Divici/blue-shell-speech}"

say() { printf '\n==> %s\n' "$1"; }

SUBSCRIPTION_ID=$(az account show --query id -o tsv)
TENANT_ID=$(az account show --query tenantId -o tsv)

say "User-assigned managed identity ${IDENTITY}"
az identity create --name "$IDENTITY" --resource-group "$RG" --location "$LOCATION" \
  --output none

CLIENT_ID=$(az identity show --name "$IDENTITY" --resource-group "$RG" --query clientId -o tsv)
PRINCIPAL_ID=$(az identity show --name "$IDENTITY" --resource-group "$RG" --query principalId -o tsv)

# ---------------------------------------------------------------------------
# Federated credentials, scoped as narrowly as the workflow allows.
#
# Only pushes to main and the `production` environment can obtain a token. Pull requests
# deliberately get NO credential — a fork PR must never be able to authenticate to Azure.
# ---------------------------------------------------------------------------
add_federated() {
  local name="$1" subject="$2"
  say "Federated credential ${name} (${subject})"
  az identity federated-credential create \
    --name "$name" --identity-name "$IDENTITY" --resource-group "$RG" \
    --issuer "https://token.actions.githubusercontent.com" \
    --subject "$subject" \
    --audiences "api://AzureADTokenExchange" \
    --output none 2>/dev/null || echo "    already exists"
}

add_federated "github-main"        "repo:${GH_REPO}:ref:refs/heads/main"
add_federated "github-env-prod"    "repo:${GH_REPO}:environment:production"

# ---------------------------------------------------------------------------
# RBAC. Contributor on the resource group only — not the subscription, and not Owner.
# The deploy identity needs to update container apps; it has no business creating
# role assignments or reading Key Vault contents.
# ---------------------------------------------------------------------------
#
# Created via the REST API, not `az role assignment create`.
#
# On this tenant that command fails with "MissingSubscription" even when the scope is
# fully qualified — it appears to resolve the role definition at tenant scope. The REST
# call is explicit about both the subscription and the role definition ID, and works.
#
# It is also NOT wrapped in `|| true`. An earlier version suppressed the error and
# printed "already assigned", which reported success for a deploy identity that in fact
# had no permissions at all. A provisioning script that lies is worse than one that fails.
say "Role assignment: Contributor on ${RG}"
CONTRIBUTOR_ROLE_ID="b24988ac-6180-42a0-ab88-20f7382dd24c"
SCOPE="/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RG}"

EXISTING=$(az role assignment list --subscription "$SUBSCRIPTION_ID" --all \
  --query "[?principalId=='${PRINCIPAL_ID}' && roleDefinitionName=='Contributor'] | length(@)" \
  -o tsv | tr -d '\r\n')

if [[ "$EXISTING" == "0" ]]; then
  ASSIGNMENT_GUID=$(python -c "import uuid;print(uuid.uuid4())" 2>/dev/null \
    || powershell -NoProfile -Command "[guid]::NewGuid().ToString()" | tr -d '\r\n')

  az rest --method put \
    --url "https://management.azure.com${SCOPE}/providers/Microsoft.Authorization/roleAssignments/${ASSIGNMENT_GUID}?api-version=2022-04-01" \
    --body "{\"properties\":{\"roleDefinitionId\":\"/subscriptions/${SUBSCRIPTION_ID}/providers/Microsoft.Authorization/roleDefinitions/${CONTRIBUTOR_ROLE_ID}\",\"principalId\":\"${PRINCIPAL_ID}\",\"principalType\":\"ServicePrincipal\"}}" \
    --output none
  echo "    assigned"
else
  echo "    already assigned (${EXISTING})"
fi

# Verify rather than assume. This is the check that would have caught the masked failure.
VERIFIED=$(az role assignment list --subscription "$SUBSCRIPTION_ID" --all \
  --query "[?principalId=='${PRINCIPAL_ID}'] | length(@)" -o tsv | tr -d '\r\n')
if [[ "$VERIFIED" == "0" ]]; then
  echo "FATAL: deploy identity has no role assignment. Deployments would fail." >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# Publish the non-secret identifiers to GitHub.
#
# Client ID, tenant ID, and subscription ID are identifiers, not credentials — none of
# them authenticates anything on its own. They are stored as secrets anyway: on a public
# repository there is no reason to hand a scanner a free map of the tenant.
# ---------------------------------------------------------------------------
if command -v gh >/dev/null 2>&1; then
  say "Setting GitHub Actions secrets on ${GH_REPO}"
  gh secret set AZURE_CLIENT_ID       --repo "$GH_REPO" --body "$CLIENT_ID"
  gh secret set AZURE_TENANT_ID       --repo "$GH_REPO" --body "$TENANT_ID"
  gh secret set AZURE_SUBSCRIPTION_ID --repo "$GH_REPO" --body "$SUBSCRIPTION_ID"
else
  cat <<EOF

gh CLI not found. Set these repository secrets manually:
  AZURE_CLIENT_ID        ${CLIENT_ID}
  AZURE_TENANT_ID        ${TENANT_ID}
  AZURE_SUBSCRIPTION_ID  ${SUBSCRIPTION_ID}
EOF
fi

say "Done."
az identity federated-credential list --identity-name "$IDENTITY" --resource-group "$RG" \
  --query "[].{name:name, subject:subject}" --output table
