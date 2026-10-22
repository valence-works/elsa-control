#!/usr/bin/env bash
# Print Key Vault reference status (and SCM/FTP basic-auth publishing) for the
# Control API site. Names and status only. Never prints setting values.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

usage() {
  cat <<'EOF'
Usage:
  scripts/check-key-vault-references.sh --resource-group RG --name APP

Prints each secret app-setting name and its Key Vault reference status.
A required setting must be Resolved. An optional setting (an opt-in
feature such as RateLimiting:BffCredentials) prints NotSet when the app
setting does not exist, fails as NotAReference when it exists with a plain
value, and must be Resolved when it is a reference.
Also prints SCM and FTP basic-auth publishing allow=true/false.
Never prints setting values or secret material.
EOF
}

resource_group=""
webapp_name=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --resource-group)
      resource_group="${2:-}"
      shift 2
      ;;
    --name)
      webapp_name="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "check-key-vault-references: unknown argument '$1'." >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ -z "$resource_group" || -z "$webapp_name" ]]; then
  echo "check-key-vault-references: --resource-group and --name are required." >&2
  usage >&2
  exit 1
fi

# One "required|optional<TAB>name" line per secret setting.
SETTING_NAMES="$(python3 - <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, str(Path("scripts") / "lib"))
from key_vault_app_settings import OPTIONAL_SECRET_APP_SETTING_NAMES, REQUIRED_SECRET_APP_SETTING_NAMES
for name in REQUIRED_SECRET_APP_SETTING_NAMES:
    print(f"required\t{name}")
for name in OPTIONAL_SECRET_APP_SETTING_NAMES:
    print(f"optional\t{name}")
PY
)"

id="$(az webapp show \
  --resource-group "$resource_group" \
  --name "$webapp_name" \
  --query id \
  --output tsv \
  --only-show-errors)"
if [[ ! "$id" =~ ^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft.Web/sites/[^/]+$ ]]; then
  echo "check-key-vault-references: the web app id is missing or invalid." >&2
  exit 1
fi

references_json="$(az rest \
  --method get \
  --uri "${id}/config/configreferences/appsettings?api-version=2022-03-01" \
  --output json \
  --only-show-errors)"

# Names only: tells an absent optional setting apart from one stored as a plain value.
present_names="$(az webapp config appsettings list \
  --resource-group "$resource_group" \
  --name "$webapp_name" \
  --query "[].name" \
  --output tsv \
  --only-show-errors)"

printf '%s\n' "$SETTING_NAMES" | python3 -c '
import json
import sys

wanted = [line.split("\t", 1) for line in sys.stdin.read().splitlines() if line]
present = {line.strip() for line in sys.argv[2].splitlines() if line.strip()}
payload = json.loads(sys.argv[1])
# ApiKVReferenceCollection: a "value" array of ApiKVReference resources. The last segment
# of each id is the setting name, two segments after configreferences; the status is
# properties.status.
items = payload.get("value") if isinstance(payload, dict) else None
if not isinstance(items, list):
    raise SystemExit("check-key-vault-references: reference status payload is invalid")
if payload.get("nextLink"):
    raise SystemExit("check-key-vault-references: reference status payload is paged; not supported")
properties = {}
for item in items:
    resource_id = item.get("id") if isinstance(item, dict) else None
    segments = resource_id.split("/") if isinstance(resource_id, str) else []
    if len(segments) < 3 or segments[-3] != "configreferences" or not segments[-1]:
        raise SystemExit("check-key-vault-references: reference status payload is invalid")
    entry = item.get("properties")
    properties[segments[-1]] = entry if isinstance(entry, dict) else {}
failed = False
for kind, name in wanted:
    entry = properties.get(name)
    if entry is None and kind == "optional":
        if name in present:
            print(f"{name}\tNotAReference")
            failed = True
        else:
            print(f"{name}\tNotSet")
        continue
    status = "Missing"
    if isinstance(entry, dict):
        raw = entry.get("status")
        status = raw if isinstance(raw, str) and raw else "Unknown"
    print(f"{name}\t{status}")
    if status != "Resolved":
        failed = True
if failed:
    raise SystemExit(2)
' "$references_json" "$present_names"

for policy in scm ftp; do
  allow="$(az resource show \
    --ids "${id}/basicPublishingCredentialsPolicies/${policy}" \
    --query properties.allow \
    --output tsv \
    --only-show-errors)"
  echo "basicPublishingCredentialsPolicies/${policy}	allow=${allow}"
  if [[ "$allow" != "false" ]]; then
    echo "check-key-vault-references: ${policy} basic-auth publishing is not off." >&2
    exit 3
  fi
done
