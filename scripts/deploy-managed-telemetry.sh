#!/usr/bin/env bash
# Deploys infra/managed-telemetry with the environment-specific RecoveryRequired
# mailbox. Staging never uses the production recipient. The address is never
# printed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WHAT_IF=false

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

recipient="${!recipient_var:-}"
if [ -z "$recipient" ]; then
  echo "::error::${recipient_var} must be set for ${target}; refusing to deploy RecoveryRequired alerts." >&2
  exit 1
fi

if [ "$environment" = "staging" ]; then
  if [ -z "${PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT:-}" ]; then
    echo "::error::Staging RecoveryRequired alerts require PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT so the mailbox cannot silently reuse production." >&2
    exit 1
  fi
  if [ "$recipient" = "${PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT}" ]; then
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
deployment_name="managed-telemetry-${environment}"
command=(az deployment group create)
if [ "$WHAT_IF" = true ]; then
  command=(az deployment group what-if)
fi

echo "Deploying managed telemetry RecoveryRequired alerts for ${environment}."
"${command[@]}" \
  --name "$deployment_name" \
  --resource-group "$AZURE_RESOURCE_GROUP" \
  --template-file "$ROOT/infra/managed-telemetry/main.bicep" \
  --parameters \
    location="$AZURE_LOCATION" \
    workspaceName="$MANAGED_TELEMETRY_WORKSPACE_NAME" \
    applicationInsightsName="$MANAGED_TELEMETRY_APPLICATION_INSIGHTS_NAME" \
    apiIdentityName="$MANAGED_TELEMETRY_API_IDENTITY_NAME" \
    apiIdentityResourceGroupName="$MANAGED_TELEMETRY_API_IDENTITY_RESOURCE_GROUP" \
    environment="$environment" \
    recoveryRequiredAlertEmail="$recipient"
