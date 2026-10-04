#!/usr/bin/env bash
set -euo pipefail
shopt -s inherit_errexit

# Arm or restore CloudCompatibility:StagingFixture on the staging Control Web App.
# Prints digest, image reference, build, commit, deployment id, and env var NAMES only.
# Never prints setting values or unexpected health fixture values.

SETTING_NAME='CloudCompatibility__StagingFixture'
IMAGE_REFERENCE_PATTERN='([[:alnum:]][[:alnum:].-]*)(:[[:digit:]]+)?/[[:alnum:]][[:alnum:]._/-]*(:[[:alnum:]][[:alnum:]._+-]*)?(@sha256:[[:xdigit:]]{64})?'
DIGEST_PATTERN='^sha256:[0-9a-f]{64}$'
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXCLUSIVE_HELPER="${SCRIPT_DIR}/lib/staging_compat_exclusive.py"
QA_WINDOW_ISSUES=(637 661)
FREEZE_ISSUE=508
BASELINE_CAPABILITIES_JSON='["cloud.bootstrap.v1","hosted.instances.list.v1","hosted.instances.create.v1","hosted.instances.status.v1","hosted.instances.provisioning-progress.v1","hosted.instances.overview.v1","hosted.studio.handoff.issue.v1","hosted.instances.quota-problem.v1","hosted.instances.confirmed-delete.v1","hosted.subscription.manage.v1","hosted.deployments.audit.v1"]'

fail() {
  echo "::error::$1" >&2
  return 1
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
  if ! out="$(gh api "$@")"; then
    fail "A required GitHub API call failed."
  fi
  printf '%s' "$out"
}

az_tsv() {
  local out
  if ! out="$(az "$@" --output tsv --only-show-errors)"; then
    fail "A required Azure CLI call failed."
  fi
  printf '%s' "$out"
}

list_setting_names() {
  az_tsv webapp config appsettings list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query '[].name' | LC_ALL=C sort
}

setting_present() {
  local count
  count="$(az_tsv webapp config appsettings list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query "[?name=='${SETTING_NAME}'] | length(@)")"
  [ "${count:-0}" != "0" ]
}

latest_deployment_id() {
  local id
  id="$(az_tsv webapp deployment list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query '[0].id')"
  if [ -z "$id" ]; then
    fail "Azure returned no latest Web App deployment id."
  fi
  printf '%s\n' "$id"
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
health_fixture_signal() {
  local response_file="$1"
  local raw
  raw="$(jq -r 'if has("compatibilityFixture") and (.compatibilityFixture | type == "string") then .compatibilityFixture else empty end' "$response_file")"
  if known_health_fixture "$raw"; then
    printf '%s\n' "$raw"
    return 0
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
  if [ -z "${CLOUD_COMPATIBILITY_TOKEN:-}" ]; then
    fail "Authenticated compatibility proof is required; the bearer token is not set."
  fi
  host="$(default_host_name)"
  response_file="$(mktemp)"
  if ! http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 10 \
    --header "Authorization: Bearer ${CLOUD_COMPATIBILITY_TOKEN}" \
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

prove_bff_smoke_compatible() {
  local response_file http_status
  if [ -z "${CLOUD_BFF_SMOKE_URL:-}" ]; then
    fail "BFF smoke proof is required after restore; CLOUD_BFF_SMOKE_URL is not set."
  fi
  response_file="$(mktemp)"
  local -a headers=()
  if [ -n "${CLOUD_COMPATIBILITY_TOKEN:-}" ]; then
    headers+=(--header "Authorization: Bearer ${CLOUD_COMPATIBILITY_TOKEN}")
  fi
  if ! http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 10 \
    "${headers[@]}" \
    "$CLOUD_BFF_SMOKE_URL")"; then
    rm -f "$response_file"
    fail "The BFF smoke request failed."
  fi
  if [ "$http_status" != "200" ]; then
    rm -f "$response_file"
    fail "The BFF smoke proof did not return HTTP 200."
  fi
  require_json_object "$(cat "$response_file")" "BFF smoke response"
  if ! jq -e '.compatible == true or .status == "compatible"' "$response_file" >/dev/null; then
    rm -f "$response_file"
    fail "The BFF smoke proof did not report compatible."
  fi
  rm -f "$response_file"
  echo "BFF smoke reported compatible."
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
  rm -f "$response_file"
  if [ -z "$HEALTH_BUILD_NUMBER" ] || [ -z "$HEALTH_COMMIT" ]; then
    fail "Control /health did not return a build number and commit."
  fi
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
    printf -- '- Latest deployment id: %s\n' "$deployment_id"
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
  if ! deployment_ids="$(gh_get --paginate \
    "repos/${repo}/deployments?environment=test&per_page=50" \
    --jq '.[] | .id')"; then
    return 1
  fi
  count=0
  while IFS= read -r deployment_id && [ "$count" -lt 20 ]; do
    [[ "$deployment_id" =~ ^[0-9]+$ ]] || continue
    count=$((count + 1))
    if ! statuses="$(gh_get "repos/${repo}/deployments/${deployment_id}/statuses?per_page=1")"; then
      return 1
    fi
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
    if ! raw="$(gh_get --paginate \
      "repos/${repo}/actions/runs?status=${status}&per_page=50" \
      --jq '.workflow_runs[] | {id, name, path, status}')"; then
      return 1
    fi
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
  test_ids="$(collect_test_environment_run_ids)"
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
  runs_json="$(collect_runs)"
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
  require_azure_target
  check_exclusive

  local digest image_reference names_file deployment_id
  digest="$(serving_image_digest)"
  image_reference="$(serving_image_reference)"
  deployment_id="$(latest_deployment_id)"
  read_health_identity
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  if grep -Fxq "$SETTING_NAME" "$names_file"; then
    fail "The compatibility fixture is already present; refusing to change staging."
  fi
  write_state_outputs "$digest" "$image_reference" "$HEALTH_BUILD_NUMBER" "$HEALTH_COMMIT" "$deployment_id" "$names_file"
  write_summary "Staging compatibility fixture preflight" "$digest" "$image_reference" \
    "$HEALTH_BUILD_NUMBER" "$HEALTH_COMMIT" "$deployment_id" "$names_file"
  echo "Preflight captured the serving digest, image reference, build, commit, deployment id, and env var names. The fixture is absent."
}

arm() {
  require_test_environment
  require_known_mode
  require_azure_target
  local expected_digest="${EXPECTED_DIGEST:?EXPECTED_DIGEST is required.}"
  local expected_build="${EXPECTED_BUILD_NUMBER:-}"
  local expected_commit="${EXPECTED_COMMIT:-}"
  if setting_present; then
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
  if ! setting_present; then
    fail "The compatibility fixture setting was not created."
  fi
  restart_webapp
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf 'armed_at=%s\n' "$(date +%s)" >> "$GITHUB_OUTPUT"
  fi
  wait_until_recycle_witness "$expected_digest" "$FIXTURE_MODE" "$expected_build" "$expected_commit"
  prove_authenticated_compatibility "$FIXTURE_MODE"
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
  if ! setting_present; then
    echo "The compatibility fixture was never written; restore is a no-op and will not restart the Web App."
    return 0
  fi
  if [ -z "$expected_digest" ]; then
    echo "No preflight digest was captured; restoring from the current serving digest."
    expected_digest="$(serving_image_digest)"
  fi
  if ! az webapp config appsettings delete \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --setting-names "$SETTING_NAME" \
    --output none \
    --only-show-errors; then
    fail "Deleting the compatibility fixture app setting failed."
  fi
  if setting_present; then
    fail "The compatibility fixture setting is still present after restore."
  fi
  restart_webapp
  wait_until_recycle_witness "$expected_digest" "" "$expected_build" "$expected_commit"
  local digest image_reference names_file deployment_id
  digest="$(serving_image_digest)"
  image_reference="$(serving_image_reference)"
  deployment_id="$(latest_deployment_id)"
  read_health_identity
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  if [ -n "$expected_names" ] && [ -f "$expected_names" ]; then
    if ! names_match "$expected_names" "$names_file"; then
      fail "Env var names after restore do not match the preflight baseline."
    fi
  fi
  if [ "$digest" != "$expected_digest" ]; then
    fail "The serving digest after restore does not match the preflight baseline."
  fi
  if [ -n "$expected_build" ] && [ "$HEALTH_BUILD_NUMBER" != "$expected_build" ]; then
    fail "The /health build number after restore does not match the preflight baseline."
  fi
  if [ -n "$expected_commit" ] && [ "$HEALTH_COMMIT" != "$expected_commit" ]; then
    fail "The /health commit after restore does not match the preflight baseline."
  fi
  if [ -n "$expected_deployment" ] && [ "$deployment_id" != "$expected_deployment" ]; then
    fail "The latest deployment id after restore does not match the preflight baseline."
  fi
  prove_authenticated_compatibility ""
  prove_bff_smoke_compatible
  write_summary "Staging compatibility fixture restore" "$digest" "$image_reference" \
    "$HEALTH_BUILD_NUMBER" "$HEALTH_COMMIT" "$deployment_id" "$names_file"
  echo "Restored the baseline: setting deleted, same deployed build, env var names match."
}

usage() {
  echo "Usage: $0 preflight|arm|restore" >&2
}

main() {
  case "${1:-}" in
    preflight) preflight ;;
    arm) arm ;;
    restore) restore ;;
    *)
      usage
      return 2
      ;;
  esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
fi
