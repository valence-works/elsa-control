#!/usr/bin/env python3
"""Render one rehearsal phase's ACI container-group JSON without printing secure values.

Every environment-specific identity (subscription, resource groups, SQL server, clone database, managed
identities, registry, probe image, candidate and previous API images with their baked source ids and
build numbers) is an explicit input; nothing is inferred from a registry or a default subscription.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import stat
from datetime import datetime, timezone
from pathlib import Path

DIGEST_IMAGE = re.compile(r"^[a-z0-9.-]+\.azurecr\.io/[a-z0-9._/-]+@sha256:[0-9a-f]{64}$")
SOURCE_RE = re.compile(r"^[0-9a-f]{40}$")
BUILD_RE = re.compile(r"^[1-9][0-9]{0,19}$")
NAME_RE = re.compile(r"^[a-z][a-z0-9-]{2,62}[a-z0-9]$")
GUID_RE = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
RESOURCE_NAME_RE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._()-]{0,89}$")
HOST_RE = re.compile(r"^[a-z0-9][a-z0-9.-]{2,253}$")
DATE_RE = re.compile(r"^20[0-9]{2}-[0-9]{2}-[0-9]{2}$")
MIGRATION_RE = re.compile(r"^[0-9]{14}_[A-Za-z0-9_]+$")


def fail() -> "NoReturn":
    raise SystemExit(2)


def required(name: str, pattern: re.Pattern[str] | None = None, lower: bool = False) -> str:
    value = os.environ.get(name, "")
    if not value or value == "REQUIRED":
        fail()
    if lower:
        value = value.lower()
    if pattern is not None and not pattern.fullmatch(value):
        fail()
    return value


def env(name: str, value: str, secure: bool = False) -> dict[str, object]:
    return {"name": name, "secureValue": value} if secure else {"name": name, "value": value}


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--output", required=True)
    parser.add_argument("--probe-script", required=True)
    arguments = parser.parse_args()

    phase = required("REHEARSAL_PHASE")
    if phase not in ("candidate", "previous"):
        fail()
    subscription = required("AZURE_SUBSCRIPTION_ID", GUID_RE, lower=True)
    resource_group = required("RESOURCE_GROUP", RESOURCE_NAME_RE)
    authority_group = required("AUTHORITY_RESOURCE_GROUP", RESOURCE_NAME_RE)
    server = required("CATALOG_SERVER", HOST_RE)
    database = required("CATALOG_DATABASE", RESOURCE_NAME_RE)
    api_identity_name = required("API_IDENTITY_NAME", RESOURCE_NAME_RE)
    api_identity_client = required("CATALOG_MI_CLIENT_ID", GUID_RE, lower=True)
    acr_identity_name = required("ACR_PULL_IDENTITY_NAME", RESOURCE_NAME_RE)
    probe_image = required("PROBE_IMAGE", DIGEST_IMAGE)
    location = required("AZURE_LOCATION", re.compile(r"^[a-z]{3,32}$"))
    baseline_migrations = required("EXPECTED_BASELINE_MIGRATIONS", re.compile(r"^[1-9][0-9]{0,3}$"))
    target_migrations = required("EXPECTED_MIGRATIONS", re.compile(r"^[1-9][0-9]{0,3}$"))
    baseline_preview = required("EXPECTED_BASELINE_PREVIEW_COLUMNS", re.compile(r"^[0-9]{1,2}$"))
    target_preview = required("EXPECTED_PREVIEW_COLUMNS", re.compile(r"^[0-9]{1,2}$"))
    prefix = "CANDIDATE" if phase == "candidate" else "PREVIOUS"
    image = required(f"{prefix}_IMAGE", DIGEST_IMAGE)
    source = required(f"{prefix}_SOURCE_ID", SOURCE_RE, lower=True)
    build = required(f"{prefix}_BUILD_NUMBER", BUILD_RE)
    registry = image.split("/", 1)[0]
    if not probe_image.startswith(registry + "/"):
        fail()
    group_name = required("REHEARSAL_GROUP_NAME", NAME_RE)
    phase_prefix = f"catalog-rehearsal-{phase}"
    if not (group_name == phase_prefix or group_name.startswith(f"{phase_prefix}-")):
        fail()
    expiry = required("REHEARSAL_EXPIRY_UTC", DATE_RE)
    if datetime.strptime(expiry, "%Y-%m-%d").date() <= datetime.now(timezone.utc).date():
        fail()
    probe_script = Path(arguments.probe_script).read_text(encoding="utf-8")
    if not probe_script.startswith("#!/usr/bin/env python3") or len(probe_script) > 64 * 1024:
        fail()
    expected_ids = Path(required("EXPECTED_MIGRATION_IDS_FILE")).read_text(encoding="utf-8").split()
    if len(expected_ids) != int(target_migrations) or any(not MIGRATION_RE.fullmatch(item) for item in expected_ids) or len(set(expected_ids)) != len(expected_ids):
        fail()

    identity_prefix = f"/subscriptions/{subscription}/resourceGroups/{authority_group}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/"
    api_identity = identity_prefix + api_identity_name
    acr_identity = identity_prefix + acr_identity_name
    # The canonical clone connection string is constructed here (Entra managed identity, encryption enabled,
    # no password-bearing aliases); no connection material is accepted from the environment.
    connection = (
        f"Server=tcp:{server},1433;Initial Catalog={database};Authentication=Active Directory Managed Identity;"
        f"User Id={api_identity_client};Encrypt=True;TrustServerCertificate=False"
    )
    probe_environment = [
        env("REHEARSAL_PHASE", phase),
        env("REHEARSAL_GROUP_NAME", group_name),
        env("CATALOG_SERVER", server),
        env("CATALOG_DATABASE", database),
        env("CATALOG_MI_CLIENT_ID", api_identity_client),
        env("CATALOG_MI_PRINCIPAL_NAME", api_identity_name),
        env("EXPECTED_IMAGE_ID", source),
        env("EXPECTED_BUILD_NUMBER", build),
        env("EXPECTED_BASELINE_MIGRATIONS", baseline_migrations),
        env("EXPECTED_BASELINE_PREVIEW_COLUMNS", baseline_preview),
        env("EXPECTED_MIGRATIONS", target_migrations),
        env("EXPECTED_PREVIEW_COLUMNS", target_preview),
        env("EXPECTED_MIGRATION_IDS", " ".join(sorted(expected_ids))),
        env("API_START_TIMEOUT_SECONDS", "900"),
        env("HEALTH_SAMPLE_SECONDS", "2"),
        env("SQLCMD_PATH", "/usr/local/bin/sqlcmd"),
        env("CURL_PATH", "/usr/bin/curl"),
        env("REHEARSAL_SCRIPT", probe_script),
        env("REHEARSAL_SCRIPT_SHA256", hashlib.sha256(probe_script.encode("utf-8")).hexdigest()),
    ]
    api_environment = [
        env("ASPNETCORE_ENVIRONMENT", "Production"),
        env("ASPNETCORE_URLS", "http://127.0.0.1:8080"),
        env("ConnectionStrings__Catalog", connection, secure=True),
        env("Database__Provider", "SqlServer"),
        env("DataProtection__KeysPath", "/rehearsal/data-protection-keys"),
        env("Application__BuildNumber", build),
        env("Authentication__ControlIdentity__Provider", "GenericOidc"),
        env("Authentication__ControlIdentity__RequireHttpsMetadata", "true"),
        env("Authentication__WorkspaceTrustedHeaders__Enabled", "false"),
        env("ManagedElsa__Handoff__Enabled", "false"),
        env("Billing__Lifecycle__Enabled", "false"),
        env("Billing__Stripe__Enabled", "false"),
        env("Sync__Scheduled__Enabled", "false"),
        env("Deployment__QueueWorker__Enabled", "false"),
        env("Deployment__WebhookDispatch__Enabled", "false"),
        env("Deployment__ElsaInstanceLifecycle__Enabled", "false"),
        env("Deployment__EngineVerification__Enabled", "false"),
        env("Deployment__AzureProvider__WorkerEnabled", "false"),
        env("Deployment__AzureProvider__InstanceLifecycle__Enabled", "false"),
        env("Deployment__AzureProvider__Runner__Enabled", "false"),
        env("Weaver__Enabled", "false"),
        env("Weaver__ProviderMode", "Disabled"),
        env("Weaver__Telemetry__Enabled", "false"),
        env("ReleaseCatalog__Verification__Enabled", "false"),
        env("OTEL_SDK_DISABLED", "true"),
        env("ManagedLifecycleTelemetry__AzureMonitor__Enabled", "false"),
    ]
    # The API waits for the probe's start barrier, runs the exact image entrypoint, and records its exit code so a
    # crashed API never leaves the probe waiting out its full health timeout. API output is discarded.
    api_command = [
        "/bin/bash",
        "-c",
        "deadline=$(( $(date +%s) + 900 )); "
        "while [ ! -e /rehearsal/start-api ] && [ ! -e /rehearsal/stop-api ] && [ $(date +%s) -lt $deadline ]; do sleep 1; done; "
        "[ -e /rehearsal/start-api ] || exit 124; "
        "cd /app && dotnet /app/ElsaControl.Api.dll >/dev/null 2>&1 & api_pid=$!; "
        "stop_api() { if kill -0 $api_pid 2>/dev/null; then kill -TERM $api_pid 2>/dev/null || true; "
        "for n in $(seq 1 15); do kill -0 $api_pid 2>/dev/null || return 0; sleep 1; done; "
        "kill -KILL $api_pid 2>/dev/null || true; fi; }; "
        "trap 'stop_api; exit 143' TERM INT; "
        "while kill -0 $api_pid 2>/dev/null; do "
        "if [ -e /rehearsal/stop-api ]; then stop_api; break; fi; "
        "if [ $(date +%s) -ge $deadline ]; then stop_api; wait $api_pid 2>/dev/null || true; exit 124; fi; sleep 1; done; "
        "wait $api_pid; api_exit=$?; printf '%s' $api_exit > /rehearsal/api-exited; exit $api_exit",
    ]
    probe_command = ["/bin/bash", "-c", "printf '%s' \"$REHEARSAL_SCRIPT\" | python3"]
    spec = {
        "location": location,
        "tags": {"owner": "elsa-control", "purpose": "catalog-migration-rehearsal", "phase": phase, "expires": expiry},
        "identity": {"type": "UserAssigned", "userAssignedIdentities": {api_identity: {}, acr_identity: {}}},
        "properties": {
            "osType": "Linux",
            "restartPolicy": "Never",
            "volumes": [{"name": "rehearsal", "emptyDir": {}}],
            "containers": [
                {
                    "name": "api",
                    "properties": {
                        "image": image,
                        "command": api_command,
                        "resources": {"requests": {"cpu": 1, "memoryInGB": 2}},
                        "environmentVariables": api_environment,
                        "volumeMounts": [{"name": "rehearsal", "mountPath": "/rehearsal"}],
                    },
                },
                {
                    "name": "probe",
                    "properties": {
                        "image": probe_image,
                        "command": probe_command,
                        "resources": {"requests": {"cpu": 1, "memoryInGB": 1}},
                        "environmentVariables": probe_environment,
                        "volumeMounts": [{"name": "rehearsal", "mountPath": "/rehearsal"}],
                    },
                },
            ],
            "imageRegistryCredentials": [{"server": registry, "identity": acr_identity}],
        },
    }
    output = Path(arguments.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    flags = os.O_WRONLY | os.O_CREAT | os.O_TRUNC | getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(output, flags, stat.S_IRUSR | stat.S_IWUSR)
    os.fchmod(fd, stat.S_IRUSR | stat.S_IWUSR)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        json.dump(spec, stream, sort_keys=True, separators=(",", ":"))
        stream.write("\n")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SystemExit:
        raise
    except Exception:
        # Deliberately value-free failure; callers emit a stable code.
        raise SystemExit(2)
