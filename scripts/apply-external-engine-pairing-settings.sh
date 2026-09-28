#!/usr/bin/env bash
set -euo pipefail

# Apply the cleaned pairing allowlist to App Service, delete stale indexed
# settings, and refuse a live list that contains the Hosted smoke owner org.
# Prints only counts. Never echoes organization ids.

PREFIX='ElsaControl__ExternalEngines__PairingAllowedOrganizationIds__'
TARGET_ENVIRONMENT="${TARGET_ENVIRONMENT:-}"
ALLOWLIST="${EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS:-}"
SMOKE_OWNER_ORG_ID="${STAGING_SMOKE_OWNER_ORGANIZATION_ID:-}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required.}"
WEBAPP_NAME="${AZURE_WEBAPP_NAME:?AZURE_WEBAPP_NAME is required.}"

pairing_guid_pattern='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
expected_ids=()
if [ -n "$ALLOWLIST" ]; then
  if [ "$TARGET_ENVIRONMENT" != "test" ]; then
    echo "::error::EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS must be unset for ${TARGET_ENVIRONMENT:-unknown}; production ships an empty pairing allowlist."
    exit 1
  fi
  IFS=',' read -r -a pairing_entries <<< "$ALLOWLIST"
  for entry in "${pairing_entries[@]}"; do
    trimmed="${entry#"${entry%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    if [ -z "$trimmed" ]; then
      continue
    fi
    expected_ids+=("$trimmed")
  done
fi
expected_count=${#expected_ids[@]}

if [ "$TARGET_ENVIRONMENT" != "test" ] && [ "$expected_count" -gt 0 ]; then
  echo "::error::EXTERNAL_ENGINE_PAIRING_ALLOWED_ORG_IDS must be unset for ${TARGET_ENVIRONMENT:-unknown}; production ships an empty pairing allowlist."
  exit 1
fi

list_pairing_settings() {
  local query="$1"
  az webapp config appsettings list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --query "$query" \
    --output tsv \
    --only-show-errors
}

if [ -n "${PAIRING_BEFORE_COUNT:-}" ]; then
  before_count="$PAIRING_BEFORE_COUNT"
else
  before_count="$(list_pairing_settings "[?starts_with(name, '${PREFIX}')] | length(@)")"
  before_count="${before_count:-0}"
fi

existing_names="$(list_pairing_settings "[?starts_with(name, '${PREFIX}')].name" || true)"
stale_names=()
while IFS= read -r name; do
  [ -z "$name" ] && continue
  keep=false
  if [ "$expected_count" -gt 0 ]; then
    for ((index = 0; index < expected_count; index++)); do
      if [ "$name" = "${PREFIX}${index}" ]; then
        keep=true
        break
      fi
    done
  fi
  if [ "$keep" = false ]; then
    stale_names+=("$name")
  fi
done <<< "$existing_names"

if [ "${#stale_names[@]}" -gt 0 ]; then
  az webapp config appsettings delete \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --setting-names "${stale_names[@]}" \
    --output none \
    --only-show-errors
  echo "Deleted ${#stale_names[@]} stale pairing allowlist app setting(s)."
fi

if [ "$expected_count" -gt 0 ]; then
  pairing_settings=()
  for ((index = 0; index < expected_count; index++)); do
    pairing_settings+=("${PREFIX}${index}=${expected_ids[$index]}")
  done
  az webapp config appsettings set \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --settings "${pairing_settings[@]}" \
    --output none \
    --only-show-errors
fi

after_count="$(list_pairing_settings "[?starts_with(name, '${PREFIX}')] | length(@)")"
after_count="${after_count:-0}"
if [ "$after_count" != "$expected_count" ]; then
  echo "::error::Pairing allowlist app setting count after deploy was ${after_count}; expected ${expected_count}."
  exit 1
fi
if [ "$TARGET_ENVIRONMENT" != "test" ] && [ "$after_count" != "0" ]; then
  echo "::error::Pairing allowlist app setting count after deploy was ${after_count}; production must stay empty."
  exit 1
fi

if [ "$TARGET_ENVIRONMENT" = "test" ] && [ "$after_count" -gt 0 ]; then
  if [ -z "$SMOKE_OWNER_ORG_ID" ]; then
    echo "::error::STAGING_SMOKE_OWNER_ORGANIZATION_ID must be set so live pairing settings can be checked against the Hosted smoke owner organization."
    exit 1
  fi
  if [[ ! "$SMOKE_OWNER_ORG_ID" =~ $pairing_guid_pattern ]] || [ "$SMOKE_OWNER_ORG_ID" = "00000000-0000-0000-0000-000000000000" ]; then
    echo "::error::STAGING_SMOKE_OWNER_ORGANIZATION_ID must be a GUID organization id."
    exit 1
  fi
  live_values="$(list_pairing_settings "[?starts_with(name, '${PREFIX}')].value" || true)"
  smoke_normalized="${SMOKE_OWNER_ORG_ID,,}"
  while IFS= read -r value; do
    [ -z "$value" ] && continue
    if [ "${value,,}" = "$smoke_normalized" ]; then
      echo "::error::Live pairing allowlist includes the staging Hosted smoke owner organization."
      exit 1
    fi
  done <<< "$live_values"
fi

echo "Pairing allowlist app setting count: before=${before_count} after=${after_count}."
