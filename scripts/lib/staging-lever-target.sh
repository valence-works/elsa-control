# Shared staging-target predicate for operator levers.
# Explicit --environment / TARGET_ENVIRONMENT wins over AZURE_ENV_NAME.
# Azure names (ENVIRONMENT_NAME / AZURE_ENV_NAME) must be exactly equal.
# TARGET_ENVIRONMENT=test is the only GitHub environment that arms the lever.
# It aliases to valence-control-staging for agreement with Azure names.
# Bare --environment test (rg-test) does not arm the lever. The Azure name that
# arms the lever is valence-control-staging only. Any mismatch or non-test
# target fails closed.

is_staging_azure_env_name() {
  [ "$1" = "test" ] || [ "$1" = "valence-control-staging" ]
}

# True when the set names cannot be the same deploy target.
# ENVIRONMENT_NAME and AZURE_ENV_NAME compare as written. TARGET_ENVIRONMENT=test
# aliases to valence-control-staging; any other TARGET_ENVIRONMENT refuses a
# staging Azure name.
staging_lever_target_names_disagree() {
  local azure=""
  local name=""
  for name in "${ENVIRONMENT_NAME:-}" "${AZURE_ENV_NAME:-}"; do
    [ -z "$name" ] && continue
    if [ -z "$azure" ]; then
      azure="$name"
    elif [ "$name" != "$azure" ]; then
      return 0
    fi
  done

  if [ -n "${TARGET_ENVIRONMENT:-}" ]; then
    if [ "$TARGET_ENVIRONMENT" = "test" ]; then
      if [ -n "$azure" ] && [ "$azure" != "valence-control-staging" ]; then
        return 0
      fi
    elif [ -n "$azure" ] && is_staging_azure_env_name "$azure"; then
      return 0
    fi
  fi
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
    [ "$TARGET_ENVIRONMENT" = "test" ]
  elif [ -n "${ENVIRONMENT_NAME:-}" ]; then
    [ "$ENVIRONMENT_NAME" = "valence-control-staging" ]
  else
    [ "${AZURE_ENV_NAME:-}" = "valence-control-staging" ]
  fi
}

# #652 / #655 name kept so existing deploy-script callers and tests stay valid.
is_staging_billing_lever_target() {
  is_staging_lever_target
}

is_test_deploy_target() {
  is_staging_lever_target
}
