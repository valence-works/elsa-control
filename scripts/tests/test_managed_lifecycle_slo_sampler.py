#!/usr/bin/env python3
"""Offline tests for the bounded, value-free managed lifecycle SLO sampler."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SAMPLER = ROOT / "scripts" / "managed-lifecycle-slo-sampler.py"
SECRET_TOKEN = "sampler-test-token-should-never-appear"


class FixtureServer:
    """Serves /runtime and /control with per-path scripted responses; records Authorization headers."""

    def __init__(self) -> None:
        self.responses: dict[str, list[tuple[int, str]]] = {"/runtime": [], "/control": []}
        self.authorization: list[str | None] = []
        fixture = self

        class Handler(BaseHTTPRequestHandler):
            def do_GET(self) -> None:  # noqa: N802 - http.server contract
                queue = fixture.responses.get(self.path, [])
                status, body = queue.pop(0) if queue else (200, '{"status":"ok"}')
                if self.path == "/control":
                    fixture.authorization.append(self.headers.get("Authorization"))
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.end_headers()
                self.wfile.write(body.encode("utf-8"))

            def log_message(self, *_: object) -> None:
                return

        self.server = HTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    def __enter__(self) -> "FixtureServer":
        self.thread.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.server.shutdown()
        self.server.server_close()

    def url(self, path: str) -> str:
        return f"http://127.0.0.1:{self.server.server_port}{path}"


class SamplerTests(unittest.TestCase):
    def run_sampler(self, *arguments: str, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
        environment = {**os.environ, **(env or {})}
        return subprocess.run(
            [str(SAMPLER), *arguments, "--interval-seconds", "0.05", "--duration-seconds", "0.2", "--minimum-window-seconds", "0.1", "--timeout-seconds", "1"],
            capture_output=True,
            text=True,
            env=environment,
            check=False,
            timeout=30,
        )

    def parse(self, completed: subprocess.CompletedProcess[str]) -> dict[str, object]:
        lines = [line for line in completed.stdout.splitlines() if line.strip()]
        self.assertEqual(1, len(lines), completed.stdout)
        return json.loads(lines[0])

    def test_healthy_window_counts_fresh_runtime_samples(self) -> None:
        with FixtureServer() as fixture:
            completed = self.run_sampler("--runtime-health-url", fixture.url("/runtime"))
        result = self.parse(completed)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual("elsa-control.slo-sample/v1", result["schema"])
        self.assertEqual(4, result["plannedSamples"])
        self.assertEqual({"total": 4, "healthy": 4, "unhealthy": 0, "unknown": 0}, result["runtime"])
        self.assertTrue(result["complete"])
        self.assertTrue(result["healthyWindow"])
        self.assertIsNone(result["control"])
        self.assertIsNone(result["sink"])
        for key in ("windowStartUtc", "windowEndUtc"):
            self.assertRegex(result[key], r"^20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")
        self.assertNotIn("127.0.0.1", completed.stdout)

    def test_unhealthy_and_unreachable_samples_are_never_healthy(self) -> None:
        with FixtureServer() as fixture:
            fixture.responses["/runtime"] = [(503, '{"status":"degraded"}')]
            completed = self.run_sampler("--runtime-health-url", fixture.url("/runtime"))
            unreachable = self.run_sampler("--runtime-health-url", f"http://127.0.0.1:{fixture.server.server_port + 1}/runtime")
        result = self.parse(completed)
        self.assertEqual(1, completed.returncode)
        self.assertEqual({"total": 4, "healthy": 3, "unhealthy": 1, "unknown": 0}, result["runtime"])
        self.assertTrue(result["complete"])
        self.assertFalse(result["healthyWindow"])
        offline = self.parse(unreachable)
        self.assertEqual(1, unreachable.returncode)
        self.assertEqual(4, offline["runtime"]["unknown"])
        self.assertEqual(0, offline["runtime"]["healthy"])
        self.assertEqual("", unreachable.stderr)

    def test_control_health_uses_bearer_token_and_classifies_fixed_statuses(self) -> None:
        with FixtureServer() as fixture:
            fixture.responses["/control"] = [
                (200, '{"status":"Healthy"}'),
                (200, '{"status":"RecoveryRequired"}'),
                (200, '{"status":"Surprise"}'),
                (404, ""),
            ]
            completed = self.run_sampler(
                "--runtime-health-url", fixture.url("/runtime"),
                "--control-health-url", fixture.url("/control"),
                env={"ELSA_CONTROL_SAMPLER_TOKEN": SECRET_TOKEN},
            )
            self.assertEqual([f"Bearer {SECRET_TOKEN}"] * 4, fixture.authorization)
        result = self.parse(completed)
        control = result["control"]
        self.assertEqual(4, control["total"])
        self.assertEqual(1, control["healthy"])
        self.assertEqual(1, control["recovery_required"])
        self.assertEqual(1, control["other"])
        self.assertEqual(1, control["unhealthy_response"])
        self.assertFalse(result["healthyWindow"])
        self.assertNotIn(SECRET_TOKEN, completed.stdout + completed.stderr)

    def test_control_health_requires_token_and_arguments_are_validated(self) -> None:
        with FixtureServer() as fixture:
            missing_token = self.run_sampler("--runtime-health-url", fixture.url("/runtime"), "--control-health-url", fixture.url("/control"), env={"ELSA_CONTROL_SAMPLER_TOKEN": ""})
            bad_signal = self.run_sampler("--runtime-health-url", fixture.url("/runtime"), "--sink-workspace-id", "11111111-2222-3333-4444-555555555555", "--sink-signal", "requests; drop table")
            no_workspace = self.run_sampler("--runtime-health-url", fixture.url("/runtime"), "--sink-signal", "managed_lifecycle.operations.completed")
        for completed in (missing_token, bad_signal, no_workspace):
            self.assertEqual(2, completed.returncode)
            self.assertEqual("", completed.stdout)

    def test_sink_counts_come_from_the_query_boundary_and_failures_mark_incomplete(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir, FixtureServer() as fixture:
            fake_az = Path(temp_dir) / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
                "if [ \"${AZ_FAIL:-}\" = 1 ]; then exit 1; fi\n"
                "printf '%s' '[{\"Name\":\"managed_lifecycle.operations.completed\",\"rows\":7},{\"Name\":\"unrelated\",\"rows\":99}]'\n"
            )
            fake_az.chmod(0o755)
            log = Path(temp_dir) / "calls"
            arguments = (
                "--runtime-health-url", fixture.url("/runtime"),
                "--sink-workspace-id", "11111111-2222-3333-4444-555555555555",
                "--sink-signal", "managed_lifecycle.operations.completed",
                "--sink-signal", "managed_lifecycle.endpoint.health.evaluations",
                "--az-bin", str(fake_az),
            )
            completed = self.run_sampler(*arguments, env={"AZ_CALL_LOG": str(log)})
            failed = self.run_sampler(*arguments, env={"AZ_CALL_LOG": str(log), "AZ_FAIL": "1"})
            call = log.read_text().splitlines()[0]
        result = self.parse(completed)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual({"queried": True, "signals": {"managed_lifecycle.operations.completed": 7, "managed_lifecycle.endpoint.health.evaluations": 0}}, result["sink"])
        self.assertIn("monitor log-analytics query", call)
        self.assertIn("11111111-2222-3333-4444-555555555555", call)
        self.assertRegex(call, r"between \(datetime\(20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z\) \.\. datetime\(")
        failed_result = self.parse(failed)
        self.assertEqual(1, failed.returncode)
        self.assertEqual({"queried": False, "signals": None}, failed_result["sink"])
        self.assertFalse(failed_result["complete"])
        self.assertFalse(failed_result["healthyWindow"])


if __name__ == "__main__":
    unittest.main()
