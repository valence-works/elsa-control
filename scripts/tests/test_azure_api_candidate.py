#!/usr/bin/env python3
"""Offline tests for the immutable Azure API candidate authority helper."""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts" / "validate-azure-api-candidate.sh"
SOURCE_REPOSITORY = "valence-works/elsa-control"
IMAGE_REPOSITORY = "acr.azurecr.io/elsa-control/api"
SOURCE_SHA = "a" * 40
DIGEST = f"sha256:{'b' * 64}"


class AzureApiCandidateTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.temp = Path(temporary.name)
        self.bin = self.temp / "bin"
        self.bin.mkdir()
        self.descriptor = self.temp / "candidate.json"
        self.output = self.temp / "github-output"
        self.write_descriptor()
        self.write_fake_commands()

    def write_descriptor(self, **overrides: object) -> None:
        descriptor = {
            "artifactSchemaVersion": 1,
            "repository": IMAGE_REPOSITORY,
            "digest": DIGEST,
            "sourceRepository": SOURCE_REPOSITORY,
            "sourceSha": SOURCE_SHA,
            "buildRunId": "123",
            "buildRunNumber": "77",
        }
        descriptor.update(overrides)
        self.descriptor.write_text(json.dumps(descriptor))

    def write_fake_commands(self) -> None:
        (self.bin / "gh").write_text(
            """#!/usr/bin/env bash
set -euo pipefail
[[ "$*" != *--silent* ]] || exit 91
printf '%s' "${FAKE_RUN_JSON:?}"
"""
        )
        (self.bin / "git").write_text(
            """#!/usr/bin/env bash
set -euo pipefail
case "$1 ${2:-}" in
  'rev-parse --is-inside-work-tree') printf '%s\\n' true ;;
  'rev-parse --is-shallow-repository') printf '%s\\n' "${FAKE_SHALLOW:-false}" ;;
  fetch*) ;;
  'cat-file -e') [[ "${FAKE_COMMIT:-true}" == true ]] ;;
  'merge-base --is-ancestor') [[ "${FAKE_ANCESTOR:-true}" == true ]] ;;
  *) exit 90 ;;
esac
"""
        )
        for command in (self.bin / "gh", self.bin / "git"):
            command.chmod(0o755)

    def run_helper(self, **overrides: str) -> subprocess.CompletedProcess[str]:
        run = {
            "id": 123,
            "name": "Azure Control API Deploy",
            "path": ".github/workflows/azure-api-deploy.yml",
            "repository": {"full_name": SOURCE_REPOSITORY},
            "head_repository": {"full_name": SOURCE_REPOSITORY},
            "status": "completed",
            "conclusion": "success",
            "event": "workflow_dispatch",
            "head_branch": "main",
            "head_sha": SOURCE_SHA,
            "run_number": 77,
        }
        environment = os.environ.copy()
        environment.update(
            PATH=f"{self.bin}:{environment['PATH']}",
            FAKE_RUN_JSON=json.dumps(run),
            GITHUB_REF="refs/heads/main",
            TARGET_ENVIRONMENT="production",
        )
        environment.update(overrides)
        return subprocess.run(
            [
                str(HELPER),
                str(self.descriptor),
                "123",
                DIGEST,
                IMAGE_REPOSITORY,
                SOURCE_REPOSITORY,
                str(self.output),
            ],
            env=environment,
            capture_output=True,
            text=True,
            check=False,
            timeout=10,
        )

    def test_valid_descriptor_requires_successful_same_repository_run_and_main_ancestor(self) -> None:
        result = self.run_helper()

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            [
                f"candidate_repository={IMAGE_REPOSITORY}",
                f"candidate_digest={DIGEST}",
                f"candidate_image={IMAGE_REPOSITORY}@{DIGEST}",
                f"candidate_source_sha={SOURCE_SHA}",
                "candidate_build_run_id=123",
                "candidate_build_number=77",
            ],
            self.output.read_text().splitlines(),
        )
        self.assertEqual("", result.stdout)

    def test_digest_mismatch_fails_before_workflow_run_verification(self) -> None:
        result = subprocess.run(
            [
                str(HELPER),
                str(self.descriptor),
                "123",
                f"sha256:{'c' * 64}",
                IMAGE_REPOSITORY,
                SOURCE_REPOSITORY,
                str(self.output),
            ],
            env={
                **os.environ,
                "PATH": f"{self.bin}:{os.environ['PATH']}",
                "GITHUB_REF": "refs/heads/main",
                "TARGET_ENVIRONMENT": "production",
            },
            capture_output=True,
            text=True,
            check=False,
            timeout=10,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("does not match", result.stderr)
        self.assertFalse(self.output.exists())

    def test_failed_workflow_run_is_rejected(self) -> None:
        result = self.run_helper(
            FAKE_RUN_JSON=json.dumps(
                {
                    "id": 123,
                    "name": "Azure Control API Deploy",
                    "path": ".github/workflows/azure-api-deploy.yml",
                    "repository": {"full_name": SOURCE_REPOSITORY},
                    "head_repository": {"full_name": SOURCE_REPOSITORY},
                    "status": "completed",
                    "conclusion": "failure",
                    "event": "workflow_dispatch",
                    "head_branch": "main",
                    "head_sha": SOURCE_SHA,
                    "run_number": 77,
                }
            )
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("trusted successful build", result.stderr)
        self.assertFalse(self.output.exists())

    def test_foreign_workflow_run_is_rejected(self) -> None:
        result = self.run_helper(
            FAKE_RUN_JSON=json.dumps(
                {
                    "id": 123,
                    "name": "Azure Control API Deploy",
                    "path": ".github/workflows/azure-api-deploy.yml",
                    "repository": {"full_name": SOURCE_REPOSITORY},
                    "head_repository": {"full_name": "untrusted/elsa-control"},
                    "status": "completed",
                    "conclusion": "success",
                    "event": "workflow_dispatch",
                    "head_branch": "main",
                    "head_sha": SOURCE_SHA,
                    "run_number": 77,
                }
            )
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("trusted successful build", result.stderr)
        self.assertFalse(self.output.exists())

    def test_workflow_run_from_a_non_main_ref_is_rejected(self) -> None:
        result = self.run_helper(
            FAKE_RUN_JSON=json.dumps(
                {
                    "id": 123,
                    "name": "Azure Control API Deploy",
                    "path": ".github/workflows/azure-api-deploy.yml",
                    "repository": {"full_name": SOURCE_REPOSITORY},
                    "head_repository": {"full_name": SOURCE_REPOSITORY},
                    "status": "completed",
                    "conclusion": "success",
                    "event": "workflow_dispatch",
                    "head_branch": "release/preview",
                    "head_sha": SOURCE_SHA,
                    "run_number": 77,
                }
            )
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("trusted successful build", result.stderr)
        self.assertFalse(self.output.exists())

    def test_staging_candidate_requires_the_exact_staging_branch_and_environment(self) -> None:
        run = {
            "id": 123,
            "name": "Azure Control API Deploy",
            "path": ".github/workflows/azure-api-deploy.yml",
            "repository": {"full_name": SOURCE_REPOSITORY},
            "head_repository": {"full_name": SOURCE_REPOSITORY},
            "status": "completed",
            "conclusion": "success",
            "event": "workflow_dispatch",
            "head_branch": "candidate/staging",
            "head_sha": SOURCE_SHA,
            "run_number": 77,
        }
        staging = self.run_helper(
            GITHUB_REF="refs/heads/candidate/staging",
            TARGET_ENVIRONMENT="test",
            FAKE_RUN_JSON=json.dumps(run),
        )
        self.assertEqual(0, staging.returncode, staging.stderr)

        self.output.unlink()
        production = self.run_helper(
            GITHUB_REF="refs/heads/candidate/staging",
            TARGET_ENVIRONMENT="production",
            FAKE_RUN_JSON=json.dumps(run),
        )
        self.assertNotEqual(0, production.returncode)
        self.assertFalse(self.output.exists())

    def test_source_that_is_not_an_ancestor_of_main_is_rejected(self) -> None:
        result = self.run_helper(FAKE_ANCESTOR="false")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("not an ancestor", result.stderr)
        self.assertFalse(self.output.exists())

    def test_unknown_descriptor_fields_are_rejected(self) -> None:
        self.write_descriptor(unexpected="value")

        result = self.run_helper()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("schema is invalid", result.stderr)
        self.assertFalse(self.output.exists())

    def test_missing_json_validator_fails_with_a_safe_diagnostic(self) -> None:
        for command in ("bash", "mktemp", "wc", "rm"):
            target = Path("/bin") / command
            if not target.exists():
                target = Path("/usr/bin") / command
            (self.bin / command).symlink_to(target)
        environment = {
            "PATH": str(self.bin),
            "FAKE_RUN_JSON": json.dumps({}),
        }
        result = subprocess.run(
            [
                str(HELPER),
                str(self.descriptor),
                "123",
                DIGEST,
                IMAGE_REPOSITORY,
                SOURCE_REPOSITORY,
                str(self.output),
            ],
            env=environment,
            capture_output=True,
            text=True,
            check=False,
            timeout=10,
        )

        self.assertNotEqual(0, result.returncode)
        self.assertIn("JSON validator is unavailable", result.stderr)
        self.assertFalse(self.output.exists())


if __name__ == "__main__":
    unittest.main()
