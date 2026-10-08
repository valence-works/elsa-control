#!/usr/bin/env bash
set -euo pipefail

# Apply the cleaned staging recovery lifecycle lever settings to App Service,
# delete stale indexed allowlist entries, and refuse a live list that contains
# the Hosted smoke owner instance. Prints only counts. Never echoes instance ids.

ENABLED_NAME='Staging__RecoveryLifecycleLever__Enabled'
PREFIX='Staging__RecoveryLifecycleLever__AllowedInstanceIds__'
SMOKE_OWNER_NAME='Staging__RecoveryLifecycleLever__SmokeOwnerInstanceId'
TARGET_ENVIRONMENT="${TARGET_ENVIRONMENT:-}"
AZURE_ENV_NAME="${AZURE_ENV_NAME:-}"
ENABLED_RAW="${STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED:-}"
ALLOWLIST="${STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS:-}"
SMOKE_OWNER_INSTANCE_ID="${STAGING_SMOKE_OWNER_INSTANCE_ID:-}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required.}"
WEBAPP_NAME="${AZURE_WEBAPP_NAME:?AZURE_WEBAPP_NAME is required.}"

# Shared two-signal environment predicate (#655): TARGET_ENVIRONMENT=test or
# AZURE_ENV_NAME=valence-control-staging. The recovery lever's own flag never
# arms the billing lever.
# shellcheck source=scripts/lib/staging-lever-target.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/staging-lever-target.sh"

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
      echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED must be true or false."
      exit 1
      ;;
  esac
fi

if { [ "$enabled" = true ] || [ -n "$ALLOWLIST" ] || [ -n "$SMOKE_OWNER_INSTANCE_ID" ]; } && ! is_staging_lever_target; then
  echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ENABLED, STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS, and STAGING_SMOKE_OWNER_INSTANCE_ID must be unset for ${TARGET_ENVIRONMENT:-${AZURE_ENV_NAME:-unknown}}; production ships the lever off."
  exit 1
fi

expected_ids=()
if [ -n "$ALLOWLIST" ]; then
  if [[ "$ALLOWLIST" == *$'\n'* || "$ALLOWLIST" == *$'\r'* ]]; then
    echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS must be a single-line comma-separated list."
    exit 1
  fi
  if [[ "$ALLOWLIST" =~ (^|,)[[:space:]]*(,|$) ]]; then
    echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS contains an empty instance id."
    exit 1
  fi
  IFS=',' read -r -a lever_entries <<< "$ALLOWLIST"
  for entry in "${lever_entries[@]}"; do
    trimmed="${entry#"${entry%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    if [ -z "$trimmed" ]; then
      echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS contains an empty instance id."
      exit 1
    fi
    if [[ ! "$trimmed" =~ $lever_guid_pattern ]] || [ "$trimmed" = "00000000-0000-0000-0000-000000000000" ]; then
      echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS contains an entry that is not a GUID instance id."
      exit 1
    fi
    expected_ids+=("$trimmed")
  done
fi
expected_count=${#expected_ids[@]}

# Validate the complete proposed setting set before even reading App Service:
# a rejected input must never first delete stale settings or write a partial
# configuration. Surrounding allowlist whitespace is normalized above, as in
# the deployment workflow; empty entries and malformed or nil GUIDs are errors.
if is_staging_lever_target; then
  if [ "$expected_count" -gt 0 ] && [ -z "$SMOKE_OWNER_INSTANCE_ID" ]; then
    echo "::error::STAGING_SMOKE_OWNER_INSTANCE_ID must be set so live staging recovery lifecycle lever settings can be checked against the Hosted smoke owner instance."
    exit 1
  fi
  if [ -n "$SMOKE_OWNER_INSTANCE_ID" ]; then
    if [[ ! "$SMOKE_OWNER_INSTANCE_ID" =~ $lever_guid_pattern ]] || [ "$SMOKE_OWNER_INSTANCE_ID" = "00000000-0000-0000-0000-000000000000" ]; then
      echo "::error::STAGING_SMOKE_OWNER_INSTANCE_ID must be a GUID instance id."
      exit 1
    fi
    smoke_normalized="${SMOKE_OWNER_INSTANCE_ID,,}"
    for instance_id in "${expected_ids[@]}"; do
      if [ "${instance_id,,}" = "$smoke_normalized" ]; then
        echo "::error::STAGING_RECOVERY_LIFECYCLE_LEVER_ALLOWED_INSTANCE_IDS must not include the staging Hosted smoke owner instance."
        exit 1
      fi
    done
  fi
fi

list_lever_settings() {
  local query="$1"
  az webapp config appsettings list \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --query "$query" \
    --output tsv \
    --only-show-errors
}

if [ -n "${RECOVERY_LEVER_BEFORE_COUNT:-}" ]; then
  before_count="$RECOVERY_LEVER_BEFORE_COUNT"
else
  before_count="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}' || name=='${SMOKE_OWNER_NAME}'] | length(@)")"
  before_count="${before_count:-0}"
fi

existing_names="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}' || name=='${SMOKE_OWNER_NAME}'].name" || true)"
stale_names=()
while IFS= read -r name; do
  [ -z "$name" ] && continue
  keep=false
  if [ "$name" = "$ENABLED_NAME" ] && [ "$enabled" = true ]; then
    keep=true
  elif [ "$name" = "$SMOKE_OWNER_NAME" ] && [ -n "$SMOKE_OWNER_INSTANCE_ID" ]; then
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
  echo "Deleted ${#stale_names[@]} stale staging recovery lifecycle lever app setting(s)."
fi

settings=()
if [ "$enabled" = true ]; then
  settings+=("${ENABLED_NAME}=true")
fi
if [ -n "$SMOKE_OWNER_INSTANCE_ID" ]; then
  settings+=("${SMOKE_OWNER_NAME}=${SMOKE_OWNER_INSTANCE_ID}")
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

after_count="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}' || name=='${SMOKE_OWNER_NAME}'] | length(@)")"
after_count="${after_count:-0}"
expected_after=0
if [ "$enabled" = true ]; then
  expected_after=$((expected_after + 1))
fi
if [ -n "$SMOKE_OWNER_INSTANCE_ID" ]; then
  expected_after=$((expected_after + 1))
fi
expected_after=$((expected_after + expected_count))
if [ "$after_count" != "$expected_after" ]; then
  echo "::error::Staging recovery lifecycle lever app setting count after deploy was ${after_count}; expected ${expected_after}."
  exit 1
fi
if ! is_staging_lever_target && [ "$after_count" != "0" ]; then
  echo "::error::Staging recovery lifecycle lever app setting count after deploy was ${after_count}; production must stay off."
  exit 1
fi

if is_staging_lever_target && [ "$expected_count" -gt 0 ]; then
  live_values="$(list_lever_settings "[?starts_with(name, '${PREFIX}')].value" || true)"
  smoke_normalized="${SMOKE_OWNER_INSTANCE_ID,,}"
  while IFS= read -r value; do
    [ -z "$value" ] && continue
    if [ "${value,,}" = "$smoke_normalized" ]; then
      echo "::error::Live staging recovery lifecycle lever allowlist includes the staging Hosted smoke owner instance."
      exit 1
    fi
  done <<< "$live_values"
fi

echo "Staging recovery lifecycle lever app setting count: before=${before_count} after=${after_count}."
