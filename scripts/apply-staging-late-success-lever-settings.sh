#!/usr/bin/env bash
set -euo pipefail

# Apply the cleaned staging late-success lever settings to App Service,
# delete stale indexed allowlist entries, and refuse a live list that contains
# the Hosted smoke owner workspace when that id is supplied. Prints only
# counts. Never echoes workspace ids.

ENABLED_NAME='Staging__LateSuccessLever__Enabled'
PREFIX='Staging__LateSuccessLever__AllowedWorkspaceIds__'
WAIT_NAME='Staging__LateSuccessLever__WorkloadWait'
SMOKE_OWNER_NAME='Staging__LateSuccessLever__SmokeOwnerWorkspaceId'
TARGET_ENVIRONMENT="${TARGET_ENVIRONMENT:-}"
AZURE_ENV_NAME="${AZURE_ENV_NAME:-}"
ENABLED_RAW="${STAGING_LATE_SUCCESS_LEVER_ENABLED:-}"
ALLOWLIST="${STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS:-}"
WAIT_RAW="${STAGING_LATE_SUCCESS_LEVER_WORKLOAD_WAIT:-}"
SMOKE_OWNER_WORKSPACE_ID="${STAGING_SMOKE_OWNER_WORKSPACE_ID:-}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required.}"
WEBAPP_NAME="${AZURE_WEBAPP_NAME:?AZURE_WEBAPP_NAME is required.}"
DEFAULT_WAIT='00:00:30'

# Shared two-signal environment predicate (#655): TARGET_ENVIRONMENT=test or
# AZURE_ENV_NAME=valence-control-staging. The late-success lever's own flag
# never arms the billing or recovery levers.
# shellcheck source=scripts/lib/staging-lever-target.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/staging-lever-target.sh"

lever_guid_pattern='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
wait_pattern='^([0-9]{2}):([0-9]{2}):([0-9]{2})$'

enabled=false
if [ -n "$ENABLED_RAW" ]; then
  case "${ENABLED_RAW,,}" in
    true) enabled=true ;;
    false)
      ENABLED_RAW=""
      enabled=false
      ;;
    *)
      echo "::error::STAGING_LATE_SUCCESS_LEVER_ENABLED must be true or false."
      exit 1
      ;;
  esac
fi

if { [ "$enabled" = true ] || [ -n "$ALLOWLIST" ] || [ -n "$WAIT_RAW" ] || [ -n "$SMOKE_OWNER_WORKSPACE_ID" ]; } && ! is_staging_lever_target; then
  echo "::error::STAGING_LATE_SUCCESS_LEVER_ENABLED, STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS, and STAGING_LATE_SUCCESS_LEVER_WORKLOAD_WAIT must be unset for ${TARGET_ENVIRONMENT:-${AZURE_ENV_NAME:-unknown}}; production ships the lever off."
  exit 1
fi

expected_ids=()
if [ -n "$ALLOWLIST" ]; then
  if [[ "$ALLOWLIST" == *$'\n'* || "$ALLOWLIST" == *$'\r'* ]]; then
    echo "::error::STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS must be a single-line comma-separated list."
    exit 1
  fi
  if [[ "$ALLOWLIST" =~ (^|,)[[:space:]]*(,|$) ]]; then
    echo "::error::STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS contains an empty workspace id."
    exit 1
  fi
  IFS=',' read -r -a lever_entries <<< "$ALLOWLIST"
  for entry in "${lever_entries[@]}"; do
    trimmed="${entry#"${entry%%[![:space:]]*}"}"
    trimmed="${trimmed%"${trimmed##*[![:space:]]}"}"
    if [ -z "$trimmed" ]; then
      echo "::error::STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS contains an empty workspace id."
      exit 1
    fi
    if [[ ! "$trimmed" =~ $lever_guid_pattern ]] || [ "$trimmed" = "00000000-0000-0000-0000-000000000000" ]; then
      echo "::error::STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS contains an entry that is not a GUID workspace id."
      exit 1
    fi
    expected_ids+=("$trimmed")
  done
fi
expected_count=${#expected_ids[@]}

workload_wait=""
if [ -n "$WAIT_RAW" ]; then
  if [[ ! "$WAIT_RAW" =~ $wait_pattern ]]; then
    echo "::error::STAGING_LATE_SUCCESS_LEVER_WORKLOAD_WAIT must be a TimeSpan between 00:00:15 and 00:05:00."
    exit 1
  fi
  wait_hours=$((10#${BASH_REMATCH[1]}))
  wait_minutes=$((10#${BASH_REMATCH[2]}))
  wait_seconds=$((10#${BASH_REMATCH[3]}))
  wait_total=$((wait_hours * 3600 + wait_minutes * 60 + wait_seconds))
  if [ "$wait_total" -lt 15 ] || [ "$wait_total" -gt 300 ]; then
    echo "::error::STAGING_LATE_SUCCESS_LEVER_WORKLOAD_WAIT must be a TimeSpan between 00:00:15 and 00:05:00."
    exit 1
  fi
  workload_wait="$WAIT_RAW"
elif [ "$enabled" = true ]; then
  workload_wait="$DEFAULT_WAIT"
fi

# Validate the complete proposed setting set before even reading App Service:
# a rejected input must never first delete stale settings or write a partial
# configuration. Surrounding allowlist whitespace is normalized above, as in
# the deployment workflow; empty entries and malformed or nil GUIDs are errors.
if is_staging_lever_target; then
  if [ "$expected_count" -gt 0 ] && [ -z "$SMOKE_OWNER_WORKSPACE_ID" ]; then
    echo "::error::STAGING_SMOKE_OWNER_WORKSPACE_ID must be set so live staging late-success lever settings can be checked against the Hosted smoke owner workspace."
    exit 1
  fi
  if [ -n "$SMOKE_OWNER_WORKSPACE_ID" ]; then
    if [[ ! "$SMOKE_OWNER_WORKSPACE_ID" =~ $lever_guid_pattern ]] || [ "$SMOKE_OWNER_WORKSPACE_ID" = "00000000-0000-0000-0000-000000000000" ]; then
      echo "::error::STAGING_SMOKE_OWNER_WORKSPACE_ID must be a GUID workspace id."
      exit 1
    fi
    smoke_normalized="${SMOKE_OWNER_WORKSPACE_ID,,}"
    for workspace_id in "${expected_ids[@]}"; do
      if [ "${workspace_id,,}" = "$smoke_normalized" ]; then
        echo "::error::STAGING_LATE_SUCCESS_LEVER_ALLOWED_WORKSPACE_IDS must not include the staging Hosted smoke owner workspace."
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

if [ -n "${LATE_SUCCESS_LEVER_BEFORE_COUNT:-}" ]; then
  before_count="$LATE_SUCCESS_LEVER_BEFORE_COUNT"
else
  before_count="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}' || name=='${WAIT_NAME}' || name=='${SMOKE_OWNER_NAME}'] | length(@)")"
  before_count="${before_count:-0}"
fi

existing_names="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}' || name=='${WAIT_NAME}' || name=='${SMOKE_OWNER_NAME}'].name" || true)"
stale_names=()
while IFS= read -r name; do
  [ -z "$name" ] && continue
  keep=false
  if [ "$name" = "$ENABLED_NAME" ] && [ "$enabled" = true ]; then
    keep=true
  elif [ "$name" = "$WAIT_NAME" ] && [ -n "$workload_wait" ]; then
    keep=true
  elif [ "$name" = "$SMOKE_OWNER_NAME" ] && [ -n "$SMOKE_OWNER_WORKSPACE_ID" ]; then
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
  echo "Deleted ${#stale_names[@]} stale staging late-success lever app setting(s)."
fi

settings=()
if [ "$enabled" = true ]; then
  settings+=("${ENABLED_NAME}=true")
fi
if [ -n "$workload_wait" ]; then
  settings+=("${WAIT_NAME}=${workload_wait}")
fi
if [ -n "$SMOKE_OWNER_WORKSPACE_ID" ]; then
  settings+=("${SMOKE_OWNER_NAME}=${SMOKE_OWNER_WORKSPACE_ID}")
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

after_count="$(list_lever_settings "[?starts_with(name, '${PREFIX}') || name=='${ENABLED_NAME}' || name=='${WAIT_NAME}' || name=='${SMOKE_OWNER_NAME}'] | length(@)")"
after_count="${after_count:-0}"
expected_after=0
if [ "$enabled" = true ]; then
  expected_after=$((expected_after + 1))
fi
if [ -n "$workload_wait" ]; then
  expected_after=$((expected_after + 1))
fi
if [ -n "$SMOKE_OWNER_WORKSPACE_ID" ]; then
  expected_after=$((expected_after + 1))
fi
expected_after=$((expected_after + expected_count))
if [ "$after_count" != "$expected_after" ]; then
  echo "::error::Staging late-success lever app setting count after deploy was ${after_count}; expected ${expected_after}."
  exit 1
fi
if ! is_staging_lever_target && [ "$after_count" != "0" ]; then
  echo "::error::Staging late-success lever app setting count after deploy was ${after_count}; production must stay off."
  exit 1
fi

if is_staging_lever_target && [ "$expected_count" -gt 0 ] && [ -n "$SMOKE_OWNER_WORKSPACE_ID" ]; then
  live_values="$(list_lever_settings "[?starts_with(name, '${PREFIX}')].value" || true)"
  smoke_normalized="${SMOKE_OWNER_WORKSPACE_ID,,}"
  while IFS= read -r value; do
    [ -z "$value" ] && continue
    if [ "${value,,}" = "$smoke_normalized" ]; then
      echo "::error::Live staging late-success lever allowlist includes the staging Hosted smoke owner workspace."
      exit 1
    fi
  done <<< "$live_values"
fi

echo "Staging late-success lever app setting count: before=${before_count} after=${after_count}."
