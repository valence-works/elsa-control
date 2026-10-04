#!/usr/bin/env bash
set -euo pipefail

# Arm or restore CloudCompatibility:StagingFixture on staging Control.
# Prints digest, revision, and env var NAMES only. Never prints setting values.

SETTING_NAME='CloudCompatibility__StagingFixture'
IMAGE_REFERENCE_PATTERN='([[:alnum:]][[:alnum:].-]*)(:[[:digit:]]+)?/[[:alnum:]][[:alnum:]._/-]*(:[[:alnum:]][[:alnum:]._+-]*)?(@sha256:[[:xdigit:]]{64})?'
DIGEST_PATTERN='^sha256:[0-9a-f]{64}$'
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXCLUSIVE_HELPER="${SCRIPT_DIR}/lib/staging_compat_exclusive.py"
QA_WINDOW_ISSUES=(637 661)
FREEZE_ISSUE=508

require_test_environment() {
  if [ "${TARGET_ENVIRONMENT:-}" != "test" ]; then
    echo "::error::The compatibility fixture can run only in the test environment."
    return 1
  fi
}

require_known_mode() {
  case "${FIXTURE_MODE:-}" in
    missing-capability|older-contract) return 0 ;;
    *)
      echo "::error::The compatibility fixture mode is not recognized."
      return 1
      ;;
  esac
}

require_azure_target() {
  : "${AZURE_RESOURCE_GROUP:?AZURE_RESOURCE_GROUP is required.}"
  : "${AZURE_WEBAPP_NAME:?AZURE_WEBAPP_NAME is required.}"
}

list_setting_names() {
  az webapp config appsettings list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query '[].name' \
    --output tsv \
    --only-show-errors | LC_ALL=C sort
}

setting_present() {
  local count
  count="$(az webapp config appsettings list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query "[?name=='${SETTING_NAME}'] | length(@)" \
    --output tsv \
    --only-show-errors)"
  [ "${count:-0}" != "0" ]
}

serving_image_reference() {
  local linux_fx_version image
  linux_fx_version="$(az webapp config show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query linuxFxVersion \
    --output tsv \
    --only-show-errors)"
  if [[ "$linux_fx_version" =~ ^DOCKER\|$IMAGE_REFERENCE_PATTERN$ ]]; then
    printf '%s\n' "${linux_fx_version#DOCKER|}"
    return 0
  fi
  if [ "$linux_fx_version" = "SITECONTAINERS" ]; then
    image="$(az webapp sitecontainers show \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --container-name main \
      --query image \
      --output tsv \
      --only-show-errors)"
    if [[ "$image" =~ ^$IMAGE_REFERENCE_PATTERN$ ]]; then
      printf '%s\n' "$image"
      return 0
    fi
  fi
  echo "::error::The captured Linux runtime has an unsupported format; expected DOCKER|... or SITECONTAINERS."
  return 1
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
        echo "::error::The serving image is not digest-pinned and no registry endpoint is configured."
        return 1
      fi
      local expected_repository="${AZURE_CONTAINER_REGISTRY_ENDPOINT}/elsa-control/api"
      local acr_name="${AZURE_CONTAINER_REGISTRY_ENDPOINT%%.azurecr.io}"
      local tag=""
      case "$image" in
        "$expected_repository":*)
          tag="${image#"$expected_repository":}"
          ;;
        *)
          echo "::error::The captured serving image is not owned by the configured API repository."
          return 1
          ;;
      esac
      if [[ ! "$tag" =~ ^[[:alnum:]][[:alnum:]._+-]{0,127}$ ]]; then
        echo "::error::The captured serving image tag has an unexpected format."
        return 1
      fi
      digest="$(az acr manifest show-metadata \
        --registry "$acr_name" \
        --name "elsa-control/api:$tag" \
        --query digest \
        --output tsv \
        --only-show-errors)"
      ;;
  esac
  if [[ ! "$digest" =~ $DIGEST_PATTERN ]]; then
    echo "::error::The serving image did not resolve to an immutable digest."
    return 1
  fi
  printf '%s\n' "$digest"
}

default_host_name() {
  local host
  host="$(az webapp show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query defaultHostName \
    --output tsv \
    --only-show-errors)"
  if [ -z "$host" ]; then
    echo "::error::Azure returned no API host."
    return 1
  fi
  printf '%s\n' "$host"
}

wait_until_healthy() {
  local expected_digest="$1"
  local host health_url response_file http_status stable
  host="$(default_host_name)"
  health_url="https://${host}/health"
  response_file="$(mktemp)"
  stable=0
  local attempt
  for attempt in $(seq 1 30); do
    if http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time 10 "$health_url")"; then
      if [ "$http_status" = "200" ] && jq -e '.status == "ok"' "$response_file" >/dev/null 2>&1; then
        local live_digest
        live_digest="$(serving_image_digest)"
        if [ "$live_digest" != "$expected_digest" ]; then
          rm -f "$response_file"
          echo "::error::The serving image digest changed; the fixture must keep the same image."
          return 1
        fi
        stable=$((stable + 1))
        if [ "$stable" -ge 2 ]; then
          rm -f "$response_file"
          echo "Control is healthy on the captured image digest."
          return 0
        fi
      else
        stable=0
      fi
    else
      http_status=000
      stable=0
    fi
    echo "Health check returned HTTP ${http_status} (attempt ${attempt}/30, stable ${stable}/2); retrying."
    sleep "${HEALTH_RETRY_SECONDS:-10}"
  done
  rm -f "$response_file"
  echo "::error::Control did not become healthy after the revision restart."
  return 1
}

serving_image_digest() {
  image_digest "$(serving_image_reference)"
}

write_summary() {
  local title="$1"
  local digest="$2"
  local revision="$3"
  local names_file="$4"
  if [ -z "${GITHUB_STEP_SUMMARY:-}" ]; then
    echo "${title}"
    echo "digest=${digest}"
    echo "revision=${revision}"
    echo "env_var_names:"
    cat "$names_file"
    return 0
  fi
  {
    printf '## %s\n\n' "$title"
    printf -- '- Image digest: `%s`\n' "$digest"
    printf -- '- Revision: `%s`\n' "$revision"
    printf -- '- Env var names:\n'
    while IFS= read -r name; do
      [ -z "$name" ] && continue
      printf -- '  - `%s`\n' "$name"
    done < "$names_file"
    printf '\n'
  } >> "$GITHUB_STEP_SUMMARY"
}

capture_state() {
  local digest revision names_file
  digest="$(serving_image_digest)"
  revision="$(serving_image_reference)"
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  printf '%s\n' "$digest"
  printf '%s\n' "$revision"
  printf '%s\n' "$names_file"
}

write_state_outputs() {
  local digest="$1"
  local revision="$2"
  local names_file="$3"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      printf 'digest=%s\n' "$digest"
      printf 'revision=%s\n' "$revision"
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
  gh api --paginate "repos/${repo}/issues/${issue}/comments"
}

fetch_issue_state() {
  local issue="$1"
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  gh api "repos/${repo}/issues/${issue}" --jq '.state'
}

collect_runs() {
  local repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  local status page_query
  local combined='[]'
  for status in in_progress queued waiting pending requested; do
    page_query="$(gh api --paginate \
      "repos/${repo}/actions/runs?status=${status}&per_page=50" \
      --jq '.workflow_runs[] | {id, name, path, status, environment: ""}' \
      | jq -s '.')"
    combined="$(jq -cn --argjson existing "$combined" --argjson page "$page_query" '$existing + $page')"
  done
  local test_status test_runs
  for test_status in in_progress queued waiting pending requested; do
    test_runs="$(gh api --paginate \
      "repos/${repo}/actions/runs?environment=test&status=${test_status}&per_page=50" \
      --jq '.workflow_runs[] | {id, name, path, status, environment: "test"}' \
      | jq -s '.')"
    combined="$(jq -cn --argjson existing "$combined" --argjson page "$test_runs" '$existing + $page')"
  done
  printf '%s\n' "$combined"
}

check_exclusive() {
  local runs_json comments issue state
  runs_json="$(collect_runs)"
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

  local digest revision names_file
  digest="$(serving_image_digest)"
  revision="$(serving_image_reference)"
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  if grep -Fxq "$SETTING_NAME" "$names_file"; then
    echo "::error::The compatibility fixture is already present; refusing to change staging."
    return 1
  fi
  write_state_outputs "$digest" "$revision" "$names_file"
  write_summary "Staging compatibility fixture preflight" "$digest" "$revision" "$names_file"
  echo "Preflight captured the serving digest, revision, and env var names. The fixture is absent."
}

arm() {
  require_test_environment
  require_known_mode
  require_azure_target
  local expected_digest="${EXPECTED_DIGEST:?EXPECTED_DIGEST is required.}"
  if setting_present; then
    echo "::error::The compatibility fixture is already present; refusing to change staging."
    return 1
  fi
  local before_digest
  before_digest="$(serving_image_digest)"
  if [ "$before_digest" != "$expected_digest" ]; then
    echo "::error::The serving digest no longer matches the preflight baseline."
    return 1
  fi
  az webapp config appsettings set \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --settings "${SETTING_NAME}=${FIXTURE_MODE}" \
    --output none \
    --only-show-errors
  if ! setting_present; then
    echo "::error::The compatibility fixture setting was not created."
    return 1
  fi
  wait_until_healthy "$expected_digest"
  echo "Armed the compatibility fixture on a new revision of the same image digest."
}

restore() {
  require_test_environment
  require_azure_target
  local expected_digest="${EXPECTED_DIGEST:-}"
  local expected_names="${EXPECTED_SETTING_NAMES_PATH:-}"
  if [ -z "$expected_digest" ]; then
    echo "No preflight digest was captured; restoring from the current serving digest."
    expected_digest="$(serving_image_digest)"
  fi
  if setting_present; then
    az webapp config appsettings delete \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --setting-names "$SETTING_NAME" \
      --output none \
      --only-show-errors
  fi
  if setting_present; then
    echo "::error::The compatibility fixture setting is still present after restore."
    return 1
  fi
  wait_until_healthy "$expected_digest"
  local digest revision names_file
  digest="$(serving_image_digest)"
  revision="$(serving_image_reference)"
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  if [ -n "$expected_names" ] && [ -f "$expected_names" ]; then
    if ! names_match "$expected_names" "$names_file"; then
      echo "::error::Env var names after restore do not match the preflight baseline."
      return 1
    fi
  fi
  if [ "$digest" != "$expected_digest" ]; then
    echo "::error::The serving digest after restore does not match the preflight baseline."
    return 1
  fi
  write_summary "Staging compatibility fixture restore" "$digest" "$revision" "$names_file"
  echo "Restored the baseline: setting removed, same image digest, env var names match."
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
