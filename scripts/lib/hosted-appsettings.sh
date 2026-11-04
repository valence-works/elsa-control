# Name-only output and exact-name reads for hosted App Service configuration.
# Azure CLI applies --query as a client-side projection; only the selected
# name or exact value is returned by this helper.

HOSTED_APPSETTINGS_LIB_LOADED=1
HOSTED_CONFIG_KINDS=(appsettings connection-string)

hosted_config_default_kind() {
  printf '%s' "${HOSTED_CONFIG_KINDS[0]}"
}

hosted_config_require_kind() {
  local kind="${1:-}"
  local allowed
  for allowed in "${HOSTED_CONFIG_KINDS[@]}"; do
    [ "$kind" = "$allowed" ] && return 0
  done
  echo "hosted-appsettings: kind must be one of: ${HOSTED_CONFIG_KINDS[*]}." >&2
  return 1
}

hosted_appsettings_resource_group() {
  printf '%s' "${HOSTED_APPSETTINGS_RESOURCE_GROUP:-${AZURE_RESOURCE_GROUP:-}}"
}

hosted_appsettings_webapp_name() {
  printf '%s' "${HOSTED_APPSETTINGS_WEBAPP_NAME:-${AZURE_WEBAPP_NAME:-}}"
}

hosted_appsettings_require_target() {
  local resource_group webapp_name
  resource_group="$(hosted_appsettings_resource_group)"
  webapp_name="$(hosted_appsettings_webapp_name)"
  if [ -z "$resource_group" ]; then
    echo "hosted-appsettings: resource group is required." >&2
    return 1
  fi
  if [ -z "$webapp_name" ]; then
    echo "hosted-appsettings: webapp name is required." >&2
    return 1
  fi
}

hosted_appsettings_require_exact_name() {
  local name="${1:-}"
  if [ -z "$name" ]; then
    echo "hosted-appsettings: reading a value requires one exact setting name." >&2
    return 1
  fi
  case "$name" in
    *'*'*|*'?'*|*'['*|*']'*|*','*|*[[:space:]]*)
      echo "hosted-appsettings: wildcard, prefix, or bulk value reads are rejected." >&2
      return 1
      ;;
  esac
  if [[ ! "$name" =~ ^[A-Za-z0-9_][A-Za-z0-9_.:-]*$ ]]; then
    echo "hosted-appsettings: setting name is not an exact identifier." >&2
    return 1
  fi
}

hosted_appsettings_require_safe_prefix() {
  local prefix="${1:-}"
  if [ -z "$prefix" ]; then
    echo "hosted-appsettings: name prefix is required." >&2
    return 1
  fi
  case "$prefix" in
    *'*'*|*'?'*|*'['*|*']'*|*','*|*[[:space:]]*)
      echo "hosted-appsettings: wildcard, prefix, or bulk value reads are rejected." >&2
      return 1
      ;;
  esac
  if [[ ! "$prefix" =~ ^[A-Za-z0-9_][A-Za-z0-9_.:-]*$ ]]; then
    echo "hosted-appsettings: name prefix is not an exact identifier." >&2
    return 1
  fi
}

hosted_config_az_list() {
  local kind="${1:-}"
  local query="${2:-}"
  local output="${3:-tsv}"
  local resource_group webapp_name
  local -a target_arguments=()
  hosted_config_require_kind "$kind" || return 1
  hosted_appsettings_require_target || return 1
  resource_group="$(hosted_appsettings_resource_group)"
  webapp_name="$(hosted_appsettings_webapp_name)"
  target_arguments=(
    --resource-group "$resource_group"
    --name "$webapp_name"
  )
  case "$kind" in
    appsettings)
      az webapp config appsettings list "${target_arguments[@]}" \
        --query "$query" --output "$output" --only-show-errors
      ;;
    connection-string)
      az webapp config connection-string list "${target_arguments[@]}" \
        --query "$query" --output "$output" --only-show-errors
      ;;
  esac
}

hosted_appsettings_az_list() {
  hosted_config_az_list appsettings "$@"
}

hosted_appsettings_name_filter() {
  local prefix=""
  local names=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --prefix)
        prefix="${2:-}"
        shift 2
        ;;
      --name)
        names+=("${2:-}")
        shift 2
        ;;
      *)
        echo "hosted-appsettings: unknown name filter '$1'." >&2
        return 1
        ;;
    esac
  done

  local parts=()
  if [ -n "$prefix" ]; then
    hosted_appsettings_require_safe_prefix "$prefix" || return 1
    parts+=("starts_with(name, '${prefix}')")
  fi
  local name
  for name in "${names[@]+"${names[@]}"}"; do
    hosted_appsettings_require_exact_name "$name" || return 1
    parts+=("name=='${name}'")
  done
  if [ "${#parts[@]}" -eq 0 ]; then
    printf '%s' ""
    return 0
  fi
  local filter="${parts[0]}"
  local index
  for ((index = 1; index < ${#parts[@]}; index++)); do
    filter="${filter} || ${parts[$index]}"
  done
  printf '%s' "$filter"
}

# List setting names only. Optional --prefix / --name filters still project names.
hosted_config_list_names() {
  local kind="${1:-}"
  shift
  local filter
  filter="$(hosted_appsettings_name_filter "$@")" || return 1
  if [ -z "$filter" ]; then
    hosted_config_az_list "$kind" '[].name' tsv
  else
    hosted_config_az_list "$kind" "[?${filter}].name" tsv
  fi
}

hosted_appsettings_list_names() {
  hosted_config_list_names appsettings "$@"
}

# Count matching settings without requesting values.
hosted_appsettings_count() {
  local filter
  filter="$(hosted_appsettings_name_filter "$@")" || return 1
  if [ -z "$filter" ]; then
    hosted_appsettings_az_list 'length(@)' tsv
  else
    hosted_appsettings_az_list "[?${filter}] | length(@)" tsv
  fi
}

# Resolve one setting name the way App Service does: case-insensitive,
# names-only listing first, then the hosted spelling for an exact read.
# Returns 2 when the name is absent.
hosted_config_resolve_name() {
  local kind="${1:-}"
  shift
  local requested="${1:-}"
  hosted_appsettings_require_exact_name "$requested" || return 1
  local names
  names="$(hosted_config_az_list "$kind" '[].name' tsv)" || return 1
  local match=""
  local name
  local requested_folded="${requested,,}"
  while IFS= read -r name || [ -n "$name" ]; do
    [ -z "$name" ] && continue
    if [ "${name,,}" = "$requested_folded" ]; then
      if [ -n "$match" ] && [ "$match" != "$name" ]; then
        echo "hosted-appsettings: setting name matches more than one hosted name." >&2
        return 1
      fi
      match="$name"
    fi
  done <<< "$names"
  if [ -z "$match" ]; then
    return 2
  fi
  printf '%s' "$match"
}

hosted_appsettings_resolve_name() {
  hosted_config_resolve_name appsettings "$@"
}

# Read one value by exact setting name. Rejects wildcard, prefix, and bulk reads.
# A missing setting fails with a not-found error. An empty value is still found.
hosted_config_get_value() {
  local kind="${1:-}"
  local setting="${2:-}"
  hosted_appsettings_require_exact_name "$setting" || return 1
  if [ "$#" -ne 2 ]; then
    echo "hosted-appsettings: reading a value requires one exact setting name." >&2
    return 1
  fi
  local records
  records="$(hosted_config_get_record "$kind" "$setting")" || return 1
  if ! printf '%s' "$records" | jq -e 'type == "array" and length == 1' >/dev/null; then
    echo "hosted-appsettings: setting not found: ${setting}" >&2
    return 1
  fi
  printf '%s' "$records" | jq -r '.[0].value // empty'
}

hosted_appsettings_get_value() {
  hosted_config_get_value appsettings "$@"
}

hosted_appsettings_get_slot_setting() {
  local setting="${1:-}"
  hosted_appsettings_require_exact_name "$setting" || return 1
  local canonical
  local status=0
  canonical="$(hosted_appsettings_resolve_name "$setting")" && status=0 || status=$?
  if [ "$status" -eq 2 ]; then
    echo "hosted-appsettings: setting not found: ${setting}" >&2
    return 1
  fi
  if [ "$status" -ne 0 ]; then
    return "$status"
  fi
  hosted_appsettings_az_list "[?name=='${canonical}'] | [0].slotSetting" json
}

# One exact setting as a JSON array of zero or one objects.
# A missing setting is an empty array so callers can treat absence as valid.
hosted_config_get_record() {
  local kind="${1:-}"
  local setting="${2:-}"
  hosted_appsettings_require_exact_name "$setting" || return 1
  if [ "$#" -ne 2 ]; then
    echo "hosted-appsettings: reading a value requires one exact setting name." >&2
    return 1
  fi
  local canonical
  local status=0
  canonical="$(hosted_config_resolve_name "$kind" "$setting")" && status=0 || status=$?
  if [ "$status" -eq 2 ]; then
    printf '%s\n' "[]"
    return 0
  fi
  if [ "$status" -ne 0 ]; then
    return "$status"
  fi
  hosted_config_az_list "$kind" "[?name=='${canonical}']" json
}

hosted_appsettings_get_record() {
  hosted_config_get_record appsettings "$@"
}
