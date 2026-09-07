#!/usr/bin/env python3
"""Compatibility gate over the candidate and previous safe results.

Usage: compare-results.py <candidate-result.json> <previous-result.json>
Reads the expected identities from the same environment the renderer used (CANDIDATE_SOURCE_ID,
CANDIDATE_BUILD_NUMBER, PREVIOUS_SOURCE_ID, PREVIOUS_BUILD_NUMBER, EXPECTED_BASELINE_MIGRATIONS,
EXPECTED_MIGRATIONS, EXPECTED_BASELINE_PREVIEW_COLUMNS, EXPECTED_PREVIEW_COLUMNS, EXPECTED_MIGRATION_IDS_FILE)
and prints exactly one stable code. Exit 0 only for CATALOG_REHEARSAL_PASSED.
"""

from __future__ import annotations

import json
import os
import re
import sys

SCHEMA = "elsa-control.catalog-rehearsal/v1"
SAFE_ID = re.compile(r"^[A-Za-z0-9._+:-]{1,128}$")
MIGRATION = re.compile(r"^[0-9]{14}_[A-Za-z0-9_]+$")
CONTRACT_FLAGS = (
    "integrityChecks", "indexChecks", "permissionChecks", "principalChecks", "commonCountsEqual",
    "providerAssignmentSchemaPresent", "recoveryObservationColumnsValid", "recoveryObservationForeignKeysValid",
    "recoveryObservationNaturalKeyIndexValid", "recoveryObservationAppendOnlyTriggerValid",
    "recoveryRequestColumnsValid", "attemptedStepColumnValid",
)


def stop(code: str) -> "NoReturn":
    print(code)
    raise SystemExit(0 if code == "CATALOG_REHEARSAL_PASSED" else 1)


def required(name: str, pattern: str) -> str:
    value = os.environ.get(name, "")
    if not re.fullmatch(pattern, value):
        stop("INPUT_INVALID")
    return value


def load(path: str, phase: str, source: str, build: str) -> dict[str, object]:
    try:
        with open(path, encoding="utf-8") as stream:
            value = json.load(stream)
    except (OSError, ValueError):
        stop("RESULT_UNREADABLE")
    if not isinstance(value, dict) or value.get("schema") != SCHEMA:
        stop("RESULT_INVALID")
    if value.get("phase") != phase or value.get("result") != "passed" or value.get("code") != "ok":
        stop("RESULT_NOT_PASSED")
    if value.get("bakedImageId") != source or value.get("buildNumber") != build or value.get("healthChecks") != 2:
        stop("RESULT_IMAGE_BINDING")
    group_name = value.get("rehearsalGroupName")
    prefix = f"catalog-rehearsal-{phase}"
    if not isinstance(group_name, str) or not SAFE_ID.fullmatch(group_name) or not (group_name == prefix or group_name.startswith(prefix + "-")):
        stop("RESULT_GROUP_BINDING")
    if not all(value.get(flag) is True for flag in CONTRACT_FLAGS):
        stop("RESULT_CONTRACT_INCOMPLETE")
    return value


def main(argv: list[str]) -> None:
    if len(argv) != 2:
        stop("USAGE")
    candidate_source = required("CANDIDATE_SOURCE_ID", r"[0-9a-fA-F]{40}").lower()
    candidate_build = required("CANDIDATE_BUILD_NUMBER", r"[1-9][0-9]{0,19}")
    previous_source = required("PREVIOUS_SOURCE_ID", r"[0-9a-fA-F]{40}").lower()
    previous_build = required("PREVIOUS_BUILD_NUMBER", r"[1-9][0-9]{0,19}")
    baseline = int(required("EXPECTED_BASELINE_MIGRATIONS", r"[1-9][0-9]{0,3}"))
    target = int(required("EXPECTED_MIGRATIONS", r"[1-9][0-9]{0,3}"))
    baseline_preview = int(required("EXPECTED_BASELINE_PREVIEW_COLUMNS", r"[0-9]{1,2}"))
    target_preview = int(required("EXPECTED_PREVIEW_COLUMNS", r"[0-9]{1,2}"))
    try:
        expected_ids = sorted(open(required("EXPECTED_MIGRATION_IDS_FILE", r".{1,4096}"), encoding="utf-8").read().split())
    except OSError:
        stop("INPUT_INVALID")
    if len(expected_ids) != target or any(not MIGRATION.fullmatch(item) for item in expected_ids):
        stop("INPUT_INVALID")
    if candidate_source == previous_source:
        stop("INPUT_INVALID")

    candidate = load(argv[0], "candidate", candidate_source, candidate_build)
    previous = load(argv[1], "previous", previous_source, previous_build)
    if candidate["rehearsalGroupName"] == previous["rehearsalGroupName"]:
        stop("RESULT_GROUP_REUSED")
    if candidate["baselineMigrationIds"] != expected_ids[:baseline] or candidate["migrationIds"] != expected_ids:
        stop("CANDIDATE_MIGRATIONS_MISMATCH")
    if candidate["baselinePreviewColumnCount"] != baseline_preview or candidate["previewColumnCount"] != target_preview:
        stop("CANDIDATE_PREVIEW_COLUMNS_MISMATCH")
    if previous["baselineMigrationIds"] != expected_ids or previous["migrationIds"] != expected_ids:
        stop("PREVIOUS_MIGRATIONS_MISMATCH")
    if previous["baselinePreviewColumnCount"] != target_preview or previous["previewColumnCount"] != target_preview:
        stop("PREVIOUS_PREVIEW_COLUMNS_MISMATCH")
    if candidate["postCounts"] != previous["postCounts"] or candidate["postCounts"] != candidate["baselineCounts"]:
        stop("COMMON_COUNTS_DIFFER")
    if any(value.get("billingProviderEventsPresent") and value.get("billingProviderEventNullStateCount") for value in (candidate, previous)):
        stop("BILLING_EVENT_STATE_NULL")
    stop("CATALOG_REHEARSAL_PASSED")


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except SystemExit:
        raise
    except Exception:
        stop("COMPARE_FAILED")
