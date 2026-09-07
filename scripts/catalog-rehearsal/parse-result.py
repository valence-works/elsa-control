#!/usr/bin/env python3
"""Accept exactly one safe probe result line and discard everything else.

Modes:
  parse-result.py <log-file> <phase>                         validate the single JSON line and echo it
  parse-result.py --failure <path> <phase> <code> <group>    write and echo a failed placeholder result
  parse-result.py --outcome <path> <state> <api> <probe>     exit 0 only for passed/ok with a clean container outcome
  parse-result.py --is-failed <path>                         exit 0 when the result is a failed result
"""

from __future__ import annotations

import json
import os
import re
import sys

SCHEMA = "elsa-control.catalog-rehearsal/v1"
SAFE_ID = re.compile(r"^[A-Za-z0-9._+:-]{1,128}$")
MIGRATION = re.compile(r"^[0-9]{14}_[A-Za-z0-9_]+$")
COUNTS = ("Accounts", "Organizations", "Workspaces", "ElsaInstances", "DeploymentRuns", "AzureProviderOperations")
COUNT_FIELDS = (
    "previewColumnCount", "baselinePreviewColumnCount", "duplicateTargetGroupCount", "duplicateOperationGroupCount",
    "billingProviderEventNullStateCount", "foreignKeyIntegrityViolationCount", "checkConstraintIntegrityViolationCount",
    "orphanProviderAssignmentCount", "orphanOperationTransitionCount",
)
BOOL_FIELDS = (
    "commonCountsEqual", "permissionChecks", "principalChecks", "integrityChecks", "indexChecks",
    "recoveryObservationColumnsValid", "recoveryObservationForeignKeysValid", "recoveryObservationNaturalKeyIndexValid",
    "recoveryObservationAppendOnlyTriggerValid", "recoveryRequestColumnsValid", "attemptedStepColumnValid",
    "billingProviderEventsPresent", "providerAssignmentSchemaPresent",
)
SAFE_FIELDS = frozenset(
    ("schema", "phase", "result", "code", "healthChecks", "migrationIds", "baselineMigrationIds", "baselineCounts",
     "postCounts", "bakedImageId", "buildNumber", "rehearsalGroupName", *COUNT_FIELDS, *BOOL_FIELDS)
)


def invalid() -> "NoReturn":
    raise SystemExit(2)


def group_name_valid(name: object, phase: str) -> bool:
    prefix = f"catalog-rehearsal-{phase}"
    return isinstance(name, str) and bool(SAFE_ID.fullmatch(name)) and (name == prefix or name.startswith(prefix + "-"))


def validate(payload: object, expected_phase: str) -> dict[str, object]:
    if not isinstance(payload, dict) or set(payload) != SAFE_FIELDS:
        invalid()
    if payload["schema"] != SCHEMA or payload["phase"] != expected_phase or payload["result"] not in ("passed", "failed"):
        invalid()
    if not isinstance(payload["code"], str) or not SAFE_ID.fullmatch(payload["code"]):
        invalid()
    if type(payload["healthChecks"]) is not int or not 0 <= payload["healthChecks"] <= 2:
        invalid()
    for key in ("migrationIds", "baselineMigrationIds"):
        values = payload[key]
        if not isinstance(values, list) or not all(isinstance(item, str) and MIGRATION.fullmatch(item) for item in values):
            invalid()
        if len(values) != len(set(values)):
            invalid()
    for key in ("baselineCounts", "postCounts"):
        counts = payload[key]
        if not isinstance(counts, dict) or set(counts) - set(COUNTS):
            invalid()
        # A failed preflight can have no snapshot; a passed result must carry both complete snapshots.
        if (payload["result"] == "passed" or counts) and set(counts) != set(COUNTS):
            invalid()
        if not all(type(value) is int and value >= 0 for value in counts.values()):
            invalid()
    if any(type(payload[key]) is not int or payload[key] < 0 for key in COUNT_FIELDS):
        invalid()
    if any(not isinstance(payload[key], bool) for key in BOOL_FIELDS):
        invalid()
    if any(not isinstance(payload[key], str) or not SAFE_ID.fullmatch(payload[key]) for key in ("bakedImageId", "buildNumber")):
        invalid()
    if not group_name_valid(payload["rehearsalGroupName"], expected_phase):
        invalid()
    return payload


def failure_payload(phase: str, code: str, group_name: str) -> dict[str, object]:
    safe_phase = phase if phase in ("candidate", "previous") else "unknown"
    return {
        "schema": SCHEMA, "phase": safe_phase, "result": "failed", "code": code if SAFE_ID.fullmatch(code) else "unknown",
        "healthChecks": 0, "migrationIds": [], "baselineMigrationIds": [],
        **{key: 0 for key in COUNT_FIELDS}, **{key: False for key in BOOL_FIELDS},
        "baselineCounts": {name: 0 for name in COUNTS}, "postCounts": {name: 0 for name in COUNTS},
        "bakedImageId": "unknown", "buildNumber": "unknown",
        "rehearsalGroupName": group_name if group_name_valid(group_name, safe_phase) else "unknown",
    }


def dump(payload: dict[str, object]) -> str:
    return json.dumps(payload, sort_keys=True, separators=(",", ":"))


def load(path: str) -> dict[str, object]:
    with open(path, encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        invalid()
    return value


def main(argv: list[str]) -> int:
    if len(argv) == 5 and argv[0] == "--failure":
        _, path, phase, code, group_name = argv
        payload = failure_payload(phase, code, group_name)
        try:
            os.makedirs(os.path.dirname(os.path.abspath(path)), mode=0o700, exist_ok=True)
            with open(path, "w", encoding="utf-8") as stream:
                stream.write(dump(payload) + "\n")
        except OSError:
            pass
        print(dump(payload))
        return 0
    if len(argv) == 5 and argv[0] == "--outcome":
        _, path, group_state, api_exit, probe_exit = argv
        value = load(path)
        passed = value.get("result") == "passed" and value.get("code") == "ok"
        return 0 if passed and group_state == "Succeeded" and api_exit == "0" and probe_exit == "0" else 1
    if len(argv) == 2 and argv[0] == "--is-failed":
        return 0 if load(argv[1]).get("result") == "failed" else 1
    if len(argv) != 2 or argv[1] not in ("candidate", "previous"):
        invalid()
    try:
        with open(argv[0], encoding="utf-8") as stream:
            lines = [line.strip() for line in stream if line.strip()]
    except (OSError, UnicodeError):
        invalid()
    if len(lines) != 1:
        invalid()
    try:
        payload = json.loads(lines[0])
    except (json.JSONDecodeError, TypeError):
        invalid()
    print(dump(validate(payload, argv[1])))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except SystemExit:
        raise
    except Exception:
        raise SystemExit(2)
