#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: scripts/validate-api-provider-image.sh <image>" >&2
}

fail() {
  echo "API provider image smoke check failed: $1" >&2
  exit 1
}

if (($# != 1)) || [[ -z "$1" ]]; then
  usage
  exit 2
fi

image="$1"
command -v docker >/dev/null 2>&1 || fail "docker is unavailable"
command -v openssl >/dev/null 2>&1 || fail "openssl is unavailable"

Authentication__ApiKey="$(openssl rand -hex 32 2>/dev/null)" || fail "could not generate an ephemeral API key"
[[ "$Authentication__ApiKey" =~ ^[[:xdigit:]]{64}$ ]] || fail "generated API key had an unexpected format"
export Authentication__ApiKey

container_id=""
# shellcheck disable=SC2317,SC2329 # cleanup is invoked indirectly by the EXIT trap (0.9/0.11).
cleanup() {
  if [[ -n "$container_id" ]]; then
    if ! docker rm --force "$container_id" >/dev/null 2>&1; then
      echo "API provider image smoke cleanup failed; the test container was retained." >&2
      exit 1
    fi
  fi
}
trap cleanup EXIT

if ! created_id="$(docker create \
  --network none \
  --env ASPNETCORE_ENVIRONMENT=Production \
  --env Database__Provider=Sqlite \
  --env 'ConnectionStrings__Catalog=Data Source=/tmp/elsa-image-smoke.db' \
  --env DataProtection__KeysPath=/tmp/elsa-image-smoke-keys \
  --env Authentication__ApiKey \
  "$image" 2>/dev/null)"; then
  fail "container could not be created"
fi
[[ "$created_id" =~ ^[a-f0-9]{64}$ ]] || fail "container creation returned an invalid identifier"
container_id="$created_id"
unset Authentication__ApiKey

if ! docker start "$container_id" >/dev/null 2>&1; then
  fail "container failed to start"
fi

healthy=false
deadline=$((SECONDS + 60))
while ((SECONDS < deadline)); do
  state=""
  if ! state="$(docker inspect --format '{{.State.Status}}' "$container_id" 2>/dev/null)"; then
    fail "container state could not be inspected"
  fi

  case "$state" in
    running)
      response=""
      if response="$(docker exec "$container_id" curl --fail --silent --max-time 2 http://127.0.0.1:8080/health 2>/dev/null)" &&
        [[ "$response" =~ \"status\"[[:space:]]*:[[:space:]]*\"ok\" ]]; then
        healthy=true
        break
      fi
      ;;
    created|restarting)
      ;;
    exited|dead|removing)
      fail "container exited before /health became ready"
      ;;
    *)
      fail "container entered an unexpected state"
      ;;
  esac

  sleep 1
done

[[ "$healthy" == true ]] || fail "container did not return a healthy /health response within 60 seconds"

# Hosts stop the image by sending SIGTERM to its entrypoint process. The API must run its graceful shutdown and exit 0
# before the stop timeout, not be killed (137) or die from the signal (143).
docker stop --time 30 "$container_id" >/dev/null 2>&1 || fail "container could not be stopped"
exit_code=""
if ! exit_code="$(docker inspect --format '{{.State.ExitCode}}' "$container_id" 2>/dev/null)"; then
  fail "container exit code could not be inspected"
fi
[[ "$exit_code" == 0 ]] || fail "container did not exit 0 after SIGTERM"
# The log is only searched for the host's shutdown marker; container output is never emitted.
container_log=""
if ! container_log="$(docker logs "$container_id" 2>&1)"; then
  fail "container log could not be read"
fi
[[ "$container_log" == *"Application is shutting down..."* ]] || fail "container did not log the graceful shutdown sequence"
unset container_log

echo "API provider image smoke check passed."
