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

SETTING_NAMES="$(python3 - <<'PY'
from pathlib import Path
import sys
sys.path.insert(0, str(Path("scripts") / "lib"))
from key_vault_app_settings import SECRET_APP_SETTING_NAMES
print("\n".join(SECRET_APP_SETTING_NAMES))
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

printf '%s\n' "$SETTING_NAMES" | python3 -c '
import json
import sys

wanted = [line for line in sys.stdin.read().splitlines() if line]
payload = json.loads(sys.argv[1])
properties = payload.get("properties")
if not isinstance(properties, dict):
    raise SystemExit("check-key-vault-references: reference status payload is invalid")
failed = False
for name in wanted:
    entry = properties.get(name)
    status = "Missing"
    if isinstance(entry, dict):
        raw = entry.get("status")
        status = raw if isinstance(raw, str) and raw else "Unknown"
    print(f"{name}\t{status}")
    if status != "Resolved":
        failed = True
if failed:
    raise SystemExit(2)
' "$references_json"

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
