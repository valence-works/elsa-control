#!/usr/bin/env python3
"""Tests for ARM deployment output lookup."""

from __future__ import annotations

import json
import subprocess
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "read-arm-deployment-output.py"


class ReadArmDeploymentOutputTests(unittest.TestCase):
    def run_reader(self, outputs: dict[str, object], name: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [str(SCRIPT), name],
            cwd=ROOT,
            input=json.dumps(outputs),
            capture_output=True,
            text=True,
            check=False,
        )

    def test_reads_exact_key(self) -> None:
        result = self.run_reader(
            {"AZURE_CONTAINER_REGISTRY_ENDPOINT": {"value": "test.azurecr.io"}},
            "AZURE_CONTAINER_REGISTRY_ENDPOINT",
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("test.azurecr.io", result.stdout.strip())

    def test_reads_arm_key_with_changed_casing(self) -> None:
        result = self.run_reader(
            {"azurE_CONTAINER_REGISTRY_ENDPOINT": {"value": "test.azurecr.io"}},
            "AZURE_CONTAINER_REGISTRY_ENDPOINT",
        )

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("test.azurecr.io", result.stdout.strip())

    def test_rejects_missing_or_empty_output(self) -> None:
        for outputs in ({}, {"AZURE_CONTAINER_REGISTRY_ENDPOINT": {"value": ""}}):
            with self.subTest(outputs=outputs):
                result = self.run_reader(outputs, "AZURE_CONTAINER_REGISTRY_ENDPOINT")

                self.assertNotEqual(0, result.returncode)
                self.assertIn(
                    "Missing deployment output: AZURE_CONTAINER_REGISTRY_ENDPOINT",
                    result.stderr,
                )


if __name__ == "__main__":
    unittest.main()
