#!/usr/bin/env bash
set -euo pipefail

# Operator CLI for hosted App Service settings and connection strings.
# `list` returns names only. `get` reads one exact named value.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/lib/hosted-appsettings.sh
. "$SCRIPT_DIR/lib/hosted-appsettings.sh"

usage() {
  local kinds="${HOSTED_CONFIG_KINDS[*]}"
  cat <<'EOF'
Usage:
  hosted-appsettings.sh list --resource-group RG --name APP [--kind KIND] [--prefix PREFIX]
  hosted-appsettings.sh get --resource-group RG --name APP --setting EXACT_NAME [--kind KIND]
EOF
  printf 'KIND is one of %s and defaults to %s. List output contains names only.\n' "$kinds" "$(hosted_config_default_kind)"
  printf '%s\n' "A value is returned only for one exact setting name. Wildcard, prefix, and bulk value reads are rejected. Azure CLI applies its query locally to the CLI response."
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
kind="$(hosted_config_default_kind)"
prefix=""
setting=""
seen_resource_group=0
seen_name=0
seen_kind=0
seen_prefix=0
seen_setting=0

while [ $# -gt 0 ]; do
  case "$1" in
    --resource-group)
      [ "$seen_resource_group" -eq 0 ] || { echo "hosted-appsettings: duplicate --resource-group." >&2; exit 1; }
      resource_group="${2:-}"
      seen_resource_group=1
      shift 2
      ;;
    --name)
      [ "$seen_name" -eq 0 ] || { echo "hosted-appsettings: duplicate --name." >&2; exit 1; }
      webapp_name="${2:-}"
      seen_name=1
      shift 2
      ;;
    --kind)
      [ "$seen_kind" -eq 0 ] || { echo "hosted-appsettings: duplicate --kind." >&2; exit 1; }
      kind="${2:-}"
      seen_kind=1
      shift 2
      ;;
    --prefix)
      [ "$seen_prefix" -eq 0 ] || { echo "hosted-appsettings: duplicate --prefix." >&2; exit 1; }
      prefix="${2:-}"
      seen_prefix=1
      shift 2
      ;;
    --setting)
      [ "$seen_setting" -eq 0 ] || { echo "hosted-appsettings: duplicate --setting." >&2; exit 1; }
      setting="${2:-}"
      seen_setting=1
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

hosted_config_require_kind "$kind" || exit 1

HOSTED_APPSETTINGS_RESOURCE_GROUP="$resource_group"
HOSTED_APPSETTINGS_WEBAPP_NAME="$webapp_name"
export HOSTED_APPSETTINGS_RESOURCE_GROUP HOSTED_APPSETTINGS_WEBAPP_NAME

case "$command" in
  list)
    if [ "$seen_setting" -ne 0 ]; then
      echo "hosted-appsettings: list does not accept --setting; use get." >&2
      exit 1
    fi
    if [ "$seen_prefix" -eq 1 ]; then
      hosted_config_list_names "$kind" --prefix "$prefix"
    else
      hosted_config_list_names "$kind"
    fi
    ;;
  get)
    if [ "$seen_prefix" -ne 0 ]; then
      echo "hosted-appsettings: wildcard, prefix, or bulk value reads are rejected." >&2
      exit 1
    fi
    if [ "$seen_setting" -ne 1 ] || [ -z "$setting" ]; then
      echo "hosted-appsettings: get requires one exact setting name via --setting." >&2
      exit 1
    fi
    hosted_config_get_value "$kind" "$setting"
    ;;
  *)
    echo "hosted-appsettings: unknown command '$command'." >&2
    usage >&2
    exit 1
    ;;
esac
