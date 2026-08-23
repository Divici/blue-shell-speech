#!/bin/bash
# Blocks writes to secret-bearing files.
# This repo is public (CLAUDE.md non-negotiable #7) — a committed secret is a real incident,
# not a cleanup task. Rotating a leaked key is worse than being told "no" here.
#
# No jq: it is not present in this environment, and a hook that fails open is worse than
# no hook at all. Pure bash + sed only.
#
# Bypass: the human edits the file directly. Agents do not get a bypass flag on purpose.

INPUT=$(cat)

# Pull "file_path":"..." out of the JSON payload, honouring backslash escapes.
FILE_PATH=$(printf '%s' "$INPUT" \
  | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\(\([^"\\]\|\\.\)*\)".*/\1/p')

# Fail closed: if the field could not be parsed, scan the whole payload instead of
# waving the call through.
SCAN="${FILE_PATH:-$INPUT}"

# Basename, tolerating both / and \ separators (JSON-escaped Windows paths arrive as \\).
BASE="${SCAN//\\\\//}"
BASE="${BASE//\\//}"
BASE="${BASE##*/}"
LOWER=$(printf '%s' "$BASE" | tr '[:upper:]' '[:lower:]')

# Templates and examples are meant to be committed — allow them through.
case "$LOWER" in
  *.example|*.example.*|*.template|*.template.*|*.sample|*.sample.*|*.dist) exit 0 ;;
esac

BLOCKED=""
case "$LOWER" in
  .env|.env.*|*.env)                              BLOCKED="environment file" ;;
  *.pfx|*.p12|*.pem|*.key|*.jks|*.keystore)       BLOCKED="certificate or private key" ;;
  *credential*|*secret*|*.publishsettings)        BLOCKED="credential file" ;;
  id_rsa*|id_ed25519*)                            BLOCKED="SSH private key" ;;
esac

[ -z "$BLOCKED" ] && exit 0

cat >&2 <<EOF
BLOCKED: refusing to write '${FILE_PATH:-$BASE}' — matches protected pattern ($BLOCKED).

This repository is public. Secrets belong in:
  - .NET local dev ...... dotnet user-secrets set "Key" "value"
  - Next.js local dev ... .env.local (gitignored, never written by an agent)
  - Deployed ............ Azure Key Vault / Container Apps secrets
  - CI .................. GitHub Actions secrets

If you need a committed placeholder, write '$BASE.example' with empty values instead.
EOF
exit 2
