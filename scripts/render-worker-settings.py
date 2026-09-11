#!/usr/bin/env python3
"""Render the checked-in Control API worker composition into an App Service settings payload.

The template in infra/control-worker-composition names every setting the production API
needs to run the managed-instance lifecycle and Azure provider workers. Parameters hold
non-secret identifiers only and are the single source of values: there is no command-line
override, so a value reaches production only through a reviewed change to that file. The
rendered payload is the sole input to `az webapp config appsettings set --settings @<file>`;
this script never prints values.

Exit codes: 0 rendered, 2 the composition is not renderable (pending decision, unresolved
placeholder, unknown parameter, forbidden key, unsafe value, or half-enabled workers).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
COMPOSITION = ROOT / "infra" / "control-worker-composition"
WORKER_TEMPLATE = COMPOSITION / "worker-settings.template.json"
VERIFICATION_TEMPLATE = COMPOSITION / "release-verification.template.json"
PRODUCTION_PARAMETERS = COMPOSITION / "worker-settings.parameters.production.json"
ROLLBACK = COMPOSITION / "worker-rollback.json"

PLACEHOLDER = re.compile(r"\$\{([A-Za-z][A-Za-z0-9_]*)\}")
ALLOWED_KEY_PREFIXES = (
    "Deployment__ElsaInstanceLifecycle__",
    "Deployment__AzureProvider__",
    "RuntimeBuilder__InstancePlans__",
    "ControlPlane__",
    "ReleaseCatalog__Verification__",
    "ReleaseCatalog__Admission__",
)
# The API image owns these; an app setting would silently retarget the runner's tools.
IMAGE_OWNED_KEYS = frozenset({
    "Deployment__AzureProvider__Runner__AzureCliPath",
    "Deployment__AzureProvider__Runner__SqlCmdPath",
    "Deployment__AzureProvider__Runner__CurlPath",
    "Deployment__AzureProvider__Runner__TemplateRoot",
})
WORKER_ENABLE_KEYS = (
    "Deployment__ElsaInstanceLifecycle__Enabled",
    "Deployment__AzureProvider__WorkerEnabled",
    "Deployment__AzureProvider__InstanceLifecycle__Enabled",
)
SETTING_NAME = re.compile(r"^[A-Za-z][A-Za-z0-9_]{0,127}$")
# Values are identifiers, locators or short tokens; anything resembling a credential is refused.
SAFE_VALUE = re.compile(r"^[A-Za-z0-9._:/@#\-]{1,512}$")
GUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
# Parameters are identifiers, never secrets: one of these lowercase shapes, or the value is refused.
PARAMETER_SHAPES = (
    GUID,
    re.compile(r"^/subscriptions/[0-9a-f-]{36}(/resourcegroups/[a-z0-9._()-]+)?(/providers/[a-z0-9./_-]+)+$", re.IGNORECASE),
    re.compile(r"^https://[a-z0-9.-]+(/[a-z0-9._/-]*)?$"),
    # Keyless-signing workflow identity: an exact GitHub Actions workflow ref, never a wildcard.
    re.compile(r"^https://github\.com/[a-z0-9-]+/[a-z0-9._-]+/\.github/workflows/[a-z0-9._-]+\.ya?ml@refs/(heads|tags)/[A-Za-z0-9._/-]+$"),
    re.compile(r"^(25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1?[0-9]?[0-9])){3}$"),
    re.compile(r"^[a-z][a-z0-9-]{0,62}(\.[a-z0-9-]{1,63})*$"),
)


class CompositionError(Exception):
    """A value-free reason the composition cannot be rendered."""


def load_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise CompositionError(f"{path.name}: unreadable or not JSON") from error


def load_template(path: Path) -> dict[str, str]:
    template = load_json(path)
    if not isinstance(template, dict):
        raise CompositionError(f"{path.name}: template must be an object")
    settings = {key: value for key, value in template.items() if not key.startswith("$")}
    for key, value in settings.items():
        if not SETTING_NAME.match(key) or not key.startswith(ALLOWED_KEY_PREFIXES):
            raise CompositionError(f"{path.name}: setting outside the governed worker composition: {key}")
        if key.endswith("__Value") or key in IMAGE_OWNED_KEYS:
            raise CompositionError(f"{path.name}: forbidden setting: {key}")
        if not isinstance(value, str):
            raise CompositionError(f"{path.name}: setting {key} must be a string")
    return settings


def load_parameters(path: Path) -> tuple[dict[str, str], dict[str, str]]:
    """Return (resolved, pending) where pending maps a name to the issue it waits on."""
    document = load_json(path)
    parameters = document.get("parameters") if isinstance(document, dict) else None
    if not isinstance(parameters, dict):
        raise CompositionError(f"{path.name}: missing parameters object")
    resolved: dict[str, str] = {}
    pending: dict[str, str] = {}
    for name, value in parameters.items():
        if not PLACEHOLDER.fullmatch("${" + name + "}"):
            raise CompositionError(f"{path.name}: unsafe parameter name")
        if isinstance(value, dict):
            issue = value.get("pending")
            if not isinstance(issue, str) or not re.fullmatch(r"#[1-9][0-9]{0,5}", issue):
                raise CompositionError(f"{path.name}: parameter {name} must be a string or a pending issue reference")
            pending[name] = issue
        elif isinstance(value, str):
            if not SAFE_VALUE.match(value) or not any(shape.match(value) for shape in PARAMETER_SHAPES):
                raise CompositionError(f"{path.name}: parameter {name} is not an identifier shape")
            resolved[name] = value
        else:
            raise CompositionError(f"{path.name}: parameter {name} must be a string")
    return resolved, pending


def render(template: dict[str, str], parameters: dict[str, str], pending: dict[str, str],
           overrides: dict[str, str] | None = None) -> dict[str, str]:
    values = dict(parameters)
    values.update(overrides or {})
    rendered: dict[str, str] = {}
    blocked: dict[str, str] = {}
    for key, raw in template.items():
        def substitute(match: re.Match[str]) -> str:
            name = match.group(1)
            if name in values:
                return values[name]
            if name in pending:
                blocked[name] = pending[name]
                return ""
            raise CompositionError(f"unknown parameter {name} referenced by {key}")
        value = PLACEHOLDER.sub(substitute, raw)
        if not SAFE_VALUE.match(value) and value != "":
            raise CompositionError(f"rendered value for {key} has an unsafe shape")
        rendered[key] = value
    if blocked:
        waits = ", ".join(f"{name} ({issue})" for name, issue in sorted(blocked.items()))
        raise CompositionError(f"pending decisions block rendering: {waits}")
    enables = [rendered[key] for key in WORKER_ENABLE_KEYS if key in rendered]
    if enables and (len(enables) != len(WORKER_ENABLE_KEYS) or any(value != "true" for value in enables)):
        raise CompositionError("lifecycle, provider and instance-provider workers must be enabled together")
    if rendered.get("Deployment__AzureProvider__Runner__DisposableProofMode", "false") != "false":
        raise CompositionError("production composition must not use disposable proof mode")
    for key, value in rendered.items():
        if (key.endswith("ClientId") or key.endswith("ObjectId") or key.endswith("SubscriptionId") or key.endswith("TenantId")) and not GUID.match(value):
            raise CompositionError(f"{key} must be a canonical lowercase GUID")
    return rendered


def to_app_settings(rendered: dict[str, str]) -> list[dict[str, object]]:
    return [{"name": key, "value": value, "slotSetting": False} for key, value in rendered.items()]


def write_payload(payload: list[dict[str, object]], output: Path) -> str:
    text = json.dumps(payload, indent=2) + "\n"
    # Create private before writing so no wider mode is ever observable.
    descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as handle:
        handle.write(text)
    output.chmod(0o600)
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def load_rollback(path: Path = ROLLBACK) -> list[dict[str, object]]:
    payload = load_json(path)
    if not isinstance(payload, list) or {entry.get("name") for entry in payload} != set(WORKER_ENABLE_KEYS):
        raise CompositionError("rollback must disable exactly the three worker switches")
    for entry in payload:
        if entry.get("value") != "false" or entry.get("slotSetting") is not False:
            raise CompositionError("rollback entries must set the switches to false")
    return payload


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("workers", "release-verification"):
        command = sub.add_parser(name, help=f"render the {name} settings payload from the checked-in parameters")
        command.add_argument("--output", type=Path, required=True)
    rollback = sub.add_parser("rollback", help="write the workers-off payload")
    rollback.add_argument("--output", type=Path, required=True)
    sub.add_parser("status", help="list resolved and pending parameters without values")
    args = parser.parse_args(argv)
    try:
        if args.command == "rollback":
            digest = write_payload(load_rollback(), args.output)
            print(f"rendered {len(WORKER_ENABLE_KEYS)} settings to {args.output} sha256={digest}")
            return 0
        resolved, pending = load_parameters(PRODUCTION_PARAMETERS)
        if args.command == "status":
            for name in sorted(resolved):
                print(f"resolved {name}")
            for name, issue in sorted(pending.items()):
                print(f"pending  {name} -> {issue}")
            return 0
        template = load_template(WORKER_TEMPLATE if args.command == "workers" else VERIFICATION_TEMPLATE)
        rendered = render(template, resolved, pending)
        digest = write_payload(to_app_settings(rendered), args.output)
        for key in rendered:
            print(f"setting {key}")
        print(f"rendered {len(rendered)} settings to {args.output} sha256={digest}")
        return 0
    except CompositionError as error:
        print(f"composition not renderable: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
