#!/usr/bin/env python3

from __future__ import annotations

import re
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
DOCKERFILE = ROOT / "src" / "Hosting" / "ElsaControl.Api" / "Dockerfile"


class ApiProviderImageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.source = DOCKERFILE.read_text()

    def test_runtime_and_azure_cli_images_are_digest_pinned(self) -> None:
        images = re.findall(r"^FROM\s+(\S+)", self.source, re.MULTILINE)
        runtime_images = [image for image in images if "/dotnet/aspnet:" in image or "/azure-cli:" in image]
        self.assertEqual(2, len(runtime_images))
        for image in runtime_images:
            self.assertRegex(image, r"@sha256:[0-9a-f]{64}$")

    def test_sqlcmd_is_version_and_checksum_pinned_for_supported_platforms(self) -> None:
        self.assertIn("tdnf install --assumeyes tar bzip2", self.source)
        self.assertIn("tdnf clean all", self.source)
        self.assertIn("ARG SQLCMD_VERSION=1.10.0", self.source)
        self.assertRegex(self.source, r"ARG SQLCMD_AMD64_SHA256=[0-9a-f]{64}")
        self.assertRegex(self.source, r"ARG SQLCMD_ARM64_SHA256=[0-9a-f]{64}")
        self.assertIn("sha256sum --check --status", self.source)
        self.assertIn('amd64) sqlcmd_sha256=', self.source)
        self.assertIn('arm64) sqlcmd_sha256=', self.source)

    def test_provider_executable_and_template_paths_are_absolute_and_baked_in(self) -> None:
        for expected in (
            "Deployment__AzureProvider__Runner__AzureCliPath=/usr/bin/az",
            "Deployment__AzureProvider__Runner__SqlCmdPath=/usr/local/bin/sqlcmd",
            "Deployment__AzureProvider__Runner__CurlPath=/usr/bin/curl",
            "Deployment__AzureProvider__Runner__TemplateRoot=/app/azure-provider",
            "COPY infra/azure-production/ /app/azure-provider/",
        ):
            self.assertIn(expected, self.source)

    def test_dotnet_native_dependencies_preserve_full_globalization(self) -> None:
        self.assertIn("icu tzdata libstdc++ openssl-libs", self.source)
        self.assertNotIn("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true", self.source)

    def test_pulumi_plugins_are_checksum_pinned_and_baked(self) -> None:
        self.assertIn("ARG PULUMI_AZURE_NATIVE_VERSION=3.11.0", self.source)
        self.assertIn("ARG PULUMI_RANDOM_VERSION=4.18.5", self.source)
        self.assertRegex(self.source, r"ARG PULUMI_AZURE_NATIVE_AMD64_SHA256=[0-9a-f]{64}")
        self.assertRegex(self.source, r"ARG PULUMI_AZURE_NATIVE_ARM64_SHA256=[0-9a-f]{64}")
        self.assertRegex(self.source, r"ARG PULUMI_RANDOM_AMD64_SHA256=[0-9a-f]{64}")
        self.assertRegex(self.source, r"ARG PULUMI_RANDOM_ARM64_SHA256=[0-9a-f]{64}")
        self.assertIn("PULUMI_DISABLE_AUTOMATIC_PLUGIN_ACQUISITION=true", self.source)
        self.assertIn("pulumi-resource-azure-native", self.source)
        self.assertIn("pulumi-resource-random", self.source)

    def test_image_does_not_enable_remote_mutation_by_default(self) -> None:
        for forbidden in (
            "Deployment__AzureProvider__WorkerEnabled=true",
            "Deployment__AzureProvider__Runner__Enabled=true",
            "Deployment__AzureProvider__InstanceLifecycle__Enabled=true",
            "Deployment__ElsaInstanceLifecycle__Enabled=true",
        ):
            self.assertNotIn(forbidden, self.source)


if __name__ == "__main__":
    unittest.main()
