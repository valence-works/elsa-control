#!/usr/bin/env python3
"""Offline contract and fake-Docker checks for the API image smoke gate."""

from __future__ import annotations

import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "validate-api-provider-image.sh"
CONTAINER_ID = "a" * 64


class ValidateApiProviderImageTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.temp = Path(temporary.name)
        self.log = self.temp / "docker.log"
        fake = self.temp / "docker"
        fake.write_text("""#!/usr/bin/env bash
set -euo pipefail
printf '%s\\n' "$*" >> "${FAKE_DOCKER_LOG:?}"
case "$1" in
  create)
    [[ "${Authentication__ApiKey:-}" =~ ^[0-9a-f]{64}$ ]] || exit 90
    printf '%s\\n' "${FAKE_ID:?}" ;;
  start) ;;
  inspect)
    case "$3" in
      *ExitCode*) printf '%s\\n' "${FAKE_EXIT_CODE:-0}" ;;
      *) printf '%s\\n' "${FAKE_STATE:-running}" ;;
    esac
    echo 'untrusted raw container detail' >&2 ;;
  exec) printf '%s\\n' '{"status":"ok"}' ;;
  stop) printf '%s\\n' "$4" ;;
  logs)
    echo 'untrusted raw container detail'
    printf '%s\\n' "${FAKE_SHUTDOWN_LOG-Application is shutting down...}" ;;
  rm) exit "${FAKE_CLEANUP_EXIT:-0}" ;;
  *) exit 91 ;;
esac
""")
        fake.chmod(0o755)

    def run_smoke(self, **overrides: str) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        environment.update(PATH=f"{self.temp}:{environment['PATH']}",
                           FAKE_DOCKER_LOG=str(self.log), FAKE_ID=CONTAINER_ID)
        environment.update(overrides)
        return subprocess.run(["bash", str(SCRIPT), "api-image:test"], env=environment,
                              capture_output=True, text=True, check=False, timeout=10)

    def test_script_uses_a_private_bounded_container_probe(self) -> None:
        source = SCRIPT.read_text()
        for required in ("--network none", "ASPNETCORE_ENVIRONMENT=Production",
                         "ConnectionStrings__Catalog=Data Source=/tmp/elsa-image-smoke.db",
                         "DataProtection__KeysPath=/tmp/elsa-image-smoke-keys",
                         "--env Authentication__ApiKey", "docker exec",
                         "curl --fail --silent --max-time 2", "deadline=$((SECONDS + 60))",
                         'docker stop --time 30 "$container_id"', "Application is shutting down...",
                         'docker rm --force "$container_id"'):
            self.assertIn(required, source)
        for forbidden in ("--publish", "--volume"):
            self.assertNotIn(forbidden, source)

    def test_success_keeps_key_out_of_arguments_and_cleans_exact_container(self) -> None:
        result = self.run_smoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("API provider image smoke check passed.\n", result.stdout)
        self.assertNotRegex(result.stdout + result.stderr, r"[0-9a-f]{64}")
        calls = self.log.read_text().splitlines()
        self.assertIn("--env Authentication__ApiKey api-image:test", calls[0])
        self.assertNotRegex(calls[0], r"Authentication__ApiKey=")
        self.assertEqual(f"rm --force {CONTAINER_ID}", calls[-1])

    def test_success_requires_a_graceful_stop_without_emitting_container_output(self) -> None:
        result = self.run_smoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn("untrusted raw container detail", result.stdout + result.stderr)
        calls = self.log.read_text().splitlines()
        self.assertEqual([f"stop --time 30 {CONTAINER_ID}", f"inspect --format {{{{.State.ExitCode}}}} {CONTAINER_ID}",
                          f"logs {CONTAINER_ID}"], calls[-4:-1])

    def test_signal_death_or_missing_shutdown_log_fails_and_still_cleans(self) -> None:
        cases = {
            "killed by SIGTERM": ({"FAKE_EXIT_CODE": "143"}, "did not exit 0 after SIGTERM"),
            "killed at stop timeout": ({"FAKE_EXIT_CODE": "137"}, "did not exit 0 after SIGTERM"),
            "no shutdown sequence": ({"FAKE_SHUTDOWN_LOG": ""}, "did not log the graceful shutdown sequence"),
        }
        for name, (overrides, message) in cases.items():
            with self.subTest(case=name):
                self.log.unlink(missing_ok=True)
                result = self.run_smoke(**overrides)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(message, result.stderr)
                self.assertNotIn("untrusted raw container detail", result.stdout + result.stderr)
                self.assertEqual(f"rm --force {CONTAINER_ID}", self.log.read_text().splitlines()[-1])

    def test_crash_fails_without_emitting_container_output_and_still_cleans(self) -> None:
        result = self.run_smoke(FAKE_STATE="exited")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("container exited before /health became ready", result.stderr)
        self.assertNotIn("untrusted raw container detail", result.stdout + result.stderr)
        self.assertEqual(f"rm --force {CONTAINER_ID}", self.log.read_text().splitlines()[-1])

    def test_untrusted_container_identifier_cannot_select_cleanup_targets(self) -> None:
        result = self.run_smoke(FAKE_ID="--all")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("invalid identifier", result.stderr)
        self.assertEqual(1, len(self.log.read_text().splitlines()))

    def test_cleanup_failure_fails_the_gate(self) -> None:
        result = self.run_smoke(FAKE_CLEANUP_EXIT="1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("cleanup failed", result.stderr)


if __name__ == "__main__":
    unittest.main()
