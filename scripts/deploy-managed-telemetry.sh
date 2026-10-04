#!/usr/bin/env bash
# Deploys infra/managed-telemetry with the environment-specific RecoveryRequired
# mailbox at resource-group scope. Staging never uses the production recipient.
# The address is never printed. The Monitoring Metrics Publisher assignment is
# never created here. A read-only preflight never blocks the what-if; it reports
# present, missing, component-absent, or error. Create never writes Authorization.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WHAT_IF=false
MONITORING_METRICS_PUBLISHER_ROLE_ID="3913510d-42f4-4e42-8a64-420c390055eb"
MONITORING_METRICS_PUBLISHER_ROLE_NAME="Monitoring Metrics Publisher"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --what-if)
      WHAT_IF=true
      shift
      ;;
    *)
      echo "::error::Unsupported argument for managed telemetry deploy." >&2
      exit 1
      ;;
  esac
done

target="${TARGET_ENVIRONMENT:-}"
case "$target" in
  test)
    environment=staging
    recipient_var=STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT
    ;;
  production)
    environment=production
    recipient_var=PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT
    ;;
  *)
    echo "::error::Managed telemetry deploy requires TARGET_ENVIRONMENT test or production." >&2
    exit 1
    ;;
esac

recipient="$(printf '%s' "${!recipient_var:-}" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
if [ -z "$recipient" ]; then
  echo "::error::${recipient_var} must be set for ${target}; refusing to deploy RecoveryRequired alerts." >&2
  exit 1
fi

normalize_mailbox() {
  printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//'
}

if [ "$environment" = "staging" ]; then
  production_recipient="$(printf '%s' "${PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT:-}" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
  if [ -z "$production_recipient" ]; then
    echo "::error::Staging RecoveryRequired alerts require PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT so the mailbox cannot silently reuse production." >&2
    exit 1
  fi
  if [ "$(normalize_mailbox "$recipient")" = "$(normalize_mailbox "$production_recipient")" ]; then
    echo "::error::Staging RecoveryRequired alerts must not use PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT." >&2
    exit 1
  fi
fi

required=(
  AZURE_SUBSCRIPTION_ID
  AZURE_RESOURCE_GROUP
  AZURE_LOCATION
  MANAGED_TELEMETRY_WORKSPACE_NAME
  MANAGED_TELEMETRY_APPLICATION_INSIGHTS_NAME
  MANAGED_TELEMETRY_API_IDENTITY_NAME
  MANAGED_TELEMETRY_API_IDENTITY_RESOURCE_GROUP
)
missing=()
for name in "${required[@]}"; do
  if [ -z "${!name:-}" ]; then
    missing+=("$name")
  fi
done
if [ "${#missing[@]}" -gt 0 ]; then
  echo "::error::Managed telemetry deploy is missing required configuration." >&2
  exit 1
fi

az account set --subscription "$AZURE_SUBSCRIPTION_ID"
insights_scope="/subscriptions/${AZURE_SUBSCRIPTION_ID}/resourceGroups/${AZURE_RESOURCE_GROUP}/providers/Microsoft.Insights/components/${MANAGED_TELEMETRY_APPLICATION_INSIGHTS_NAME}"

preflight_state=""
principal_id=""

print_grant() {
  local principal="${1:-}"
  if [ -z "$principal" ]; then
    echo "A human must create this one role assignment (the deploy identity cannot): principal=<API identity ${MANAGED_TELEMETRY_API_IDENTITY_NAME} principal id> role=${MONITORING_METRICS_PUBLISHER_ROLE_NAME} (${MONITORING_METRICS_PUBLISHER_ROLE_ID}) scope=${insights_scope}. An RG-level or subscription-level grant does not satisfy this preflight. Do not grant subscription-scope rights or Authorization write to the deploy identity."
  else
    echo "A human must create this one role assignment (the deploy identity cannot): principal=${principal} role=${MONITORING_METRICS_PUBLISHER_ROLE_NAME} (${MONITORING_METRICS_PUBLISHER_ROLE_ID}) scope=${insights_scope}. An RG-level or subscription-level grant does not satisfy this preflight. Do not grant subscription-scope rights or Authorization write to the deploy identity."
  fi
}

echo "Checking Monitoring Metrics Publisher assignment on the Insights component (read-only; inherited RG or subscription grants are ignored)."

identity_err="$(mktemp)"
set +e
principal_id="$(az identity show \
  --name "$MANAGED_TELEMETRY_API_IDENTITY_NAME" \
  --resource-group "$MANAGED_TELEMETRY_API_IDENTITY_RESOURCE_GROUP" \
  --query principalId \
  --output tsv \
  --only-show-errors 2>"$identity_err")"
identity_code=$?
set -e
rm -f "$identity_err"
if [ "$identity_code" -ne 0 ] || [ -z "${principal_id:-}" ]; then
  echo "::error::Could not read the Control API identity principal (az exit ${identity_code}). Not treating this as a missing role assignment."
  preflight_state="error"
  principal_id=""
else
  component_err="$(mktemp)"
  set +e
  az resource show --ids "$insights_scope" --output none --only-show-errors >/dev/null 2>"$component_err"
  component_code=$?
  component_not_found=0
  if grep -Eq 'ResourceNotFound|ResourceGroupNotFound|was not found' "$component_err"; then
    component_not_found=1
  fi
  rm -f "$component_err"
  set -e
  if [ "$component_not_found" -eq 1 ]; then
    preflight_state="component-absent"
  elif [ "$component_code" -ne 0 ]; then
    echo "::error::Could not read the Insights component (az exit ${component_code}). Not treating this as a missing role assignment."
    preflight_state="error"
  else
    assignment_err="$(mktemp)"
    set +e
    assignments="$(az role assignment list \
      --scope "$insights_scope" \
      --output json \
      --only-show-errors 2>"$assignment_err")"
    assignment_code=$?
    rm -f "$assignment_err"
    set -e
    if [ "$assignment_code" -ne 0 ]; then
      echo "::error::Could not list role assignments on the Insights component (az exit ${assignment_code}). Not treating this as a missing role assignment."
      preflight_state="error"
    else
      assignment_match="$(
        ASSIGNMENTS="${assignments:-}" PRINCIPAL_ID="$principal_id" ROLE_ID="$MONITORING_METRICS_PUBLISHER_ROLE_ID" SCOPE="$insights_scope" python3 - <<'PY'
import json
import os
import sys

try:
    rows = json.loads(os.environ.get("ASSIGNMENTS") or "")
except json.JSONDecodeError:
    raise SystemExit(2)
if not isinstance(rows, list):
    raise SystemExit(2)

principal = os.environ["PRINCIPAL_ID"].casefold()
role = os.environ["ROLE_ID"].casefold()
scope = os.environ["SCOPE"].casefold()
for row in rows:
    if not isinstance(row, dict):
        continue
    principal_id = str(row.get("principalId") or "").casefold()
    role_id = str(row.get("roleDefinitionId") or "").casefold()
    row_scope = str(row.get("scope") or "").casefold()
    if (
        principal_id == principal
        and (role_id == role or role_id.endswith("/" + role))
        and row_scope == scope
    ):
        print("yes")
        raise SystemExit(0)
print("no")
PY
      )" || {
        echo "::error::Could not parse role assignments on the Insights component. Not treating this as a missing role assignment."
        preflight_state="error"
        assignment_match=""
      }
      if [ "$preflight_state" != "error" ]; then
        if [ "$assignment_match" = "yes" ]; then
          preflight_state="present"
        else
          preflight_state="missing"
        fi
      fi
    fi
  fi
fi

if [ -z "$preflight_state" ]; then
  echo "::error::Could not complete the role-assignment preflight. Not treating this as a missing role assignment."
  preflight_state="error"
fi

echo "Managed telemetry preflight: ${preflight_state}."
case "$preflight_state" in
  present)
    echo "Monitoring Metrics Publisher assignment is present on the Insights component; skipping role assignment create."
    ;;
  missing)
    echo "Monitoring Metrics Publisher assignment is missing on the Insights component."
    print_grant "$principal_id"
    ;;
  component-absent)
    echo "Insights component is absent. Create will deploy the workspace, component, action group and alert without the role assignment."
    print_grant "$principal_id"
    ;;
  error)
    echo "Managed telemetry preflight could not verify the role assignment."
    ;;
esac

deployment_name="managed-telemetry-${environment}"
template_file="$ROOT/infra/managed-telemetry/main.bicep"
parameters=(
  location="$AZURE_LOCATION"
  workspaceName="$MANAGED_TELEMETRY_WORKSPACE_NAME"
  applicationInsightsName="$MANAGED_TELEMETRY_APPLICATION_INSIGHTS_NAME"
  apiIdentityName="$MANAGED_TELEMETRY_API_IDENTITY_NAME"
  apiIdentityResourceGroupName="$MANAGED_TELEMETRY_API_IDENTITY_RESOURCE_GROUP"
  environment="$environment"
  recoveryRequiredAlertEmail="$recipient"
  assignMonitoringMetricsPublisher=false
)

echo "Managed telemetry resource-group what-if for ${environment} (no deletes expected; workspace, Insights component, alert rule, and action group only; no role assignment)."
az deployment group what-if \
  --name "$deployment_name" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --template-file "$template_file" \
  --parameters "${parameters[@]}"

if [ "$WHAT_IF" = true ]; then
  if [ "$preflight_state" = "error" ]; then
    exit 1
  fi
  exit 0
fi

echo "Creating managed telemetry RecoveryRequired alerts for ${environment}."
az deployment group create \
  --name "$deployment_name" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --template-file "$template_file" \
  --parameters "${parameters[@]}"

case "$preflight_state" in
  present)
    echo "Managed telemetry create finished. Monitoring Metrics Publisher assignment was already present; no Authorization write was requested."
    ;;
  missing|component-absent)
    echo "::warning::Managed telemetry create finished without a Monitoring Metrics Publisher assignment. Ingestion stays off until a human creates that one grant and a later preflight reports present."
    print_grant "$principal_id"
    ;;
  error)
    echo "::error::Managed telemetry create finished, but the role-assignment preflight could not be verified. Not treating this as a missing role assignment."
    exit 1
    ;;
esac
