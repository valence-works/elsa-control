# Shared staging-target predicate for operator levers.
# Explicit --environment / TARGET_ENVIRONMENT wins over AZURE_ENV_NAME.
# Set names must be exactly equal. TARGET_ENVIRONMENT=test is the GitHub
# environment alias for valence-control-staging only; --environment test
# and AZURE_ENV_NAME=valence-control-staging stay distinct (rg-test vs
# rg-valence-control-staging). Any mismatch or non-test target fails closed.

is_staging_azure_env_name() {
  [ "$1" = "test" ] || [ "$1" = "valence-control-staging" ]
}

# TARGET_ENVIRONMENT=test aliases to the real staging Azure env name.
# ENVIRONMENT_NAME and AZURE_ENV_NAME are compared as written.
canonical_staging_lever_target_name() {
  if [ "$1" = "test" ]; then
    printf '%s\n' "valence-control-staging"
  else
    printf '%s\n' "$1"
  fi
}

# True when the set names are not exactly equal after aliasing
# TARGET_ENVIRONMENT=test only.
staging_lever_target_names_disagree() {
  local first=""
  local name=""
  if [ -n "${TARGET_ENVIRONMENT:-}" ]; then
    first="$(canonical_staging_lever_target_name "$TARGET_ENVIRONMENT")"
  fi
  for name in "${ENVIRONMENT_NAME:-}" "${AZURE_ENV_NAME:-}"; do
    [ -z "$name" ] && continue
    if [ -z "$first" ]; then
      first="$name"
    elif [ "$name" != "$first" ]; then
      return 0
    fi
  done
  return 1
}

require_consistent_staging_lever_target_names() {
  if staging_lever_target_names_disagree; then
    echo "Staging lever target names disagree; explicit --environment / TARGET_ENVIRONMENT wins over AZURE_ENV_NAME, and a mismatch or non-test target is refused." >&2
    return 1
  fi
  return 0
}

is_staging_lever_target() {
  if staging_lever_target_names_disagree; then
    return 1
  fi
  if [ -n "${TARGET_ENVIRONMENT:-}" ]; then
    [ "$TARGET_ENVIRONMENT" = "test" ] || is_staging_azure_env_name "$TARGET_ENVIRONMENT"
  elif [ -n "${ENVIRONMENT_NAME:-}" ]; then
    is_staging_azure_env_name "$ENVIRONMENT_NAME"
  else
    is_staging_azure_env_name "${AZURE_ENV_NAME:-}"
  fi
}

# #652 / #655 name kept so existing deploy-script callers and tests stay valid.
is_staging_billing_lever_target() {
  is_staging_lever_target
}

is_test_deploy_target() {
  is_staging_lever_target
}
