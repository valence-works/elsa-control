#!/usr/bin/env bash
set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
proof_root=$(mktemp -d "${TMPDIR:-/tmp}/elsa-managed-browser-proof.XXXXXX")
proof_id=$$
proof_network="elsa-managed-browser-proof-${proof_id}"
runtime_container="elsa-managed-browser-proof-runtime-${proof_id}"
proxy_container="elsa-managed-browser-proof-proxy-${proof_id}"
runtime_alias="managed-runtime"
compose_project="elsa-managed-browser-proof-${proof_id}"
trusted_runtime_image="elsa-managed-browser-proof-runtime:${proof_id}"
runtime_origin="https://runtime.localhost:7444"
fixture_database="$proof_root/catalog.db"
control_pid=""
console_pid=""

immutable_runtime_image=${MANAGED_ELSA_PROOF_RUNTIME_IMAGE:-valenceruntimeimages.azurecr.io/runtime-combined@sha256:f078521ca5395722fbc829bcaebfd62d924664db0e6947199444296b4aabb1cf}

cleanup() {
  set +e
  if [[ -n "$console_pid" ]]; then kill "$console_pid" 2>/dev/null; fi
  if [[ -n "$control_pid" ]]; then kill "$control_pid" 2>/dev/null; fi
  docker stop "$proxy_container" "$runtime_container" >/dev/null 2>&1
  docker container rm "$proxy_container" "$runtime_container" >/dev/null 2>&1
  docker compose -p "$compose_project" -f "$repo_root/docker-compose.identity.yml" down --volumes >/dev/null 2>&1
  docker network rm "$proof_network" >/dev/null 2>&1
  docker image rm "$trusted_runtime_image" >/dev/null 2>&1
  case "$proof_root" in
    "${TMPDIR:-/tmp}"/elsa-managed-browser-proof.*) rm -rf "$proof_root" ;;
  esac
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Required command is unavailable: $1" >&2
    exit 2
  fi
}

require_free_port() {
  if lsof -nP -iTCP:"$1" -sTCP:LISTEN >/dev/null 2>&1; then
    echo "Required local port is already in use: $1" >&2
    exit 2
  fi
}

wait_for_url() {
  local name=$1
  local url=$2
  local attempts=${3:-60}
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if curl --silent --show-error --fail "$url" >/dev/null 2>&1; then
      echo "$name is ready"
      return 0
    fi
    sleep 2
  done
  echo "$name did not become ready" >&2
  return 1
}

for command in curl docker dotnet grep lsof mkcert npm openssl tee tr; do
  require_command "$command"
done
for port in 5173 5220 7094 7444 8080; do
  require_free_port "$port"
done
docker info >/dev/null

fixture_project="$repo_root/src/Hosting/ElsaControl.ManagedBrowserProof/ElsaControl.ManagedBrowserProof.csproj"
keycloak_issuer="http://127.0.0.1:8080/realms/elsa-control"
keycloak_realm="$repo_root/dev/keycloak/elsa-control-realm.json"
keycloak_username=${MANAGED_ELSA_PROOF_USERNAME:-ada}
dotnet build "$fixture_project" >/dev/null
dotnet run --no-build --project "$fixture_project" -- \
  initialize "$fixture_database" "$keycloak_issuer" "$keycloak_realm" "$keycloak_username" >/dev/null
mkcert -cert-file "$proof_root/tls.crt" -key-file "$proof_root/tls.key" runtime.localhost control.localhost localhost >/dev/null
cp "$(mkcert -CAROOT)/rootCA.pem" "$proof_root/local-root-ca.crt"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$proof_root/control-handoff-key.pem" 2>/dev/null
chmod 600 "$proof_root/control-handoff-key.pem"

dotnet run --no-build --project "$fixture_project" -- \
  seed "$fixture_database" "$runtime_origin" >/dev/null
dotnet run --no-build --project "$fixture_project" -- \
  seed "$fixture_database" "$runtime_origin" >/dev/null
if dotnet run --no-build --project "$fixture_project" -- \
  seed "$fixture_database" "https://conflict.localhost:7444" >/dev/null 2>&1; then
  echo "The fixture accepted a conflicting runtime origin." >&2
  exit 1
fi
dotnet run --no-build --project "$fixture_project" -- \
  seed "$fixture_database" "$runtime_origin" >/dev/null

docker build --platform linux/amd64 \
  --build-arg "RUNTIME_IMAGE=$immutable_runtime_image" \
  -f "$repo_root/scripts/managed-elsa-browser-proof/Dockerfile.runtime-trust" \
  -t "$trusted_runtime_image" \
  "$proof_root" >/dev/null

docker compose -p "$compose_project" -f "$repo_root/docker-compose.identity.yml" up -d >/dev/null
wait_for_url "Keycloak" "http://127.0.0.1:8080/realms/elsa-control/.well-known/openid-configuration"

docker network create "$proof_network" >/dev/null
runtime_admin_password=$(openssl rand -base64 32)
runtime_signing_key=$(openssl rand -base64 48)
docker run -d --platform linux/amd64 \
  --name "$runtime_container" \
  --network "$proof_network" \
  --network-alias "$runtime_alias" \
  -e ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
  -e Studio__HostingModel=BlazorServer \
  -e Backend__Url=http://localhost:8080/elsa/api \
  -e CShells__Shells__Default__Features__DefaultAdminUser__AdminUsername=proof-admin \
  -e CShells__Shells__Default__Features__DefaultAdminUser__AdminPassword="$runtime_admin_password" \
  -e CShells__Shells__Default__Features__Identity__SigningKey="$runtime_signing_key" \
  -e ManagedElsa__Handoff__Enabled=true \
  -e ManagedElsa__Handoff__ControlBaseUrl=https://control.localhost \
  -e ManagedElsa__Handoff__ControlContinuationUrl=https://localhost:7094/admin/runtimes \
  -e ManagedElsa__Handoff__InstanceId=00000000-0000-0000-0000-000000000185 \
  -e ManagedElsa__Handoff__Audience=urn:elsa:instance:00000000-0000-0000-0000-000000000185 \
  -e ManagedElsa__Handoff__CallbackUri="$runtime_origin/managed-elsa/handoff/callback" \
  -e ManagedElsa__Handoff__StateLifetime=00:01:00 \
  -e ManagedElsa__Handoff__UpstreamAuthenticationScheme=Jwt-or-ApiKey \
  -e ManagedElsa__Handoff__AllowedRuntimePermissions__0=read:diagnostics:structured-logs \
  "$trusted_runtime_image" >/dev/null
unset runtime_admin_password runtime_signing_key

docker run -d \
  --name "$proxy_container" \
  --network "$proof_network" \
  --network-alias control.localhost \
  -p 7444:443 \
  -v "$repo_root/scripts/managed-elsa-browser-proof/nginx.conf:/etc/nginx/nginx.conf:ro" \
  -v "$proof_root:/proof-tls:ro" \
  nginx:1.27-alpine >/dev/null
wait_for_url "Managed runtime" "$runtime_origin/health" 90

(
  export ASPNETCORE_ENVIRONMENT=Keycloak
  export ASPNETCORE_URLS='https://localhost:7094;http://localhost:5220'
  export ASPNETCORE_Kestrel__Certificates__Default__Path="$proof_root/tls.crt"
  export ASPNETCORE_Kestrel__Certificates__Default__KeyPath="$proof_root/tls.key"
  export ConnectionStrings__Catalog="Data Source=$fixture_database"
  export DataProtection__KeysPath="$proof_root/control-data-protection"
  export ManagedElsa__Handoff__Enabled=true
  export ManagedElsa__Handoff__Issuer=https://localhost:7094
  export ManagedElsa__Handoff__ActiveKeyId=local-proof
  export ManagedElsa__Handoff__ActivePrivateKeyPem
  ManagedElsa__Handoff__ActivePrivateKeyPem=$(<"$proof_root/control-handoff-key.pem")
  dotnet run --no-launch-profile --project "$repo_root/src/Hosting/ElsaControl.Api"
) >"$proof_root/control.log" 2>&1 &
control_pid=$!
wait_for_url "Elsa Control" "https://localhost:7094/health" 90

npm --prefix "$repo_root/src/Hosting/ElsaControl.Console" ci --prefer-offline --no-audit --no-fund >/dev/null
(
  export CATALOG_API_PROXY_TARGET=http://localhost:5220
  npm --prefix "$repo_root/src/Hosting/ElsaControl.Console" run dev -- --host 127.0.0.1
) >"$proof_root/console.log" 2>&1 &
console_pid=$!
wait_for_url "Elsa Control console" "http://localhost:5173/admin/"

npm --prefix "$repo_root/tests/Hosting/ElsaControl.Console.E2E" ci --prefer-offline --no-audit --no-fund >/dev/null
playwright_list_output="$proof_root/playwright-list.log"
if ! MANAGED_ELSA_BROWSER_PROOF=1 \
  MANAGED_ELSA_PROOF_DATABASE="$fixture_database" \
  MANAGED_ELSA_PROOF_RUNTIME_ORIGIN="$runtime_origin/" \
  MANAGED_ELSA_PROOF_USERNAME="$keycloak_username" \
  ADMIN_UI_BASE_URL=http://localhost:5173 \
  FORCE_COLOR=0 \
  npm --prefix "$repo_root/tests/Hosting/ElsaControl.Console.E2E" run e2e -- \
    managed-elsa-browser-proof.spec.ts --project=chromium --list --reporter=line 2>&1 | tee "$playwright_list_output"; then
  echo "Managed Elsa browser proof test discovery failed." >&2
  exit 1
fi

expected_scenarios=$(tr -d '\r' <"$playwright_list_output" | grep -Eo 'Total: [0-9]+' | tr -cd '0-9' || true)
if [[ "$expected_scenarios" != 4 ]]; then
  echo "Managed Elsa browser proof must discover exactly 4 scenarios (found: ${expected_scenarios:-none})." >&2
  exit 1
fi

playwright_output="$proof_root/playwright.log"
if ! MANAGED_ELSA_BROWSER_PROOF=1 \
  MANAGED_ELSA_PROOF_DATABASE="$fixture_database" \
  MANAGED_ELSA_PROOF_RUNTIME_ORIGIN="$runtime_origin/" \
  MANAGED_ELSA_PROOF_USERNAME="$keycloak_username" \
  ADMIN_UI_BASE_URL=http://localhost:5173 \
  FORCE_COLOR=0 \
  npm --prefix "$repo_root/tests/Hosting/ElsaControl.Console.E2E" run e2e -- \
    managed-elsa-browser-proof.spec.ts --project=chromium --reporter=line 2>&1 | tee "$playwright_output"; then
  echo "Managed Elsa local browser proof failed." >&2
  exit 1
fi

playwright_summary=$(tr -d '\r' <"$playwright_output")
if printf '%s\n' "$playwright_summary" | grep -Eiq '([1-9][0-9]* (failed|flaky|skipped|interrupted)|did not run)'; then
  echo "Managed Elsa local browser proof did not complete every discovered scenario." >&2
  exit 1
fi

if ! printf '%s\n' "$playwright_summary" | grep -Eq "(^|[[:space:]])${expected_scenarios} passed([[:space:]]|$)"; then
  echo "Managed Elsa local browser proof did not complete all $expected_scenarios scenarios." >&2
  exit 1
fi

echo "Managed Elsa local browser proof passed."
