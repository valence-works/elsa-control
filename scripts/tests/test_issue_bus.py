#!/usr/bin/env python3
"""Offline tests for the fail-closed Issue Bus command."""

from __future__ import annotations

import copy
import importlib.util
import json
import sys
import unittest
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("issue_bus", ROOT / "scripts" / "issue_bus.py")
assert SPEC and SPEC.loader
issue_bus = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = issue_bus
SPEC.loader.exec_module(issue_bus)


class GhClientAdapterTests(unittest.TestCase):
    def test_targeted_rest_project_query_parses_numeric_ids_and_options(self) -> None:
        fields_response = [
            {
                "id": 1001,
                "node_id": "PVTF_status",
                "name": "Status",
                "data_type": "single_select",
                "options": [
                    {"id": "status-ready", "name": {"raw": "Ready", "html": "Ready"}},
                    {"id": "status-progress", "name": {"raw": "In Progress", "html": "In Progress"}},
                ],
            },
            {
                "id": 1002,
                "node_id": "PVTF_agent",
                "name": "Agent State",
                "data_type": "single_select",
                "options": [
                    {"id": "agent-ready", "name": {"raw": "Agent Ready", "html": "Agent Ready"}},
                    {"id": "agent-assigned", "name": {"raw": "Assigned", "html": "Assigned"}},
                ],
            },
        ]
        items_response = [
            {
                "id": 2001,
                "node_id": "PVTI_item",
                "content": {
                    "id": 99901,
                    "number": 401,
                    "type": "Issue",
                    "labels": [{"name": "ready-for-agent"}],
                    "assignees": [],
                },
                "fields": [
                    {
                        "id": 1001,
                        "name": "Status",
                        "value": {
                            "color": "yellow",
                            "id": "status-progress",
                            "name": {"html": "In Progress", "raw": "In Progress"},
                        },
                        "option_id": "status-progress",
                    },
                    {
                        "id": 1002,
                        "name": "Agent State",
                        "value": {
                            "color": "yellow",
                            "id": "agent-assigned",
                            "name": {"html": "Assigned", "raw": "Assigned"},
                        },
                        "option_id": "agent-assigned",
                    },
                ],
            },
            {
                "id": 2002,
                "node_id": "PVTI_other",
                "content": {"id": 99902, "number": 1401, "type": "Issue"},
                "fields": [],
            },
        ]
        commands: list[tuple[str, ...]] = []

        def runner(*command: str, **kwargs: Any) -> issue_bus.CommandResult:
            commands.append(command)
            response = fields_response if "/fields?" in " ".join(command) else items_response
            return issue_bus.CommandResult(json.dumps(response), "", 0)

        client = issue_bus.GhClient("valence-works/elsa-control", runner=runner)

        snapshot = client.project_for_issue(401)

        self.assertIsNotNone(snapshot.item)
        assert snapshot.item is not None
        self.assertEqual(snapshot.item.id, "PVTI_item")
        self.assertEqual(snapshot.item.rest_id, 2001)
        self.assertEqual(snapshot.item.project_id, "7")
        self.assertEqual(snapshot.item.status, "In Progress")
        self.assertEqual(snapshot.item.agent_state, "Assigned")
        self.assertEqual(snapshot.fields["status"].rest_id, 1001)
        self.assertEqual(snapshot.fields["status"].options["In Progress"], "status-progress")
        self.assertEqual(snapshot.fields["agent state"].options["Assigned"], "agent-assigned")
        self.assertIn("/fields?per_page=100", commands[0][-1])
        self.assertIn("q=401", commands[1][-1])
        self.assertIn("fields=1001,1002", commands[1][-1])
        self.assertNotIn("item-list", " ".join(commands[0] + commands[1]))

        rows = client.project_items()
        self.assertEqual(rows[0]["status"], "In Progress")
        self.assertEqual(rows[0]["agent State"], "Assigned")
        self.assertEqual(rows[0]["content"]["number"], 401)
        self.assertEqual(rows[0]["labels"], [{"name": "ready-for-agent"}])
        self.assertEqual(rows[0]["assignees"], [])
        self.assertTrue(any("/items?per_page=100&fields=1001,1002" in part for part in commands[2]))
        self.assertIn("--paginate", commands[2])
        self.assertNotIn("item-list", " ".join(commands[2]))

    def test_project_patch_uses_numeric_item_and_field_ids(self) -> None:
        calls: list[tuple[tuple[str, ...], str | None]] = []

        def runner(*command: str, **kwargs: Any) -> issue_bus.CommandResult:
            calls.append((command, kwargs.get("input_data")))
            return issue_bus.CommandResult("{}", "", 0)

        client = issue_bus.GhClient("valence-works/elsa-control", runner=runner)
        item = issue_bus.ProjectItem("PVTI_item", "7", 401, "Ready", "Agent Ready", {}, rest_id=2001)
        status = issue_bus.ProjectField(
            "1001", "Status", {"Ready": "status-ready", "In Progress": "status-progress"}, 1001
        )
        agent = issue_bus.ProjectField(
            "1002", "Agent State", {"Agent Ready": "agent-ready", "Assigned": "agent-assigned"}, 1002
        )

        client.set_project_fields(item, ((status, "In Progress"), (agent, "Assigned")))

        command, input_data = calls[0]
        self.assertIn("--method", command)
        self.assertIn("PATCH", command)
        self.assertTrue(any("/items/2001" in part for part in command))
        self.assertIn(issue_bus.GITHUB_API_VERSION, command)
        self.assertEqual(
            json.loads(input_data or "{}"),
            {"fields": [{"id": 1001, "value": "status-progress"}, {"id": 1002, "value": "agent-assigned"}]},
        )

        client.set_project_fields(item, ((status, "Ready"), (agent, "Agent Ready")))
        _, rollback_input = calls[1]
        self.assertEqual(
            json.loads(rollback_input or "{}"),
            {"fields": [{"id": 1001, "value": "status-ready"}, {"id": 1002, "value": "agent-ready"}]},
        )

    def test_claim_reads_and_writes_use_rest_endpoints(self) -> None:
        calls: list[tuple[tuple[str, ...], str | None]] = []

        def runner(*command: str, **kwargs: Any) -> issue_bus.CommandResult:
            calls.append((command, kwargs.get("input_data")))
            rendered = " ".join(command)
            if rendered.endswith(" user"):
                return issue_bus.CommandResult(json.dumps({"login": "codex-user"}), "", 0)
            if "/issues/401/comments?" in rendered:
                return issue_bus.CommandResult(
                    json.dumps(
                        [
                            {
                                "id": 10,
                                "node_id": "IC_10",
                                "created_at": "2026-09-13T00:00:00Z",
                                "body": "pr: https://github.com/valence-works/elsa-control/pull/398",
                            }
                        ]
                    ),
                    "",
                    0,
                )
            if "/issues/401" in rendered and "/timeline" not in rendered:
                return issue_bus.CommandResult(
                    json.dumps(
                        {
                            "number": 401,
                            "state": "open",
                            "labels": [{"name": "ready-for-agent"}],
                            "assignees": [],
                        }
                    ),
                    "",
                    0,
                )
            if "/timeline" in rendered:
                return issue_bus.CommandResult("[]", "", 0)
            if "/pulls/398" in rendered:
                return issue_bus.CommandResult(
                    json.dumps({"number": 398, "state": "open", "html_url": "https://example/pr/398"}),
                    "",
                    0,
                )
            return issue_bus.CommandResult("{}", "", 0)

        client = issue_bus.GhClient("valence-works/elsa-control", runner=runner)
        self.assertEqual(client.identity(), "codex-user")
        issue = client.issue(401)
        self.assertEqual(issue["comments"][0]["createdAt"], "2026-09-13T00:00:00Z")
        linked = client.linked_open_prs(401)
        self.assertEqual([pr["number"] for pr in linked], [398])
        client.assign(401, "codex-user")
        client.unassign(401, "codex-user")
        client.comment(401, "claim: codex starting")
        client.add_label(401, "blocked")
        client.remove_label(401, "blocked")

        rendered_calls = [" ".join(command) for command, _ in calls]
        self.assertTrue(any("repos/valence-works/elsa-control/issues/401" in call for call in rendered_calls))
        self.assertTrue(any("/comments?per_page=100 --paginate --slurp" in call for call in rendered_calls))
        self.assertTrue(any("/pulls/398" in call for call in rendered_calls))
        self.assertTrue(any("/assignees" in call and "POST" in call for call in rendered_calls))
        self.assertTrue(any("/assignees" in call and "DELETE" in call for call in rendered_calls))
        self.assertTrue(any("/labels" in call and "POST" in call for call in rendered_calls))
        self.assertTrue(any("/labels/blocked" in call and "DELETE" in call for call in rendered_calls))
        self.assertTrue(all(issue_bus.GITHUB_API_VERSION in call for call in rendered_calls))


class FakeClient:
    def __init__(self, *, linked: list[dict[str, Any]] | None = None) -> None:
        self.issue_data: dict[str, Any] = {
            "number": 401,
            "state": "OPEN",
            "closed": False,
            "issueType": {"name": "Task"},
            "labels": [{"name": "ready-for-agent"}, {"name": "type:task"}],
            "assignees": [],
            "comments": [],
        }
        self.item = issue_bus.ProjectItem(
            "item-1",
            "project-1",
            401,
            "Ready",
            "Agent Ready",
            {"work item type": "Task"},
        )
        self.fields = {
            "status": issue_bus.ProjectField(
                "status-field",
                "Status",
                {"Ready": "ready", "In Progress": "progress", "Blocked": "blocked"},
            ),
            "agent state": issue_bus.ProjectField(
                "agent-field",
                "Agent State",
                {"Agent Ready": "agent-ready", "Assigned": "assigned", "Not Ready": "not-ready"},
            ),
        }
        self.linked = linked or []
        self.calls: list[tuple[str, Any]] = []
        self.fail_on: set[str] = set()
        self.apply_then_error: set[str] = set()
        self.inject_earlier_claim = False
        self.inject_later_claim = False
        self.link_after_claim = False
        self.link_at_final = False
        self.external_assignee_at_final: str | None = None
        self.external_project_status_at_final: str | None = None
        self.linked_calls = 0
        self.assignee_after_claim: str | None = None
        self.comment_sequence = 0

    def identity(self) -> str:
        self.calls.append(("identity",))
        return "codex-user"

    def issue(self, number: int) -> dict[str, Any]:
        self.calls.append(("issue", number))
        return copy.deepcopy(self.issue_data)

    def project_for_issue(self, number: int) -> issue_bus.ProjectSnapshot:
        self.calls.append(("project", number))
        return issue_bus.ProjectSnapshot(self.item, self.fields)

    def linked_open_prs(self, number: int) -> list[dict[str, Any]]:
        self._maybe_fail("linked")
        self.calls.append(("linked", number))
        self.linked_calls += 1
        if self.link_at_final and self.linked_calls >= 3:
            self.linked = [{"number": 398, "state": "OPEN"}]
            if self.external_assignee_at_final:
                self.issue_data["assignees"] = [{"login": self.external_assignee_at_final}]
            if self.external_project_status_at_final:
                self.item = issue_bus.ProjectItem(
                    self.item.id,
                    self.item.project_id,
                    self.item.issue_number,
                    self.external_project_status_at_final,
                    self.item.agent_state,
                    self.item.raw,
                )
        return copy.deepcopy(self.linked)

    def assign(self, number: int, login: str) -> None:
        self._maybe_fail("assign")
        self.calls.append(("assign", number, login))
        self.issue_data["assignees"] = [{"login": login}]
        self._maybe_fail_after("assign")

    def unassign(self, number: int, login: str) -> None:
        self._maybe_fail("unassign")
        self.calls.append(("unassign", number, login))
        self.issue_data["assignees"] = []

    def comment(self, number: int, body: str) -> dict[str, str]:
        self._maybe_fail("comment")
        self.calls.append(("comment", number, body))
        self.comment_sequence += 1
        comment_id = f"comment-{self.comment_sequence}"
        self.issue_data["comments"].append(
            {
                "id": comment_id,
                "node_id": comment_id,
                "createdAt": f"2026-09-13T00:00:{self.comment_sequence:02d}Z",
                "body": body,
            }
        )
        if self.inject_earlier_claim:
            self.issue_data["comments"].insert(
                0,
                {
                    "id": "earlier-claim",
                    "node_id": "earlier-claim",
                    "createdAt": "2026-09-12T23:59:59Z",
                    "body": "claim: cursor starting",
                },
            )
        if self.link_after_claim:
            self.linked = [{"number": 398, "state": "OPEN"}]
        if self.assignee_after_claim:
            self.issue_data["assignees"] = [{"login": self.assignee_after_claim}]
        if self.inject_later_claim:
            self.issue_data["comments"].append(
                {
                    "id": "later-claim",
                    "node_id": "later-claim",
                    "createdAt": "2026-09-13T00:00:02Z",
                    "body": "claim: claude starting",
                }
            )
        self._maybe_fail_after("comment")
        return {"node_id": comment_id, "id": comment_id, "body": body}

    def add_label(self, number: int, label: str) -> None:
        self._maybe_fail("add_label")
        self.calls.append(("add-label", number, label))
        if not any(entry.get("name") == label for entry in self.issue_data["labels"]):
            self.issue_data["labels"].append({"name": label})

    def remove_label(self, number: int, label: str) -> None:
        self._maybe_fail("remove_label")
        self.calls.append(("remove-label", number, label))
        self.issue_data["labels"] = [entry for entry in self.issue_data["labels"] if entry.get("name") != label]
        self._maybe_fail_after("remove_label")

    def set_project_field(self, item: issue_bus.ProjectItem, field: issue_bus.ProjectField, option_name: str) -> None:
        self._maybe_fail(f"set:{field.name}:{option_name}")
        self.calls.append(("set-field", field.name, option_name))
        if field.name == "Status":
            self.item = issue_bus.ProjectItem(item.id, item.project_id, item.issue_number, option_name, self.item.agent_state, item.raw)
        else:
            self.item = issue_bus.ProjectItem(item.id, item.project_id, item.issue_number, self.item.status, option_name, item.raw)

    def project_items(self) -> list[dict[str, Any]]:
        return [
            {
                "id": self.item.id,
                "content": {"number": self.item.issue_number},
                "labels": [label["name"] for label in self.issue_data["labels"]],
                "status": self.item.status,
                "agent State": self.item.agent_state,
            }
        ]

    def _maybe_fail(self, name: str) -> None:
        if name in self.fail_on:
            raise issue_bus.GhError(f"permission denied for {name}")

    def _maybe_fail_after(self, name: str) -> None:
        if name in self.apply_then_error:
            raise issue_bus.GhError(f"response lost after {name}")


class IssueBusTests(unittest.TestCase):
    def run_bus(self, client: FakeClient) -> tuple[issue_bus.IssueBus, list[str]]:
        output: list[str] = []
        clock = lambda: datetime(2026, 9, 13, 0, 1, tzinfo=timezone.utc)
        return issue_bus.IssueBus(client, output.append, clock=clock), output

    def test_success_applies_all_three_claim_state_mutations(self) -> None:
        client = FakeClient()
        bus, output = self.run_bus(client)

        self.assertTrue(bus.claim(401, "codex"))
        self.assertEqual(client.item.status, "In Progress")
        self.assertEqual(client.item.agent_state, "Assigned")
        self.assertNotIn("ready-for-agent", {label["name"] for label in client.issue_data["labels"]})
        claim = issue_bus.active_claims_from(client.issue_data["comments"])[0]
        self.assertEqual(claim.worker, "codex")
        self.assertEqual(claim.id, "comment-1")
        self.assertEqual(
            [call[2] for call in client.calls if call[0] == "comment"][0],
            "claim: codex starting",
        )
        self.assertTrue(output[-1].startswith("claimed #401"))

    def test_collision_abandons_and_rolls_back_without_project_claim_state(self) -> None:
        client = FakeClient()
        client.inject_earlier_claim = True
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertEqual(client.item.status, "Ready")
        self.assertEqual(client.item.agent_state, "Agent Ready")
        self.assertIn("ready-for-agent", {label["name"] for label in client.issue_data["labels"]})
        self.assertEqual(client.issue_data["assignees"], [])
        self.assertFalse(any(call[0] in {"assign", "unassign"} for call in client.calls))
        self.assertTrue(any("claim-abandoned:" in call[2] for call in client.calls if call[0] == "comment"))
        self.assertIn("rollback completed", output[-1])

    def test_loser_does_not_unassign_login_assigned_during_arbitration(self) -> None:
        client = FakeClient()
        client.inject_earlier_claim = True
        client.assignee_after_claim = "codex-user"
        bus, _ = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertEqual(client.issue_data["assignees"], [{"login": "codex-user"}])
        self.assertFalse(any(call[0] == "unassign" for call in client.calls))

    def test_earliest_winner_ignores_later_claim_without_unassigning(self) -> None:
        client = FakeClient()
        client.inject_later_claim = True
        bus, _ = self.run_bus(client)

        self.assertTrue(bus.claim(401, "codex"))
        self.assertTrue(any(call[0] == "assign" for call in client.calls))
        self.assertFalse(any(call[0] == "unassign" for call in client.calls))

    def test_partial_failure_rolls_back_every_applied_mutation(self) -> None:
        client = FakeClient()
        client.fail_on.add("set:Agent State:Assigned")
        bus, _ = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertEqual(client.item.status, "Ready")
        self.assertEqual(client.item.agent_state, "Agent Ready")
        self.assertEqual(client.issue_data["assignees"], [])
        self.assertIn("ready-for-agent", {label["name"] for label in client.issue_data["labels"]})

    def test_comment_apply_then_error_never_reports_false_rollback_success(self) -> None:
        client = FakeClient()
        client.apply_then_error.add("comment")
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertNotIn("rollback completed", output[-1])
        self.assertIn("rollback failed", output[-1])
        self.assertIn("blocked", {label["name"] for label in client.issue_data["labels"]})
        self.assertEqual(client.item.status, "Blocked")
        self.assertEqual(client.item.agent_state, "Not Ready")

    def test_assignment_apply_then_error_uses_blocked_path_when_unassign_fails(self) -> None:
        client = FakeClient()
        client.apply_then_error.add("assign")
        client.fail_on.add("unassign")
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertNotIn("rollback completed", output[-1])
        self.assertIn("rollback failed", output[-1])
        self.assertEqual(client.issue_data["assignees"], [{"login": "codex-user"}])
        self.assertIn("blocked", {label["name"] for label in client.issue_data["labels"]})
        self.assertEqual(client.item.status, "Blocked")
        self.assertEqual(client.item.agent_state, "Not Ready")

    def test_label_apply_then_error_uses_blocked_path_when_restore_fails(self) -> None:
        client = FakeClient()
        client.apply_then_error.add("remove_label")
        client.fail_on.add("add_label")
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertNotIn("rollback completed", output[-1])
        self.assertIn("rollback failed", output[-1])
        self.assertNotIn("ready-for-agent", {label["name"] for label in client.issue_data["labels"]})
        self.assertEqual(client.item.status, "Blocked")
        self.assertEqual(client.item.agent_state, "Not Ready")

    def test_rollback_failure_reports_incomplete_blocked_path(self) -> None:
        client = FakeClient()
        client.fail_on.update({"set:Agent State:Assigned", "add_label"})
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertIn("rollback failed", output[-1])
        self.assertIn("canonical blocked path incomplete", output[-1])

    def test_preflight_permission_failure_fails_closed_without_mutation(self) -> None:
        client = FakeClient()
        client.fail_on.add("linked")
        bus, _ = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertFalse(any(call[0] in {"assign", "comment", "remove-label", "set-field"} for call in client.calls))

    def test_preflight_rejects_conflicting_lane_and_open_linked_pr(self) -> None:
        client = FakeClient(linked=[{"number": 398, "state": "OPEN"}])
        client.issue_data["labels"].append({"name": "worker:cursor"})
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertTrue(any("linked open pull request" in line for line in output))
        self.assertTrue(any("conflicting worker lane" in line for line in output))

    def test_revalidates_non_claim_invariants_before_assignment(self) -> None:
        client = FakeClient()
        client.link_after_claim = True
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertFalse(any(call[0] in {"assign", "unassign"} for call in client.calls))
        self.assertIn("pre-assignment invariants changed", output[-1])

    def test_final_verification_rejects_link_appearing_after_mutations(self) -> None:
        client = FakeClient()
        client.link_at_final = True
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertIn("rollback completed", output[-1])
        self.assertEqual(client.item.status, "Ready")
        self.assertEqual(client.item.agent_state, "Agent Ready")
        self.assertIn("ready-for-agent", {label["name"] for label in client.issue_data["labels"]})
        self.assertEqual(client.issue_data["assignees"], [])

    def test_rollback_does_not_unassign_newer_external_owner(self) -> None:
        client = FakeClient()
        client.link_at_final = True
        client.external_assignee_at_final = "other-worker"
        bus, output = self.run_bus(client)

        self.assertFalse(bus.claim(401, "codex"))
        self.assertIn("rollback failed", output[-1])
        self.assertEqual(client.issue_data["assignees"], [{"login": "other-worker"}])
        self.assertFalse(any(call[0] == "unassign" for call in client.calls))
        self.assertEqual(client.item.status, "Blocked")
        self.assertEqual(client.item.agent_state, "Not Ready")

    def test_drift_reports_known_contradictions(self) -> None:
        client = FakeClient(linked=[{"number": 398, "state": "OPEN"}])
        client.item = issue_bus.ProjectItem("item-1", "project-1", 401, "In Progress", "Agent Ready", client.item.raw)
        bus, _ = self.run_bus(client)

        results = bus.drift(401)
        codes = {finding["code"] for finding in results[0]["findings"]}
        self.assertIn("ready-in-progress", codes)
        self.assertIn("ready-linked-open-pr", codes)
        self.assertIn("in-progress-agent-ready", codes)

    def test_drift_all_aggregates_result_objects_and_avoids_issue_reads(self) -> None:
        client = FakeClient(linked=[{"number": 398, "state": "OPEN"}])
        client.item = issue_bus.ProjectItem("item-1", "project-1", 401, "In Progress", "Agent Ready", client.item.raw)
        bus, _ = self.run_bus(client)

        results = bus.drift(all_items=True)

        self.assertEqual(len(results), 1)
        self.assertIsInstance(results[0], dict)
        self.assertIn("findings", results[0])
        self.assertIn("ready-in-progress", {finding["code"] for finding in results[0]["findings"]})
        self.assertNotIn(("issue", 401), client.calls)

    def test_claim_parser_uses_earliest_active_claim_and_abandonment(self) -> None:
        comments = [
            {"id": "later", "createdAt": "2026-09-13T00:00:02Z", "body": "claim: codex starting"},
            {"id": "early", "createdAt": "2026-09-13T00:00:01Z", "body": "claim: claude starting"},
            {"id": "abandon", "createdAt": "2026-09-13T00:00:03Z", "body": "claim-abandoned: early"},
        ]
        active = issue_bus.active_claims_from(comments)
        self.assertEqual([claim.id for claim in active], ["later"])

    def test_claim_parser_uses_rest_comment_node_id_as_lease_identity(self) -> None:
        comments = [
            {
                "id": 12345,
                "node_id": "IC_kwDOlease",
                "created_at": "2026-09-13T00:00:01Z",
                "body": "claim: codex starting",
            }
        ]

        active = issue_bus.active_claims_from(comments)

        self.assertEqual([claim.id for claim in active], ["IC_kwDOlease"])

    def test_claim_expires_only_after_fifteen_minutes_in_fully_ready_state(self) -> None:
        client = FakeClient()
        client.issue_data["comments"] = [
            {
                "id": "expired",
                "createdAt": "2026-09-12T23:45:00Z",
                "body": "claim: claude starting",
            }
        ]
        bus, _ = self.run_bus(client)

        preflight = bus.preflight(401, "codex")

        self.assertNotIn("active claim exists (expired)", preflight.failures)

    def test_missing_or_malformed_claim_timestamp_remains_active(self) -> None:
        for created_at in (None, "not-a-timestamp"):
            with self.subTest(created_at=created_at):
                client = FakeClient()
                comment = {"id": "unreadable", "body": "claim: claude starting"}
                if created_at is not None:
                    comment["createdAt"] = created_at
                client.issue_data["comments"] = [comment]
                bus, _ = self.run_bus(client)

                preflight = bus.preflight(401, "codex")

                self.assertIn("active claim exists (unreadable)", preflight.failures)

    def test_expiry_does_not_apply_after_claim_state_leaves_ready(self) -> None:
        client = FakeClient()
        client.issue_data["comments"] = [
            {
                "id": "old-active",
                "createdAt": "2026-09-12T23:00:00Z",
                "body": "claim: claude starting",
            }
        ]
        client.item = issue_bus.ProjectItem("item-1", "project-1", 401, "In Progress", "Assigned", client.item.raw)
        bus, _ = self.run_bus(client)

        preflight = bus.preflight(401, "codex")

        self.assertIn("active claim exists (old-active)", preflight.failures)


if __name__ == "__main__":
    unittest.main()
