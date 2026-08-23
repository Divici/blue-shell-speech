#!/usr/bin/env bash
# Provisions the AI half of Blue Shell Speech: Azure OpenAI + Azure Speech.
# Idempotent — safe to re-run. Requires `az login` first.
#
# Why this exists: portal clicks leave no artifact. A blown-away resource group
# should be a re-run, not an archaeology exercise (presearch §12.3).

set -euo pipefail

RG="${RG:-blueShellRG}"
LOCATION="${LOCATION:-eastus}"
OPENAI_ACCOUNT="${OPENAI_ACCOUNT:-blueshellOpenAI}"
SPEECH_ACCOUNT="${SPEECH_ACCOUNT:-blueshellSpeech}"

# ---------------------------------------------------------------------------
# Model deployment
#
# DEV ONLY. GlobalStandard routes requests to any Microsoft region worldwide,
# which is acceptable here solely because development is synthetic-data-only
# (presearch §22). No PHI may touch this deployment.
#
# It is also the only option available: on a fresh subscription every
# DataZoneStandard and regional Standard quota for the gpt-5 family is 0.
# `OpenAI.GlobalStandard.gpt-5-mini` (500K TPM) is the sole non-zero grant.
#
# Before go-live, request DataZoneStandard quota and redeploy. See
# docs/PRELAUNCH_BLOCKERS.md.
# ---------------------------------------------------------------------------
MODEL_NAME="${MODEL_NAME:-gpt-5-mini}"
MODEL_VERSION="${MODEL_VERSION:-2025-08-07}"
DEPLOYMENT_NAME="${DEPLOYMENT_NAME:-gpt-5-mini-global}"
DEPLOYMENT_SKU="${DEPLOYMENT_SKU:-GlobalStandard}"
DEPLOYMENT_CAPACITY="${DEPLOYMENT_CAPACITY:-50}"

say() { printf '\n==> %s\n' "$1"; }

say "Resource group ${RG} (${LOCATION})"
az group create --name "$RG" --location "$LOCATION" --output none

say "Azure OpenAI account ${OPENAI_ACCOUNT}"
az cognitiveservices account create \
  --name "$OPENAI_ACCOUNT" --resource-group "$RG" --location "$LOCATION" \
  --kind OpenAI --sku S0 --custom-domain "$OPENAI_ACCOUNT" --yes --output none

say "Azure Speech account ${SPEECH_ACCOUNT} (free F0 tier)"
# F0 is limited to one instance per subscription per region. Transcription runs
# here rather than through Azure OpenAI's audio models, which are NOT
# HIPAA-eligible under Microsoft's BAA — only text endpoints are.
az cognitiveservices account create \
  --name "$SPEECH_ACCOUNT" --resource-group "$RG" --location "$LOCATION" \
  --kind SpeechServices --sku F0 --yes --output none

say "Model deployment ${DEPLOYMENT_NAME} (${MODEL_NAME} ${MODEL_VERSION}, ${DEPLOYMENT_SKU})"
az cognitiveservices account deployment create \
  --name "$OPENAI_ACCOUNT" --resource-group "$RG" \
  --deployment-name "$DEPLOYMENT_NAME" \
  --model-name "$MODEL_NAME" --model-version "$MODEL_VERSION" --model-format OpenAI \
  --sku-name "$DEPLOYMENT_SKU" --sku-capacity "$DEPLOYMENT_CAPACITY" --output none

say "Done. Current state:"
az cognitiveservices account deployment list \
  --name "$OPENAI_ACCOUNT" --resource-group "$RG" \
  --query "[].{deployment:name, model:properties.model.name, sku:sku.name, capacity:sku.capacity}" \
  --output table

cat <<'NOTE'

Endpoints (not secrets — safe in config):
  OpenAI  https://blueshellopenai.openai.azure.com/
  Speech  https://eastus.api.cognitive.microsoft.com/

Keys are deliberately not read by this script. Auth is via managed identity;
run `az cognitiveservices account update --name <acct> -g <rg> --custom-properties
disableLocalAuth=true` once identity is wired.

Useful when quota changes:
  az cognitiveservices usage list -l eastus -o table
NOTE
