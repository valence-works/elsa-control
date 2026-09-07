#!/usr/bin/env bash
# Operator runner for one rehearsal phase. Creates the exact ACI group from the rendered spec, waits for a
# terminal state, parses exactly one safe probe result line, and writes it to RESULT_PATH. It never prints raw
# Azure CLI output or container logs and never deletes the group: cleanup is a separate, verified operator step.
set -u -o pipefail
umask 077
exec 2>/dev/null

package_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)" || exit 2
readonly package_dir
readonly renderer="$package_dir/render-container-group.py"
readonly probe="$package_dir/probe.py"
readonly parser="$package_dir/parse-result.py"
phase="${REHEARSAL_PHASE:-}"
result_path="${RESULT_PATH:-$PWD/${phase:-invalid}-result.json}"
spec_file=""
log_file=""
state_file=""
trap 'rm -f -- "$spec_file" "$log_file" "$state_file"' EXIT

safe_failure_phase() {
  case "$phase" in
    candidate|previous) printf '%s' "$phase" ;;
    *) printf '%s' unknown ;;
  esac
}

safe_failure_group_name() {
  local safe_phase="$1"
  local group_name="${REHEARSAL_GROUP_NAME:-}"
  local prefix
  local LC_ALL=C
  case "$safe_phase" in
    candidate) prefix="catalog-rehearsal-candidate" ;;
    previous) prefix="catalog-rehearsal-previous" ;;
    *) printf '%s' unknown; return 0 ;;
  esac
  if [[ "$group_name" =~ ^[a-z][a-z0-9-]{2,62}[a-z0-9]$ ]] &&
     [[ "$group_name" == "$prefix" || "$group_name" == "$prefix-"* ]]; then
    printf '%s' "$group_name"
  else
    printf '%s' unknown
  fi
}

emit_failure() {
  local code="$1"
  local failure_phase failure_group_name
  failure_phase="$(safe_failure_phase)"
  failure_group_name="$(safe_failure_group_name "$failure_phase")"
  python3 "$parser" --failure "$result_path" "$failure_phase" "$code" "$failure_group_name"
}

case "$phase" in
  candidate|previous) ;;
  *) emit_failure phase-invalid; exit 2 ;;
esac
for name in AZURE_SUBSCRIPTION_ID RESOURCE_GROUP REHEARSAL_GROUP_NAME; do
  if [[ -z "${!name:-}" || "${!name}" == REQUIRED ]]; then
    emit_failure input-missing
    exit 2
  fi
done

spec_file="$(mktemp)" || { emit_failure temp-file-failed; exit 2; }
if ! python3 "$renderer" --output "$spec_file" --probe-script "$probe" >/dev/null 2>&1; then
  emit_failure input-invalid
  exit 2
fi

az_bin="${AZ_BIN:-az}"
timeout_seconds="${REHEARSAL_TIMEOUT_SECONDS:-2100}"
if ! [[ "$timeout_seconds" =~ ^[0-9]+$ ]] || (( timeout_seconds < 1 || timeout_seconds > 7200 )); then
  emit_failure timeout-invalid
  exit 2
fi
cli_timeout="${AZURE_CLI_TIMEOUT_SECONDS:-120}"
if ! [[ "$cli_timeout" =~ ^[0-9]+$ ]] || (( cli_timeout < 1 || cli_timeout > 300 )); then
  emit_failure cli-timeout-invalid
  exit 2
fi

az_call() {
  local output_path="$1"
  shift
  python3 - "$cli_timeout" "$output_path" "$az_bin" "$@" <<'PY'
import os
import subprocess
import sys

timeout_seconds = int(sys.argv[1])
output_path = sys.argv[2]
command = sys.argv[3:]
try:
    completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=timeout_seconds, check=False, text=True)
except (OSError, subprocess.TimeoutExpired, ValueError):
    raise SystemExit(124)
try:
    if output_path != os.devnull:
        with open(output_path, "w", encoding="utf-8") as stream:
            stream.write(completed.stdout or "")
except OSError:
    raise SystemExit(125)
raise SystemExit(completed.returncode)
PY
}

if ! az_call /dev/null container create --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$RESOURCE_GROUP" --name "$REHEARSAL_GROUP_NAME" --file "$spec_file" --no-wait --only-show-errors; then
  emit_failure container-create-failed
  exit 3
fi

deadline=$(( $(date +%s) + timeout_seconds ))
state_file="$(mktemp)" || { emit_failure state-temp-file-failed; exit 5; }
state=""
while (( $(date +%s) < deadline )); do
  : > "$state_file"
  if az_call "$state_file" container show --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$RESOURCE_GROUP" --name "$REHEARSAL_GROUP_NAME" --query instanceView.state --output tsv --only-show-errors; then
    state="$(tr -d '\r\n' <"$state_file")"
  else
    state=""
  fi
  case "$state" in
    Succeeded|Failed|Terminated|Stopped) break ;;
    *) sleep 5 ;;
  esac
done
if [[ "$state" != Succeeded && "$state" != Failed && "$state" != Terminated && "$state" != Stopped ]]; then
  emit_failure container-timeout
  exit 4
fi

container_query() {
  : > "$state_file"
  az_call "$state_file" container show --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$RESOURCE_GROUP" --name "$REHEARSAL_GROUP_NAME" --query "$1" --output tsv --only-show-errors || return 1
  tr -d '\r\n' <"$state_file"
}

if ! api_state="$(container_query "containers[?name=='api'].instanceView.currentState.state | [0]")" ||
   ! probe_state="$(container_query "containers[?name=='probe'].instanceView.currentState.state | [0]")" ||
   ! api_exit="$(container_query "containers[?name=='api'].instanceView.currentState.exitCode | [0]")" ||
   ! probe_exit="$(container_query "containers[?name=='probe'].instanceView.currentState.exitCode | [0]")" ||
   [[ "$api_state" != Terminated || "$probe_state" != Terminated ]] ||
   ! [[ "$api_exit" =~ ^[0-9]+$ && "$probe_exit" =~ ^[0-9]+$ ]]; then
  emit_failure container-outcome-unavailable
  exit 5
fi

log_file="$(mktemp)" || { emit_failure result-temp-file-failed; exit 5; }
if ! az_call "$log_file" container logs --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$RESOURCE_GROUP" --name "$REHEARSAL_GROUP_NAME" --container-name probe --only-show-errors; then
  emit_failure probe-log-unavailable
  exit 5
fi
if ! python3 "$parser" "$log_file" "$phase" >"$result_path" 2>/dev/null; then
  emit_failure result-invalid
  exit 6
fi
cat "$result_path"
# A passing result also requires the group to have succeeded with both containers terminal at exit code zero.
if python3 "$parser" --outcome "$result_path" "$state" "$api_exit" "$probe_exit"; then
  exit 0
fi
if python3 "$parser" --is-failed "$result_path"; then
  exit 7
fi
emit_failure container-outcome-invalid
exit 7
