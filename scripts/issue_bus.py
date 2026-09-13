#!/usr/bin/env python3
"""Fail-closed Issue Bus claim and drift checks.

The command deliberately delegates GitHub mutations to the authenticated ``gh``
identity.  The orchestration is kept separate from the command adapter so the
claim protocol can be tested without making live GitHub changes.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Iterable, Mapping, Sequence
from urllib.parse import quote


DEFAULT_REPOSITORY = "valence-works/elsa-control"
DEFAULT_PROJECT_NUMBER = 7
LEAF_LABELS = {"type:task", "type:bug", "type:spike"}
PARENT_LABELS = {"type:feature", "type:epic", "type:program"}
READY_LABEL = "ready-for-agent"
BLOCKED_LABEL = "blocked"
DECISION_LABEL = "needs:decision"
CLAIM_PATTERN = re.compile(r"^claim:\s+(?P<worker>\S+)\s+starting(?:\s|$)", re.IGNORECASE)
ABANDONED_PATTERN = re.compile(r"^claim-abandoned:\s*(?P<claim>\S+)", re.IGNORECASE)
BLOCKED_PATTERN = re.compile(r"^blocked:\s+", re.IGNORECASE)
CLAIM_EXPIRY = timedelta(minutes=15)
GITHUB_API_VERSION = "X-GitHub-Api-Version: 2026-03-10"
PR_URL_PATTERN = re.compile(
    r"https://github\.com/(?P<repo>[^/]+/[^/#]+)/(?:pull|issues)/(?P<number>\d+)",
    re.IGNORECASE,
)


class IssueBusError(RuntimeError):
    """A GitHub read or mutation prevented a safe claim."""


class GhError(IssueBusError):
    """The authenticated gh command failed."""


@dataclass(frozen=True)
class CommandResult:
    stdout: str
    stderr: str
    returncode: int


@dataclass(frozen=True)
class ProjectField:
    id: str
    name: str
    options: Mapping[str, str]
    rest_id: int | None = None


@dataclass(frozen=True)
class ProjectItem:
    id: str
    project_id: str
    issue_number: int
    status: str | None
    agent_state: str | None
    raw: Mapping[str, Any]
    rest_id: int | None = None


@dataclass(frozen=True)
class ProjectSnapshot:
    item: ProjectItem | None
    fields: Mapping[str, ProjectField]


@dataclass(frozen=True)
class ClaimComment:
    id: str
    worker: str
    created_at: str
    lease: str | None
    raw: Mapping[str, Any]


@dataclass(frozen=True)
class Preflight:
    issue: Mapping[str, Any]
    project: ProjectSnapshot
    active_claims: tuple[ClaimComment, ...]
    linked_open_prs: tuple[Mapping[str, Any], ...]
    failures: tuple[str, ...]

    @property
    def passed(self) -> bool:
        return not self.failures


def _json_object(stdout: str, command: Sequence[str]) -> Any:
    try:
        return json.loads(stdout)
    except json.JSONDecodeError as exc:
        rendered = " ".join(command)
        raise GhError(f"gh returned invalid JSON for {rendered}: {exc}") from exc


class GhClient:
    """Thin, injectable adapter around the authenticated gh CLI."""

    def __init__(
        self,
        repository: str = DEFAULT_REPOSITORY,
        project_number: int = DEFAULT_PROJECT_NUMBER,
        project_owner: str | None = None,
        runner: Callable[..., CommandResult] | None = None,
    ) -> None:
        self.repository = repository
        self.project_number = project_number
        self.project_owner = project_owner or repository.split("/", 1)[0]
        self._runner = runner or self._subprocess_runner
        self._fields: Mapping[str, ProjectField] | None = None

    @staticmethod
    def _subprocess_runner(*command: str, input_data: str | None = None) -> CommandResult:
        try:
            completed = subprocess.run(
                ["gh", *command],
                capture_output=True,
                input=input_data,
                text=True,
                check=False,
            )
        except OSError as exc:
            raise GhError(f"Unable to execute gh: {exc}") from exc
        return CommandResult(completed.stdout, completed.stderr, completed.returncode)

    def run(self, *command: str, json_output: bool = False, input_data: str | None = None) -> Any:
        if input_data is None:
            result = self._runner(*command)
        else:
            result = self._runner(*command, input_data=input_data)
        if result.returncode != 0:
            detail = result.stderr.strip() or result.stdout.strip() or "unknown gh error"
            raise GhError(f"gh {' '.join(command)} failed: {detail}")
        return _json_object(result.stdout, command) if json_output else result.stdout

    def identity(self) -> str:
        value = self.run("api", "-H", GITHUB_API_VERSION, "user", json_output=True)
        identity = str(value.get("login", "")).strip() if isinstance(value, Mapping) else ""
        if not identity:
            raise GhError("gh api user returned an empty login")
        return identity

    def issue(self, number: int) -> Mapping[str, Any]:
        value = self.run(
            "api",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}",
            json_output=True,
        )
        if not isinstance(value, Mapping):
            raise GhError("REST issue endpoint returned a non-object")
        comments_value = self.run(
            "api",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/comments?per_page=100",
            "--paginate",
            "--slurp",
            json_output=True,
        )
        comments = []
        for comment in _flatten_pages(comments_value):
            if not isinstance(comment, Mapping):
                continue
            normalized = dict(comment)
            if "createdAt" not in normalized and "created_at" in normalized:
                normalized["createdAt"] = normalized["created_at"]
            comments.append(normalized)
        issue = dict(value)
        issue["closed"] = str(issue.get("state", "")).casefold() == "closed"
        issue["comments"] = comments
        return issue

    def project_fields(self) -> Mapping[str, ProjectField]:
        if self._fields is None:
            value = self.run(
                "api",
                "-H",
                GITHUB_API_VERSION,
                f"orgs/{self.project_owner}/projectsV2/{self.project_number}/fields?per_page=100",
                json_output=True,
            )
            raw_fields = _rest_collection(value, "fields")
            fields: dict[str, ProjectField] = {}
            for raw in raw_fields:
                if not isinstance(raw, Mapping) or "name" not in raw or "id" not in raw:
                    continue
                try:
                    rest_id = int(raw["id"])
                except (TypeError, ValueError):
                    continue
                options = {
                    _rest_option_name(option): str(option["id"])
                    for option in raw.get("options", [])
                    if isinstance(option, Mapping) and "id" in option and _rest_option_name(option)
                }
                field = ProjectField(str(raw["id"]), str(raw["name"]), options, rest_id)
                fields[field.name.casefold()] = field
            self._fields = fields
        return self._fields

    def project_items(self) -> Sequence[Mapping[str, Any]]:
        """Read all Project items through the paginated REST endpoint.

        This is intentionally used only by explicit ``drift --all``. Claim and
        single-issue drift paths use the targeted ``q=<issue>`` request.
        """

        fields = self.project_fields()
        status_field = fields.get("status")
        agent_field = fields.get("agent state")
        if status_field is None or agent_field is None:
            raise GhError("Project is missing Status or Agent State field")
        if status_field.rest_id is None or agent_field.rest_id is None:
            raise GhError("Project Status or Agent State has no REST field id")

        value = self.run(
            "api",
            "-H",
            GITHUB_API_VERSION,
            f"orgs/{self.project_owner}/projectsV2/{self.project_number}/items?per_page=100"
            f"&fields={status_field.rest_id},{agent_field.rest_id}",
            "--paginate",
            "--slurp",
            json_output=True,
        )
        return [_normalize_project_item(item) for item in _rest_collection(value, "items")]

    def project_for_issue(self, number: int) -> ProjectSnapshot:
        """Read one issue's matching Project item without scanning the board."""

        fields = self.project_fields()
        status_field = fields.get("status")
        agent_field = fields.get("agent state")
        if status_field is None or agent_field is None:
            raise GhError("Project is missing Status or Agent State field")
        if status_field.rest_id is None or agent_field.rest_id is None:
            raise GhError("Project Status or Agent State has no REST field id")
        value = self.run(
            "api",
            "-H",
            GITHUB_API_VERSION,
            f"orgs/{self.project_owner}/projectsV2/{self.project_number}/items?per_page=100&q={number}"
            f"&fields={status_field.rest_id},{agent_field.rest_id}",
            json_output=True,
        )
        raw_items = _rest_collection(value, "items")
        item: ProjectItem | None = None
        for raw in raw_items:
            if not isinstance(raw, Mapping):
                continue
            content = raw.get("content")
            content_number = content.get("number") if isinstance(content, Mapping) else None
            try:
                item_matches = int(content_number) == number
            except (TypeError, ValueError):
                item_matches = False
            if not item_matches:
                continue
            values = raw.get("fields", raw)
            item = ProjectItem(
                id=str(raw.get("node_id") or raw.get("id") or ""),
                project_id=str(self.project_number),
                issue_number=number,
                status=_rest_field_value(values, "Status"),
                agent_state=_rest_field_value(values, "Agent State"),
                raw=raw,
                rest_id=_rest_int(raw.get("id")),
            )
            break
        return ProjectSnapshot(item, fields)

    def linked_open_prs(self, number: int) -> Sequence[Mapping[str, Any]]:
        """Return open pull requests cross-referenced from an issue.

        Timeline access is deliberately fail-closed: an inability to inspect
        links cannot safely establish that no implementation PR exists.
        """

        value = self.run(
            "api",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/timeline",
            "--paginate",
            "--slurp",
            json_output=True,
        )
        events = _flatten_pages(value)
        candidates: dict[int, Mapping[str, Any]] = {}
        for event in events:
            if not isinstance(event, Mapping):
                continue
            source = event.get("source")
            source_issue = source.get("issue") if isinstance(source, Mapping) else None
            if not isinstance(source_issue, Mapping):
                source_issue = source if isinstance(source, Mapping) else None
            pull_request = source_issue.get("pull_request") if isinstance(source_issue, Mapping) else None
            number_value = source_issue.get("number") if isinstance(source_issue, Mapping) else None
            if not isinstance(pull_request, Mapping) and "pull_request" not in event:
                continue
            if isinstance(event.get("pull_request"), Mapping):
                pull_request = event["pull_request"]
            try:
                pr_number = int(number_value)
            except (TypeError, ValueError):
                continue
            state = str(source_issue.get("state", "")) if isinstance(source_issue, Mapping) else ""
            if state.upper() == "OPEN":
                candidates[pr_number] = {
                    "number": pr_number,
                    "url": source_issue.get("html_url") or source_issue.get("url"),
                    "state": state,
                }

        # A canonical ``pr:`` comment is also an explicit link.  Check the
        # referenced PR directly because timeline responses vary by event type.
        issue = self.issue(number)
        for comment in issue.get("comments", []):
            body = str(comment.get("body", "")) if isinstance(comment, Mapping) else ""
            if not body.lower().startswith("pr:"):
                continue
            match = PR_URL_PATTERN.search(body)
            if match is None or match.group("repo").lower() != self.repository.lower():
                continue
            pr_number = int(match.group("number"))
            pr = self.run(
                "api",
                "-H",
                GITHUB_API_VERSION,
                f"repos/{self.repository}/pulls/{pr_number}",
                json_output=True,
            )
            if isinstance(pr, Mapping) and str(pr.get("state", "")).upper() == "OPEN":
                candidates[pr_number] = pr
        return tuple(candidates.values())

    def assign(self, number: int, login: str) -> None:
        self.run(
            "api",
            "--method",
            "POST",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/assignees",
            "--input",
            "-",
            input_data=json.dumps({"assignees": [login]}),
        )

    def unassign(self, number: int, login: str) -> None:
        self.run(
            "api",
            "--method",
            "DELETE",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/assignees",
            "--input",
            "-",
            input_data=json.dumps({"assignees": [login]}),
        )

    def comment(self, number: int, body: str) -> Mapping[str, Any] | None:
        """Post a comment and return its immutable GitHub node identity."""

        value = self.run(
            "api",
            "--method",
            "POST",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/comments",
            "--input",
            "-",
            input_data=json.dumps({"body": body}),
            json_output=True,
        )
        return value if isinstance(value, Mapping) else None

    def add_label(self, number: int, label: str) -> None:
        self.run(
            "api",
            "--method",
            "POST",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/labels",
            "--input",
            "-",
            input_data=json.dumps({"labels": [label]}),
        )

    def remove_label(self, number: int, label: str) -> None:
        self.run(
            "api",
            "--method",
            "DELETE",
            "-H",
            GITHUB_API_VERSION,
            f"repos/{self.repository}/issues/{number}/labels/{quote(label, safe='')}",
        )

    def set_project_field(self, item: ProjectItem, field: ProjectField, option_name: str) -> None:
        self.set_project_fields(item, ((field, option_name),))

    def set_project_fields(
        self, item: ProjectItem, updates: Sequence[tuple[ProjectField, str]]
    ) -> None:
        if item.rest_id is None:
            raise GhError("Project item has no numeric REST id")
        values = []
        for field, option_name in updates:
            if field.rest_id is None:
                raise GhError(f"Project field {field.name!r} has no numeric REST id")
            try:
                option_id = field.options[option_name]
            except KeyError as exc:
                raise GhError(f"Project field {field.name!r} has no option {option_name!r}") from exc
            values.append({"id": field.rest_id, "value": str(option_id)})
        if not values:
            return
        try:
            payload = json.dumps({"fields": values})
        except (TypeError, ValueError) as exc:
            raise GhError("Project field update could not be serialized") from exc
        self.run(
            "api",
            "--method",
            "PATCH",
            "-H",
            GITHUB_API_VERSION,
            f"orgs/{self.project_owner}/projectsV2/{self.project_number}/items/{item.rest_id}",
            "--input",
            "-",
            input_data=payload,
        )


class IssueBus:
    """Issue Bus protocol implementation independent of the gh transport."""

    def __init__(
        self,
        client: GhClient,
        output: Callable[[str], None] = print,
        clock: Callable[[], datetime] | None = None,
    ) -> None:
        self.client = client
        self.output = output
        self.clock = clock or (lambda: datetime.now(timezone.utc))

    def preflight(
        self,
        number: int,
        worker: str,
        ignored_claim_id: str | None = None,
        allow_active_claims: bool = False,
    ) -> Preflight:
        issue = self.client.issue(number)
        project = self.client.project_for_issue(number)
        linked_open_prs = tuple(self.client.linked_open_prs(number))
        labels = _labels(issue)
        failures: list[str] = []
        if str(issue.get("state", "")).upper() != "OPEN" or bool(issue.get("closed", False)):
            failures.append("issue is not open")
        if not _is_leaf(issue, labels, project):
            failures.append("issue is not an eligible leaf (type:task, type:bug, or type:spike)")
        if READY_LABEL not in labels:
            failures.append("missing ready-for-agent label")
        if BLOCKED_LABEL in labels:
            failures.append("blocked label is present")
        if DECISION_LABEL in labels:
            failures.append("needs:decision label is present")
        assignees = issue.get("assignees", [])
        if isinstance(assignees, list) and assignees:
            failures.append("issue is already assigned")
        active_claims = tuple(
            claim
            for claim in active_claims_for_issue(issue, project.item, self.clock())
            if claim.id != ignored_claim_id
        )
        if active_claims and not allow_active_claims:
            failures.append(f"active claim exists ({active_claims[0].id})")
        if project.item is None:
            failures.append("issue is not present on the execution project")
        else:
            if project.item.status != "Ready":
                failures.append(f"Project Status is {project.item.status!r}, expected 'Ready'")
            if project.item.agent_state != "Agent Ready":
                failures.append(f"Agent State is {project.item.agent_state!r}, expected 'Agent Ready'")
        if linked_open_prs:
            numbers = ", ".join(str(pr.get("number")) for pr in linked_open_prs)
            failures.append(f"linked open pull request exists ({numbers})")
        lanes = sorted(label for label in labels if label.startswith("worker:"))
        conflicting = [lane for lane in lanes if lane != f"worker:{worker}"]
        if conflicting:
            failures.append(f"conflicting worker lane(s): {', '.join(conflicting)}")
        return Preflight(issue, project, active_claims, linked_open_prs, tuple(failures))

    def claim(self, number: int, worker: str, dry_run: bool = False) -> bool:
        if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", worker):
            self.output(f"refusing claim: invalid worker lane {worker!r}")
            return False
        try:
            login = self.client.identity()
            preflight = self.preflight(number, worker)
        except IssueBusError as exc:
            self.output(f"refusing claim: preflight failed closed: {exc}")
            return False

        if not preflight.passed:
            self.output("refusing claim:")
            for failure in preflight.failures:
                self.output(f"- {failure}")
            return False
        if dry_run:
            self.output(f"claim preflight passed for #{number}; no mutation performed")
            return True

        item = preflight.project.item
        assert item is not None
        fields = preflight.project.fields
        status_field = fields.get("status")
        agent_field = fields.get("agent state")
        if status_field is None or agent_field is None:
            self.output("refusing claim: project is missing Status or Agent State field")
            return False
        # The exact first-line protocol body is the complete claim body. The
        # REST response's immutable node_id is the machine-readable lease.
        claim_body = f"claim: {worker} starting"
        changed: list[str] = []
        claim_comment_id: str | None = None

        try:
            changed.append("claim comment")
            claim_response = self.client.comment(number, claim_body)
            claim_comment_id = _comment_identity(claim_response)
            if claim_comment_id is None:
                raise ClaimLost("claim comment response did not contain an immutable node identity")

            after_comment = self.client.issue(number)
            claim_comment = find_claim_by_id(after_comment.get("comments", []), claim_comment_id)
            after_comment_project = self.client.project_for_issue(number)
            active_after_comment = tuple(
                active_claims_for_issue(after_comment, after_comment_project.item, self.clock())
            )
            winner = earliest_claim(active_after_comment)
            if claim_comment is None:
                raise ClaimLost("claim comment was not visible after posting")
            if winner is None or winner.id != claim_comment.id:
                raise ClaimLost(f"earlier active claim {winner.id if winner else 'unknown'} won")

            # Revalidate every non-claim precondition after arbitration. The
            # issue, labels, board fields, assignment, and linked PRs may have
            # changed while this worker was posting its comment.
            after_arbitration = self.preflight(
                number,
                worker,
                ignored_claim_id=claim_comment.id,
                allow_active_claims=True,
            )
            if not after_arbitration.passed:
                raise ClaimLost(
                    "pre-assignment invariants changed: " + "; ".join(after_arbitration.failures)
                )
            after_arbitration_winner = earliest_claim(
                active_claims_for_issue(
                    after_arbitration.issue,
                    after_arbitration.project.item,
                    self.clock(),
                )
            )
            if after_arbitration_winner is None or after_arbitration_winner.id != claim_comment.id:
                raise ClaimLost(
                    f"earlier active claim {after_arbitration_winner.id if after_arbitration_winner else 'unknown'} won"
                )
            refreshed_item = after_arbitration.project.item
            refreshed_status = after_arbitration.project.fields.get("status")
            refreshed_agent = after_arbitration.project.fields.get("agent state")
            if refreshed_item is None or refreshed_status is None or refreshed_agent is None:
                raise ClaimLost("project claim fields changed during arbitration")
            item = refreshed_item
            status_field = refreshed_status
            agent_field = refreshed_agent

            # Arbitration must precede assignment. Concurrent workers often use
            # the same authenticated login; a losing worker must never remove
            # an assignment that the winner owns.
            changed.append("assignment")
            self.client.assign(number, login)
            changed.append("ready-for-agent removal")
            self.client.remove_label(number, READY_LABEL)
            # The combined PATCH is recorded before the call so a transport
            # failure after a partial server-side update still rolls back both
            # fields. It is only reached after this worker won arbitration.
            changed.append("project claim fields")
            self._set_project_fields(
                item,
                (
                    (status_field, "In Progress"),
                    (agent_field, "Assigned"),
                ),
            )

            final_issue = self.client.issue(number)
            final_project = self.client.project_for_issue(number)
            final_linked_open_prs = tuple(self.client.linked_open_prs(number))
            final_claim = find_claim_by_id(final_issue.get("comments", []), claim_comment_id)
            final_labels = _labels(final_issue)
            final_item = final_project.item
            final_winner = earliest_claim(
                active_claims_for_issue(final_issue, final_project.item, self.clock())
            )
            if (
                final_claim is None
                or final_winner is None
                or final_winner.id != final_claim.id
                or final_linked_open_prs
                or _assignee_logins(final_issue) != {login}
                or READY_LABEL in final_labels
                or final_item is None
                or final_item.status != "In Progress"
                or final_item.agent_state != "Assigned"
            ):
                raise ClaimLost("claim state was changed during final verification")
        except (IssueBusError, ClaimLost) as exc:
            reason = str(exc)
            rollback_ok = self._rollback(number, login, item, status_field, agent_field, changed, claim_comment_id, reason)
            if not rollback_ok:
                blocked_ok = self._blocked_path(
                    number,
                    login,
                    item,
                    status_field,
                    agent_field,
                    f"claim failed - {reason}",
                    changed,
                )
                outcome = "canonical blocked path applied" if blocked_ok else "canonical blocked path incomplete"
                self.output(f"claim refused; rollback failed; {outcome}: {reason}")
            else:
                self.output(f"claim refused; rollback completed: {reason}")
            return False

        self.output(f"claimed #{number} as {worker} (lease {claim_comment_id})")
        return True

    def _rollback(
        self,
        number: int,
        login: str,
        item: ProjectItem,
        status_field: ProjectField,
        agent_field: ProjectField,
        changed: Sequence[str],
        claim_comment_id: str | None,
        reason: str,
    ) -> bool:
        ok = True
        for mutation in reversed(changed):
            try:
                if mutation == "project claim fields":
                    self._restore_project_fields(number, claim_comment_id, status_field, agent_field)
                elif mutation == "ready-for-agent removal":
                    self._restore_ready_label(number, claim_comment_id)
                elif mutation == "assignment":
                    self._restore_assignment(number, claim_comment_id, login)
            except IssueBusError:
                ok = False
        if "claim comment" in changed:
            if claim_comment_id is None:
                ok = False
            else:
                try:
                    self.client.comment(
                        number,
                        f"claim-abandoned: {claim_comment_id}\nreason: {reason}",
                    )
                except IssueBusError:
                    ok = False
        elif claim_comment_id is not None:
            try:
                self.client.comment(
                    number,
                    f"claim-abandoned: {claim_comment_id}\nreason: {reason}",
                )
            except IssueBusError:
                ok = False
        return ok

    def _rollback_state(
        self, number: int, claim_comment_id: str | None
    ) -> tuple[Mapping[str, Any], ProjectSnapshot]:
        if claim_comment_id is None:
            raise ClaimLost("claim lease identity is unavailable")
        issue = self.client.issue(number)
        project = self.client.project_for_issue(number)
        winner = earliest_claim(active_claims_for_issue(issue, project.item, self.clock()))
        if winner is None or winner.id != claim_comment_id:
            raise ClaimLost("claim lease no longer owns the current state")
        return issue, project

    def _restore_project_fields(
        self,
        number: int,
        claim_comment_id: str | None,
        status_field: ProjectField,
        agent_field: ProjectField,
    ) -> None:
        _, project = self._rollback_state(number, claim_comment_id)
        current_item = project.item
        current_status = project.fields.get("status") or status_field
        current_agent = project.fields.get("agent state") or agent_field
        if current_item is None:
            raise ClaimLost("project item disappeared during rollback")
        current_values = (current_item.status, current_item.agent_state)
        if current_values == ("Ready", "Agent Ready"):
            return
        if current_item.status not in {"Ready", "In Progress"} or current_item.agent_state not in {
            "Agent Ready",
            "Assigned",
        }:
            raise ClaimLost("project state changed externally during rollback")
        self._set_project_fields(
            current_item,
            (
                (current_status, "Ready"),
                (current_agent, "Agent Ready"),
            ),
        )

    def _restore_ready_label(self, number: int, claim_comment_id: str | None) -> None:
        issue, _ = self._rollback_state(number, claim_comment_id)
        if READY_LABEL in _labels(issue):
            return
        self.client.add_label(number, READY_LABEL)

    def _restore_assignment(
        self, number: int, claim_comment_id: str | None, login: str
    ) -> None:
        issue, _ = self._rollback_state(number, claim_comment_id)
        assignees = _assignee_logins(issue)
        if not assignees:
            return
        if assignees != {login}:
            raise ClaimLost("assignment changed externally during rollback")
        self.client.unassign(number, login)

    def _blocked_path(
        self,
        number: int,
        login: str,
        item: ProjectItem,
        status_field: ProjectField,
        agent_field: ProjectField,
        reason: str,
        changed: Sequence[str],
    ) -> bool:
        """Best-effort canonical stop state when rollback cannot be trusted."""

        ok = True
        operations: list[Callable[[], None]] = [
            lambda: self.client.comment(number, f"blocked: {reason}"),
            lambda: self.client.add_label(number, BLOCKED_LABEL),
            lambda: self._set_project_fields(
                item,
                (
                    (status_field, "Blocked"),
                    (agent_field, "Not Ready"),
                ),
            ),
        ]
        if "assignment" in changed:
            operations.append(lambda: self._unassign_if_owned(number, login))
        for operation in operations:
            try:
                operation()
            except IssueBusError:
                ok = False
        return ok

    def _unassign_if_owned(self, number: int, login: str) -> None:
        issue = self.client.issue(number)
        assignees = _assignee_logins(issue)
        if not assignees:
            return
        if assignees != {login}:
            raise ClaimLost("refusing to unassign a newer owner")
        self.client.unassign(number, login)

    def _set_project_fields(
        self, item: ProjectItem, updates: Sequence[tuple[ProjectField, str]]
    ) -> None:
        """Use one REST PATCH while retaining a narrow fake-client seam in tests."""

        setter = getattr(self.client, "set_project_fields", None)
        if callable(setter):
            setter(item, updates)
            return
        for field, option_name in updates:
            self.client.set_project_field(item, field, option_name)

    def drift(self, number: int | None = None, all_items: bool = False) -> list[dict[str, Any]]:
        if number is None and not all_items:
            raise IssueBusError("drift requires an issue number or --all")
        if number is not None:
            return [self._drift_issue(number)]
        item_rows = self.client.project_items()
        findings: list[dict[str, Any]] = []
        for row in item_rows:
            content = row.get("content")
            raw_number = content.get("number") if isinstance(content, Mapping) else None
            try:
                issue_number = int(raw_number)
            except (TypeError, ValueError):
                continue
            findings.append(self._drift_project_row(issue_number, row))
        return findings

    def _drift_project_row(self, number: int, row: Mapping[str, Any]) -> dict[str, Any]:
        """Check normalized REST item fields locally; query links only for ready items."""

        labels = _labels({"labels": row.get("labels", [])})
        ready = READY_LABEL in labels
        status = _text_value(row, "status", "Status")
        agent_state = _text_value(row, "agent State", "Agent State", "agent_state")
        issue_findings = _drift_field_findings(number, labels, status, agent_state)
        if ready:
            try:
                linked = tuple(self.client.linked_open_prs(number))
            except IssueBusError as exc:
                return {"issue": number, "code": "unable-to-check", "message": str(exc)}
            if linked:
                issue_findings.append(
                    _finding(number, "ready-linked-open-pr", "ready-for-agent is present while an open linked PR exists")
                )
        return {"issue": number, "findings": issue_findings}

    def _drift_issue(self, number: int) -> dict[str, Any]:
        try:
            issue = self.client.issue(number)
            project = self.client.project_for_issue(number)
            linked = tuple(self.client.linked_open_prs(number))
        except IssueBusError as exc:
            return {"issue": number, "code": "unable-to-check", "message": str(exc)}
        labels = _labels(issue)
        ready = READY_LABEL in labels
        status = project.item.status if project.item else None
        agent_state = project.item.agent_state if project.item else None
        issue_findings = _drift_field_findings(number, labels, status, agent_state)
        if ready and linked:
            issue_findings.append(_finding(number, "ready-linked-open-pr", "ready-for-agent is present while an open linked PR exists"))
        return {"issue": number, "findings": issue_findings}


class ClaimLost(IssueBusError):
    """A concurrent claim or external mutation won arbitration."""


def _text_value(raw: Mapping[str, Any], *keys: str) -> str | None:
    for key in keys:
        value = raw.get(key)
        if value is not None:
            return str(value)
    return None


def _rest_int(value: Any) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def _rest_option_name(option: Mapping[str, Any]) -> str:
    return _rest_text(option.get("name"))


def _rest_text(value: Any) -> str:
    if isinstance(value, Mapping):
        direct = value.get("raw") or value.get("html")
        if direct is not None:
            return str(direct)
        nested_name = value.get("name")
        if nested_name is not None:
            return _rest_text(nested_name)
        return ""
    return str(value or "")


def _rest_field_value(values: Any, field_name: str) -> str | None:
    if isinstance(values, Mapping):
        if isinstance(values.get("fields"), list):
            values = values["fields"]
        else:
            direct = next(
                (value for key, value in values.items() if str(key).casefold() == field_name.casefold()),
                None,
            )
            if direct is not None:
                if isinstance(direct, Mapping):
                    direct = direct.get("value") or direct.get("name") or direct.get("raw")
                return str(direct) if direct is not None else None
            values = list(values.values())
    if not isinstance(values, list):
        return None
    for value in values:
        if not isinstance(value, Mapping):
            continue
        name = value.get("name") or value.get("field_name")
        if str(name or "").casefold() != field_name.casefold():
            continue
        selected = value.get("value")
        if selected is None:
            selected = value.get("selected_value") or value.get("option_name")
        if isinstance(selected, Mapping):
            selected = _rest_text(selected)
        if selected is not None:
            return str(selected)
        if value.get("option_id") is not None:
            return str(value["option_id"])
    return None


def _labels(issue: Mapping[str, Any]) -> set[str]:
    values = issue.get("labels", [])
    return {
        str(label.get("name")) if isinstance(label, Mapping) else str(label)
        for label in values
        if (isinstance(label, Mapping) and label.get("name")) or isinstance(label, str)
    }


def _assignee_logins(issue: Mapping[str, Any]) -> set[str]:
    values = issue.get("assignees", [])
    return {
        str(assignee.get("login")) if isinstance(assignee, Mapping) else str(assignee)
        for assignee in values
        if (isinstance(assignee, Mapping) and assignee.get("login")) or isinstance(assignee, str)
    }


def _is_leaf(issue: Mapping[str, Any], labels: set[str], project: ProjectSnapshot) -> bool:
    if labels & PARENT_LABELS:
        return False
    if labels & LEAF_LABELS:
        return True
    issue_type = issue.get("issueType")
    issue_type_name = issue_type.get("name") if isinstance(issue_type, Mapping) else issue_type
    project_type = project.item.raw.get("work item type") if project.item else None
    return str(issue_type_name or project_type or "").casefold() in {"task", "bug", "spike"}


def _flatten_pages(value: Any) -> list[Any]:
    if isinstance(value, list):
        result: list[Any] = []
        for entry in value:
            result.extend(_flatten_pages(entry))
        return result
    return [value]


def _rest_collection(value: Any, key: str) -> list[Mapping[str, Any]]:
    """Normalize REST bare arrays and optional envelope/page responses."""

    pending: list[Any] = [value]
    result: list[Mapping[str, Any]] = []
    while pending:
        entry = pending.pop()
        if isinstance(entry, Mapping) and key in entry:
            pending.append(entry[key])
        elif isinstance(entry, list):
            pending.extend(reversed(entry))
        elif isinstance(entry, Mapping):
            result.append(entry)
    return result


def _normalize_project_item(item: Mapping[str, Any]) -> Mapping[str, Any]:
    """Expose REST selected values in the row shape consumed by drift checks."""

    content = item.get("content")
    content_mapping = dict(content) if isinstance(content, Mapping) else {}
    labels = content_mapping.get("labels", item.get("labels", []))
    assignees = content_mapping.get("assignees", item.get("assignees", []))
    fields = item.get("fields", item)
    normalized = dict(item)
    normalized["content"] = content_mapping
    normalized["labels"] = labels
    normalized["assignees"] = assignees
    normalized["status"] = _rest_field_value(fields, "Status")
    normalized["agent State"] = _rest_field_value(fields, "Agent State")
    return normalized


def active_claims_from(comments: Any) -> list[ClaimComment]:
    if not isinstance(comments, list):
        return []
    ordered = sorted(
        (comment for comment in comments if isinstance(comment, Mapping)),
        key=lambda comment: (str(comment.get("createdAt", "")), str(comment.get("id", ""))),
    )
    abandoned: set[str] = set()
    blocked_at: list[str] = []
    claims: list[ClaimComment] = []
    for comment in ordered:
        body = str(comment.get("body", ""))
        abandoned_match = ABANDONED_PATTERN.match(body)
        if abandoned_match:
            abandoned.add(abandoned_match.group("claim"))
        if BLOCKED_PATTERN.match(body):
            blocked_at.append(str(comment.get("createdAt", "")))
        claim_match = CLAIM_PATTERN.match(body)
        if claim_match:
            lease_match = re.search(r"^claim-lease:\s*(\S+)", body, re.MULTILINE | re.IGNORECASE)
            comment_identity = comment.get("node_id") or comment.get("nodeId") or comment.get("id")
            claims.append(
                ClaimComment(
                    id=str(comment_identity or ""),
                    worker=claim_match.group("worker"),
                    created_at=str(comment.get("createdAt", "")),
                    lease=lease_match.group(1) if lease_match else None,
                    raw=comment,
                )
            )
    active: list[ClaimComment] = []
    for claim in claims:
        if claim.id in abandoned:
            continue
        if any(timestamp > claim.created_at for timestamp in blocked_at):
            continue
        active.append(claim)
    return sorted(active, key=lambda claim: (claim.created_at, claim.id))


def active_claims_for_issue(
    issue: Mapping[str, Any], project_item: ProjectItem | None, now: datetime
) -> list[ClaimComment]:
    """Apply the protocol's 15-minute expiry only in the fully-ready state.

    A malformed or missing timestamp remains active. This keeps an unreadable
    reservation fail-closed while allowing an operator to reconcile it.
    """

    claims = active_claims_from(issue.get("comments", []))
    if (
        READY_LABEL not in _labels(issue)
        or project_item is None
        or project_item.status != "Ready"
        or project_item.agent_state != "Agent Ready"
    ):
        return claims
    cutoff = _as_utc(now) - CLAIM_EXPIRY
    return [claim for claim in claims if not _claim_expired(claim, cutoff)]


def _claim_expired(claim: ClaimComment, cutoff: datetime) -> bool:
    if not claim.created_at:
        return False
    try:
        created_at = datetime.fromisoformat(claim.created_at.replace("Z", "+00:00"))
    except ValueError:
        return False
    if created_at.tzinfo is None:
        return False
    return _as_utc(created_at) <= cutoff


def _as_utc(value: datetime) -> datetime:
    if value.tzinfo is None:
        # Treat an injected naive clock as UTC so deterministic unit fixtures
        # remain usable; timestamps read from GitHub are still required to be
        # timezone-aware before expiry can be applied.
        return value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def earliest_claim(claims: Iterable[ClaimComment]) -> ClaimComment | None:
    return min(claims, key=lambda claim: (claim.created_at, claim.id), default=None)


def find_claim_by_lease(comments: Any, lease: str) -> ClaimComment | None:
    return next((claim for claim in active_claims_from(comments) if claim.lease == lease), None)


def find_claim_by_id(comments: Any, claim_id: str) -> ClaimComment | None:
    return next((claim for claim in active_claims_from(comments) if claim.id == claim_id), None)


def _comment_identity(comment: Any) -> str | None:
    if not isinstance(comment, Mapping):
        return None
    for key in ("node_id", "nodeId", "id"):
        value = comment.get(key)
        if value is not None and str(value):
            return str(value)
    return None


def _finding(number: int, code: str, message: str) -> dict[str, Any]:
    return {"issue": number, "code": code, "message": message}


def _drift_field_findings(
    number: int, labels: set[str], status: str | None, agent_state: str | None
) -> list[dict[str, Any]]:
    findings: list[dict[str, Any]] = []
    ready = READY_LABEL in labels
    if ready and status == "In Progress":
        findings.append(_finding(number, "ready-in-progress", "ready-for-agent is present while Status is In Progress"))
    if status == "Done" and agent_state == "Review Required":
        findings.append(_finding(number, "done-review-required", "Status is Done while Agent State is Review Required"))
    lanes = sorted(label for label in labels if label.startswith("worker:"))
    if len(lanes) > 1:
        findings.append(_finding(number, "multiple-worker-lanes", f"conflicting worker lanes: {', '.join(lanes)}"))
    if status == "In Progress" and agent_state == "Agent Ready":
        findings.append(_finding(number, "in-progress-agent-ready", "Status is In Progress while Agent State is Agent Ready"))
    return findings


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=os.environ.get("GH_REPO", DEFAULT_REPOSITORY))
    parser.add_argument("--project", type=int, default=DEFAULT_PROJECT_NUMBER)
    parser.add_argument("--owner", default=None, help="GitHub owner of the Project (defaults to repository owner)")
    subparsers = parser.add_subparsers(dest="command", required=True)

    claim = subparsers.add_parser("claim", help="Preflight and claim one eligible issue")
    claim.add_argument("issue", type=int)
    claim.add_argument("--worker", required=True)
    claim.add_argument("--dry-run", action="store_true", help="Run preflight without mutations")

    drift = subparsers.add_parser("drift", help="Report contradictory Issue Bus state")
    drift.add_argument("issue", type=int, nargs="?")
    drift.add_argument("--all", action="store_true", dest="all_items")
    drift.add_argument("--json", action="store_true", dest="json_output")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    client = GhClient(args.repo, args.project, args.owner)
    bus = IssueBus(client)
    if args.command == "claim":
        return 0 if bus.claim(args.issue, args.worker, args.dry_run) else 1
    try:
        results = bus.drift(args.issue, args.all_items)
    except IssueBusError as exc:
        print(f"drift check failed: {exc}", file=sys.stderr)
        return 1
    if args.json_output:
        print(json.dumps(results, indent=2, sort_keys=True))
    else:
        findings = [finding for result in results for finding in result.get("findings", [])]
        failures = [result for result in results if result.get("code") == "unable-to-check"]
        if not findings and not failures:
            print("no Issue Bus drift detected")
        for result in results:
            for finding in result.get("findings", []):
                print(f"#{finding['issue']}: {finding['code']}: {finding['message']}")
            if result.get("code") == "unable-to-check":
                print(f"#{result['issue']}: unable-to-check: {result['message']}")
    return 1 if any(result.get("findings") or result.get("code") == "unable-to-check" for result in results) else 0


if __name__ == "__main__":
    raise SystemExit(main())
