#!/usr/bin/env bash
set -euo pipefail

# Apply the cleaned staging billing lifecycle lever settings to App Service,
# delete stale indexed allowlist entries, and refuse a live list that contains
# the Hosted smoke owner org. Prints only counts. Never echoes organization ids.

ENABLED_NAME='Billing__StagingLifecycleLever__Enabled'
PREFIX='Billing__StagingLifecycleLever__AllowedOrganizationIds__'
TARGET_ENVIRONMENT="${TARGET_ENVIRONMENT:-}"
ENABLED_RAW="${STAGING_BILLING_LIFECYCLE_LEVER_ENABLED:-}"
ALLOWLIST="${STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS:-}"
SMOKE_OWNER_ORG_ID="${STAGING_SMOKE_OWNER_ORGANIZATION_ID:-}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required.}"
WEBAPP_NAME="${AZURE_WEBAPP_NAME:?AZURE_WEBAPP_NAME is required.}"

lever_guid_pattern='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

enabled=false
if [ -n "$ENABLED_RAW" ]; then
  case "${ENABLED_RAW,,}" in
    true) enabled=true ;;
    false)
      ENABLED_RAW=""
      enabled=false
      ;;
    *)
      echo "::error::STAGING_BILLING_LIFECYCLE_LEVER_ENABLED must be true or false."
      exit 1
      ;;
  esac
fi

if { [ "$enabled" = true ] || [ -n "$ALLOWLIST" ]; } && [ "$TARGET_ENVIRONMENT" != "test" ]; then
  echo "::error::STAGING_BILLING_LIFECYCLE_LEVER_ENABLED and STAGING_BILLING_LIFECYCLE_LEVER_ALLOWED_ORG_IDS must be unset for ${TARGET_ENVIRONMENT:-unknown}; production ships the lever off."
  exit 1
fi

expected_ids=()
if [ -n "$ALLOWLIST" ]; then
  IFS=',' read -r -a lever_entries <<< "$ALLOWLIST"
  for entry in "${lever_entries[@]}"; do
    trimmed="${entry#"${entry%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    if [ -z "$trimmed" ]; then
      continue
    fi
    expected_ids+=("$trimmed")
  done
fi
expected_count=${#expected_ids[@]}

list_lever_settings() {
  local query="$1"
  az webapp config appsettings list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --query "$query" \
    --output tsv \
    --only-show-errors
}

if [ -n "${LEVER_BEFORE_COUNT:-}" ]; then
  before_count="$LEVER_BEFORE_COUNT"
else
  before_count="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}'] | length(@)")"
  before_count="${before_count:-0}"
fi

existing_names="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}'].name" || true)"
stale_names=()
while IFS= read -r name; do
  [ -z "$name" ] && continue
  keep=false
  if [ "$name" = "$ENABLED_NAME" ] && [ "$enabled" = true ]; then
    keep=true
  elif [ "$expected_count" -gt 0 ]; then
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
  echo "Deleted ${#stale_names[@]} stale staging billing lifecycle lever app setting(s)."
fi

settings=()
if [ "$enabled" = true ]; then
  settings+=("${ENABLED_NAME}=true")
fi
if [ "$expected_count" -gt 0 ]; then
  for ((index = 0; index < expected_count; index++)); do
    settings+=("${PREFIX}${index}=${expected_ids[$index]}")
  done
fi
if [ "${#settings[@]}" -gt 0 ]; then
  az webapp config appsettings set \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --settings "${settings[@]}" \
    --output none \
    --only-show-errors
fi

after_count="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}'] | length(@)")"
after_count="${after_count:-0}"
expected_after=0
if [ "$enabled" = true ]; then
  expected_after=$((expected_after + 1))
fi
expected_after=$((expected_after + expected_count))
if [ "$after_count" != "$expected_after" ]; then
  echo "::error::Staging billing lifecycle lever app setting count after deploy was ${after_count}; expected ${expected_after}."
  exit 1
fi
if [ "$TARGET_ENVIRONMENT" != "test" ] && [ "$after_count" != "0" ]; then
  echo "::error::Staging billing lifecycle lever app setting count after deploy was ${after_count}; production must stay off."
  exit 1
fi

if [ "$TARGET_ENVIRONMENT" = "test" ] && [ "$expected_count" -gt 0 ]; then
  if [ -z "$SMOKE_OWNER_ORG_ID" ]; then
    echo "::error::STAGING_SMOKE_OWNER_ORGANIZATION_ID must be set so live staging billing lifecycle lever settings can be checked against the Hosted smoke owner organization."
    exit 1
  fi
  if [[ ! "$SMOKE_OWNER_ORG_ID" =~ $lever_guid_pattern ]] || [ "$SMOKE_OWNER_ORG_ID" = "00000000-0000-0000-0000-000000000000" ]; then
    echo "::error::STAGING_SMOKE_OWNER_ORGANIZATION_ID must be a GUID organization id."
    exit 1
  fi
  live_values="$(list_lever_settings "[?starts_with(name, '${PREFIX}')].value" || true)"
  smoke_normalized="${SMOKE_OWNER_ORG_ID,,}"
  while IFS= read -r value; do
    [ -z "$value" ] && continue
    if [ "${value,,}" = "$smoke_normalized" ]; then
      echo "::error::Live staging billing lifecycle lever allowlist includes the staging Hosted smoke owner organization."
      exit 1
    fi
  done <<< "$live_values"
fi

echo "Staging billing lifecycle lever app setting count: before=${before_count} after=${after_count}."
