# Shared staging-target predicate for operator levers.
# Explicit --environment / TARGET_ENVIRONMENT wins over AZURE_ENV_NAME.
# Any disagreement among the provided names, or a non-test target, fails closed.

is_staging_azure_env_name() {
  [ "$1" = "test" ] || [ "$1" = "valence-control-staging" ]
}

# True when TARGET_ENVIRONMENT, ENVIRONMENT_NAME, and AZURE_ENV_NAME (those that
# are set) do not all classify as staging or all classify as non-staging.
staging_lever_target_names_disagree() {
  local staging=0
  local other=0
  if [ -n "${TARGET_ENVIRONMENT:-}" ]; then
    if [ "$TARGET_ENVIRONMENT" = "test" ]; then
      staging=1
    else
      other=1
    fi
  fi
  if [ -n "${ENVIRONMENT_NAME:-}" ]; then
    if is_staging_azure_env_name "$ENVIRONMENT_NAME"; then
      staging=1
    else
      other=1
    fi
  fi
  if [ -n "${AZURE_ENV_NAME:-}" ]; then
    if is_staging_azure_env_name "$AZURE_ENV_NAME"; then
      staging=1
    else
      other=1
    fi
  fi
  [ "$staging" -eq 1 ] && [ "$other" -eq 1 ]
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
