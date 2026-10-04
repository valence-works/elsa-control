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
# Mirror of elsa-cloud supabase/functions/control-bff/handler.ts BodySchema
# at 30ffdc1f (elsa-cloud#146). Copied field-for-field into
# scripts/tests/fixtures/elsa-cloud-control-bff-handler.ts. Being stricter
# than that schema (for example UUID-shaped ids) is fail-closed and allowed.
BFF_HANDLER_SOURCE_SHA = "30ffdc1f"
CATALOG_MIN_LENGTH = 1
CATALOG_MAX_LENGTH = 120
IDEMPOTENCY_KEY_MIN_LENGTH = 8
IDEMPOTENCY_KEY_MAX_LENGTH = 200
KNOWN_BFF_ACTIONS = frozenset(
    {
        "compatibility",
        "bootstrap",
        "listOrganizations",
        "updateInstance",
        "createInstanceDeleteConfirmation",
    }
)
RELEASE_CATALOG_FIELDS = (
    "distributionId",
    "releaseLine",
    "requestedVersion",
    "channel",
)
RELEASE_LITERALS = {
    "patchUpdates": "automatic-within-minor",
    "minorUpdates": "explicit-approval",
    "majorMigrations": "explicit-migration",
}
APPLICATION_NULL_FIELDS = (
    "featurePresetId",
    "packagePolicy",
    "configurationShapeRevisionId",
)
PLACEMENT_CATALOG_FIELDS = (
    "targetMode",
    "regionCode",
    "isolationProfile",
    "capacityProfile",
    "networkOutcome",
    "domainOutcome",
)
DESIRED_LIFECYCLE = "Running"


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


def _catalog_error(value: Any, path: str) -> str | None:
    if not isinstance(value, str):
        return f"{path} must be a catalog string."
    if not CATALOG_MIN_LENGTH <= len(value) <= CATALOG_MAX_LENGTH:
        return (
            f"{path} must be a catalog string of "
            f"{CATALOG_MIN_LENGTH}-{CATALOG_MAX_LENGTH} characters."
        )
    return None


def _require_object(value: Any, path: str) -> str | None:
    if not isinstance(value, Mapping):
        return f"{path} must be an object."
    return None


def _require_uuid(payload: Mapping[str, Any], field: str) -> str | None:
    value = payload.get(field)
    if not isinstance(value, str) or not UUID_PATTERN.fullmatch(value):
        return f"{field} must be a UUID."
    return None


def _validate_managed_elsa_intent(intent: Any) -> str | None:
    error = _require_object(intent, "intent")
    if error:
        return error
    assert isinstance(intent, Mapping)
    release = intent.get("release")
    error = _require_object(release, "intent.release")
    if error:
        return error
    assert isinstance(release, Mapping)
    for field in RELEASE_CATALOG_FIELDS:
        error = _catalog_error(release.get(field), f"intent.release.{field}")
        if error:
            return error
    for field, expected in RELEASE_LITERALS.items():
        if release.get(field) != expected:
            return f"intent.release.{field} must be {expected!r}."
    application = intent.get("application")
    error = _require_object(application, "intent.application")
    if error:
        return error
    assert isinstance(application, Mapping)
    error = _catalog_error(application.get("topologyId"), "intent.application.topologyId")
    if error:
        return error
    for field in APPLICATION_NULL_FIELDS:
        if field not in application or application[field] is not None:
            return f"intent.application.{field} must be null."
    overrides = application.get("featureOverrides")
    if not isinstance(overrides, Mapping) or len(overrides) != 0:
        return "intent.application.featureOverrides must be an empty object."
    placement = intent.get("placement")
    error = _require_object(placement, "intent.placement")
    if error:
        return error
    assert isinstance(placement, Mapping)
    for field in PLACEMENT_CATALOG_FIELDS:
        error = _catalog_error(placement.get(field), f"intent.placement.{field}")
        if error:
            return error
    if intent.get("desiredLifecycle") != DESIRED_LIFECYCLE:
        return f"intent.desiredLifecycle must be {DESIRED_LIFECYCLE!r}."
    return None


def validate_bff_action_payload(payload: Any) -> str | None:
    """Return an error if the payload would fail the real Cloud BFF schema."""
    if not isinstance(payload, Mapping):
        return "BFF payload must be a JSON object."
    action = payload.get("action")
    if action in {None, ""}:
        return "action is required."
    if action not in KNOWN_BFF_ACTIONS:
        return "action is not a recognized control-bff action."
    if action in {"compatibility", "bootstrap"}:
        return None
    if action == "listOrganizations":
        extra = set(payload) - {"action"}
        if extra:
            return "listOrganizations accepts only the action field."
        return None
    if action == "createInstanceDeleteConfirmation":
        for field in ("organizationId", "workspaceId", "instanceId"):
            error = _require_uuid(payload, field)
            if error:
                return error
        return None
    if action == "updateInstance":
        for field in ("organizationId", "workspaceId", "instanceId"):
            error = _require_uuid(payload, field)
            if error:
                return error
        key = payload.get("idempotencyKey")
        if not isinstance(key, str) or not IDEMPOTENCY_KEY_MIN_LENGTH <= len(key) <= IDEMPOTENCY_KEY_MAX_LENGTH:
            return (
                "idempotencyKey must be a string of "
                f"{IDEMPOTENCY_KEY_MIN_LENGTH}-{IDEMPOTENCY_KEY_MAX_LENGTH} characters."
            )
        if not UUID_PATTERN.fullmatch(key):
            return "idempotencyKey must be a UUID."
        version = payload.get("version")
        if isinstance(version, bool) or not isinstance(version, int) or version < 1:
            return "version must be a positive integer."
        return _validate_managed_elsa_intent(payload.get("intent"))
    return "action is not a recognized control-bff action."


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
