#!/usr/bin/env python3
"""Fail-closed exclusive-window checks for the staging compatibility fixture."""

from __future__ import annotations

import argparse
import json
import re
import sys
from typing import Any, Iterable, Mapping


FREEZE_MARKER = re.compile(r"^staging-freeze:\s+(?P<state>on|off)\b", re.IGNORECASE)
QA_WINDOW_MARKER = re.compile(r"^qa-window:\s+(?P<state>open|closed)\b", re.IGNORECASE)
PROVE_NAME = re.compile(r"prove", re.IGNORECASE)
FIXTURE_WORKFLOW = "staging-compat-fixture.yml"
TRUSTED_ASSOCIATIONS = frozenset({"OWNER", "MEMBER"})
TRUSTED_LOGINS = frozenset({"sfmskywalker"})

# Fixture-on cap stays 20 minutes from the app-setting write to confirmed
# deletion. Restore reserves a short delete budget inside that cap. The longer
# health witness runs after deletion and must not push deletion later.
FIXTURE_CAP_SECONDS = 20 * 60
RESTORE_BUDGET_SECONDS = 4 * 60
RESTORE_HEALTH_ATTEMPTS = 36
RESTORE_HEALTH_RETRY_SECONDS = 15
RESTORE_HEALTH_CURL_MAX_TIME = 10
RESTORE_HEALTH_BUDGET_SECONDS = 10 * 60
RESTORE_STEP_TIMEOUT_SECONDS = 16 * 60
ARM_STEP_TIMEOUT_SECONDS = 12 * 60
ARM_HEALTH_BUDGET_SECONDS = 5 * 60
IN_HOLD_K_PROBES_TIMEOUT_SECONDS = 2 * 60
IN_HOLD_SCREENS_TIMEOUT_SECONDS = 5 * 60
IN_HOLD_BUDGET_SECONDS = IN_HOLD_K_PROBES_TIMEOUT_SECONDS + IN_HOLD_SCREENS_TIMEOUT_SECONDS
POST_RESTORE_SCREENS_TIMEOUT_SECONDS = 5 * 60
UPLOAD_ARTIFACT_TIMEOUT_SECONDS = 2 * 60
ARTIFACT_RETENTION_DAYS = 7
PLAYWRIGHT_SETUP_TIMEOUT_SECONDS = 6 * 60
MAX_HOLD_SECONDS = 15 * 60
# Backstop above fixture-on + post-delete health + in-hold + post-restore.
JOB_BACKSTOP_SECONDS = 60 * 60
UUID_PATTERN = re.compile(
    r"^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
    re.IGNORECASE,
)
K_PROBE_INTENT_REQUIRED = (
    "release.distributionId",
    "release.releaseLine",
    "release.channel",
    "release.patchUpdates",
    "release.minorUpdates",
    "release.majorMigrations",
    "application.topologyId",
    "placement.targetMode",
    "placement.regionCode",
    "placement.isolationProfile",
    "placement.capacityProfile",
    "desiredLifecycle",
)


def first_nonempty_line(body: str) -> str:
    for line in body.splitlines():
        stripped = line.strip()
        if stripped:
            return stripped
    return ""


def commenter_is_trusted(comment: Mapping[str, Any]) -> bool:
    association = str(comment.get("author_association") or "").strip().upper()
    if association in TRUSTED_ASSOCIATIONS:
        return True
    user = comment.get("user")
    login = ""
    if isinstance(user, Mapping):
        login = str(user.get("login") or "").strip().lower()
    elif isinstance(comment.get("login"), str):
        login = comment["login"].strip().lower()
    return login in TRUSTED_LOGINS


def latest_marker_state(comments: Iterable[Mapping[str, Any]], pattern: re.Pattern[str]) -> str | None:
    state: str | None = None
    for comment in comments:
        if not commenter_is_trusted(comment):
            continue
        body = comment.get("body")
        if not isinstance(body, str):
            continue
        match = pattern.match(first_nonempty_line(body))
        if match is not None:
            state = match.group("state").lower()
    return state


def freeze_is_on(comments: Iterable[Mapping[str, Any]]) -> bool:
    return latest_marker_state(comments, FREEZE_MARKER) == "on"


def qa_window_is_open(issue_state: str, comments: Iterable[Mapping[str, Any]]) -> bool:
    if issue_state == "open":
        return True
    return latest_marker_state(comments, QA_WINDOW_MARKER) == "open"


def compute_hold_seconds(
    elapsed_since_arm: int,
    fixture_cap: int = FIXTURE_CAP_SECONDS,
    restore_budget: int = RESTORE_BUDGET_SECONDS,
    max_hold: int = MAX_HOLD_SECONDS,
) -> int:
    remaining = int(fixture_cap) - int(elapsed_since_arm) - int(restore_budget)
    return max(0, min(int(max_hold), remaining))


def remaining_before_restore(
    elapsed_since_arm: int,
    fixture_cap: int = FIXTURE_CAP_SECONDS,
    restore_budget: int = RESTORE_BUDGET_SECONDS,
) -> int:
    return max(0, int(fixture_cap) - int(elapsed_since_arm) - int(restore_budget))


def skip_in_hold(
    elapsed_since_arm: int,
    needed_seconds: int,
    fixture_cap: int = FIXTURE_CAP_SECONDS,
    restore_budget: int = RESTORE_BUDGET_SECONDS,
) -> bool:
    return remaining_before_restore(elapsed_since_arm, fixture_cap, restore_budget) < int(needed_seconds)


def _nested_get(payload: Mapping[str, Any], dotted: str) -> Any:
    current: Any = payload
    for part in dotted.split("."):
        if not isinstance(current, Mapping) or part not in current:
            return None
        current = current[part]
    return current


def validate_bff_action_payload(payload: Any) -> str | None:
    """Return an error if the payload would fail the real Cloud BFF schema."""
    if not isinstance(payload, Mapping):
        return "BFF payload must be a JSON object."
    action = payload.get("action")
    if action == "compatibility":
        return None
    if action == "bootstrap":
        return None
    if action == "listOrganizations":
        extra = set(payload) - {"action"}
        if extra:
            return "listOrganizations accepts only the action field."
        return None
    if action == "createInstanceDeleteConfirmation":
        for field in ("organizationId", "workspaceId", "instanceId"):
            value = payload.get(field)
            if not isinstance(value, str) or not UUID_PATTERN.fullmatch(value):
                return f"{field} must be a UUID."
        return None
    if action == "updateInstance":
        for field in ("organizationId", "workspaceId", "instanceId", "idempotencyKey"):
            value = payload.get(field)
            if not isinstance(value, str) or not UUID_PATTERN.fullmatch(value):
                return f"{field} must be a UUID."
        version = payload.get("version")
        if isinstance(version, bool) or not isinstance(version, int) or version < 1:
            return "version must be a positive integer."
        intent = payload.get("intent")
        if not isinstance(intent, Mapping):
            return "intent must be an object."
        for path in K_PROBE_INTENT_REQUIRED:
            value = _nested_get(intent, path)
            if not isinstance(value, str) or not value.strip():
                return f"intent.{path} is required."
        return None
    if action in {None, ""}:
        return "action is required."
    return None


def conflicting_runs(
    runs: Iterable[Mapping[str, Any]],
    this_run_id: str | None,
) -> list[str]:
    conflicts: list[str] = []
    this_id = str(this_run_id or "").strip()
    for run in runs:
        run_id = str(run.get("id") or "").strip()
        if not run_id or run_id == this_id:
            continue
        environment = str(run.get("environment") or "").strip().lower()
        name = str(run.get("name") or "")
        path = str(run.get("path") or "").replace("\\", "/")
        status = str(run.get("status") or "").strip().lower()
        if status not in {"in_progress", "queued", "waiting", "pending", "requested"}:
            continue
        # environment=test is set only for runs mapped from the Deployments API.
        # Workflow runs have no environment field; never infer it from list-runs.
        if environment == "test":
            conflicts.append(run_id)
            continue
        if path.endswith(FIXTURE_WORKFLOW) or path.endswith("/" + FIXTURE_WORKFLOW):
            conflicts.append(run_id)
            continue
        if PROVE_NAME.search(name) or PROVE_NAME.search(path):
            conflicts.append(run_id)
    return conflicts


def load_json(path: str) -> Any:
    if path == "-":
        return json.load(sys.stdin)
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    freeze = sub.add_parser("freeze")
    freeze.add_argument("--comments", required=True)

    window = sub.add_parser("qa-window")
    window.add_argument("--issue-state", required=True)
    window.add_argument("--comments", required=True)
    window.add_argument("--issue", required=True)

    runs = sub.add_parser("runs")
    runs.add_argument("--runs", required=True)
    runs.add_argument("--this-run-id", default="")

    hold = sub.add_parser("hold")
    hold.add_argument("--elapsed-since-arm", required=True, type=int)

    skip = sub.add_parser("skip-in-hold")
    skip.add_argument("--elapsed-since-arm", required=True, type=int)
    skip.add_argument("--needed", required=True, type=int)

    validate = sub.add_parser("validate-bff")
    validate.add_argument("--payload", required=True)

    args = parser.parse_args(argv)
    if args.command == "freeze":
        comments = load_json(args.comments)
        if not isinstance(comments, list):
            print("::error::Staging freeze comments could not be read.", file=sys.stderr)
            return 1
        if freeze_is_on(comments):
            print("Staging freeze marker is on.")
            return 0
        print("::error::Staging is not exclusive; #508 has no current staging-freeze: on marker.", file=sys.stderr)
        return 1
    if args.command == "qa-window":
        comments = load_json(args.comments)
        if not isinstance(comments, list):
            print("::error::QA window comments could not be read.", file=sys.stderr)
            return 1
        if qa_window_is_open(args.issue_state, comments):
            print(f"::error::A QA window for #{args.issue} is still open.", file=sys.stderr)
            return 1
        print(f"QA window #{args.issue} is closed.")
        return 0
    if args.command == "runs":
        payload = load_json(args.runs)
        if not isinstance(payload, list):
            print("::error::Workflow run list could not be read.", file=sys.stderr)
            return 1
        conflicts = conflicting_runs(payload, args.this_run_id)
        if conflicts:
            print(
                "::error::Staging is not exclusive; another Deploy staging or Prove run is in progress.",
                file=sys.stderr,
            )
            return 1
        print("No conflicting Deploy staging or Prove runs.")
        return 0
    if args.command == "hold":
        print(compute_hold_seconds(args.elapsed_since_arm))
        return 0
    if args.command == "skip-in-hold":
        print("skip" if skip_in_hold(args.elapsed_since_arm, args.needed) else "run")
        return 0
    if args.command == "validate-bff":
        try:
            payload = json.loads(args.payload)
        except json.JSONDecodeError:
            print("BFF payload was not valid JSON.", file=sys.stderr)
            return 1
        error = validate_bff_action_payload(payload)
        if error:
            print(error, file=sys.stderr)
            return 1
        return 0
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
