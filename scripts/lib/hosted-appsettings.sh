# Name-only projection for hosted App Service application settings.
# Listings never request or print values. A value is read only when the
# caller names exactly one setting.

HOSTED_APPSETTINGS_LIB_LOADED=1

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

hosted_appsettings_az_list() {
  local query="$1"
  local output="${2:-tsv}"
  local resource_group webapp_name
  hosted_appsettings_require_target || return 1
  resource_group="$(hosted_appsettings_resource_group)"
  webapp_name="$(hosted_appsettings_webapp_name)"
  az webapp config appsettings list \
    --resource-group "$resource_group" \
    --name "$webapp_name" \
    --query "$query" \
    --output "$output" \
    --only-show-errors
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
hosted_appsettings_list_names() {
  local filter
  filter="$(hosted_appsettings_name_filter "$@")" || return 1
  if [ -z "$filter" ]; then
    hosted_appsettings_az_list '[].name' tsv
  else
    hosted_appsettings_az_list "[?${filter}].name" tsv
  fi
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
hosted_appsettings_resolve_name() {
  local requested="${1:-}"
  hosted_appsettings_require_exact_name "$requested" || return 1
  local names
  names="$(hosted_appsettings_az_list '[].name' tsv)" || return 1
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

# Read one value by exact setting name. Rejects wildcard, prefix, and bulk reads.
# A missing setting fails with a not-found error. An empty value is still found.
hosted_appsettings_get_value() {
  local setting="${1:-}"
  hosted_appsettings_require_exact_name "$setting" || return 1
  if [ "$#" -ne 1 ]; then
    echo "hosted-appsettings: reading a value requires one exact setting name." >&2
    return 1
  fi
  local records
  records="$(hosted_appsettings_get_record "$setting")" || return 1
  if ! printf '%s' "$records" | jq -e 'type == "array" and length == 1' >/dev/null; then
    echo "hosted-appsettings: setting not found: ${setting}" >&2
    return 1
  fi
  printf '%s' "$records" | jq -r '.[0].value // empty'
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
hosted_appsettings_get_record() {
  local setting="${1:-}"
  hosted_appsettings_require_exact_name "$setting" || return 1
  local canonical
  local status=0
  canonical="$(hosted_appsettings_resolve_name "$setting")" && status=0 || status=$?
  if [ "$status" -eq 2 ]; then
    printf '%s\n' "[]"
    return 0
  fi
  if [ "$status" -ne 0 ]; then
    return "$status"
  fi
  hosted_appsettings_az_list "[?name=='${canonical}']" json
}
