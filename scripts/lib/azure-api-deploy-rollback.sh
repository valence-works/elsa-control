#!/usr/bin/env bash

# Shared Azure App Service runtime capture, restore, and health helpers.
#
# The deployment workflow and the staging rollback proof source this file.  The
# helpers deliberately accept only the two mutable runtime values that the
# workflows own: the main image reference and Application__BuildNumber.  They
# never read or write secrets and they do not change infrastructure settings.

azure_require_immutable_digest() {
  local digest="${1:-}"
  if [[ ! "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]; then
    echo "::error::The image digest must be an immutable sha256 digest." >&2
    return 1
  fi
}

azure_verify_acr_digest() {
  local registry_endpoint="$1"
  local digest="$2"
  local acr_name verified

  if [[ ! "$registry_endpoint" =~ ^[a-z0-9][a-z0-9.-]*\.azurecr\.io$ ]]; then
    echo "::error::The configured container registry endpoint has an unexpected format." >&2
    return 1
  fi
  azure_require_immutable_digest "$digest" || return 1
  acr_name="${registry_endpoint%%.azurecr.io}"
  if ! verified="$(az acr manifest show-metadata \
    --registry "$acr_name" \
    --name "elsa-control/api@$digest" \
    --query digest \
    --output tsv \
    --only-show-errors)"; then
    echo "::error::The image digest could not be verified in the configured ACR repository." >&2
    return 1
  fi
  if [ "$verified" != "$digest" ]; then
    echo "::error::The configured ACR repository returned a different image digest." >&2
    return 1
  fi
}

azure_capture_health_identity() {
  local default_host_name health_url health_response_file http_status identifier value
  AZURE_CAPTURED_HEALTH_BUILD_NUMBER=""
  AZURE_CAPTURED_HEALTH_IMAGE_ID=""

  if ! default_host_name="$(az webapp show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query defaultHostName \
    --output tsv)"; then
    echo "::error::Could not resolve the current API host for deployment identity capture; refusing an unprotected deployment." >&2
    return 1
  fi
  if [ -z "$default_host_name" ]; then
    echo "::error::Azure returned no current API host for deployment identity capture; refusing an unprotected deployment." >&2
    return 1
  fi

  health_url="https://$default_host_name/health"
  health_response_file="$(mktemp)"
  if ! http_status="$(curl --silent --show-error --output "$health_response_file" --write-out '%{http_code}' --max-time 10 "$health_url")"; then
    http_status=000
  fi
  if [ "$http_status" = "200" ] && jq -e '.status == "ok"' "$health_response_file" >/dev/null 2>&1; then
    if jq -e 'type == "object"' "$health_response_file" >/dev/null 2>&1; then
      AZURE_CAPTURED_HEALTH_BUILD_NUMBER="$(jq -r '.buildNumber // empty' "$health_response_file")"
      AZURE_CAPTURED_HEALTH_IMAGE_ID="$(jq -r '.imageId // empty' "$health_response_file")"
      if [ "$AZURE_CAPTURED_HEALTH_BUILD_NUMBER" = "unknown" ]; then
        AZURE_CAPTURED_HEALTH_BUILD_NUMBER=""
      fi
      if [ "$AZURE_CAPTURED_HEALTH_IMAGE_ID" = "unknown" ]; then
        AZURE_CAPTURED_HEALTH_IMAGE_ID=""
      fi
      for identifier in AZURE_CAPTURED_HEALTH_BUILD_NUMBER AZURE_CAPTURED_HEALTH_IMAGE_ID; do
        value="${!identifier}"
        if [ -n "$value" ] && [[ ! "$value" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$ ]]; then
          rm -f "$health_response_file"
          echo "::error::The current API health response contains an unexpected or unsafe identity; refusing an unprotected deployment." >&2
          return 1
        fi
      done
      echo "Captured safe current API build/image identity for rollback verification."
    else
      echo "::notice::The current API health response had no object identity fields; retaining Azure image/build metadata and using rollback compatibility verification."
    fi
  else
    echo "::notice::Current API health was unavailable or not healthy at $health_url (HTTP ${http_status:-000}); retaining Azure image/build metadata and using rollback compatibility verification."
  fi
  rm -f "$health_response_file"
}

azure_update_sitecontainer_runtime() {
  local image="$1"
  local build_present="$2"
  local build_number="$3"
  local build_slot_setting="${4:-false}"

  if [[ ! "$image" =~ ^[[:alnum:]][[:alnum:].-]*(:[[:digit:]]+)?/[[:alnum:]][[:alnum:]._/-]*@sha256:[0-9a-f]{64}$ ]]; then
    echo "::error::The sitecontainer image must be an owned immutable repository reference." >&2
    return 1
  fi
  if ! az webapp sitecontainers update \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --container-name main \
    --image "$image" \
    --is-main true \
    --output none \
    --only-show-errors; then
    echo "::error::Could not update the main sitecontainer image." >&2
    return 1
  fi
  if [ "$build_present" = true ]; then
    local -a setting_args=(--settings "Application__BuildNumber=$build_number")
    if [ "$build_slot_setting" = true ]; then
      setting_args=(--slot-settings "Application__BuildNumber=$build_number")
    fi
    if ! az webapp config appsettings set \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      "${setting_args[@]}" \
      --output none \
      --only-show-errors; then
      echo "::error::Could not update Application__BuildNumber." >&2
      return 1
    fi
  else
    if ! az webapp config appsettings delete \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --setting-names Application__BuildNumber \
      --output none \
      --only-show-errors; then
      echo "::error::Could not remove Application__BuildNumber." >&2
      return 1
    fi
  fi
}

azure_restore_api_runtime_configuration() {
  local deployment_mode="$1"
  local linux_fx_version="$2"
  local sitecontainer_image="$3"
  local build_present="$4"
  local build_number="$5"
  local build_slot_setting="${6:-false}"

  case "$deployment_mode" in
    classic)
      if ! az webapp config set \
        --resource-group "$AZURE_RESOURCE_GROUP" \
        --name "$AZURE_WEBAPP_NAME" \
        --linux-fx-version "$linux_fx_version" \
        --output none \
        --only-show-errors; then
        echo "::error::Rollback failed: could not restore the previous Docker runtime image." >&2
        return 1
      fi
      ;;
    sitecontainers)
      if [ -z "$sitecontainer_image" ]; then
        echo "::error::Rollback failed: the previous main sitecontainer image was not captured." >&2
        return 1
      fi
      if ! az webapp config set \
        --resource-group "$AZURE_RESOURCE_GROUP" \
        --name "$AZURE_WEBAPP_NAME" \
        --linux-fx-version "$linux_fx_version" \
        --output none \
        --only-show-errors; then
        echo "::error::Rollback failed: could not restore the SITECONTAINERS runtime." >&2
        return 1
      fi
      if ! az webapp sitecontainers update \
        --resource-group "$AZURE_RESOURCE_GROUP" \
        --name "$AZURE_WEBAPP_NAME" \
        --container-name main \
        --image "$sitecontainer_image" \
        --is-main true \
        --output none \
        --only-show-errors; then
        echo "::error::Rollback failed: could not restore the previous main sitecontainer image." >&2
        return 1
      fi
      ;;
    *)
      echo "::error::Rollback failed: the captured deployment mode is unsupported." >&2
      return 1
      ;;
  esac

  if [ "$build_present" = true ]; then
    local -a setting_args=(--settings "Application__BuildNumber=$build_number")
    if [ "$build_slot_setting" = true ]; then
      setting_args=(--slot-settings "Application__BuildNumber=$build_number")
    fi
    az webapp config appsettings set \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      "${setting_args[@]}" \
      --output none \
      --only-show-errors || {
        echo "::error::Rollback failed: could not restore the previous Application__BuildNumber." >&2
        return 1
      }
  else
    az webapp config appsettings delete \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --setting-names Application__BuildNumber \
      --output none \
      --only-show-errors || {
        echo "::error::Rollback failed: could not remove the previous Application__BuildNumber." >&2
        return 1
      }
  fi
}

azure_verify_api_runtime_reference() {
  local deployment_mode="$1"
  local expected_linux_fx_version="$2"
  local expected_sitecontainer_image="$3"
  local restored_runtime_image

  if [ "$deployment_mode" = classic ]; then
    restored_runtime_image="$(az webapp config show \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --query linuxFxVersion \
      --output tsv \
      --only-show-errors)" || {
        echo "::error::Rollback failed: could not verify the restored Docker runtime image." >&2
        return 1
      }
    if [ "$restored_runtime_image" != "$expected_linux_fx_version" ]; then
      echo "::error::Rollback failed: Azure is not configured with the captured previous Docker runtime image." >&2
      return 1
    fi
  elif [ "$deployment_mode" = sitecontainers ]; then
    restored_runtime_image="$(az webapp sitecontainers show \
      --resource-group "$AZURE_RESOURCE_GROUP" \
      --name "$AZURE_WEBAPP_NAME" \
      --container-name main \
      --query image \
      --output tsv \
      --only-show-errors)" || {
        echo "::error::Rollback failed: could not verify the restored main sitecontainer image." >&2
        return 1
      }
    if [ "$restored_runtime_image" != "$expected_sitecontainer_image" ]; then
      echo "::error::Rollback failed: Azure is not configured with the captured previous main sitecontainer image." >&2
      return 1
    fi
  else
    echo "::error::Rollback failed: the captured deployment mode is unsupported." >&2
    return 1
  fi
}

azure_restart_and_verify_api_runtime() {
  local deployment_mode="$1"
  local expected_linux_fx_version="$2"
  local expected_sitecontainer_image="$3"
  if ! az webapp restart \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --output none \
    --only-show-errors; then
    echo "::error::Rollback failed: could not restart the API after restoring the previous deployment." >&2
    return 1
  fi
  azure_verify_api_runtime_reference \
    "$deployment_mode" "$expected_linux_fx_version" "$expected_sitecontainer_image"
}

azure_wait_for_stable_api_health() {
  local expected_build_number="$1"
  local expected_image_id="$2"
  local description="${3:-API}"
  local host health_url response_file http_status stable=0
  local attempts="${AZURE_HEALTH_ATTEMPTS:-30}"
  local retry_seconds="${AZURE_HEALTH_RETRY_SECONDS:-10}"
  local curl_max_time="${AZURE_HEALTH_CURL_MAX_TIME:-10}"
  local attempt

  host="$(az webapp show \
    --resource-group "$AZURE_RESOURCE_GROUP" \
    --name "$AZURE_WEBAPP_NAME" \
    --query defaultHostName \
    --output tsv \
    --only-show-errors)" || {
      echo "::error::Could not resolve the API host for the ${description,,} health check." >&2
      return 1
    }
  [ -n "$host" ] || {
    echo "::error::Azure returned no API host for the ${description,,} health check." >&2
    return 1
  }
  health_url="https://$host/health"
  response_file="$(mktemp)"

  for attempt in $(seq 1 "$attempts"); do
    if http_status="$(curl --silent --show-error --output "$response_file" --write-out '%{http_code}' --max-time "$curl_max_time" "$health_url")"; then
      if [ "$http_status" = 200 ] && jq -e \
        --arg expected_build_number "$expected_build_number" \
        --arg expected_image_id "$expected_image_id" \
        '(.status == "ok") and
         (if $expected_image_id == "" then
            ($expected_build_number == "" or ((.buildNumber // $expected_build_number) == $expected_build_number))
          else
            ($expected_build_number == "" or .buildNumber == $expected_build_number) and .imageId == $expected_image_id
          end)' \
        "$response_file" >/dev/null 2>&1; then
        stable=$((stable + 1))
        if [ "$stable" -ge 2 ]; then
          rm -f "$response_file"
          echo "${description} health check passed with HTTP 200 and the expected build/image identity on attempt $attempt."
          return 0
        fi
      else
        stable=0
      fi
    else
      http_status=000
      stable=0
    fi
    echo "${description} health check returned HTTP $http_status or an unexpected build/image identity (attempt $attempt/$attempts, stable probes $stable/2); retrying."
    sleep "$retry_seconds"
  done
  rm -f "$response_file"
  echo "::error::${description} health check failed after deployment: $health_url" >&2
  return 1
}
