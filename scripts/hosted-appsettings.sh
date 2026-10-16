#!/usr/bin/env bash
set -euo pipefail

# Operator CLI for hosted App Service application settings.
# `list` prints names only. `get` reads exactly one named setting.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/hosted-appsettings.sh
. "$SCRIPT_DIR/lib/hosted-appsettings.sh"

usage() {
  cat <<'EOF'
Usage:
  hosted-appsettings.sh list --resource-group RG --name APP [--prefix PREFIX]
  hosted-appsettings.sh get --resource-group RG --name APP --setting EXACT_NAME

Listings print names only. A value is returned only when one exact setting
name is given to get. Wildcard, prefix, and bulk value reads are rejected.
EOF
}

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ] || [ $# -eq 0 ]; then
  usage
  [ $# -eq 0 ] && exit 1
  exit 0
fi

command="$1"
shift

resource_group=""
webapp_name=""
prefix=""
setting=""

while [ $# -gt 0 ]; do
  case "$1" in
    --resource-group)
      resource_group="${2:-}"
      shift 2
      ;;
    --name)
      webapp_name="${2:-}"
      shift 2
      ;;
    --prefix)
      prefix="${2:-}"
      shift 2
      ;;
    --setting)
      setting="${2:-}"
      shift 2
      ;;
    --query|--filter)
      echo "hosted-appsettings: raw queries are rejected; use list or get." >&2
      exit 1
      ;;
    *)
      echo "hosted-appsettings: unknown argument '$1'." >&2
      usage >&2
      exit 1
      ;;
  esac
done

HOSTED_APPSETTINGS_RESOURCE_GROUP="$resource_group"
HOSTED_APPSETTINGS_WEBAPP_NAME="$webapp_name"
export HOSTED_APPSETTINGS_RESOURCE_GROUP HOSTED_APPSETTINGS_WEBAPP_NAME

case "$command" in
  list)
    if [ -n "$setting" ]; then
      echo "hosted-appsettings: list does not accept --setting; use get." >&2
      exit 1
    fi
    if [ -n "$prefix" ]; then
      hosted_appsettings_list_names --prefix "$prefix"
    else
      hosted_appsettings_list_names
    fi
    ;;
  get)
    if [ -n "$prefix" ]; then
      echo "hosted-appsettings: wildcard, prefix, or bulk value reads are rejected." >&2
      exit 1
    fi
    hosted_appsettings_get_value "$setting"
    ;;
  *)
    echo "hosted-appsettings: unknown command '$command'." >&2
    usage >&2
    exit 1
    ;;
esac
