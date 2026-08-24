#!/usr/bin/env bash
# Azure SQL server + free-offer database. Idempotent. Requires `az login`.
#
# TWO THINGS HERE ARE NOT OPTIONAL:
#
# 1. Microsoft Entra-only authentication. No SQL login, no password, anywhere — not in
#    Key Vault, not in an environment variable, not in a connection string. A credential
#    that does not exist cannot be leaked, and this repository is public
#    (docs/SECURITY.md, CLAUDE.md non-negotiable #7).
#
# 2. The free offer must be selected AT DATABASE CREATION. It cannot be applied to an
#    existing database. Getting this wrong means dropping and recreating.

set -euo pipefail

RG="${RG:-blueShellRG}"
ENVIRONMENT="${ENVIRONMENT:-dev}"

# ---------------------------------------------------------------------------
# Region: centralus, NOT eastus, and not by choice.
#
# East US is refusing new SQL server provisioning ("RegionDoesNotAllowProvisioning"),
# as are eastus2 and westus2. Probed 2026-08-24: centralus and westus3 were the
# available options, and centralus is the closer of the two to a Maryland practice.
#
# Cost: compute is in eastus, so every database round trip crosses a region
# (~25ms) and inter-region egress applies. Acceptable at this traffic level; revisit
# for production, which is a separate subscription and may find eastus capacity.
# ---------------------------------------------------------------------------
LOCATION="${LOCATION:-centralus}"
SQL_SERVER="${SQL_SERVER:-blueshell-${ENVIRONMENT}-sql-cus}"
SQL_DB="${SQL_DB:-BlueShell}"

# Entra admin — the human who administers the server. Defaults to the signed-in user.
ADMIN_NAME="${ADMIN_NAME:-$(az ad signed-in-user show --query userPrincipalName -o tsv)}"
ADMIN_SID="${ADMIN_SID:-$(az ad signed-in-user show --query id -o tsv)}"

say() { printf '\n==> %s\n' "$1"; }

say "SQL server ${SQL_SERVER} (Entra-only auth, no SQL login)"
az sql server create \
  --name "$SQL_SERVER" --resource-group "$RG" --location "$LOCATION" \
  --enable-ad-only-auth \
  --external-admin-principal-type User \
  --external-admin-name "$ADMIN_NAME" \
  --external-admin-sid "$ADMIN_SID" \
  --minimal-tls-version 1.2 \
  --output none

# ---------------------------------------------------------------------------
# Free offer: 100,000 vCore-seconds + 32 GB data + 32 GB backup per month, for the
# lifetime of the subscription.
#
# AutoPause on exhaustion, NOT BillOverUsage. For a solo practice a surprise invoice is
# worse than a pause, and the capacity banner plus admin alerts (presearch §13) mean a
# pause is never a surprise. This is a deliberate choice, not a default.
# ---------------------------------------------------------------------------
say "Database ${SQL_DB} (free offer, auto-pause on limit)"
if az sql db show --name "$SQL_DB" --server "$SQL_SERVER" --resource-group "$RG" \
     --output none 2>/dev/null; then
  echo "    already exists — leaving alone (the free offer cannot be applied retroactively)"
else
  az sql db create \
    --name "$SQL_DB" --server "$SQL_SERVER" --resource-group "$RG" \
    --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 2 \
    --use-free-limit --free-limit-exhaustion-behavior AutoPause \
    --backup-storage-redundancy Local \
    --output none
fi

# ---------------------------------------------------------------------------
# Network.
#
# Public access stays ON only while there is no VNet-integrated Container Apps
# environment to reach it privately, and only on the dev subscription, which never holds
# PHI (DECISIONS.md D025). Production requires a private endpoint with public access
# disabled — docs/THREAT_MODEL.md boundary 3.
# ---------------------------------------------------------------------------
say "Allowing Azure services (dev only — production uses a private endpoint)"
az sql server firewall-rule create \
  --name AllowAzureServices --server "$SQL_SERVER" --resource-group "$RG" \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 \
  --output none 2>/dev/null || true

say "Done."
az sql db show --name "$SQL_DB" --server "$SQL_SERVER" --resource-group "$RG" \
  --query "{db:name, sku:sku.name, tier:sku.tier, freeLimit:useFreeLimit, onExhaustion:freeLimitExhaustionBehavior, autoPauseMin:autoPauseDelay}" \
  --output table

cat <<NOTE

Connection string (no password — managed identity):
  Server=tcp:${SQL_SERVER}.database.windows.net,1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;

Grant the API's managed identity access once the container app exists:
  CREATE USER [<app-name>] FROM EXTERNAL PROVIDER;
  ALTER ROLE db_datareader ADD MEMBER [<app-name>];
  ALTER ROLE db_datawriter ADD MEMBER [<app-name>];

Deliberately NOT granted: db_owner, and any UPDATE/DELETE on AuditEvent
(docs/SECURITY.md — the audit log is append-only).
NOTE
