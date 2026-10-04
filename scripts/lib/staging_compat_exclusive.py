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


def first_nonempty_line(body: str) -> str:
    for line in body.splitlines():
        stripped = line.strip()
        if stripped:
            return stripped
    return ""


def latest_marker_state(comments: Iterable[Mapping[str, Any]], pattern: re.Pattern[str]) -> str | None:
    state: str | None = None
    for comment in comments:
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
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
