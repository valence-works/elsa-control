#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

ENVIRONMENT_NAME="${ENVIRONMENT_NAME:-dev}"
LOCATION="${LOCATION:-westeurope}"
RESOURCE_GROUP="${RESOURCE_GROUP:-}"
RESOURCE_GROUP_EXPLICIT=false
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short HEAD 2>/dev/null || date +%Y%m%d%H%M%S)}"
DOCKER_PLATFORM="${DOCKER_PLATFORM:-linux/amd64}"
SUBSCRIPTION_ID="${AZURE_SUBSCRIPTION_ID:-}"
ADMIN_API_KEY="${ADMIN_API_KEY:-}"
BUILDER_CLIENT_API_KEY="${BUILDER_CLIENT_API_KEY:-}"
CONTROL_ENTRA_TENANT_ID="${CONTROL_ENTRA_TENANT_ID:-${AZURE_ENTRA_TENANT_ID:-}}"
CONTROL_ENTRA_CLIENT_ID="${CONTROL_ENTRA_CLIENT_ID:-${AZURE_ENTRA_CLIENT_ID:-}}"
CONTROL_ENTRA_CLIENT_SECRET="${CONTROL_ENTRA_CLIENT_SECRET:-${AZURE_ENTRA_CLIENT_SECRET:-}}"
AZURE_PRINCIPAL_ID="${AZURE_PRINCIPAL_ID:-}"
CLOUD_ACCOUNT_ISSUER="${CLOUD_ACCOUNT_ISSUER:-}"
EXPECTED_CLOUD_ACCOUNT_ISSUER="${EXPECTED_CLOUD_ACCOUNT_ISSUER:-}"
AZURE_PROVISIONER_IDENTITY_ID="${AZURE_PROVISIONER_IDENTITY_ID:-}"
AZURE_API_EGRESS_SUBNET_ID="${AZURE_API_EGRESS_SUBNET_ID:-}"
APPLICATION_BUILD_NUMBER="${APPLICATION_BUILD_NUMBER:-}"
WHAT_IF=false
BASE_ONLY=false

usage() {
  cat <<'USAGE'
Usage: scripts/deploy-azure-elsa-control.sh [options]

Provisions the current Aspire-generated base infrastructure, builds and pushes
the API image, and deploys the generated App Service module.

Options:
  --environment <name>       Environment name. Default: dev.
  --resource-group <name>    Must be rg-<environment>, matching infra/main.bicep.
  --location <name>          Azure region. Default: westeurope.
  --subscription <id>        Azure subscription ID. Can also use AZURE_SUBSCRIPTION_ID.
  --image-tag <tag>          Container image tag. Default: current git SHA.
  --docker-platform <value>  Docker target platform. Default: linux/amd64.
  --what-if                  Preview the base infrastructure deployment only.
  --base-only                Apply only the base infrastructure, then stop before image deployment.
  -h, --help                 Show this help.

Required environment variables for a full deployment:
  ADMIN_API_KEY
  BUILDER_CLIENT_API_KEY
  CONTROL_ENTRA_TENANT_ID
  CONTROL_ENTRA_CLIENT_ID
  CONTROL_ENTRA_CLIENT_SECRET

Optional environment variables:
  AZURE_PRINCIPAL_ID
  CLOUD_ACCOUNT_ISSUER
  EXPECTED_CLOUD_ACCOUNT_ISSUER
  AZURE_PROVISIONER_IDENTITY_ID
  AZURE_API_EGRESS_SUBNET_ID
  APPLICATION_BUILD_NUMBER
  DOCKER_PLATFORM

The API managed identity must already be a contained user in the Catalog
database before the Web App can start. See docs/deployment/azure-app-service.md.
USAGE
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --environment)
      ENVIRONMENT_NAME="$2"
      if [[ "$RESOURCE_GROUP_EXPLICIT" != true ]]; then
        RESOURCE_GROUP=""
      fi
      shift 2
      ;;
    --resource-group)
      RESOURCE_GROUP="$2"
      RESOURCE_GROUP_EXPLICIT=true
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
    --image-tag)
      IMAGE_TAG="$2"
      shift 2
      ;;
    --docker-platform)
      DOCKER_PLATFORM="$2"
      shift 2
      ;;
    --what-if)
      WHAT_IF=true
      shift
      ;;
    --base-only)
      BASE_ONLY=true
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

if [[ ! "$ENVIRONMENT_NAME" =~ ^[A-Za-z0-9][A-Za-z0-9-]{0,62}$ ]]; then
  echo "Environment name must contain only letters, numbers, and hyphens." >&2
  exit 1
fi
if [[ ! "$IMAGE_TAG" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]]; then
  echo "Image tag has an invalid container tag format." >&2
  exit 1
fi
if [[ -n "$CLOUD_ACCOUNT_ISSUER" ]]; then
  if [[ ! "$CLOUD_ACCOUNT_ISSUER" =~ ^https://[a-z0-9]{20}\.supabase\.co/auth/v1$ ]]; then
    echo "CLOUD_ACCOUNT_ISSUER must be an exact Supabase Auth issuer URL." >&2
    exit 1
  fi
  if [[ "$CLOUD_ACCOUNT_ISSUER" != "$EXPECTED_CLOUD_ACCOUNT_ISSUER" ]]; then
    echo "CLOUD_ACCOUNT_ISSUER must match the approved environment issuer." >&2
    exit 1
  fi
fi

EXPECTED_RESOURCE_GROUP="rg-$ENVIRONMENT_NAME"
RESOURCE_GROUP="${RESOURCE_GROUP:-$EXPECTED_RESOURCE_GROUP}"
if [[ "$RESOURCE_GROUP" != "$EXPECTED_RESOURCE_GROUP" ]]; then
  echo "Resource group must be $EXPECTED_RESOURCE_GROUP because infra/main.bicep owns that name." >&2
  exit 1
fi

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

require_value() {
  if [[ -z "${!1:-}" ]]; then
    echo "$1 is required." >&2
    exit 1
  fi
}

require_command az
require_command python3
if [[ "$WHAT_IF" != true && "$BASE_ONLY" != true ]]; then
  require_command docker
fi

if [[ "$WHAT_IF" != true && "$BASE_ONLY" != true ]]; then
  require_value ADMIN_API_KEY
  require_value BUILDER_CLIENT_API_KEY
  require_value CONTROL_ENTRA_TENANT_ID
  require_value CONTROL_ENTRA_CLIENT_ID
  require_value CONTROL_ENTRA_CLIENT_SECRET
fi

if [[ -n "$SUBSCRIPTION_ID" ]]; then
  az account set --subscription "$SUBSCRIPTION_ID"
else
  SUBSCRIPTION_ID="$(az account show --query id --output tsv)"
fi

BASE_DEPLOYMENT_NAME="elsa-control-$ENVIRONMENT_NAME"
API_DEPLOYMENT_NAME="$BASE_DEPLOYMENT_NAME-api"
BASE_PARAMETERS_FILE="$(mktemp)"
API_PARAMETERS_FILE=""

cleanup() {
  python3 - "$BASE_PARAMETERS_FILE" "$API_PARAMETERS_FILE" <<'PY'
import os
import sys

for path in sys.argv[1:]:
    if not path:
        continue
    try:
        os.unlink(path)
    except FileNotFoundError:
        pass
PY
}
trap cleanup EXIT

write_parameters() {
  local file_path="$1"
  local payload="$2"
  PAYLOAD="$payload" python3 - "$file_path" <<'PY'
import json
import os
import sys

path = sys.argv[1]
payload = json.loads(os.environ["PAYLOAD"])
document = {
    "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
    "contentVersion": "1.0.0.0",
    "parameters": {key: {"value": value} for key, value in payload.items()},
}
fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
with os.fdopen(fd, "w", encoding="utf-8") as handle:
    json.dump(document, handle)
PY
}

export ENVIRONMENT_NAME LOCATION AZURE_PRINCIPAL_ID

BASE_PAYLOAD="$(python3 - <<'PY'
import json
import os

print(json.dumps({
    "environmentName": os.environ["ENVIRONMENT_NAME"],
    "location": os.environ["LOCATION"],
    "principalId": os.environ["AZURE_PRINCIPAL_ID"],
}))
PY
)"
write_parameters "$BASE_PARAMETERS_FILE" "$BASE_PAYLOAD"

if [[ "$WHAT_IF" == true ]]; then
  az deployment sub what-if \
    --name "$BASE_DEPLOYMENT_NAME" \
    --location "$LOCATION" \
    --template-file infra/main.bicep \
    --parameters "@$BASE_PARAMETERS_FILE"
  exit 0
fi

echo "Provisioning base infrastructure in $RESOURCE_GROUP."
az deployment sub create \
  --name "$BASE_DEPLOYMENT_NAME" \
  --location "$LOCATION" \
  --template-file infra/main.bicep \
  --parameters "@$BASE_PARAMETERS_FILE" \
  --output none

if [[ "$BASE_ONLY" == true ]]; then
  echo "Base deployment completed. Bootstrap the API identity in Catalog before deploying the Web App."
  exit 0
fi

export ADMIN_API_KEY BUILDER_CLIENT_API_KEY
export CONTROL_ENTRA_TENANT_ID CONTROL_ENTRA_CLIENT_ID CONTROL_ENTRA_CLIENT_SECRET
export CLOUD_ACCOUNT_ISSUER AZURE_PROVISIONER_IDENTITY_ID AZURE_API_EGRESS_SUBNET_ID

OUTPUTS="$(az deployment sub show \
  --name "$BASE_DEPLOYMENT_NAME" \
  --query properties.outputs \
  --output json)"

output_value() {
  local name="$1"
  OUTPUTS="$OUTPUTS" python3 - "$name" <<'PY'
import json
import os
import sys

outputs = json.loads(os.environ["OUTPUTS"])
value = outputs.get(sys.argv[1], {}).get("value")
if value is None or value == "":
    raise SystemExit(f"Missing deployment output: {sys.argv[1]}")
print(value)
PY
}

ACR_LOGIN_SERVER="$(output_value AZURE_CONTAINER_REGISTRY_ENDPOINT)"
ELSA_CONTROL_PLANID="$(output_value ELSA_CONTROL_PLANID)"
ACR_IDENTITY_ID="$(output_value ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID)"
ACR_IDENTITY_CLIENT_ID="$(output_value ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID)"
SQL_SERVER_FQDN="$(output_value CONTROL_SQL_SQLSERVERFQDN)"
API_IDENTITY_ID="$(output_value API_IDENTITY_ID)"
API_IDENTITY_CLIENT_ID="$(output_value API_IDENTITY_CLIENTID)"
export ACR_LOGIN_SERVER ELSA_CONTROL_PLANID ACR_IDENTITY_ID ACR_IDENTITY_CLIENT_ID
export SQL_SERVER_FQDN API_IDENTITY_ID API_IDENTITY_CLIENT_ID

ACR_NAME="${ACR_LOGIN_SERVER%%.azurecr.io}"
IMAGE_REPOSITORY="$ACR_LOGIN_SERVER/elsa-control/api"
IMAGE_TAGGED="$IMAGE_REPOSITORY:$IMAGE_TAG"

echo "Building and publishing the API image."
az acr login --name "$ACR_NAME" --only-show-errors
docker build \
  --platform "$DOCKER_PLATFORM" \
  --build-arg ELSA_CONTROL_IMAGE_ID="$IMAGE_TAG" \
  --file src/Hosting/ElsaControl.Api/Dockerfile \
  --tag "$IMAGE_TAGGED" \
  .
docker push "$IMAGE_TAGGED"

IMAGE_DIGEST="$(az acr manifest show-metadata \
  --registry "$ACR_NAME" \
  --name "elsa-control/api:$IMAGE_TAG" \
  --query digest \
  --output tsv \
  --only-show-errors)"
if [[ ! "$IMAGE_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "The published API image did not resolve to an immutable digest." >&2
  exit 1
fi
IMAGE="$IMAGE_REPOSITORY@$IMAGE_DIGEST"

export IMAGE

API_PAYLOAD="$(python3 - <<'PY'
import json
import os

print(json.dumps({
    "location": os.environ["LOCATION"],
    "elsa_control_outputs_azure_container_registry_endpoint": os.environ["ACR_LOGIN_SERVER"],
    "elsa_control_outputs_planid": os.environ["ELSA_CONTROL_PLANID"],
    "elsa_control_outputs_azure_container_registry_managed_identity_id": os.environ["ACR_IDENTITY_ID"],
    "elsa_control_outputs_azure_container_registry_managed_identity_client_id": os.environ["ACR_IDENTITY_CLIENT_ID"],
    "api_containerimage": os.environ["IMAGE"],
    "api_containerport": "8080",
    "adminapikey_value": os.environ["ADMIN_API_KEY"],
    "control_sql_outputs_sqlserverfqdn": os.environ["SQL_SERVER_FQDN"],
    "entratenantid_value": os.environ["CONTROL_ENTRA_TENANT_ID"],
    "entraclientid_value": os.environ["CONTROL_ENTRA_CLIENT_ID"],
    "cloudaccountissuer_value": os.environ["CLOUD_ACCOUNT_ISSUER"],
    "entraclientsecret_value": os.environ["CONTROL_ENTRA_CLIENT_SECRET"],
    "builderclientapikey_value": os.environ["BUILDER_CLIENT_API_KEY"],
    "api_identity_outputs_id": os.environ["API_IDENTITY_ID"],
    "api_identity_outputs_clientid": os.environ["API_IDENTITY_CLIENT_ID"],
    "provisioner_identity_outputs_id": os.environ["AZURE_PROVISIONER_IDENTITY_ID"],
    "api_egress_subnet_id": os.environ["AZURE_API_EGRESS_SUBNET_ID"],
}))
PY
)"
API_PARAMETERS_FILE="$(mktemp)"
write_parameters "$API_PARAMETERS_FILE" "$API_PAYLOAD"

echo "Deploying the immutable API image to App Service."
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_DEPLOYMENT_NAME" \
  --template-file infra/api/api-website.module.bicep \
  --parameters "@$API_PARAMETERS_FILE" \
  --output none

WEBAPP_COUNT="$(az webapp list --resource-group "$RESOURCE_GROUP" --query 'length(@)' --output tsv)"
if [[ "$WEBAPP_COUNT" != "1" ]]; then
  echo "Expected exactly one Web App in $RESOURCE_GROUP after deployment." >&2
  exit 1
fi
WEBAPP_NAME="$(az webapp list --resource-group "$RESOURCE_GROUP" --query '[0].name' --output tsv)"

if [[ -n "$APPLICATION_BUILD_NUMBER" ]]; then
  az webapp config appsettings set \
    --resource-group "$RESOURCE_GROUP" \
    --name "$WEBAPP_NAME" \
    --settings "Application__BuildNumber=$APPLICATION_BUILD_NUMBER" \
    --output none
fi

WEB_HOST="$(az webapp show \
  --resource-group "$RESOURCE_GROUP" \
  --name "$WEBAPP_NAME" \
  --query defaultHostName \
  --output tsv)"
echo "Deployment completed: https://$WEB_HOST"
