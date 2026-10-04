#!/usr/bin/env bash
set -euo pipefail
shopt -s inherit_errexit

# Arm or restore CloudCompatibility:StagingFixture on the staging Control Web App.
# Prints digest, image reference, build, imageId, Deploy-staging GitHub
# deployment id, and env var NAMES only. Never prints setting values,
# tokens, or unexpected health fixture values.

SETTING_NAME='CloudCompatibility__StagingFixture'
IMAGE_REFERENCE_PATTERN='([[:alnum:]][[:alnum:].-]*)(:[[:digit:]]+)?/[[:alnum:]][[:alnum:]._/-]*(:[[:alnum:]][[:alnum:]._+-]*)?(@sha256:[[:xdigit:]]{64})?'
DIGEST_PATTERN='^sha256:[0-9a-f]{64}$'
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXCLUSIVE_HELPER="${SCRIPT_DIR}/lib/staging_compat_exclusive.py"
QA_WINDOW_ISSUES=(637 661)
FREEZE_ISSUE=508
DEPLOY_STAGING_WORKFLOW_PATH='.github/workflows/azure-api-deploy.yml'
SUPABASE_PROJECT_REF_PATTERN='^[a-z0-9]{20}$'
PRODUCTION_SUPABASE_PROJECT_REF='jhrcnclyydzngnyvhdht'
BASELINE_CAPABILITIES_JSON='["cloud.bootstrap.v1","hosted.instances.list.v1","hosted.instances.create.v1","hosted.instances.status.v1","hosted.instances.provisioning-progress.v1","hosted.instances.overview.v1","hosted.studio.handoff.issue.v1","hosted.instances.quota-problem.v1","hosted.instances.confirmed-delete.v1","hosted.subscription.manage.v1","hosted.deployments.audit.v1"]'

fail() {
  echo "::error::$1" >&2
  # exit 1 only ends a command-substitution subshell. Terminate the
  # top-level script so if/! /$(...) callers cannot continue after a failure.
  if [ "${BASHPID:-$$}" -ne "$$" ]; then
    kill -s TERM "$$" 2>/dev/null || true
  fi
  exit 1
}

require_test_environment() {
  if [ "${TARGET_ENVIRONMENT:-}" != "test" ]; then
    fail "The compatibility fixture can run only in the test environment."
  fi
}

require_known_mode() {
  case "${FIXTURE_MODE:-}" in
    missing-capability|older-contract) return 0 ;;
    *)
      fail "The compatibility fixture mode is not recognized."
      ;;
  esac
}

require_azure_target() {
  : "${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required.}"
  : "${AZURE_WEBAPP_NAME:?AZURE_WEBAPP_NAME is required.}"
}

require_json() {
  local payload="$1"
  local what="$2"
  if ! printf '%s' "$payload" | jq -e 'type == "object" or type == "array"' >/dev/null 2>&1; then
    fail "${what} was not valid JSON."
  fi
}

require_json_array() {
  local payload="$1"
  local what="$2"
  if ! printf '%s' "$payload" | jq -e 'type == "array"' >/dev/null 2>&1; then
    fail "${what} was not a JSON array."
  fi
}

require_json_object() {
  local payload="$1"
  local what="$2"
  if ! printf '%s' "$payload" | jq -e 'type == "object"' >/dev/null 2>&1; then
    fail "${what} was not a JSON object."
  fi
}

gh_get() {
  local out
  out="$(gh api "$@")" || fail "A required GitHub API call failed."
  printf '%s' "$out"
}

az_tsv() {
  local out
  out="$(az "$@" --output tsv --only-show-errors)" || fail "A required Azure CLI call failed."
  printf '%s' "$out"
}

list_setting_names() {
  az_tsv webapp config appsettings list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query '[].name' | LC_ALL=C sort
}

# Three outcomes: prints present|absent, or fail() exits on a read error.
# A failed Azure read is never treated as absent.
read_setting_presence() {
  local attempt count
  local attempts="${SETTING_READ_ATTEMPTS:-3}"
  local retry="${SETTING_READ_RETRY_SECONDS:-2}"
  for attempt in $(seq 1 "$attempts"); do
    if count="$(az webapp config appsettings list \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --query "[?name=='${SETTING_NAME}'] | length(@)" \
      --output tsv \
      --only-show-errors)"; then
      if [[ "$count" =~ ^[0-9]+$ ]]; then
        if [ "$count" = "0" ]; then
          printf '%s\n' "absent"
        else
          printf '%s\n' "present"
        fi
        return 0
      fi
    fi
    if [ "$attempt" -lt "$attempts" ]; then
      sleep "$retry"
    fi
  done
  fail "Could not read whether the compatibility fixture setting is present."
}

url_host() {
  python3 -c 'from urllib.parse import urlparse; import sys; print(urlparse(sys.argv[1]).hostname or "")' "$1"
}

missing_smoke_inputs() {
  local -a missing=()
  [ -n "${STAGING_E2E_COMPAT_EMAIL:-}" ] || missing+=("secret STAGING_E2E_COMPAT_EMAIL")
  [ -n "${STAGING_E2E_COMPAT_PASSWORD:-}" ] || missing+=("secret STAGING_E2E_COMPAT_PASSWORD")
  [ -n "${VITE_SUPABASE_PUBLISHABLE_KEY:-}" ] || missing+=("secret VITE_SUPABASE_PUBLISHABLE_KEY")
  [ -n "${STAGING_SUPABASE_PROJECT_REF:-}" ] || missing+=("variable STAGING_SUPABASE_PROJECT_REF")
  [ -n "${EXPECTED_STAGING_SUPABASE_ORIGIN:-}" ] || missing+=("variable EXPECTED_STAGING_SUPABASE_ORIGIN")
  [ -n "${CLOUD_BFF_SMOKE_URL:-}" ] || missing+=("variable CLOUD_BFF_SMOKE_URL")
  if [ "${#missing[@]}" -gt 0 ]; then
    local IFS=', '
    fail "Compatibility fixture inputs are missing: ${missing[*]}."
  fi
}

pinned_staging_issuer() {
  printf 'https://%s.supabase.co/auth/v1\n' "$STAGING_SUPABASE_PROJECT_REF"
}

pinned_staging_origin() {
  printf 'https://%s.supabase.co\n' "$STAGING_SUPABASE_PROJECT_REF"
}

refuse_production_supabase_ref() {
  local value="${1:-}"
  local what="$2"
  if [[ "$value" == *"$PRODUCTION_SUPABASE_PROJECT_REF"* ]]; then
    fail "${what} uses the production Supabase project ref; the fixture refuses to run."
  fi
}

require_pinned_staging_ref() {
  if [[ ! "${STAGING_SUPABASE_PROJECT_REF:-}" =~ $SUPABASE_PROJECT_REF_PATTERN ]]; then
    fail "STAGING_SUPABASE_PROJECT_REF must be the pinned 20-character staging Supabase project ref."
  fi
  refuse_production_supabase_ref "$STAGING_SUPABASE_PROJECT_REF" "STAGING_SUPABASE_PROJECT_REF"
}

require_staging_supabase_origin() {
  require_pinned_staging_ref
  local expected host
  expected="$(pinned_staging_origin)"
  if [ "${EXPECTED_STAGING_SUPABASE_ORIGIN}" != "$expected" ]; then
    fail "EXPECTED_STAGING_SUPABASE_ORIGIN must be the pinned staging Supabase origin."
  fi
  host="$(url_host "$EXPECTED_STAGING_SUPABASE_ORIGIN")"
  refuse_production_supabase_ref "$host" "EXPECTED_STAGING_SUPABASE_ORIGIN"
  if [ "$host" != "${STAGING_SUPABASE_PROJECT_REF}.supabase.co" ]; then
    fail "EXPECTED_STAGING_SUPABASE_ORIGIN host must be the pinned staging Supabase project ref."
  fi
}

require_bff_url_matches_pinned_ref() {
  require_pinned_staging_ref
  local host
  host="$(url_host "$CLOUD_BFF_SMOKE_URL")"
  refuse_production_supabase_ref "$host" "CLOUD_BFF_SMOKE_URL"
  if [ "$host" != "${STAGING_SUPABASE_PROJECT_REF}.supabase.co" ]; then
    fail "CLOUD_BFF_SMOKE_URL host must be the pinned staging Supabase project ref."
  fi
}

require_smoke_inputs() {
  missing_smoke_inputs
  require_staging_supabase_origin
  require_bff_url_matches_pinned_ref
}

jwt_claim() {
  local token="$1"
  local claim="$2"
  python3 -c '
import base64, json, sys
token, claim = sys.argv[1], sys.argv[2]
parts = token.split(".")
if len(parts) < 2:
    raise SystemExit("The minted Cloud token is not a JWT.")
payload = parts[1] + "=" * ((4 - len(parts[1]) % 4) % 4)
try:
    data = json.loads(base64.urlsafe_b64decode(payload.encode("ascii")))
except Exception:
    raise SystemExit("The minted Cloud token payload could not be decoded.")
value = data.get(claim, "")
if isinstance(value, list):
    print("authenticated" if "authenticated" in value else (value[0] if value else ""))
elif value is None:
    print("")
else:
    print(value)
' "$token" "$claim" || fail "The minted Cloud token payload could not be decoded."
}

mask_secret() {
  local value="$1"
  if [ -n "$value" ]; then
    printf '::add-mask::%s\n' "$value"
  fi
}

require_minted_token_claims() {
  local token="${STAGING_CLOUD_ACCESS_TOKEN:-}"
  if [ -z "$token" ]; then
    fail "Authenticated compatibility proof is required; the Cloud access token was not minted."
  fi
  require_pinned_staging_ref
  local issuer audience role expected
  issuer="$(jwt_claim "$token" iss)"
  audience="$(jwt_claim "$token" aud)"
  role="$(jwt_claim "$token" role)"
  expected="$(pinned_staging_issuer)"
  refuse_production_supabase_ref "$issuer" "The minted Cloud token issuer"
  if [ "$issuer" != "$expected" ]; then
    fail "The minted Cloud token issuer must be the pinned staging Supabase Auth issuer."
  fi
  if [ "$audience" != "authenticated" ]; then
    fail "The minted Cloud token audience must be authenticated."
  fi
  if [ "$role" != "authenticated" ]; then
    fail "The minted Cloud token role must be authenticated."
  fi
}

mint_cloud_token() {
  require_smoke_inputs
  local response_file http_status token grant_url
  grant_url="${EXPECTED_STAGING_SUPABASE_ORIGIN}/auth/v1/token?grant_type=password"
  response_file="$(mktemp)"
  if ! http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 15 \
    --request POST \
    --header "apikey: ${VITE_SUPABASE_PUBLISHABLE_KEY}" \
    --header "Content-Type: application/json" \
    --data "$(jq -cn --arg email "$STAGING_E2E_COMPAT_EMAIL" --arg password "$STAGING_E2E_COMPAT_PASSWORD" \
      '{email:$email,password:$password}')" \
    "$grant_url")"; then
    rm -f "$response_file"
    fail "The staging Cloud password grant failed."
  fi
  if [ "$http_status" != "200" ]; then
    rm -f "$response_file"
    fail "The staging Cloud password grant did not return HTTP 200."
  fi
  require_json_object "$(cat "$response_file")" "Staging Cloud password-grant response"
  token="$(jq -r '.access_token // empty' "$response_file")"
  rm -f "$response_file"
  if [ -z "$token" ]; then
    fail "The staging Cloud password grant did not return an access token."
  fi
  mask_secret "$token"
  STAGING_CLOUD_ACCESS_TOKEN="$token"
  export STAGING_CLOUD_ACCESS_TOKEN
  require_minted_token_claims
  echo "Minted a per-run Cloud user access token and masked it."
}

workflow_run_path() {
  local run_id="$1"
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local path
  path="$(gh_get "repos/${repo}/actions/runs/${run_id}" --jq '.path')"
  printf '%s\n' "$path"
}

# Latest GitHub test-environment deployment created by Deploy staging
# (azure-api-deploy.yml). The fixture job also runs in environment: test, so
# an unfiltered latest test deployment is this run and would always mismatch.
latest_deploy_staging_deployment_id() {
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local deployment_ids deployment_id statuses url run_id path
  deployment_ids="$(gh_get --paginate \
    "repos/${repo}/deployments?environment=test&per_page=50" \
    --jq '.[] | .id')"
  while IFS= read -r deployment_id; do
    [[ "$deployment_id" =~ ^[0-9]+$ ]] || continue
    statuses="$(gh_get "repos/${repo}/deployments/${deployment_id}/statuses?per_page=1")"
    require_json_array "$statuses" "Test-environment deployment status"
    url="$(printf '%s\n' "$statuses" | jq -r '.[0].log_url // .[0].target_url // empty')"
    if ! run_id="$(extract_run_id_from_url "$url")"; then
      fail "A test-environment deployment could not be mapped to a workflow run."
    fi
    path="$(workflow_run_path "$run_id")"
    if [ "$path" = "$DEPLOY_STAGING_WORKFLOW_PATH" ]; then
      printf '%s\n' "$deployment_id"
      return 0
    fi
  done <<< "$deployment_ids"
  fail "No GitHub test deployment created by Deploy staging was found."
}

assert_no_deploy_staging_since() {
  local since="${1:-}"
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local rows deployment_id created statuses url run_id path
  if [ -z "$since" ]; then
    fail "The preflight start time is required to prove Deploy staging did not start."
  fi
  # Only GitHub environment `test` deployments. azure-api-deploy.yml also
  # deploys production and development; those must not fail restore.
  # Newest-first: stop at the first row older than preflight.
  rows="$(gh_get --paginate \
    "repos/${repo}/deployments?environment=test&per_page=100" \
    --jq '.[] | [.id, .created_at] | @tsv')"
  while IFS=$'\t' read -r deployment_id created; do
    [[ "$deployment_id" =~ ^[0-9]+$ ]] || continue
    if [ -z "$created" ]; then
      fail "A test-environment deployment created_at could not be read."
    fi
    if ! [[ "$created" > "$since" ]]; then
      break
    fi
    statuses="$(gh_get "repos/${repo}/deployments/${deployment_id}/statuses?per_page=1")"
    require_json_array "$statuses" "Test-environment deployment status"
    url="$(printf '%s\n' "$statuses" | jq -r '.[0].log_url // .[0].target_url // empty')"
    if ! run_id="$(extract_run_id_from_url "$url")"; then
      fail "A test-environment deployment could not be mapped to a workflow run."
    fi
    if [ "$run_id" = "${GITHUB_RUN_ID:-}" ]; then
      continue
    fi
    path="$(workflow_run_path "$run_id")"
    if [ "$path" = "$DEPLOY_STAGING_WORKFLOW_PATH" ]; then
      fail "A Deploy staging run started between preflight and postflight."
    fi
  done <<< "$rows"
}

restart_webapp() {
  if ! az webapp restart \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --output none \
    --only-show-errors; then
    fail "The staging Web App restart failed."
  fi
}

serving_image_reference() {
  local linux_fx_version image
  linux_fx_version="$(az_tsv webapp config show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query linuxFxVersion)"
  if [[ "$linux_fx_version" =~ ^DOCKER\|$IMAGE_REFERENCE_PATTERN$ ]]; then
    printf '%s\n' "${linux_fx_version#DOCKER|}"
    return 0
  fi
  if [ "$linux_fx_version" = "SITECONTAINERS" ]; then
    image="$(az_tsv webapp sitecontainers show \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --container-name main \
      --query image)"
    if [[ "$image" =~ ^$IMAGE_REFERENCE_PATTERN$ ]]; then
      printf '%s\n' "$image"
      return 0
    fi
  fi
  fail "The captured Linux runtime has an unsupported format; expected DOCKER|... or SITECONTAINERS."
}

image_digest() {
  local image="$1"
  local digest=""
  case "$image" in
    *@sha256:*)
      digest="${image##*@}"
      ;;
    *)
      if [ -z "${AZURE_CONTAINER_REGISTRY_ENDPOINT:-}" ]; then
        fail "The serving image is not digest-pinned and no registry endpoint is configured."
      fi
      local expected_repository="${AZURE_CONTAINER_REGISTRY_ENDPOINT}/elsa-control/api"
      local acr_name="${AZURE_CONTAINER_REGISTRY_ENDPOINT%%.azurecr.io}"
      local tag=""
      case "$image" in
        "$expected_repository":*)
          tag="${image#"$expected_repository":}"
          ;;
        *)
          fail "The captured serving image is not owned by the configured API repository."
          ;;
      esac
      if [[ ! "$tag" =~ ^[[:alnum:]][[:alnum:]._+-]{0,127}$ ]]; then
        fail "The captured serving image tag has an unexpected format."
      fi
      digest="$(az_tsv acr manifest show-metadata \
        --registry "$acr_name" \
        --name "elsa-control/api:$tag" \
        --query digest)"
      ;;
  esac
  if [[ ! "$digest" =~ $DIGEST_PATTERN ]]; then
    fail "The serving image did not resolve to an immutable digest."
  fi
  printf '%s\n' "$digest"
}

default_host_name() {
  local host
  host="$(az_tsv webapp show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query defaultHostName)"
  if [ -z "$host" ]; then
    fail "Azure returned no API host."
  fi
  printf '%s\n' "$host"
}

known_health_fixture() {
  case "${1:-}" in
    ""|missing-capability|older-contract) return 0 ;;
    *) return 1 ;;
  esac
}

# Witness only. /health.compatibilityFixture is never the arm/restore proof.
# A missing field is not null; that fails closed as unrecognized.
health_fixture_signal() {
  local response_file="$1"
  if jq -e 'has("compatibilityFixture") and .compatibilityFixture == null' "$response_file" >/dev/null 2>&1; then
    printf '\n'
    return 0
  fi
  if jq -e 'has("compatibilityFixture") and (.compatibilityFixture | type == "string")' "$response_file" >/dev/null 2>&1; then
    local raw
    raw="$(jq -r '.compatibilityFixture' "$response_file")"
    if known_health_fixture "$raw"; then
      printf '%s\n' "$raw"
      return 0
    fi
  fi
  printf '%s\n' "__unrecognized__"
}

health_field() {
  local response_file="$1"
  local field="$2"
  jq -r --arg field "$field" '.[$field] // empty' "$response_file"
}

wait_until_recycle_witness() {
  local expected_digest="$1"
  local expected_fixture="${2-}"
  local expected_build="${3-}"
  local expected_commit="${4-}"
  local host health_url response_file http_status stable attempts retry curl_max budget started remaining
  if ! known_health_fixture "$expected_fixture"; then
    fail "The expected compatibility fixture witness is not recognized."
  fi
  host="$(default_host_name)"
  health_url="https://${host}/health"
  response_file="$(mktemp)"
  stable=0
  attempts="${HEALTH_ATTEMPTS:-30}"
  retry="${HEALTH_RETRY_SECONDS:-10}"
  curl_max="${HEALTH_CURL_MAX_TIME:-10}"
  budget="${HEALTH_BUDGET_SECONDS:-}"
  started="$SECONDS"
  local attempt
  for attempt in $(seq 1 "$attempts"); do
    if [ -n "$budget" ]; then
      remaining=$((budget - (SECONDS - started)))
      if [ "$remaining" -le 0 ]; then
        rm -f "$response_file"
        fail "Control did not recycle onto the expected Web App process."
      fi
    fi
    if http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time "$curl_max" "$health_url")"; then
      if [ "$http_status" = "200" ] && jq -e '.status == "ok"' "$response_file" >/dev/null 2>&1; then
        local live_fixture live_build live_commit live_digest
        live_build="$(health_field "$response_file" buildNumber)"
        live_commit="$(health_field "$response_file" imageId)"
        if [ -n "$expected_build" ] && [ "$live_build" != "$expected_build" ]; then
          stable=0
        elif [ -n "$expected_commit" ] && [ "$live_commit" != "$expected_commit" ]; then
          stable=0
        else
          live_fixture="$(health_fixture_signal "$response_file")"
          if [ "$live_fixture" = "$expected_fixture" ]; then
            stable=$((stable + 1))
            if [ "$stable" -ge 2 ]; then
              live_digest="$(serving_image_digest)"
              rm -f "$response_file"
              if [ "$live_digest" != "$expected_digest" ]; then
                fail "The serving image digest changed; the fixture must keep the same deployed build."
              fi
              echo "Recycle witness: /health is ok on the captured build and commit. This is not the proof."
              return 0
            fi
          else
            stable=0
          fi
        fi
      else
        stable=0
      fi
    else
      http_status=000
      stable=0
    fi
    echo "Health witness returned HTTP ${http_status} (attempt ${attempt}/${attempts}, stable ${stable}/2); retrying."
    if [ -n "$budget" ]; then
      remaining=$((budget - (SECONDS - started)))
      if [ "$remaining" -le 0 ]; then
        rm -f "$response_file"
        fail "Control did not recycle onto the expected Web App process."
      fi
      if [ "$retry" -gt "$remaining" ]; then
        sleep "$remaining"
      else
        sleep "$retry"
      fi
    else
      sleep "$retry"
    fi
  done
  rm -f "$response_file"
  fail "Control did not recycle onto the expected Web App process."
}

serving_image_digest() {
  image_digest "$(serving_image_reference)"
}

expected_compatibility_json() {
  local mode="${1-}"
  case "$mode" in
    "")
      jq -cn --argjson capabilities "$BASELINE_CAPABILITIES_JSON" \
        '{contractVersion:1,capabilities:$capabilities}'
      ;;
    missing-capability)
      jq -cn --argjson capabilities "$BASELINE_CAPABILITIES_JSON" \
        '{contractVersion:1,capabilities:[$capabilities[] | select(. != "hosted.instances.provisioning-progress.v1")]}'
      ;;
    older-contract)
      jq -cn --argjson capabilities "$BASELINE_CAPABILITIES_JSON" \
        '{contractVersion:0,capabilities:$capabilities}'
      ;;
    *)
      fail "The expected compatibility contract is not recognized."
      ;;
  esac
}

prove_authenticated_compatibility() {
  local mode="${1-}"
  local host response_file http_status expected actual
  if [ -z "${STAGING_CLOUD_ACCESS_TOKEN:-}" ]; then
    fail "Authenticated compatibility proof is required; the Cloud access token was not minted."
  fi
  host="$(default_host_name)"
  response_file="$(mktemp)"
  if ! http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 10 \
    --header "Authorization: Bearer ${STAGING_CLOUD_ACCESS_TOKEN}" \
    --header "Accept: application/json" \
    "https://${host}/api/cloud/compatibility")"; then
    rm -f "$response_file"
    fail "The authenticated compatibility request failed."
  fi
  if [ "$http_status" != "200" ]; then
    rm -f "$response_file"
    fail "The authenticated compatibility proof did not return HTTP 200."
  fi
  require_json_object "$(cat "$response_file")" "Authenticated compatibility response"
  expected="$(expected_compatibility_json "$mode" | jq -S -c '{contractVersion,capabilities}')"
  actual="$(jq -S -c '{contractVersion,capabilities}' "$response_file")"
  rm -f "$response_file"
  if [ "$actual" != "$expected" ]; then
    fail "The authenticated compatibility contract did not match the expected baseline or fixture mode."
  fi
  echo "Authenticated /api/cloud/compatibility matched the expected contract."
}

post_bff_compatibility() {
  local response_file="$1"
  local http_status
  if [ -z "${CLOUD_BFF_SMOKE_URL:-}" ]; then
    fail "BFF smoke proof is required; CLOUD_BFF_SMOKE_URL is not set."
  fi
  if [ -z "${STAGING_CLOUD_ACCESS_TOKEN:-}" ]; then
    fail "BFF smoke proof is required; the Cloud access token was not minted."
  fi
  if [ -z "${VITE_SUPABASE_PUBLISHABLE_KEY:-}" ]; then
    fail "BFF smoke proof is required; secret VITE_SUPABASE_PUBLISHABLE_KEY is not set."
  fi
  if ! http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 10 \
    --request POST \
    --header "Authorization: Bearer ${STAGING_CLOUD_ACCESS_TOKEN}" \
    --header "apikey: ${VITE_SUPABASE_PUBLISHABLE_KEY}" \
    --header "Content-Type: application/json" \
    --data '{"action":"compatibility"}' \
    "$CLOUD_BFF_SMOKE_URL")"; then
    rm -f "$response_file"
    fail "The BFF compatibility request failed."
  fi
  printf '%s\n' "$http_status"
}

prove_bff_smoke_compatible() {
  local response_file http_status
  response_file="$(mktemp)"
  if ! http_status="$(post_bff_compatibility "$response_file")"; then
    rm -f "$response_file"
    exit 1
  fi
  if [ "$http_status" != "200" ]; then
    rm -f "$response_file"
    fail "The BFF smoke proof did not return HTTP 200."
  fi
  require_json_object "$(cat "$response_file")" "BFF smoke response"
  if ! jq -e '.data.state == "compatible" and .data.contractVersion == 1' "$response_file" >/dev/null; then
    rm -f "$response_file"
    fail "The BFF smoke proof did not report compatible at contractVersion 1."
  fi
  rm -f "$response_file"
  echo "BFF smoke reported compatible."
}

prove_bff_update_in_progress() {
  local response_file http_status
  response_file="$(mktemp)"
  if ! http_status="$(post_bff_compatibility "$response_file")"; then
    rm -f "$response_file"
    exit 1
  fi
  if [ "$http_status" != "503" ]; then
    rm -f "$response_file"
    fail "The armed BFF compatibility proof did not return HTTP 503."
  fi
  require_json_object "$(cat "$response_file")" "Armed BFF compatibility response"
  if ! jq -e '.code == "control_update_in_progress"' "$response_file" >/dev/null; then
    rm -f "$response_file"
    fail "The armed BFF compatibility proof did not report control_update_in_progress."
  fi
  rm -f "$response_file"
  echo "BFF reported control_update_in_progress while the fixture was armed."
}

read_health_identity() {
  local host health_url response_file http_status
  host="$(default_host_name)"
  health_url="https://${host}/health"
  response_file="$(mktemp)"
  if ! http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 10 "$health_url")"; then
    rm -f "$response_file"
    fail "The /health request failed."
  fi
  if [ "$http_status" != "200" ] || ! jq -e '.status == "ok"' "$response_file" >/dev/null 2>&1; then
    rm -f "$response_file"
    fail "Control /health was not ok while capturing the baseline."
  fi
  HEALTH_BUILD_NUMBER="$(health_field "$response_file" buildNumber)"
  HEALTH_COMMIT="$(health_field "$response_file" imageId)"
  HEALTH_FIXTURE="$(health_fixture_signal "$response_file")"
  rm -f "$response_file"
  if [ -z "$HEALTH_BUILD_NUMBER" ] || [ -z "$HEALTH_COMMIT" ]; then
    fail "Control /health did not return a build number and commit."
  fi
}

require_null_health_fixture() {
  if [ "${HEALTH_FIXTURE:-}" != "" ]; then
    fail "The /health compatibilityFixture must be JSON null before arming and after restore."
  fi
}

require_restore_baseline() {
  local expected_digest="$1"
  local expected_names="$2"
  local expected_build="$3"
  local expected_commit="$4"
  local expected_image="$5"
  local expected_deployment="$6"
  local expected_started="$7"
  if [ -z "$expected_digest" ]; then
    fail "Restore is missing the preflight digest."
  fi
  if [ -z "$expected_names" ] || [ ! -f "$expected_names" ]; then
    fail "Restore is missing the preflight env-var names file."
  fi
  if [ -z "$expected_build" ]; then
    fail "Restore is missing the preflight /health build number."
  fi
  if [ -z "$expected_commit" ]; then
    fail "Restore is missing the preflight /health imageId."
  fi
  if [ -z "$expected_image" ]; then
    fail "Restore is missing the preflight sitecontainers image reference."
  fi
  if [ -z "$expected_deployment" ]; then
    fail "Restore is missing the preflight Deploy staging deployment id."
  fi
  if [ -z "$expected_started" ]; then
    fail "Restore is missing the preflight start time."
  fi
}

assert_health_agrees_with_image() {
  local image="$1"
  local digest="$2"
  local image_id="$3"
  local tag=""
  case "$image" in
    *@sha256:*)
      if [ "${image##*@}" != "$digest" ]; then
        fail "The sitecontainers image digest does not match the resolved digest."
      fi
      if [ -z "${AZURE_CONTAINER_REGISTRY_ENDPOINT:-}" ]; then
        fail "The serving image is digest-pinned and no registry endpoint is configured to agree imageId with that digest."
      fi
      if [ "$(image_digest "${AZURE_CONTAINER_REGISTRY_ENDPOINT}/elsa-control/api:${image_id}")" != "$digest" ]; then
        fail "The /health imageId does not agree with the sitecontainers digest."
      fi
      ;;
    *:*)
      tag="${image##*:}"
      if [ "$tag" != "$image_id" ]; then
        fail "The /health imageId does not agree with the sitecontainers image tag and digest."
      fi
      ;;
    *)
      fail "The captured serving image is not a tag or digest reference."
      ;;
  esac
}

write_summary() {
  local title="$1"
  local digest="$2"
  local image_reference="$3"
  local build_number="$4"
  local commit="$5"
  local deployment_id="$6"
  local names_file="$7"
  if [ -z "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "${title}"
    echo "digest=${digest}"
    echo "image_reference=${image_reference}"
    echo "build_number=${build_number}"
    echo "commit=${commit}"
    echo "deployment_id=${deployment_id}"
    echo "env_var_names:"
    cat "$names_file"
    return 0
  fi
  {
    printf '## %s\n\n' "$title"
    printf -- '- Image digest: %s\n' "$digest"
    printf -- '- Image reference: %s\n' "$image_reference"
    printf -- '- Build number: %s\n' "$build_number"
    printf -- '- Commit: %s\n' "$commit"
    printf -- '- Deploy staging GitHub deployment id: %s\n' "$deployment_id"
    printf -- '- Env var names:\n'
    while IFS= read -r name; do
      [ -z "$name" ] && continue
      printf -- '  - %s\n' "$name"
    done < "$names_file"
    printf '\n'
    printf -- 'Baseline compatibility is contractVersion 1 with the 11 capabilities from this commit unless this change adds one.\n'
    printf -- 'Recapture this baseline from the first staging deploy of the merged change.\n'
  } >> "$GITHUB_STEP_SUMMARY"
}

write_state_outputs() {
  local digest="$1"
  local image_reference="$2"
  local build_number="$3"
  local commit="$4"
  local deployment_id="$5"
  local names_file="$6"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      printf 'digest=%s\n' "$digest"
      printf 'image_reference=%s\n' "$image_reference"
      printf 'build_number=%s\n' "$build_number"
      printf 'commit=%s\n' "$commit"
      printf 'deployment_id=%s\n' "$deployment_id"
      printf 'setting_names_path=%s\n' "$names_file"
      printf 'preflight_started_at=%s\n' "${PREFLIGHT_STARTED_AT:-}"
    } >> "$GITHUB_OUTPUT"
  fi
}

names_match() {
  local expected="$1"
  local actual="$2"
  cmp -s "$expected" "$actual"
}

fetch_issue_comments() {
  local issue="$1"
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local comments
  comments="$(gh_get --paginate "repos/${repo}/issues/${issue}/comments")"
  require_json_array "$comments" "Issue #${issue} comments"
  printf '%s' "$comments"
}

fetch_issue_state() {
  local issue="$1"
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local state
  state="$(gh_get "repos/${repo}/issues/${issue}" --jq '.state')"
  case "$state" in
    open|closed) printf '%s' "$state" ;;
    *) fail "Issue #${issue} state could not be read." ;;
  esac
}

extract_run_id_from_url() {
  local url="$1"
  if [[ "$url" =~ /actions/runs/([0-9]+) ]]; then
    printf '%s\n' "${BASH_REMATCH[1]}"
    return 0
  fi
  return 1
}

# The list-runs API has no environment filter and workflow runs have no
# environment field. Resolve active GitHub environment `test` deployments
# separately and map them to run IDs. Fail closed on API errors or a missing mapping.
collect_test_environment_run_ids() {
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local deployment_ids deployment_id state url run_id count statuses
  local ids='[]'
  deployment_ids="$(gh_get --paginate \
    "repos/${repo}/deployments?environment=test&per_page=50" \
    --jq '.[] | .id')"
  count=0
  while IFS= read -r deployment_id && [ "$count" -lt 20 ]; do
    [[ "$deployment_id" =~ ^[0-9]+$ ]] || continue
    count=$((count + 1))
    statuses="$(gh_get "repos/${repo}/deployments/${deployment_id}/statuses?per_page=1")"
    require_json_array "$statuses" "Test-environment deployment status"
    state="$(printf '%s\n' "$statuses" | jq -r '.[0].state // empty' | tr '[:upper:]' '[:lower:]')"
    case "$state" in
      in_progress|queued|pending|waiting) ;;
      "")
        fail "A test-environment deployment status could not be read."
        ;;
      *) continue ;;
    esac
    url="$(printf '%s\n' "$statuses" | jq -r '.[0].log_url // .[0].target_url // empty')"
    if ! run_id="$(extract_run_id_from_url "$url")"; then
      fail "A test-environment deployment is active but could not be mapped to a workflow run."
    fi
    ids="$(jq -cn --argjson existing "$ids" --arg id "$run_id" '$existing + [$id]')"
  done <<< "$deployment_ids"
  require_json_array "$ids" "Mapped test-environment run ids"
  printf '%s\n' "$ids"
}

collect_runs() {
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local status raw page_query
  local combined='[]'
  for status in in_progress queued waiting pending requested; do
    raw="$(gh_get --paginate \
      "repos/${repo}/actions/runs?status=${status}&per_page=50" \
      --jq '.workflow_runs[] | {id, name, path, status}')"
    if [ -n "$raw" ]; then
      if ! printf '%s\n' "$raw" | jq -se 'length == 0 or all(type == "object")' >/dev/null; then
        fail "Workflow run list was not valid JSON."
      fi
    fi
    page_query="$(printf '%s\n' "$raw" | jq -s '.')"
    require_json_array "$page_query" "Workflow run page"
    combined="$(jq -cn --argjson existing "$combined" --argjson page "$page_query" '$existing + $page')"
  done
  local test_ids
  test_ids="$(collect_test_environment_run_ids)" || exit 1
  require_json_array "$test_ids" "Test-environment run ids"
  combined="$(jq -cn --argjson runs "$combined" --argjson test_ids "$test_ids" '
    ($test_ids | map(tostring) | unique) as $ids
    | ($runs | unique_by(.id)
      | map(
          (.id | tostring) as $run_id
          | if ($ids | index($run_id)) != null then
              . + {environment: "test"}
            else
              .
            end
        )) as $annotated
    | ($ids - ($annotated | map(.id | tostring))) as $missing
    | $annotated + ($missing | map({
        id: .,
        name: "",
        path: "",
        status: "in_progress",
        environment: "test"
      }))
  ')"
  require_json_array "$combined" "Collected workflow runs"
  printf '%s\n' "$combined"
}

check_exclusive() {
  local runs_json comments issue state
  runs_json="$(collect_runs)" || exit 1
  require_json_array "$runs_json" "Collected workflow runs"
  if ! printf '%s\n' "$runs_json" | python3 "$EXCLUSIVE_HELPER" runs --runs - --this-run-id "${GITHUB_RUN_ID:-}"; then
    return 1
  fi
  for issue in "${QA_WINDOW_ISSUES[@]}"; do
    state="$(fetch_issue_state "$issue")"
    comments="$(fetch_issue_comments "$issue")"
    if ! printf '%s\n' "$comments" | python3 "$EXCLUSIVE_HELPER" qa-window --comments - --issue-state "$state" --issue "$issue"; then
      return 1
    fi
  done
  comments="$(fetch_issue_comments "$FREEZE_ISSUE")"
  if ! printf '%s\n' "$comments" | python3 "$EXCLUSIVE_HELPER" freeze --comments -; then
    return 1
  fi
}

preflight() {
  require_test_environment
  require_known_mode
  require_smoke_inputs
  require_azure_target
  check_exclusive
  mint_cloud_token
  PREFLIGHT_STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

  local digest image_reference names_file deployment_id
  image_reference="$(serving_image_reference)"
  digest="$(image_digest "$image_reference")"
  deployment_id="$(latest_deploy_staging_deployment_id)"
  read_health_identity
  require_null_health_fixture
  assert_health_agrees_with_image "$image_reference" "$digest" "$HEALTH_COMMIT"
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  if grep -Fxq "$SETTING_NAME" "$names_file"; then
    fail "The compatibility fixture is already present; refusing to change staging."
  fi
  prove_authenticated_compatibility ""
  write_state_outputs "$digest" "$image_reference" "$HEALTH_BUILD_NUMBER" "$HEALTH_COMMIT" "$deployment_id" "$names_file"
  write_summary "Staging compatibility fixture preflight" "$digest" "$image_reference" \
    "$HEALTH_BUILD_NUMBER" "$HEALTH_COMMIT" "$deployment_id" "$names_file"
  echo "Preflight captured the serving digest, image reference, build, imageId, Deploy staging deployment id, and env var names. The fixture is absent."
}

arm() {
  require_test_environment
  require_known_mode
  require_smoke_inputs
  require_azure_target
  mint_cloud_token
  local expected_digest="${EXPECTED_DIGEST:?EXPECTED_DIGEST is required.}"
  local expected_build="${EXPECTED_BUILD_NUMBER:-}"
  local expected_commit="${EXPECTED_COMMIT:-}"
  local presence
  presence="$(read_setting_presence)" || exit 1
  if [ "$presence" = "present" ]; then
    fail "The compatibility fixture is already present; refusing to change staging."
  fi
  local before_digest
  before_digest="$(serving_image_digest)"
  if [ "$before_digest" != "$expected_digest" ]; then
    fail "The serving digest no longer matches the preflight baseline."
  fi
  if ! az webapp config appsettings set \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --settings "${SETTING_NAME}=${FIXTURE_MODE}" \
    --output none \
    --only-show-errors; then
    fail "Writing the compatibility fixture app setting failed."
  fi
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf 'armed_at=%s\n' "$(date +%s)" >> "$GITHUB_OUTPUT"
  fi
  presence="$(read_setting_presence)" || exit 1
  if [ "$presence" != "present" ]; then
    fail "The compatibility fixture setting was not created."
  fi
  restart_webapp
  wait_until_recycle_witness "$expected_digest" "$FIXTURE_MODE" "$expected_build" "$expected_commit"
  prove_authenticated_compatibility "$FIXTURE_MODE"
  prove_bff_update_in_progress
  echo "Armed the compatibility fixture on the same deployed Web App build."
}

restore() {
  require_test_environment
  require_azure_target
  local expected_digest="${EXPECTED_DIGEST:-}"
  local expected_names="${EXPECTED_SETTING_NAMES_PATH:-}"
  local expected_build="${EXPECTED_BUILD_NUMBER:-}"
  local expected_commit="${EXPECTED_COMMIT:-}"
  local expected_deployment="${EXPECTED_DEPLOYMENT_ID:-}"
  local expected_image="${EXPECTED_IMAGE_REFERENCE:-}"
  local expected_started="${EXPECTED_PREFLIGHT_STARTED_AT:-}"
  local presence
  presence="$(read_setting_presence)" || exit 1
  if [ "$presence" = "absent" ]; then
    sleep "${RESTORE_ABSENT_RECHECK_SECONDS:-2}"
    presence="$(read_setting_presence)" || exit 1
  fi
  if [ "$presence" = "absent" ]; then
    echo "The compatibility fixture was never written; restore is a no-op and will not restart the Web App."
    return 0
  fi
  # Remove the fixture before minting. A password-grant failure must not
  # leave CloudCompatibility__StagingFixture set on staging.
  local witness_digest="$expected_digest"
  if [ -z "$witness_digest" ]; then
    witness_digest="$(serving_image_digest)"
  fi
  if ! az webapp config appsettings delete \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --setting-names "$SETTING_NAME" \
    --output none \
    --only-show-errors; then
    fail "Deleting the compatibility fixture app setting failed."
  fi
  presence="$(read_setting_presence)" || exit 1
  if [ "$presence" = "present" ]; then
    fail "The compatibility fixture setting is still present after restore."
  fi
  restart_webapp
  wait_until_recycle_witness "$witness_digest" "" "$expected_build" "$expected_commit"
  require_restore_baseline "$expected_digest" "$expected_names" "$expected_build" \
    "$expected_commit" "$expected_image" "$expected_deployment" "$expected_started"
  local digest image_reference names_file deployment_id
  image_reference="$(serving_image_reference)"
  digest="$(image_digest "$image_reference")"
  deployment_id="$(latest_deploy_staging_deployment_id)"
  read_health_identity
  require_null_health_fixture
  assert_health_agrees_with_image "$image_reference" "$digest" "$HEALTH_COMMIT"
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  if ! names_match "$expected_names" "$names_file"; then
    fail "Env var names after restore do not match the preflight baseline."
  fi
  if [ "$digest" != "$expected_digest" ]; then
    fail "The serving digest after restore does not match the preflight baseline."
  fi
  if [ "$HEALTH_BUILD_NUMBER" != "$expected_build" ]; then
    fail "The /health build number after restore does not match the preflight baseline."
  fi
  if [ "$HEALTH_COMMIT" != "$expected_commit" ]; then
    fail "The /health commit after restore does not match the preflight baseline."
  fi
  if [ "$image_reference" != "$expected_image" ]; then
    fail "The sitecontainers image reference after restore does not match the preflight baseline."
  fi
  if [ "$deployment_id" != "$expected_deployment" ]; then
    fail "The Deploy staging GitHub deployment id after restore does not match the preflight baseline."
  fi
  assert_no_deploy_staging_since "$expected_started"
  require_smoke_inputs
  mint_cloud_token
  prove_authenticated_compatibility ""
  prove_bff_smoke_compatible
  write_summary "Staging compatibility fixture restore" "$digest" "$image_reference" \
    "$HEALTH_BUILD_NUMBER" "$HEALTH_COMMIT" "$deployment_id" "$names_file"
  echo "Restored the baseline: setting deleted, same deployed build, env var names match."
}

usage() {
  echo "Usage: $0 preflight|arm|restore|mint" >&2
}

main() {
  case "${1:-}" in
    preflight) preflight ;;
    arm) arm ;;
    restore) restore ;;
    mint) mint_cloud_token ;;
    *)
      usage
      return 2
      ;;
  esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
fi
