#!/usr/bin/env python3
"""Offline contract checks for the required CI workflow topology."""

from __future__ import annotations

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"


def job_block(source: str, job_id: str) -> str:
    """Return one top-level job block without requiring PyYAML."""

    match = re.search(
        rf"^  {re.escape(job_id)}:\n(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:\n|\Z)",
        source,
        re.MULTILINE | re.DOTALL,
    )
    if match is None:
        raise AssertionError(f"Missing CI job {job_id!r}.")
    return match.group(0)


class CiWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = WORKFLOW.read_text()

    def test_triggers_and_permissions_remain_unrestricted_and_least_privilege(self) -> None:
        self.assertIn("  pull_request:\n", self.source)
        self.assertIn("  push:\n    branches:\n      - main\n", self.source)
        self.assertIn("  workflow_dispatch:\n", self.source)
        permission_blocks = re.findall(
            r"(?ms)^permissions:\n.*?(?=^[A-Za-z0-9_-]+:\n|\Z)",
            self.source,
        )
        self.assertEqual(
            ["permissions:\n  contents: read"],
            [block.strip() for block in permission_blocks],
        )
        self.assertNotRegex(self.source, r"(?m)^[ \t]+permissions:")
        self.assertNotRegex(self.source, r"(?m)^\s+environment:")
        self.assertNotRegex(self.source, r"\$\{\{\s*secrets\b")
        self.assertNotRegex(self.source, r"(?m)^\s*secrets:\s*")
        self.assertNotIn("pull_request_target", self.source)
        self.assertNotRegex(self.source, r"(?m)^\s+paths(?:-ignore)?:")
        self.assertNotIn("continue-on-error", self.source)

    def test_parallel_jobs_keep_the_existing_full_paths(self) -> None:
        azure = job_block(self.source, "azure-proof")
        api_image = job_block(self.source, "api-image")
        dotnet = job_block(self.source, "dotnet")

        self.assertIn("name: Azure proof and contract checks", azure)
        self.assertIn(
            "uses: actions/checkout@v4\n        with:\n          ref: ${{ github.sha }}",
            azure,
        )
        self.assertNotRegex(azure, r"(?m)^\s+if:")
        for command in (
            "python3 scripts/tests/test_ci_workflow.py",
            "python3 scripts/tests/test_azure_api_deploy_workflow.py",
            "python3 scripts/tests/test_azure_production_templates.py",
            "python3 scripts/tests/test_api_infrastructure.py",
            "python3 scripts/tests/test_customer_subscription_bootstrap.py",
            "python3 scripts/tests/test_api_provider_image.py",
            "python3 scripts/tests/test_managed_lifecycle_slo_sampler.py",
            "python3 scripts/tests/test_catalog_rehearsal_harness.py",
            "bash -n scripts/catalog-rehearsal/run-phase.sh",
            "python3 scripts/tests/test_validate_api_provider_image.py",
            "python3 scripts/tests/test_azure_workload_proof.py",
            "python3 scripts/tests/test_azure_workload_restore_proof.py",
            "bash -n \\",
            "shellcheck \\",
            "az bicep build --file infra/azure-workload-proof/main.bicep",
        ):
            self.assertRegex(
                azure,
                rf"(?m)^\s+(?:run:\s+)?{re.escape(command)}(?:\s|$)",
            )

        self.assertIn("name: Full .NET restore, build, and test", dotnet)
        self.assertIn(
            "uses: actions/checkout@v4\n        with:\n          ref: ${{ github.sha }}",
            dotnet,
        )
        for command in (
            'dotnet restore ElsaControl.sln',
            'dotnet build ElsaControl.sln --configuration Release --no-restore',
            'dotnet test ElsaControl.sln --configuration Release --no-build --verbosity normal',
        ):
            self.assertRegex(
                dotnet,
                rf"(?m)^\s+run:\s+{re.escape(command)}\s*$",
            )
        self.assertNotRegex(dotnet, r"(?m)^\s+if:")
        self.assertIn("scripts/validate-api-provider-image.sh", azure)

        self.assertIn("name: Build and smoke-test API provider image", api_image)
        self.assertIn(
            "uses: actions/checkout@v4\n        with:\n          ref: ${{ github.sha }}",
            api_image,
        )
        self.assertIn(
            "docker build --file src/Hosting/ElsaControl.Api/Dockerfile",
            api_image,
        )
        self.assertIn(
            "scripts/validate-api-provider-image.sh elsa-control-api-ci:${{ github.sha }}",
            api_image,
        )
        self.assertNotRegex(api_image, r"(?m)^\s+if:")

    def test_required_build_and_test_check_aggregates_fail_closed(self) -> None:
        aggregator = job_block(self.source, "build")

        self.assertIn("name: Build and Test", aggregator)
        self.assertEqual(
            ["    if: ${{ always() }}"],
            re.findall(r"(?m)^\s+if:.*$", aggregator),
        )
        self.assertIn("needs:", aggregator)
        self.assertIn("- azure-proof", aggregator)
        self.assertIn("- api-image", aggregator)
        self.assertIn("- dotnet", aggregator)
        self.assertNotIn("uses:", aggregator)
        for line in (
            "AZURE_PROOF_RESULT: ${{ needs['azure-proof'].result }}",
            "API_IMAGE_RESULT: ${{ needs['api-image'].result }}",
            "DOTNET_RESULT: ${{ needs.dotnet.result }}",
            'if [ "$AZURE_PROOF_RESULT" != "success" ] \\',
            '|| [ "$API_IMAGE_RESULT" != "success" ] \\',
            '|| [ "$DOTNET_RESULT" != "success" ]; then',
            "exit 1",
        ):
            self.assertRegex(aggregator, rf"(?m)^\s+{re.escape(line)}\s*$")

    def test_console_required_check_remains_unchanged(self) -> None:
        console = job_block(self.source, "console")

        self.assertIn("name: Console quality gates", console)
        self.assertIn("run: npm ci", console)
        self.assertIn("run: npm test", console)
        self.assertIn("run: npm run typecheck", console)
        self.assertIn("run: npm run build", console)


if __name__ == "__main__":
    unittest.main()
