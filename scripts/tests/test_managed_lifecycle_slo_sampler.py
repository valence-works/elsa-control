#!/usr/bin/env python3
"""Offline tests for the bounded, value-free managed lifecycle SLO sampler."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SAMPLER = ROOT / "scripts" / "managed-lifecycle-slo-sampler.py"
SECRET_TOKEN = "sampler-test-token-should-never-appear"
WORKSPACE_ID = "11111111-2222-3333-4444-555555555555"
COMPLETED_SIGNAL = "managed_lifecycle.operations.completed"
HEALTH_SIGNAL = "managed_lifecycle.endpoint.health.evaluations"


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
                if status == 0:  # scripted stall: exceed the sampler's per-request timeout
                    time.sleep(body and float(body) or 2.0)
                    status, body = 200, '{"status":"ok"}'
                if self.path == "/control":
                    fixture.authorization.append(self.headers.get("Authorization"))
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                if 300 <= status < 400:
                    self.send_header("Location", fixture.url("/runtime"))
                self.end_headers()
                self.wfile.write(body.encode("utf-8"))

            def log_message(self, *_: object) -> None:
                return

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self) -> None:
        self.server.shutdown()
        self.server.server_close()

    def url(self, path: str) -> str:
        return f"http://127.0.0.1:{self.server.server_port}{path}"


class SamplerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.fixture = FixtureServer()
        self.addCleanup(self.fixture.close)

    def run_sampler(self, *arguments: str, runtime_url: str | None = None, env: dict[str, str] | None = None, duration: str = "0.2", minimum: str = "0.1") -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                str(SAMPLER), "--runtime-health-url", runtime_url or self.fixture.url("/runtime"), *arguments,
                "--interval-seconds", "0.05", "--duration-seconds", duration, "--minimum-window-seconds", minimum, "--timeout-seconds", "0.5",
                "--allow-short-window", "--allow-insecure-http",
            ],
            capture_output=True,
            text=True,
            env={**os.environ, **(env or {})},
            check=False,
            timeout=30,
        )

    def parse(self, completed: subprocess.CompletedProcess[str]) -> dict[str, object]:
        lines = [line for line in completed.stdout.splitlines() if line.strip()]
        self.assertEqual(1, len(lines), completed.stdout)
        return json.loads(lines[0])

    def test_healthy_window_counts_fresh_runtime_samples(self) -> None:
        completed = self.run_sampler()
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

    def test_unhealthy_unreachable_timed_out_and_redirected_samples_are_never_healthy(self) -> None:
        self.fixture.responses["/runtime"] = [(503, '{"status":"degraded"}'), (0, "1.5"), (302, "")]
        completed = self.run_sampler()
        unreachable = self.run_sampler(runtime_url=f"http://127.0.0.1:{self.fixture.server.server_port + 1}/runtime")
        result = self.parse(completed)
        self.assertEqual(1, completed.returncode)
        self.assertEqual({"total": 4, "healthy": 1, "unhealthy": 2, "unknown": 1}, result["runtime"])
        self.assertTrue(result["complete"])
        self.assertFalse(result["healthyWindow"])
        offline = self.parse(unreachable)
        self.assertEqual(1, unreachable.returncode)
        self.assertEqual({"total": 4, "healthy": 0, "unhealthy": 0, "unknown": 4}, offline["runtime"])
        self.assertEqual("", unreachable.stderr)

    def test_window_shorter_than_the_minimum_is_incomplete_and_the_floor_is_enforced(self) -> None:
        short = self.run_sampler(duration="0.1", minimum="0.5")
        result = self.parse(short)
        self.assertEqual(1, short.returncode)
        self.assertEqual(2, result["plannedSamples"])
        self.assertFalse(result["complete"])
        self.assertFalse(result["healthyWindow"])
        floor = subprocess.run(
            [str(SAMPLER), "--runtime-health-url", "https://runtime.example.test/health", "--minimum-window-seconds", "299", "--duration-seconds", "300"],
            capture_output=True, text=True, check=False, timeout=30,
        )
        self.assertEqual(2, floor.returncode)
        self.assertEqual("", floor.stdout + floor.stderr)

    def test_control_health_uses_bearer_token_and_classifies_fixed_statuses(self) -> None:
        self.fixture.responses["/control"] = [
            (200, '{"status":"Healthy"}'),
            (200, '{"status":"RecoveryRequired"}'),
            (200, '{"status":"Surprise"}'),
            (404, ""),
        ]
        completed = self.run_sampler("--control-health-url", self.fixture.url("/control"), env={"ELSA_CONTROL_SAMPLER_TOKEN": SECRET_TOKEN})
        self.assertEqual([f"Bearer {SECRET_TOKEN}"] * 4, self.fixture.authorization)
        result = self.parse(completed)
        control = result["control"]
        self.assertEqual(4, control["total"])
        self.assertEqual(1, control["healthy"])
        self.assertEqual(1, control["recovery_required"])
        self.assertEqual(1, control["other"])
        self.assertEqual(1, control["unhealthy_response"])
        self.assertFalse(result["healthyWindow"])
        self.assertNotIn(SECRET_TOKEN, completed.stdout + completed.stderr)

    def test_invalid_input_is_rejected_silently(self) -> None:
        cases = {
            "missing token": (("--control-health-url", self.fixture.url("/control")), {"ELSA_CONTROL_SAMPLER_TOKEN": ""}),
            "unsafe signal name": (("--sink-workspace-id", WORKSPACE_ID, "--sink-signal", "requests; drop table"), {}),
            "signal without workspace": (("--sink-signal", COMPLETED_SIGNAL), {}),
            "non-numeric timeout": (("--timeout-seconds", "soon"), {}),
            "unknown option echoing a host": (("--runtime-healthurl", "https://typo-host.example.test/x"), {}),
        }
        for name, (arguments, env) in cases.items():
            with self.subTest(case=name):
                completed = self.run_sampler(*arguments, env=env)
                self.assertEqual(2, completed.returncode)
                self.assertEqual("", completed.stdout)
                self.assertEqual("", completed.stderr)
        insecure = subprocess.run(
            [str(SAMPLER), "--runtime-health-url", "https://runtime.example.test/health", "--control-health-url", "http://control.example.test/health", "--allow-short-window", "--minimum-window-seconds", "1", "--duration-seconds", "1", "--interval-seconds", "1"],
            capture_output=True, text=True, env={**os.environ, "ELSA_CONTROL_SAMPLER_TOKEN": SECRET_TOKEN}, check=False, timeout=30,
        )
        self.assertEqual(2, insecure.returncode)
        self.assertEqual("", insecure.stdout + insecure.stderr)

    def test_sink_counts_come_from_the_query_boundary_and_failures_mark_incomplete(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            fake_az = Path(temp_dir) / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "printf '%s\\n' \"$*\" >> \"${AZ_CALL_LOG:?}\"\n"
                "if [ \"${AZ_FAIL:-}\" = 1 ]; then exit 1; fi\n"
                f"printf '%s' '[{{\"Name\":\"{COMPLETED_SIGNAL}\",\"rows\":7}},{{\"Name\":\"unrelated\",\"rows\":99}}]'\n"
            )
            fake_az.chmod(0o755)
            log = Path(temp_dir) / "calls"
            arguments = ("--sink-workspace-id", WORKSPACE_ID, "--sink-signal", COMPLETED_SIGNAL, "--sink-signal", HEALTH_SIGNAL, "--az-bin", str(fake_az))
            completed = self.run_sampler(*arguments, env={"AZ_CALL_LOG": str(log)})
            failed = self.run_sampler(*arguments, env={"AZ_CALL_LOG": str(log), "AZ_FAIL": "1"})
            call = log.read_text().splitlines()[0]
        result = self.parse(completed)
        self.assertEqual(0, completed.returncode, completed.stderr)
        self.assertEqual({"queried": True, "signals": {COMPLETED_SIGNAL: 7, HEALTH_SIGNAL: 0}}, result["sink"])
        self.assertIn("monitor log-analytics query", call)
        self.assertIn(WORKSPACE_ID, call)
        self.assertRegex(call, r"between \(datetime\(20[0-9]{2}-[0-9]{2}-[0-9]{2}T[0-9:]{8}Z\) \.\. datetime\(")
        failed_result = self.parse(failed)
        self.assertEqual(1, failed.returncode)
        self.assertEqual({"queried": False, "signals": None}, failed_result["sink"])
        self.assertFalse(failed_result["complete"])
        self.assertFalse(failed_result["healthyWindow"])


if __name__ == "__main__":
    unittest.main()
