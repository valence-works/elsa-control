# Shared staging-target predicate for operator levers.
# Prefer TARGET_ENVIRONMENT (the workflow's GitHub environment / dispatch input).
# Direct invocation also accepts the staging Azure env name valence-control-staging.
is_staging_lever_target() {
  if [ -n "${TARGET_ENVIRONMENT:-}" ]; then
    [ "$TARGET_ENVIRONMENT" = "test" ]
  else
    local env_name="${AZURE_ENV_NAME:-${ENVIRONMENT_NAME:-}}"
    [ "$env_name" = "test" ] || [ "$env_name" = "valence-control-staging" ]
  fi
}

# #652 / #655 name kept so existing deploy-script callers and tests stay valid.
is_staging_billing_lever_target() {
  is_staging_lever_target
}

is_test_deploy_target() {
  is_staging_lever_target
}
