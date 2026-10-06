#!/usr/bin/env bash
set -euo pipefail
shopt -s inherit_errexit

# Manual, staging-only Control image rollback proof. This script owns the
# ordering boundary: every read and safety gate completes before switch() can
# write, while restore() remains safe to call from an always() Actions step.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck disable=SC1091
source "$SCRIPT_DIR/staging-compat-fixture.sh"
# shellcheck disable=SC1091
source "$SCRIPT_DIR/lib/azure-api-deploy-rollback.sh"

ROLLBACK_MIGRATION_ROOT="src/PackageCatalog/ElsaControl.PackageCatalog.Persistence.SqlServerMigrations/Migrations"
ROLLBACK_COMPATIBILITY_SOURCE="src/Hosting/ElsaControl.Api/Cloud/CloudCompatibilityEndpoints.cs"
readonly ROLLBACK_CAP_SECONDS=1200
readonly ROLLBACK_RESERVE_SECONDS=300

rollback_require_scope() {
  local actual expected label
  require_test_environment
  for entry in \
    "AZURE_SUBSCRIPTION_ID|${AZURE_SUBSCRIPTION_ID:-}|${EXPECTED_STAGING_AZURE_SUBSCRIPTION_ID:-}" \
    "AZURE_RESOURCE_GROUP|${AZURE_RESOURCE_GROUP:-}|${EXPECTED_STAGING_AZURE_RESOURCE_GROUP:-}" \
    "AZURE_WEBAPP_NAME|${AZURE_WEBAPP_NAME:-}|${EXPECTED_STAGING_AZURE_WEBAPP_NAME:-}" \
    "AZURE_CONTAINER_REGISTRY_ENDPOINT|${AZURE_CONTAINER_REGISTRY_ENDPOINT:-}|${EXPECTED_STAGING_AZURE_CONTAINER_REGISTRY_ENDPOINT:-}"; do
    IFS='|' read -r label actual expected <<<"$entry"
    [ -n "$actual" ] || fail "$label is required for the staging rollback proof."
    [ -n "$expected" ] || fail "$label has no explicit staging allowlist value; refusing to guess the target."
    [ "$actual" = "$expected" ] || fail "$label does not equal its explicit staging allowlist value."
    if [[ "$actual" =~ [Pp][Rr][Oo][Dd] ]]; then
      fail "$label contains a production identifier; refusing the staging rollback proof."
    fi
  done
}

rollback_require_inputs() {
  PREVIOUS_DIGEST="${PREVIOUS_DIGEST:-${INPUT_PREVIOUS_DIGEST:-}}"
  EXPECTED_DIGEST="${EXPECTED_DIGEST:-${INPUT_EXPECTED_DIGEST:-}}"
  azure_require_immutable_digest "$PREVIOUS_DIGEST" || fail "The N-1 input must be a sha256 digest."
  azure_require_immutable_digest "$EXPECTED_DIGEST" || fail "The expected N input must be a sha256 digest."
  [ "$PREVIOUS_DIGEST" != "$EXPECTED_DIGEST" ] || fail "The N-1 and expected N digests must be different."
}

rollback_capture_build_setting() {
  local settings count value slot_setting
  if ! settings="$(az webapp config appsettings list \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --output json \
    --only-show-errors)"; then
    fail "Application__BuildNumber could not be read; refusing the rollback proof."
  fi
  printf '%s' "$settings" | jq -e 'type == "array" and all(.[]; type == "object" and (.name | type == "string"))' >/dev/null ||
    fail "Application settings returned an invalid schema; refusing the rollback proof."
  count="$(printf '%s' "$settings" | jq '[.[] | select(.name == "Application__BuildNumber")] | length')"
  [ "$count" -le 1 ] || fail "Application__BuildNumber appeared more than once; refusing the rollback proof."
  if [ "$count" = 0 ]; then
    ROLLBACK_BUILD_PRESENT=false
    ROLLBACK_BUILD_NUMBER=""
    ROLLBACK_BUILD_SLOT_SETTING=false
    return 0
  fi
  value="$(printf '%s' "$settings" | jq -r '[.[] | select(.name == "Application__BuildNumber")][0].value // empty')"
  slot_setting="$(printf '%s' "$settings" | jq -r '[.[] | select(.name == "Application__BuildNumber")][0].slotSetting // false')"
  [ -n "$value" ] || fail "Application__BuildNumber was present without a value; refusing the rollback proof."
  [[ "$value" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$ ]] || fail "Application__BuildNumber had an unsafe value; refusing the rollback proof."
  [ "$slot_setting" = true ] || [ "$slot_setting" = false ] || fail "Application__BuildNumber slotSetting was not boolean."
  ROLLBACK_BUILD_PRESENT=true
  ROLLBACK_BUILD_NUMBER="$value"
  ROLLBACK_BUILD_SLOT_SETTING="$slot_setting"
}

rollback_assert_build_setting() {
  local expected_present="$1" expected_value="$2" expected_slot="$3"
  rollback_capture_build_setting
  [ "$ROLLBACK_BUILD_PRESENT" = "$expected_present" ] || fail "Application__BuildNumber presence changed."
  [ "$ROLLBACK_BUILD_SLOT_SETTING" = "$expected_slot" ] || fail "Application__BuildNumber slot stickiness changed."
  if [ "$expected_present" = true ]; then
    [ "$ROLLBACK_BUILD_NUMBER" = "$expected_value" ] || fail "Application__BuildNumber value changed."
  fi
}

rollback_has_trusted_marker() {
  local marker="$1" prefix="$2" comments
  comments="$(fetch_issue_comments "$FREEZE_ISSUE")"
  printf '%s' "$comments" | python3 -c '
import json
import sys

marker = sys.argv[1]
prefix = sys.argv[2]
comments = json.load(sys.stdin)
state = None
for comment in comments:
    association = str(comment.get("author_association") or "").upper()
    user = comment.get("user") or {}
    login = str(user.get("login") or "").lower()
    if association not in {"OWNER", "MEMBER"} and login != "sfmskywalker":
        continue
    for line in str(comment.get("body") or "").splitlines():
        line = line.strip()
        if line.startswith(prefix):
            state = "approved" if line == marker else "revoked"
sys.exit(0 if state == "approved" else 1)
' "$marker" "$prefix"
}

rollback_require_pair_approval() {
  local marker="control-rollback-pair: approved N=${EXPECTED_DIGEST} N1=${PREVIOUS_DIGEST} N1_COMMIT=${N_MINUS_1_COMMIT}"
  if ! rollback_has_trusted_marker "$marker" 'control-rollback-pair:'; then
    fail "Issue #${FREEZE_ISSUE} has no exact pair-specific rollback approval marker; refusing before any write."
  fi
  ROLLBACK_PAIR_APPROVED=true
  echo "Accepted the exact pair-specific rollback approval marker from issue #${FREEZE_ISSUE}."
}

rollback_run_has_successful_api_steps() {
  local repo="$1" run_id="$2" jobs
  jobs="$(gh_get --paginate "repos/${repo}/actions/runs/${run_id}/jobs?per_page=100" \
    --jq '.jobs[] | {name, conclusion, steps}')"
  [ -n "$jobs" ] || return 1
  printf '%s\n' "$jobs" | jq -se '
    all(.[];
      type == "object" and
      (.name | type == "string") and
      (.conclusion | type == "string" or . == null) and
      (.steps | type == "array") and
      all(.steps[]?; type == "object" and (.name | type == "string") and (.conclusion | type == "string" or . == null))
    )
  ' >/dev/null || fail "A successful Deploy-staging run returned an invalid job/step schema."
  if printf '%s\n' "$jobs" | jq -se '
    any(.[];
      .name == "Validate and Deploy Control API" and
      .conclusion == "success" and
      any(.steps[]?; .name == "Deploy API app" and .conclusion == "success") and
      any(.steps[]?; .name == "Verify deployed API health" and .conclusion == "success")
    )
  ' >/dev/null; then
    return 0
  fi
  return 1
}

rollback_deployment_digest() {
  local acr_name="$1" commit="$2"
  az acr manifest show-metadata \
    --registry "$acr_name" \
    --name "elsa-control/api:$commit" \
    --query digest \
    --output tsv \
    --only-show-errors 2>/dev/null
}

rollback_resolve_n1_provenance() {
  local repo deployment_rows deployment_id deployment_sha created_at statuses status_url run_id path run_json run_sha digest acr_name
  local current_n_seen=false
  repo="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required.}"
  # GitHub returns environment deployments newest first. That order is part of
  # the boundary: after the current N deployment is anchored, the first older
  # successful API+health deployment is the only candidate for N-1.
  deployment_rows="$(gh_get --paginate "repos/${repo}/deployments?environment=test&per_page=100" --jq '.[] | [.id, .sha, .created_at] | @tsv')"
  acr_name="${AZURE_CONTAINER_REGISTRY_ENDPOINT%%.azurecr.io}"
  while IFS=$'\t' read -r deployment_id deployment_sha created_at; do
    [[ "$deployment_id" =~ ^[0-9]+$ && "$deployment_sha" =~ ^[0-9a-f]{40}$ ]] || continue
    [ -n "$created_at" ] || fail "A test deployment lacked a creation timestamp; refusing provenance inference."
    statuses="$(gh_get "repos/${repo}/deployments/${deployment_id}/statuses?per_page=1")"
    printf '%s' "$statuses" | jq -e 'type == "array" and length > 0 and (.[0] | type == "object")' >/dev/null ||
      fail "A test deployment status was unreadable; immediate-predecessor provenance cannot be proven."
    status_url="$(printf '%s' "$statuses" | jq -r '.[0].log_url // .[0].target_url // empty')"
    if ! run_id="$(extract_run_id_from_url "$status_url")"; then
      fail "A test deployment had no readable Actions run mapping; refusing to skip unknown provenance."
    fi
    path="$(workflow_run_path "$run_id")"
    [ "$path" = "$DEPLOY_STAGING_WORKFLOW_PATH" ] || continue
    run_json="$(gh_get "repos/${repo}/actions/runs/${run_id}")"
    [ "$(printf '%s' "$run_json" | jq -r '.conclusion // empty')" = success ] || continue
    run_sha="$(printf '%s' "$run_json" | jq -r '.head_sha // empty')"
    [ "$run_sha" = "$deployment_sha" ] || continue
    rollback_run_has_successful_api_steps "$repo" "$run_id" || continue
    if ! digest="$(rollback_deployment_digest "$acr_name" "$deployment_sha")"; then
      fail "The ACR source-SHA tag could not be resolved for a successful Deploy-staging app run."
    fi

    if [ "$current_n_seen" = false ]; then
      [ "$deployment_sha" = "$EXPECTED_N_COMMIT" ] || fail "The latest successful Deploy-staging app source did not match N's health source commit."
      [ "$digest" = "$EXPECTED_DIGEST" ] || fail "The current N deployment source tag did not resolve to N's serving digest."
      current_n_seen=true
      continue
    fi

    # Do not search older history for a matching digest. The first distinct,
    # successful API+health deployment before N is the only admissible N-1.
    [ "$deployment_sha" = "$EXPECTED_N_COMMIT" ] && continue
    [ "$digest" = "$PREVIOUS_DIGEST" ] || fail "The immediately preceding successful Deploy-staging app digest was not the requested N-1."
    N_MINUS_1_COMMIT="$deployment_sha"
    N_MINUS_1_BUILD_NUMBER="$(printf '%s' "$run_json" | jq -r '.run_number // empty')"
    N_MINUS_1_DEPLOYMENT_ID="$deployment_id"
    [[ "$N_MINUS_1_BUILD_NUMBER" =~ ^[1-9][0-9]{0,19}$ ]] || fail "The matched N-1 Deploy-staging run has no safe build number."
    rollback_write_output n1_commit "$N_MINUS_1_COMMIT"
    rollback_write_output n1_build_number "$N_MINUS_1_BUILD_NUMBER"
    rollback_write_output n1_deployment_id "$N_MINUS_1_DEPLOYMENT_ID"
    echo "Resolved the current N and its immediately preceding successful Deploy-staging app provenance by source SHA and immutable ACR digest."
    return 0
  done <<< "$deployment_rows"
  [ "$current_n_seen" = true ] || fail "No successful Deploy-staging app run matched the current N health source and immutable digest."
  fail "No immediately preceding successful Deploy-staging app run was available for N-1 provenance."
}

rollback_verify_source_tag_digest() {
  local commit="$1" expected_digest="$2" acr_name actual
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || fail "The deployed source identity was not a full commit SHA."
  acr_name="${AZURE_CONTAINER_REGISTRY_ENDPOINT%%.azurecr.io}"
  if ! actual="$(az acr manifest show-metadata \
    --registry "$acr_name" \
    --name "elsa-control/api:$commit" \
    --query digest \
    --output tsv \
    --only-show-errors)"; then
    fail "The ACR source-SHA tag could not be resolved; refusing the rollback proof."
  fi
  [ "$actual" = "$expected_digest" ] || fail "The ACR source-SHA tag did not resolve to the expected immutable digest."
}

rollback_sorted_lines_hash() {
  LC_ALL=C sort | python3 -c 'import hashlib, sys; print(hashlib.sha256("\n".join(sys.stdin.read().splitlines()).encode()).hexdigest())'
}

rollback_write_epoch_path() {
  local directory="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
  local run_id="${GITHUB_RUN_ID:-manual}"
  [ -n "$directory" ] || fail "No private runner temp directory is available for the rollback write marker."
  printf '%s/elsa-control-rollback-%s.write-epoch\n' "$directory" "$run_id"
}

rollback_persist_write_epoch() {
  local path
  path="$(rollback_write_epoch_path)"
  (umask 077 && printf '%s\n' "$ROLLBACK_WRITE_EPOCH" > "$path") ||
    fail "The rollback write epoch could not be persisted before the Azure mutation."
}

rollback_read_write_epoch() {
  local path value
  path="$(rollback_write_epoch_path)"
  [ -s "$path" ] || return 1
  value="$(cat "$path")" || return 1
  [[ "$value" =~ ^[0-9]+$ ]] || fail "The persisted rollback write epoch was invalid."
  printf '%s\n' "$value"
}

rollback_cleanup_write_epoch() {
  local path
  path="$(rollback_write_epoch_path)"
  rm -f "$path"
}

rollback_require_n_commit_on_main() {
  local main_ref
  main_ref="$(git rev-parse --verify 'refs/remotes/origin/main^{commit}' 2>/dev/null)" ||
    fail "The dispatched main ref could not be resolved; refusing the rollback proof."
  git merge-base --is-ancestor "$EXPECTED_N_COMMIT" "$main_ref" ||
    fail "N's health source commit is not an ancestor of dispatched main; refusing the rollback proof."
}

rollback_migration_ids_at_commit() {
  local commit="$1"
  python3 - "$commit" "$ROLLBACK_MIGRATION_ROOT" <<'PY'
import re
import subprocess
import sys

commit, root = sys.argv[1:]
try:
    paths = subprocess.check_output(
        ["git", "ls-tree", "-r", "--name-only", commit, "--", root],
        text=True,
        stderr=subprocess.PIPE,
    ).splitlines()
except subprocess.CalledProcessError as error:
    print(error.stderr, file=sys.stderr, end="")
    raise SystemExit("migration tree could not be read")
designers = [path for path in paths if path.endswith(".Designer.cs")]
if not designers:
    raise SystemExit("migration tree contained no designer files")
ids = []
for path in designers:
    try:
        source = subprocess.check_output(["git", "show", f"{commit}:{path}"], text=True, stderr=subprocess.PIPE)
    except subprocess.CalledProcessError as error:
        print(error.stderr, file=sys.stderr, end="")
        raise SystemExit(f"migration designer could not be read: {path}")
    matches = re.findall(r'\[Migration\("([0-9]+_[A-Za-z0-9_.-]+)"\)\]', source)
    if len(matches) != 1:
        raise SystemExit(f"migration designer must contain exactly one Migration attribute: {path}")
    ids.append(matches[0])
if len(set(ids)) != len(ids):
    raise SystemExit("migration designers contained duplicate migration IDs")
for migration_id in sorted(ids):
    print(migration_id)
PY
}

rollback_current_migration_ids() {
  rollback_migration_ids_at_commit "$EXPECTED_N_COMMIT"
}

rollback_applied_migration_ids() {
  local payload="${STAGING_APPLIED_MIGRATIONS_JSON:-}"
  if [ -n "$payload" ]; then
    printf '%s' "$payload" | jq -er 'type == "array" and all(.[]; type == "string" and test("^[0-9]+_[A-Za-z0-9_.-]+$"))' >/dev/null ||
      fail "The applied migration history was not a valid migration-id array."
    printf '%s' "$payload" | jq -r '.[]' | LC_ALL=C sort -u
    return 0
  fi
  if [ "${STAGING_ALLOW_INFERRED_HISTORY:-false}" = true ]; then
    echo "::notice::Applied EF history is inferred from the successful N source migration set; no SQL grant or firewall change was made." >&2
    rollback_current_migration_ids
    return 0
  fi
  fail "Applied EF migration history was unavailable; refusing the rollback proof."
}

rollback_schema_gate() {
  local n1_ids applied_ids missing extra
  n1_ids="$(rollback_migration_ids_at_commit "$N_MINUS_1_COMMIT")"
  applied_ids="$(rollback_applied_migration_ids)"
  missing="$(comm -23 <(printf '%s\n' "$applied_ids") <(printf '%s\n' "$n1_ids"))"
  extra="$(comm -13 <(printf '%s\n' "$applied_ids") <(printf '%s\n' "$n1_ids"))"
  if [ -n "$missing" ]; then
    fail "Refused: schema boundary; N-1 lacks applied migration IDs: $(tr '\n' ',' <<<"$missing" | sed 's/,$//')."
  fi
  if [ -n "$extra" ]; then
    fail "Refused: schema boundary; N-1 contains unapplied migration IDs: $(tr '\n' ',' <<<"$extra" | sed 's/,$//')."
  fi
  ROLLBACK_MIGRATION_HISTORY_HASH="$(printf '%s\n' "$applied_ids" | rollback_sorted_lines_hash)"
  ROLLBACK_N1_MIGRATION_HASH="$(printf '%s\n' "$n1_ids" | rollback_sorted_lines_hash)"
  [ "$ROLLBACK_MIGRATION_HISTORY_HASH" = "$ROLLBACK_N1_MIGRATION_HASH" ] || fail "Refused: schema boundary; migration set hashes differ."
  echo "Applied migration IDs (source-inferred when explicitly configured):"
  printf '%s\n' "$applied_ids"
  echo "N-1 source migration IDs:"
  printf '%s\n' "$n1_ids"
  rollback_write_output schema_gate pass
  rollback_write_output migration_history_hash "$ROLLBACK_MIGRATION_HISTORY_HASH"
}

rollback_persisted_data_check() {
  local changed
  if ! changed="$(git diff --name-only "$N_MINUS_1_COMMIT" "$EXPECTED_N_COMMIT" -- \
    'src/PackageCatalog' 'src/Deployment' 'src/Domain' 'src/Hosting/ElsaControl.Api/Cloud' \
    'docs/cloud*' 'specs/*/contracts' 2>/dev/null)"; then
    fail "The persisted-data changed-file read failed; refusing the rollback proof."
  fi
  [ "${ROLLBACK_PAIR_APPROVED:-false}" = true ] || fail "The exact pair-specific approval marker was not accepted."
  ROLLBACK_PERSISTED_DATA_HASH="$(printf '%s\n' "$changed" | rollback_sorted_lines_hash)"
  if [ -n "$changed" ]; then
    echo "Persistence, Domain, or Cloud contract paths changed in this pair:"
    printf '%s\n' "$changed"
    local marker="control-rollback-data: approved N=${EXPECTED_DIGEST} N1=${PREVIOUS_DIGEST} paths_sha256=${ROLLBACK_PERSISTED_DATA_HASH}"
    rollback_has_trusted_marker "$marker" 'control-rollback-data:' ||
      fail "Changed persisted-data paths require an exact pair/path-hash data-owner sign-off before rollback."
  else
    echo "Persisted-data check passed: N-1..N changed no Persistence, Domain, or Cloud contract files, and the pair marker was accepted."
  fi
  rollback_write_output persisted_data_hash "$ROLLBACK_PERSISTED_DATA_HASH"
}

rollback_capabilities_at_commit() {
  local commit="$1"
  python3 - "$commit" "$ROLLBACK_COMPATIBILITY_SOURCE" <<'PY'
import json
import re
import subprocess
import sys

commit, path = sys.argv[1:]
source = subprocess.check_output(["git", "show", f"{commit}:{path}"], text=True)
version = re.search(r"CurrentContractVersion\s*=\s*(\d+)", source)
if not version:
    version = re.search(r"new\s+CloudCompatibilityResponse\(\s*(\d+)\s*,", source)
block = re.search(r"private static readonly string\[\] Capabilities\s*=\s*\[(.*?)\];", source, re.S)
if not version or not block:
    raise SystemExit("compatibility capabilities could not be parsed")
constant = dict(re.findall(r'(\w+)\s*=\s*"([^"]+)"', source)).get("ProvisioningProgressCapability")
values = []
for token in re.findall(r'"([^"]+)"|\b([A-Za-z_]\w*)\b', block.group(1)):
    literal, name = token
    if literal:
        values.append(literal)
    elif name == "ProvisioningProgressCapability" and constant:
        values.append(constant)
if not values:
    raise SystemExit("compatibility capability list was empty")
print(json.dumps({"contractVersion": int(version.group(1)), "capabilities": values}, separators=(",", ":")))
PY
}

rollback_predict_bff_outcome() {
  local source="$1"
  jq -e --argjson required "$CLOUD_REQUIRED_CAPABILITIES_JSON" \
    --slurpfile candidate "$source" \
    '($candidate[0].contractVersion == 1) and ($required | all(.[] as $cap; ($candidate[0].capabilities | index($cap) != null)))' >/dev/null || {
      ROLLBACK_BFF_OUTCOME=gated
      return 0
    }
  ROLLBACK_BFF_OUTCOME=compatible
}

rollback_capture_cloud_required_capabilities() {
  local response_file header_file http_status cache_control
  response_file="$(mktemp)"
  header_file="$(mktemp)"
  if ! http_status="$(post_bff_compatibility "$response_file" "$header_file")"; then
    rm -f "$response_file" "$header_file"
    fail "The deployed Cloud compatibility read failed; refusing rollback."
  fi
  cache_control="$(read_cache_control "$header_file")"
  if [ "$http_status" != 200 ] || [[ ! "$cache_control" =~ [Nn]o-[Ss]tore ]]; then
    rm -f "$response_file" "$header_file"
    fail "The deployed Cloud compatibility read was not an HTTP 200 no-store response."
  fi
  jq -e '.data.state == "compatible" and .data.contractVersion == 1 and (.data.capabilities | type == "array" and length > 0 and all(.[]; type == "string" and length > 0))' "$response_file" >/dev/null || {
    rm -f "$response_file" "$header_file"
    fail "The deployed Cloud did not return its exact compatible required-capability list."
  }
  CLOUD_REQUIRED_CAPABILITIES_JSON="$(jq -c '.data.capabilities' "$response_file")"
  rm -f "$response_file" "$header_file"
  echo "Captured the deployed Cloud BFF required-capability list from its compatible no-store response."
}

rollback_fetch_compatibility() {
  local destination="$1" host status
  host="$(default_host_name)"
  if ! status="$(curl --silent --show-error --output "$destination" --write-out '%{http_code}' --max-time 10 \
    --header "Authorization: Bearer ${STAGING_CLOUD_ACCESS_TOKEN:?}" \
    --header 'Accept: application/json' "https://${host}/api/cloud/compatibility")" || [ "$status" != 200 ]; then
    fail "Authenticated compatibility read failed closed with HTTP ${status:-000}."
  fi
  jq -e 'type == "object" and (.contractVersion | type == "number") and (.capabilities | type == "array")' "$destination" >/dev/null ||
    fail "Authenticated compatibility response was not the expected object."
}

rollback_capture_non_admin_data() {
  local destination="$1" host response status workspace page page_count has_more total
  local -a workspace_ids=() instance_ids=() organization_ids=()
  host="$(default_host_name)"
  response="$(mktemp)"
  for endpoint in /api/me/workspaces /api/me/organizations; do
    if ! status="$(curl --silent --show-error --output "$response" --write-out '%{http_code}' --max-time 10 \
      --header "Authorization: Bearer ${STAGING_CLOUD_ACCESS_TOKEN:?}" \
      --header 'Accept: application/json' "https://${host}${endpoint}")" || [ "$status" != 200 ]; then
      rm -f "$response"
      fail "Synthetic non-admin read ${endpoint} failed closed with HTTP ${status:-000}."
    fi
    jq -e 'def valid_id: type == "string" and test("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$") and . != "00000000-0000-0000-0000-000000000000";
      (.account.id | valid_id) and
      (.organizations | type == "array" and length > 0 and all(.[]; .id | valid_id)) and
      (.workspaces | type == "array" and length > 0 and all(.[]; .id | valid_id))' "$response" >/dev/null || {
      rm -f "$response"
      fail "Synthetic non-admin read ${endpoint} did not return a non-empty account, organization, and workspace set."
    }
  done
  while IFS= read -r organization; do
    [ -n "$organization" ] && organization_ids+=("$organization")
  done < <(jq -r '.organizations[].id' "$response")
  while IFS= read -r workspace; do
    [ -n "$workspace" ] || continue
    workspace_ids+=("$workspace")
  done < <(jq -r '.workspaces[].id' "$response")
  [ "${#organization_ids[@]}" -gt 0 ] || fail "Synthetic non-admin read returned no organizations."
  [ "${#workspace_ids[@]}" -gt 0 ] || fail "Synthetic non-admin read returned no workspaces."
  for workspace in "${workspace_ids[@]}"; do
    page=1
    page_count=0
    while :; do
      if ! status="$(curl --silent --show-error --output "$response" --write-out '%{http_code}' --max-time 10 \
        --header "Authorization: Bearer ${STAGING_CLOUD_ACCESS_TOKEN:?}" \
        --header 'Accept: application/json' \
        "https://${host}/api/workspaces/${workspace}/instances?page=${page}&pageSize=100")" || [ "$status" != 200 ]; then
        rm -f "$response"
        fail "Synthetic non-admin instance read failed closed with HTTP ${status:-000}."
      fi
      jq -e --argjson page "$page" 'def valid_id: type == "string" and test("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$") and . != "00000000-0000-0000-0000-000000000000";
        type == "object" and (.items | type == "array" and length <= 100 and all(.[]; .instanceId | valid_id)) and
        (.totalCount | type == "number" and . >= 0 and floor == .) and
        (.hasMore | type == "boolean") and .page == $page and .pageSize == 100 and
        .hasMore == (.page * .pageSize < .totalCount)' "$response" >/dev/null || {
        rm -f "$response"
        fail "Synthetic non-admin instance read returned an invalid page."
      }
      while IFS= read -r instance; do
        [ -n "$instance" ] && instance_ids+=("$instance")
      done < <(jq -r '.items[].instanceId' "$response")
      total="$(jq -r '.totalCount' "$response")"
      has_more="$(jq -r '.hasMore' "$response")"
      page_count=$((page_count + 1))
      [ "$page_count" -lt 101 ] || { rm -f "$response"; fail "Synthetic non-admin instance pagination exceeded the safety bound."; }
      [ "$has_more" != true ] && break
      page=$((page + 1))
      [ "$((page - 1))" -lt "$total" ] || { rm -f "$response"; fail "Synthetic non-admin instance pagination was inconsistent."; }
    done
  done
  rm -f "$response"
  jq -cn --argjson organizations "$(printf '%s\n' "${organization_ids[@]}" | jq -Rsc 'split("\n") | map(select(length > 0))')" \
    --argjson workspaces "$(printf '%s\n' "${workspace_ids[@]}" | jq -Rsc 'split("\n") | map(select(length > 0))')" \
    --argjson instances "$(printf '%s\n' "${instance_ids[@]}" | jq -Rsc 'split("\n") | map(select(length > 0))')" \
    '{organizations:($organizations|sort),workspaces:($workspaces|sort),instances:($instances|sort)}' | sha256sum | awk '{print $1}' > "$destination"
  [ -s "$destination" ] || fail "Synthetic non-admin read produced no evidence hash."
}

rollback_write_output() {
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"
  fi
}

rollback_preflight() {
  rollback_require_scope
  rollback_require_inputs
  PREFLIGHT_STARTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  check_exclusive
  require_smoke_inputs
  mint_cloud_token

  local image_reference digest linux_fx_version names_file settings_hash deployment_id expected_n1
  image_reference="$(serving_image_reference)"
  [[ "$image_reference" == *@sha256:* ]] || fail "The staging baseline is tag-backed; pin N by digest and rerun before any rollback write."
  [[ "$image_reference" == "$AZURE_CONTAINER_REGISTRY_ENDPOINT/elsa-control/api@"* ]] || fail "The staging baseline image is not owned by the configured staging ACR."
  digest="$(image_digest "$image_reference")"
  [ "$digest" = "$EXPECTED_DIGEST" ] || fail "The serving digest no longer matches expected N; refusing rollback."
  linux_fx_version="$(az webapp config show --resource-group "$AZURE_RESOURCE_GROUP" --name "$AZURE_WEBAPP_NAME" --query linuxFxVersion --output tsv --only-show-errors)"
  [ "$linux_fx_version" = SITECONTAINERS ] || fail "The staging rollback proof only mutates the main sitecontainer runtime."
  read_health_identity
  require_null_health_fixture
  assert_health_agrees_with_image "$image_reference" "$digest" "$HEALTH_COMMIT"
  [ "$HEALTH_BUILD_NUMBER" != "" ] || fail "Staging /health did not return N's build number."
  [[ "$HEALTH_BUILD_NUMBER" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$ ]] || fail "Staging /health returned an unsafe N build number."
  [ "$HEALTH_COMMIT" != "" ] || fail "Staging /health did not return N's imageId."
  [[ "$HEALTH_COMMIT" =~ ^[0-9a-f]{40}$ ]] || fail "Staging /health returned an unsafe N imageId."
  EXPECTED_N_COMMIT="$HEALTH_COMMIT"
  git cat-file -e "$EXPECTED_N_COMMIT^{commit}" || fail "N's health source commit is not available in the checkout."
  rollback_require_n_commit_on_main
  rollback_verify_source_tag_digest "$EXPECTED_N_COMMIT" "$EXPECTED_DIGEST"
  rollback_capture_build_setting
  if [ "$ROLLBACK_BUILD_PRESENT" = true ]; then
    [ "$ROLLBACK_BUILD_NUMBER" = "$HEALTH_BUILD_NUMBER" ] ||
      fail "N's health build disagrees with Application__BuildNumber; refusing the rollback proof."
  fi
  names_file="$(mktemp)"
  list_setting_names > "$names_file"
  settings_hash="$(rollback_sorted_lines_hash < "$names_file")"
  ROLLBACK_COMPATIBILITY_PATH="$(mktemp)"
  rollback_fetch_compatibility "$ROLLBACK_COMPATIBILITY_PATH"
  rollback_capture_cloud_required_capabilities
  deployment_id="$(latest_deploy_staging_deployment_id)"
  azure_verify_acr_digest "$AZURE_CONTAINER_REGISTRY_ENDPOINT" "$PREVIOUS_DIGEST"
  rollback_resolve_n1_provenance
  rollback_require_pair_approval
  git cat-file -e "$N_MINUS_1_COMMIT^{commit}" || fail "N-1 source commit is not available in the checkout."
  git merge-base --is-ancestor "$N_MINUS_1_COMMIT" "$EXPECTED_N_COMMIT" || fail "N-1 source commit is not an ancestor of N/main."
  rollback_schema_gate
  rollback_persisted_data_check
  expected_n1="$(rollback_capabilities_at_commit "$N_MINUS_1_COMMIT")" || fail "N-1 compatibility capabilities could not be extracted from source."
  printf '%s\n' "$expected_n1" > "${ROLLBACK_N1_COMPATIBILITY_PATH:=$(mktemp)}"
  rollback_predict_bff_outcome "$ROLLBACK_N1_COMPATIBILITY_PATH"
  ROLLBACK_DATA_HASH_PATH="$(mktemp)"
  rollback_capture_non_admin_data "$ROLLBACK_DATA_HASH_PATH"
  rollback_write_output image_reference "$image_reference"
  rollback_write_output digest "$digest"
  rollback_write_output build_number "$HEALTH_BUILD_NUMBER"
  rollback_write_output image_id "$HEALTH_COMMIT"
  rollback_write_output build_present "$ROLLBACK_BUILD_PRESENT"
  rollback_write_output build_value "$ROLLBACK_BUILD_NUMBER"
  rollback_write_output build_slot_setting "$ROLLBACK_BUILD_SLOT_SETTING"
  rollback_write_output n1_bff_outcome "$ROLLBACK_BFF_OUTCOME"
  rollback_write_output expected_n_commit "$EXPECTED_N_COMMIT"
  rollback_write_output setting_names_path "$names_file"
  rollback_write_output setting_names_hash "$settings_hash"
  rollback_write_output compatibility_path "$ROLLBACK_COMPATIBILITY_PATH"
  rollback_write_output n1_compatibility_path "$ROLLBACK_N1_COMPATIBILITY_PATH"
  rollback_write_output data_hash_path "$ROLLBACK_DATA_HASH_PATH"
  rollback_write_output deployment_id "$deployment_id"
  rollback_write_output preflight_started_at "$PREFLIGHT_STARTED_AT"
  rollback_write_output preflight_success true
  echo "Preflight passed: pinned N identity, exact schema/data gates, N-1 ACR digest, non-admin read, and predicted BFF outcome are recorded without writes."
}

rollback_switch() {
  rollback_require_scope
  rollback_require_inputs
  [ "${PREFLIGHT_SUCCESS:-}" = true ] || fail "Rollback switch requires successful preflight."
  check_exclusive
  rollback_require_pair_approval
  local current_image expected_image digest
  expected_image="${ORIGINAL_IMAGE_REFERENCE:?}"
  current_image="$(serving_image_reference)"
  [ "$current_image" = "$expected_image" ] || fail "The serving N image reference changed after preflight; refusing the rollback write."
  digest="$(image_digest "$current_image")"
  [ "$digest" = "${EXPECTED_DIGEST:?}" ] || fail "The serving N digest changed after preflight; refusing the rollback write."
  read_health_identity
  require_null_health_fixture
  [ "$HEALTH_BUILD_NUMBER" = "${EXPECTED_BUILD_NUMBER:?}" ] || fail "The serving N build changed after preflight; refusing the rollback write."
  [ "$HEALTH_COMMIT" = "${EXPECTED_IMAGE_ID:?}" ] || fail "The serving N source identity changed after preflight; refusing the rollback write."
  assert_health_agrees_with_image "$current_image" "$digest" "$HEALTH_COMMIT"
  local image="$AZURE_CONTAINER_REGISTRY_ENDPOINT/elsa-control/api@$PREVIOUS_DIGEST"
  azure_require_immutable_digest "$PREVIOUS_DIGEST"
  ROLLBACK_WRITE_EPOCH="$(date +%s)"
  rollback_persist_write_epoch
  rollback_write_output write_epoch "$ROLLBACK_WRITE_EPOCH"
  azure_update_sitecontainer_runtime "$image" "${BUILD_PRESENT:?}" "${N_MINUS_1_BUILD_NUMBER:?}" "${BUILD_SLOT_SETTING:-false}"
  az webapp restart --resource-group "$AZURE_RESOURCE_GROUP" --name "$AZURE_WEBAPP_NAME" --output none --only-show-errors
  azure_wait_for_stable_api_health "$N_MINUS_1_BUILD_NUMBER" "${N_MINUS_1_COMMIT:?}" "N-1"
  [ "$(image_digest "$(serving_image_reference)")" = "$PREVIOUS_DIGEST" ] || fail "N-1 did not become the serving immutable image."
  echo "Switched staging to N-1 by digest and observed stable health."
}

rollback_check() {
  [ "${PREFLIGHT_SUCCESS:-}" = true ] || fail "Rollback checks require successful preflight."
  mint_cloud_token
  local now remaining actual normalized_actual normalized_expected
  now="$(date +%s)"
  remaining=$(( ${WRITE_EPOCH:?} + ROLLBACK_CAP_SECONDS - now ))
  [ "$remaining" -gt "$ROLLBACK_RESERVE_SECONDS" ] || fail "The 20-minute staging rollback cap cannot reserve restore time before checks."
  azure_wait_for_stable_api_health "$N_MINUS_1_BUILD_NUMBER" "${N_MINUS_1_COMMIT:?}" "N-1"
  local n1_compat
  n1_compat="$(mktemp)"
  rollback_fetch_compatibility "$n1_compat"
  if ! normalized_actual="$(jq -cS . "$n1_compat")" || ! normalized_expected="$(jq -cS . "${N1_COMPATIBILITY_PATH:?}")"; then
    rm -f "$n1_compat"
    fail "N-1 compatibility evidence could not be normalized for comparison."
  fi
  rm -f "$n1_compat"
  [ "$normalized_actual" = "$normalized_expected" ] || fail "N-1 authenticated compatibility did not match its source capability list."
  case "${N1_BFF_OUTCOME:?}" in
    compatible) prove_bff_smoke_compatible ;;
    gated) prove_bff_update_in_progress ;;
    *) fail "The predicted N-1 BFF outcome was not recognized." ;;
  esac
  actual="$(mktemp)"
  rollback_capture_non_admin_data "$actual"
  cmp -s "$actual" "${DATA_HASH_PATH:?}" || fail "N-1 non-admin organization/workspace/instance ids did not match preflight."
  rm -f "$actual"
  actual="$(rollback_applied_migration_ids | rollback_sorted_lines_hash)"
  [ "$actual" = "${MIGRATION_HISTORY_HASH:?}" ] || fail "Migration history changed during the N-1 hold."
  echo "N-1 checks passed; all proof requests were reads plus the compatibility BFF smoke."
}

rollback_hold() {
  local requested elapsed remaining available
  requested="${ROLLBACK_HOLD_SECONDS:-30}"
  [[ "$requested" =~ ^[0-9]+$ ]] || fail "ROLLBACK_HOLD_SECONDS must be a non-negative integer."
  elapsed=$(( $(date +%s) - ${WRITE_EPOCH:?} ))
  remaining=$((ROLLBACK_CAP_SECONDS - elapsed))
  available=$((remaining - ROLLBACK_RESERVE_SECONDS))
  [ "$available" -ge 0 ] || fail "The 20-minute staging rollback cap cannot reserve restore time."
  [ "$requested" -le "$available" ] || requested="$available"
  echo "Holding N-1 for ${requested}s; restore remains reserved inside the ${ROLLBACK_CAP_SECONDS}s cap."
  [ "$requested" -eq 0 ] || sleep "$requested"
}

rollback_restore() {
  [ "${PREFLIGHT_SUCCESS:-}" = true ] || fail "Restore is skipped because preflight did not pass."
  if [ -z "${WRITE_EPOCH:-}" ]; then
    if ! WRITE_EPOCH="$(rollback_read_write_epoch)"; then
      echo "No rollback write started before cancellation; restore is a no-op."
      return 0
    fi
    echo "Recovered the rollback write epoch from the private runner marker after the switch output was unavailable."
  fi
  local image="${ORIGINAL_IMAGE_REFERENCE:?}"
  local deadline=$((WRITE_EPOCH + ROLLBACK_CAP_SECONDS))
  local cap_exceeded=false
  azure_require_immutable_digest "$EXPECTED_DIGEST"
  [ "$image" = "$AZURE_CONTAINER_REGISTRY_ENDPOINT/elsa-control/api@$EXPECTED_DIGEST" ] ||
    fail "The captured N reference is not the verified immutable staging image."
  if [ "$(date +%s)" -gt "$deadline" ]; then
    cap_exceeded=true
    echo "::warning::The rollback cap elapsed before restore began; restoring N and marking the proof failed."
  fi
  azure_update_sitecontainer_runtime "$image" "${BUILD_PRESENT:?}" "${BUILD_VALUE:-}" "${BUILD_SLOT_SETTING:-false}"
  azure_restart_and_verify_api_runtime sitecontainers SITECONTAINERS "$image"
  azure_wait_for_stable_api_health "$EXPECTED_BUILD_NUMBER" "$EXPECTED_IMAGE_ID" "restore"
  [ "$(image_digest "$(serving_image_reference)")" = "$EXPECTED_DIGEST" ] || fail "Restore did not return staging to N by digest."
  rollback_assert_build_setting "${BUILD_PRESENT:?}" "${BUILD_VALUE:-}" "${BUILD_SLOT_SETTING:-false}"
  if [ "$cap_exceeded" = true ]; then
    fail "Restore started after the 20-minute cap; the proof is failed even though N was restored."
  fi
  echo "Restore completed after success, failed checks, or ordinary cancellation."
}

rollback_postflight() {
  [ "${RESTORE_SUCCESS:-}" = true ] || fail "Post-restore identity checks require a successful restore."
  check_exclusive
  mint_cloud_token
  local image digest names_hash deployment_id actual expected
  image="$(serving_image_reference)"
  digest="$(image_digest "$image")"
  [ "$image" = "${IMAGE_REFERENCE:?}" ] || fail "Post-restore image reference did not match preflight."
  [ "$digest" = "${DIGEST:?}" ] || fail "Post-restore image digest did not match preflight."
  read_health_identity
  require_null_health_fixture
  [ "$HEALTH_BUILD_NUMBER" = "${BUILD_NUMBER:?}" ] || fail "Post-restore /health build number did not match preflight."
  [ "$HEALTH_COMMIT" = "${IMAGE_ID:?}" ] || fail "Post-restore /health imageId did not match preflight."
  rollback_assert_build_setting "${BUILD_PRESENT:?}" "${BUILD_VALUE:-}" "${BUILD_SLOT_SETTING:-false}"
  names_hash="$(list_setting_names | rollback_sorted_lines_hash)"
  [ "$names_hash" = "${SETTING_NAMES_HASH:?}" ] || fail "Post-restore app-setting names did not match preflight."
  actual="$(mktemp)"
  rollback_fetch_compatibility "$actual"
  cmp -s "$actual" "${COMPATIBILITY_PATH:?}" || fail "Post-restore authenticated compatibility did not byte-match preflight."
  rm -f "$actual"
  actual="$(mktemp)"
  rollback_capture_non_admin_data "$actual"
  cmp -s "$actual" "${DATA_HASH_PATH:?}" || fail "Post-restore non-admin organization/workspace/instance ids did not match preflight."
  rm -f "$actual"
  actual="$(rollback_applied_migration_ids | rollback_sorted_lines_hash)"
  [ "$actual" = "${MIGRATION_HISTORY_HASH:?}" ] || fail "Post-restore migration history changed."
  deployment_id="$(latest_deploy_staging_deployment_id)"
  [ "$deployment_id" = "${DEPLOYMENT_ID:?}" ] || fail "Post-restore Deploy-staging deployment id changed."
  assert_no_deploy_staging_since "${PREFLIGHT_STARTED_AT:?}"
  prove_bff_smoke_compatible
  echo "Post-restore identity checks passed; evidence contains hashes and no customer response bodies."
}

usage() {
  echo "Usage: $0 preflight|switch|check|hold|restore|postflight" >&2
}

main() {
  case "${1:-}" in
    preflight) rollback_preflight ;;
    switch) rollback_switch ;;
    check) rollback_check ;;
    hold) rollback_hold ;;
    restore) rollback_restore ;;
    postflight) rollback_postflight ;;
    *) usage; return 2 ;;
  esac
}

if [ "${BASH_SOURCE[0]}" = "$0" ]; then
  main "$@"
fi
