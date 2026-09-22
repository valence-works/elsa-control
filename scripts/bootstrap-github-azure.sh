#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

GITHUB_ENVIRONMENT="${GITHUB_ENVIRONMENT:-production}"
AZURE_ENVIRONMENT="${AZURE_ENVIRONMENT:-}"
LOCATION="${AZURE_LOCATION:-westeurope}"
RESOURCE_GROUP="${AZURE_RESOURCE_GROUP:-}"
SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-}"
APP_DISPLAY_NAME="${APP_DISPLAY_NAME:-}"
AZURE_CLIENT_ID="${AZURE_CLIENT_ID:-}"
CONTROL_ENTRA_CLIENT_ID="${CONTROL_ENTRA_CLIENT_ID:-${AZURE_ENTRA_CLIENT_ID:-}}"
CONTROL_ENTRA_TENANT_ID="${CONTROL_ENTRA_TENANT_ID:-${AZURE_ENTRA_TENANT_ID:-}}"
CLOUD_ACCOUNT_ISSUER="${CLOUD_ACCOUNT_ISSUER:-}"
AZURE_PROVISIONER_IDENTITY_ID="${AZURE_PROVISIONER_IDENTITY_ID:-}"
AZURE_API_EGRESS_SUBNET_ID="${AZURE_API_EGRESS_SUBNET_ID:-}"
DRY_RUN=false
SKIP_ROLE_ASSIGNMENTS=false
DISABLE_CLOUD_ACCOUNT_ISSUER=false

usage() {
  cat <<'USAGE'
Usage: scripts/bootstrap-github-azure.sh [options]

Creates or updates a GitHub Actions environment that deploys to a matching
Azure resource group through OpenID Connect.

Options:
  --environment <name>        GitHub environment name. Default: production.
  --azure-environment <name>  Azure/Bicep environment name. Default: GitHub environment,
                              with development mapped to dev.
  --resource-group <name>     Azure resource group. Default: rg-<azure-env>.
  --location <name>           Azure region. Default: westeurope.
  --subscription <id>         Azure subscription ID. Default: current az account.
  --client-id <id>            Existing Entra app registration client ID to use.
  --app-display-name <name>   Entra app display name when creating/reusing OIDC app.
  --skip-role-assignments     Do not create Azure role assignments.
  --disable-cloud-account-issuer
                              Disable Cloud JWT admission while retaining the independently
                              approved issuer for a later re-enable.
  --dry-run                   Print changes without writing Azure/GitHub state.
  -h, --help                  Show this help.

Optional environment variables used as GitHub environment secrets:
  ADMIN_API_KEY
  BUILDER_CLIENT_API_KEY
  CONTROL_ENTRA_CLIENT_SECRET

Optional environment variables used as GitHub environment variables:
  CONTROL_ENTRA_CLIENT_ID
  CONTROL_ENTRA_TENANT_ID
  CLOUD_ACCOUNT_ISSUER
  AZURE_PROVISIONER_IDENTITY_ID
  AZURE_API_EGRESS_SUBNET_ID
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --environment)
      GITHUB_ENVIRONMENT="$2"
      shift 2
      ;;
    --azure-environment)
      AZURE_ENVIRONMENT="$2"
      shift 2
      ;;
    --resource-group)
      RESOURCE_GROUP="$2"
      shift 2
      ;;
    --location)
      LOCATION="$2"
      shift 2
      ;;
    --subscription)
      SUBSCRIPTION_ID="$2"
      shift 2
      ;;
    --client-id)
      AZURE_CLIENT_ID="$2"
      shift 2
      ;;
    --app-display-name)
      APP_DISPLAY_NAME="$2"
      shift 2
      ;;
    --skip-role-assignments)
      SKIP_ROLE_ASSIGNMENTS=true
      shift
      ;;
    --disable-cloud-account-issuer)
      DISABLE_CLOUD_ACCOUNT_ISSUER=true
      shift
      ;;
    --dry-run)
      DRY_RUN=true
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "$DISABLE_CLOUD_ACCOUNT_ISSUER" == true && -n "$CLOUD_ACCOUNT_ISSUER" ]]; then
  echo "Do not set CLOUD_ACCOUNT_ISSUER when --disable-cloud-account-issuer is used." >&2
  exit 1
fi

if [[ -n "$CLOUD_ACCOUNT_ISSUER" && ! "$CLOUD_ACCOUNT_ISSUER" =~ ^https://[a-z0-9]{20}\.supabase\.co/auth/v1$ ]]; then
  echo "CLOUD_ACCOUNT_ISSUER must be an exact Supabase Auth issuer URL." >&2
  exit 1
fi

if [[ -z "$AZURE_ENVIRONMENT" ]]; then
  if [[ "$GITHUB_ENVIRONMENT" == "development" ]]; then
    AZURE_ENVIRONMENT="dev"
  else
    AZURE_ENVIRONMENT="$GITHUB_ENVIRONMENT"
  fi
fi
if [[ ! "$AZURE_ENVIRONMENT" =~ ^[A-Za-z0-9][A-Za-z0-9-]{0,62}$ ]]; then
  echo "Azure environment name must contain only letters, numbers, and hyphens." >&2
  exit 1
fi

EXPECTED_RESOURCE_GROUP="rg-$AZURE_ENVIRONMENT"
RESOURCE_GROUP="${RESOURCE_GROUP:-$EXPECTED_RESOURCE_GROUP}"
if [[ "$RESOURCE_GROUP" != "$EXPECTED_RESOURCE_GROUP" ]]; then
  echo "Resource group must be $EXPECTED_RESOURCE_GROUP because infra/main.bicep owns that name." >&2
  exit 1
fi
APP_DISPLAY_NAME="${APP_DISPLAY_NAME:-elsa-control-$GITHUB_ENVIRONMENT-github-actions}"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

run() {
  if [[ "$DRY_RUN" == true ]]; then
    printf 'DRY RUN:'
    printf ' %q' "$@"
    printf '\n'
  else
    "$@"
  fi
}

set_github_var() {
  local name="$1"
  local value="$2"

  if [[ -z "$value" ]]; then
    echo "Cannot set $name because its value is empty." >&2
    exit 1
  fi

  echo "Setting GitHub variable $name in environment $GITHUB_ENVIRONMENT."
  run gh variable set "$name" --env "$GITHUB_ENVIRONMENT" --body "$value"
}

set_github_secret_if_present() {
  local name="$1"
  local value="${!name:-}"

  if [[ -z "$value" ]]; then
    echo "Skipping GitHub secret $name because it is not set locally."
    return
  fi

  echo "Setting GitHub secret $name in environment $GITHUB_ENVIRONMENT."
  if [[ "$DRY_RUN" == true ]]; then
    echo "DRY RUN: gh secret set $name --env $GITHUB_ENVIRONMENT --body <redacted>"
  else
    gh secret set "$name" --env "$GITHUB_ENVIRONMENT" --body "$value" >/dev/null
  fi
}

ensure_role_assignment() {
  local assignee="$1"
  local role="$2"
  local scope="$3"

  echo "Ensuring Azure role '$role' at $scope."
  if [[ "$DRY_RUN" == true ]]; then
    echo "DRY RUN: az role assignment create --assignee $assignee --role $role --scope $scope"
    return
  fi

  local existing
  if ! existing="$(az role assignment list --assignee "$assignee" --role "$role" --scope "$scope" --query "[?scope=='$scope'] | [0].id" -o tsv 2>/dev/null)"; then
    echo "Could not verify Azure role '$role' at $scope." >&2
    return 1
  fi
  if [[ -n "$existing" ]]; then
    return
  fi

  if ! az role assignment create \
    --assignee "$assignee" \
    --role "$role" \
    --scope "$scope" \
    --only-show-errors \
    --output none; then
    echo "Could not create Azure role '$role' at $scope." >&2
    return 1
  fi
}

require_command az
require_command gh
require_command python3

if [[ "$DRY_RUN" != true ]]; then
  gh auth status >/dev/null
fi

if [[ -n "$CLOUD_ACCOUNT_ISSUER" ]]; then
  APPROVED_CLOUD_ACCOUNT_ISSUER="$(gh variable get EXPECTED_CLOUD_ACCOUNT_ISSUER --env "$GITHUB_ENVIRONMENT" --json value --jq .value 2>/dev/null || true)"
  if [[ -z "$APPROVED_CLOUD_ACCOUNT_ISSUER" ]]; then
    echo "GitHub environment $GITHUB_ENVIRONMENT has no independently approved Cloud account issuer." >&2
    exit 1
  fi
  if [[ "$CLOUD_ACCOUNT_ISSUER" != "$APPROVED_CLOUD_ACCOUNT_ISSUER" ]]; then
    echo "CLOUD_ACCOUNT_ISSUER does not match the independently approved environment issuer." >&2
    exit 1
  fi
fi

if [[ -z "$SUBSCRIPTION_ID" ]]; then
  SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
fi

az account set --subscription "$SUBSCRIPTION_ID"
TENANT_ID="$(az account show --query tenantId -o tsv)"

REPO_FULL_NAME="$(gh repo view --json nameWithOwner --jq .nameWithOwner)"
SUBJECT="repo:$REPO_FULL_NAME:environment:$GITHUB_ENVIRONMENT"
ISSUER="https://token.actions.githubusercontent.com"

echo "Using GitHub environment: $GITHUB_ENVIRONMENT"
echo "Using Azure environment: $AZURE_ENVIRONMENT"
echo "Using resource group: $RESOURCE_GROUP"
echo "Using subscription: $SUBSCRIPTION_ID"

DEPLOYMENT_NAME="elsa-control-$AZURE_ENVIRONMENT"
OUTPUTS="$(az deployment sub show --name "$DEPLOYMENT_NAME" --query properties.outputs -o json 2>/dev/null || true)"
if [[ -z "$OUTPUTS" || "$OUTPUTS" == "null" ]]; then
  echo "Could not read subscription deployment outputs from $DEPLOYMENT_NAME. Run scripts/deploy-azure-elsa-control.sh first." >&2
  exit 1
fi

ACR_ENDPOINT="$(printf '%s' "$OUTPUTS" | python3 scripts/read-arm-deployment-output.py AZURE_CONTAINER_REGISTRY_ENDPOINT)"
ACR_NAME="${ACR_ENDPOINT%%.azurecr.io}"
WEBAPP_COUNT="$(az webapp list --resource-group "$RESOURCE_GROUP" --query 'length(@)' --output tsv)"
if [[ "$WEBAPP_COUNT" != "1" ]]; then
  echo "Expected exactly one Web App in $RESOURCE_GROUP. Deploy the API module before bootstrapping GitHub." >&2
  exit 1
fi
WEBAPP_NAME="$(az webapp list --resource-group "$RESOURCE_GROUP" --query '[0].name' --output tsv)"
RESOURCE_GROUP_ID="/subscriptions/$SUBSCRIPTION_ID/resourceGroups/$RESOURCE_GROUP"
ACR_ID="$(az acr show --resource-group "$RESOURCE_GROUP" --name "$ACR_NAME" --query id -o tsv)"

if [[ -z "$AZURE_CLIENT_ID" ]]; then
  AZURE_CLIENT_ID="$(az ad app list --display-name "$APP_DISPLAY_NAME" --query '[0].appId' -o tsv)"
fi

if [[ -z "$AZURE_CLIENT_ID" ]]; then
  echo "Creating Entra app registration $APP_DISPLAY_NAME."
  if [[ "$DRY_RUN" == true ]]; then
    AZURE_CLIENT_ID="00000000-0000-0000-0000-000000000000"
    echo "DRY RUN: az ad app create --display-name $APP_DISPLAY_NAME"
  else
    AZURE_CLIENT_ID="$(az ad app create --display-name "$APP_DISPLAY_NAME" --query appId -o tsv)"
  fi
else
  echo "Using Entra app registration client ID $AZURE_CLIENT_ID."
fi

APP_OBJECT_ID="$(az ad app show --id "$AZURE_CLIENT_ID" --query id -o tsv 2>/dev/null || true)"
if [[ -z "$APP_OBJECT_ID" ]]; then
  if [[ "$DRY_RUN" == true ]]; then
    APP_OBJECT_ID="00000000-0000-0000-0000-000000000001"
  else
    echo "Could not resolve the Entra application object for $AZURE_CLIENT_ID." >&2
    exit 1
  fi
fi

if [[ "$DRY_RUN" != true ]]; then
  az ad sp create --id "$AZURE_CLIENT_ID" --only-show-errors --output none 2>/dev/null || true
fi

SERVICE_PRINCIPAL_OBJECT_ID="$(az ad sp show --id "$AZURE_CLIENT_ID" --query id -o tsv 2>/dev/null || true)"
if [[ -z "$SERVICE_PRINCIPAL_OBJECT_ID" ]]; then
  if [[ "$DRY_RUN" == true ]]; then
    SERVICE_PRINCIPAL_OBJECT_ID="00000000-0000-0000-0000-000000000002"
  else
    echo "Could not resolve the Entra service principal for $AZURE_CLIENT_ID." >&2
    exit 1
  fi
fi

if [[ -n "$APP_OBJECT_ID" ]]; then
  EXISTING_CREDENTIAL="$(az ad app federated-credential list --id "$APP_OBJECT_ID" --query "[?subject=='$SUBJECT'].id | [0]" -o tsv 2>/dev/null || true)"
  if [[ -z "$EXISTING_CREDENTIAL" ]]; then
    echo "Creating GitHub environment federated credential."
    if [[ "$DRY_RUN" == true ]]; then
      echo "DRY RUN: az ad app federated-credential create --id $APP_OBJECT_ID --subject $SUBJECT"
    else
      CREDENTIAL_FILE="$(mktemp)"
      trap 'rm -f "$CREDENTIAL_FILE"' EXIT
      python3 - "$CREDENTIAL_FILE" "$GITHUB_ENVIRONMENT" "$ISSUER" "$SUBJECT" <<'PY'
import json
import sys

path, environment, issuer, subject = sys.argv[1:]
credential = {
    "name": f"github-{environment}",
    "issuer": issuer,
    "subject": subject,
    "audiences": ["api://AzureADTokenExchange"],
}

with open(path, "w", encoding="utf-8") as handle:
    json.dump(credential, handle)
PY
      az ad app federated-credential create --id "$APP_OBJECT_ID" --parameters "@$CREDENTIAL_FILE" --only-show-errors --output none
    fi
  else
    echo "GitHub environment federated credential already exists."
  fi
fi

if [[ "$SKIP_ROLE_ASSIGNMENTS" != true ]]; then
  ensure_role_assignment "$SERVICE_PRINCIPAL_OBJECT_ID" Contributor "$RESOURCE_GROUP_ID"
  ensure_role_assignment "$SERVICE_PRINCIPAL_OBJECT_ID" AcrPush "$ACR_ID"
fi

run gh api --method PUT "repos/:owner/:repo/environments/$GITHUB_ENVIRONMENT" >/dev/null

set_github_var AZURE_CLIENT_ID "$AZURE_CLIENT_ID"
set_github_var AZURE_TENANT_ID "$TENANT_ID"
set_github_var AZURE_SUBSCRIPTION_ID "$SUBSCRIPTION_ID"
set_github_var AZURE_ENV_NAME "$AZURE_ENVIRONMENT"
set_github_var AZURE_LOCATION "$LOCATION"
set_github_var AZURE_RESOURCE_GROUP "$RESOURCE_GROUP"
set_github_var AZURE_WEBAPP_NAME "$WEBAPP_NAME"
set_github_var AZURE_CONTAINER_REGISTRY_ENDPOINT "$ACR_ENDPOINT"

if [[ -n "$CONTROL_ENTRA_CLIENT_ID" ]]; then
  set_github_var CONTROL_ENTRA_CLIENT_ID "$CONTROL_ENTRA_CLIENT_ID"
fi
if [[ -n "$CONTROL_ENTRA_TENANT_ID" ]]; then
  set_github_var CONTROL_ENTRA_TENANT_ID "$CONTROL_ENTRA_TENANT_ID"
fi
if [[ "$DISABLE_CLOUD_ACCOUNT_ISSUER" == true ]]; then
  if gh variable get CLOUD_ACCOUNT_ISSUER --env "$GITHUB_ENVIRONMENT" >/dev/null 2>&1; then
    echo "Disabling Cloud JWT admission in environment $GITHUB_ENVIRONMENT."
    run gh variable delete CLOUD_ACCOUNT_ISSUER --env "$GITHUB_ENVIRONMENT"
  fi
elif [[ -n "$CLOUD_ACCOUNT_ISSUER" ]]; then
  set_github_var CLOUD_ACCOUNT_ISSUER "$CLOUD_ACCOUNT_ISSUER"
fi
if [[ -n "$AZURE_PROVISIONER_IDENTITY_ID" ]]; then
  set_github_var AZURE_PROVISIONER_IDENTITY_ID "$AZURE_PROVISIONER_IDENTITY_ID"
fi
if [[ -n "$AZURE_API_EGRESS_SUBNET_ID" ]]; then
  set_github_var AZURE_API_EGRESS_SUBNET_ID "$AZURE_API_EGRESS_SUBNET_ID"
fi

set_github_secret_if_present ADMIN_API_KEY
set_github_secret_if_present BUILDER_CLIENT_API_KEY
set_github_secret_if_present CONTROL_ENTRA_CLIENT_SECRET

echo "GitHub environment $GITHUB_ENVIRONMENT is configured for $RESOURCE_GROUP."
