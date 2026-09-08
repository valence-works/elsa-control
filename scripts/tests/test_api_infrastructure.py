#!/usr/bin/env python3
"""Offline contracts for the generated API infrastructure seam."""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
API_MODULE = ROOT / "infra" / "api" / "api-website.module.bicep"
API_PARAMETERS = ROOT / "src" / "Hosting" / "ElsaControl.AppHost" / "infra" / "api" / "api.tmpl.bicepparam"
REGENERATE_INFRA = ROOT / "dev" / "regenerate-infra.sh"
PATCH_API_IDENTITY = ROOT / "dev" / "patch-api-provisioner-identity.py"
APP_SERVICE_DOC = ROOT / "docs" / "deployment" / "azure-app-service.md"
# Hand-maintained directories dev/regenerate-infra.sh must carry across a regeneration.
PRESERVED_INFRA_DIRECTORIES = ("azure-production", "azure-workload-proof", "azure-customer-subscription", "managed-telemetry", "control-deploy-identity", "control-worker-composition", "control-egress")

SUBSCRIPTION = "00000000-0000-0000-0000-000000000000"
RESOURCE_GROUP = "rg-api-test"
ACR_ID = (
    f"/subscriptions/{SUBSCRIPTION}/resourceGroups/{RESOURCE_GROUP}/"
    "providers/Microsoft.ManagedIdentity/userAssignedIdentities/acr"
)
API_ID = (
    f"/subscriptions/{SUBSCRIPTION}/resourceGroups/{RESOURCE_GROUP}/"
    "providers/Microsoft.ManagedIdentity/userAssignedIdentities/api"
)
PROVISIONER_ID = (
    f"/subscriptions/{SUBSCRIPTION}/resourceGroups/rg-provisioner/"
    "providers/Microsoft.ManagedIdentity/userAssignedIdentities/provisioner"
)
ACR_CLIENT_ID = "00000000-0000-0000-0000-000000000001"
API_CLIENT_ID = "00000000-0000-0000-0000-000000000004"


EGRESS_SUBNET_ID = (
    f"/subscriptions/{SUBSCRIPTION}/resourceGroups/{RESOURCE_GROUP}/"
    "providers/Microsoft.Network/virtualNetworks/vnet/subnets/snet-api-egress"
)


def parameter_file(module_path: Path, parameter_directory: Path, provisioner_id: str, egress_subnet_id: str = "") -> str:
    """Return a synthetic, non-secret parameter file for Bicep snapshot evaluation."""

    using_path = os.path.relpath(module_path, start=parameter_directory).replace(os.sep, "/")
    values = {
        "location": "westeurope",
        "elsa_control_outputs_azure_container_registry_endpoint": "acr.example",
        "elsa_control_outputs_planid": (
            f"/subscriptions/{SUBSCRIPTION}/resourceGroups/{RESOURCE_GROUP}/"
            "providers/Microsoft.Web/serverfarms/plan"
        ),
        "elsa_control_outputs_azure_container_registry_managed_identity_id": ACR_ID,
        "elsa_control_outputs_azure_container_registry_managed_identity_client_id": ACR_CLIENT_ID,
        "api_containerimage": "acr.example/api:ci",
        "api_containerport": "8080",
        "adminapikey_value": "test-only-redacted",
        "control_sql_outputs_sqlserverfqdn": "sql.example",
        "entratenantid_value": "00000000-0000-0000-0000-000000000002",
        "entraclientid_value": "00000000-0000-0000-0000-000000000003",
        "entraclientsecret_value": "test-only-redacted",
        "builderclientapikey_value": "test-only-redacted",
        "api_identity_outputs_id": API_ID,
        "api_identity_outputs_clientid": API_CLIENT_ID,
        "provisioner_identity_outputs_id": provisioner_id,
        "api_egress_subnet_id": egress_subnet_id,
    }
    lines = [f"using '{using_path}'", ""]
    lines.extend(f"param {name} = '{value}'" for name, value in values.items())
    return "\n".join(lines) + "\n"


class ApiInfrastructureTests(unittest.TestCase):
    def test_catalog_sql_pins_api_identity_without_credential_chain_fallback(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            webapp = self.webapp(self.snapshot(Path(temporary), PROVISIONER_ID))
        settings = {setting["name"]: setting["value"] for setting in webapp["properties"]["siteConfig"]["appSettings"]}
        self.assertEqual(
            settings["ConnectionStrings__Catalog"],
            f"Server=tcp:sql.example,1433;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id={API_CLIENT_ID};Database=Catalog",
        )

    @staticmethod
    def generated_api_module() -> str:
        """Build the module shape emitted before the regeneration patch runs."""

        module = API_MODULE.read_text().replace(
            'TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id=${api_identity_outputs_clientid}',
            'Authentication="Active Directory Default"',
        )
        optional_parameter = re.compile(
            r"\n@description\('Optional full resource ID of the dedicated Azure provider provisioner identity\..*?"
            r"\nparam provisioner_identity_outputs_id string = ''\n",
            re.DOTALL,
        )
        generated = optional_parameter.sub("", module, count=1)
        egress_parameter = re.compile(
            r"\n@description\('Optional resource ID of the delegated App Service integration subnet.*?"
            r"\nparam api_egress_subnet_id string = ''\n",
            re.DOTALL,
        )
        generated = egress_parameter.sub("", generated, count=1)
        generated = re.sub(
            r"\n    // Regional VNet integration for one static egress \(#310\).*?\n    virtualNetworkSubnetId: [^\n]*\n",
            "\n",
            generated,
            count=1,
            flags=re.DOTALL,
        )
        generated = generated.replace("      vnetRouteAllEnabled: !empty(api_egress_subnet_id)\n", "", 1)
        return re.sub(
            r"(?ms)^  identity: \{\n.*?^  \}\n",
            "\n".join(
                [
                    "  identity: {",
                    "    type: 'UserAssigned'",
                    "    userAssignedIdentities: {",
                    "      '${elsa_control_outputs_azure_container_registry_managed_identity_id}': { }",
                    "      '${api_identity_outputs_id}': { }",
                    "    }",
                    "  }",
                ]
            )
            + "\n",
            generated,
            count=1,
        )

    def snapshot(self, directory: Path, provisioner_id: str, egress_subnet_id: str = "") -> dict:
        """Evaluate the actual module with Bicep's offline deployment snapshot."""

        if shutil.which("az") is None:
            self.fail("Azure CLI is required for offline Bicep snapshot evaluation.")

        parameter_path = directory / ("api-with-provisioner.bicepparam" if provisioner_id else "api-default.bicepparam")
        parameter_path.write_text(parameter_file(API_MODULE, directory, provisioner_id, egress_subnet_id))
        result = subprocess.run(
            [
                "az",
                "bicep",
                "snapshot",
                "--file",
                str(parameter_path),
                "--mode",
                "Overwrite",
                "--resource-group",
                RESOURCE_GROUP,
                "--location",
                "westeurope",
                "--subscription-id",
                SUBSCRIPTION,
                "--only-show-errors",
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            self.fail("Bicep snapshot evaluation failed without exposing command output.")
        return json.loads(parameter_path.with_suffix(".snapshot.json").read_text())

    @staticmethod
    def webapp(snapshot: dict) -> dict:
        return next(resource for resource in snapshot["predictedResources"] if resource["type"] == "Microsoft.Web/sites")

    def test_bicep_module_evaluates_exact_default_identity_set(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            webapp = self.webapp(self.snapshot(Path(temporary), ""))

        self.assertEqual(
            set(webapp["identity"]["userAssignedIdentities"]),
            {ACR_ID, API_ID},
        )
        self.assertEqual(webapp["properties"]["keyVaultReferenceIdentity"], API_ID)
        client_setting = next(
            setting
            for setting in webapp["properties"]["siteConfig"]["appSettings"]
            if setting["name"] == "AZURE_CLIENT_ID"
        )
        self.assertEqual(client_setting["value"], API_CLIENT_ID)

    def test_bicep_module_keeps_platform_egress_unless_a_subnet_is_supplied(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            default = self.webapp(self.snapshot(Path(temporary), ""))["properties"]
            attached = self.webapp(self.snapshot(Path(temporary), PROVISIONER_ID, EGRESS_SUBNET_ID))["properties"]

        self.assertIsNone(default.get("virtualNetworkSubnetId"))
        self.assertFalse(default["siteConfig"]["vnetRouteAllEnabled"])
        self.assertEqual(attached["virtualNetworkSubnetId"], EGRESS_SUBNET_ID)
        self.assertTrue(attached["siteConfig"]["vnetRouteAllEnabled"])

    def test_bicep_module_evaluates_only_supplied_provisioner_identity(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            webapp = self.webapp(self.snapshot(Path(temporary), PROVISIONER_ID))

        self.assertEqual(
            set(webapp["identity"]["userAssignedIdentities"]),
            {ACR_ID, API_ID, PROVISIONER_ID},
        )
        self.assertEqual(webapp["properties"]["keyVaultReferenceIdentity"], API_ID)

    def test_template_and_regeneration_chain_are_explicit(self) -> None:
        module = API_MODULE.read_text()
        parameters = API_PARAMETERS.read_text()
        regeneration = REGENERATE_INFRA.read_text()

        self.assertIn("param provisioner_identity_outputs_id string = ''", module)
        self.assertIn("param api_egress_subnet_id string = ''", module)
        self.assertIn("virtualNetworkSubnetId: empty(api_egress_subnet_id) ? null : api_egress_subnet_id", module)
        self.assertIn("      vnetRouteAllEnabled: !empty(api_egress_subnet_id)", module)
        self.assertRegex(
            parameters,
            r"\{\{ if index \.Env \"AZURE_API_EGRESS_SUBNET_ID\" \}\}\s+"
            r"param api_egress_subnet_id = '\{\{ \.Env\.AZURE_API_EGRESS_SUBNET_ID \}\}'\s+"
            r"\{\{ else \}\}\s+param api_egress_subnet_id = ''\s+\{\{ end \}\}",
        )
        self.assertRegex(
            parameters,
            r"\{\{ if index \.Env \"AZURE_PROVISIONER_IDENTITY_ID\" \}\}\s+"
            r"param provisioner_identity_outputs_id = '\{\{ \.Env\.AZURE_PROVISIONER_IDENTITY_ID \}\}'\s+"
            r"\{\{ else \}\}\s+param provisioner_identity_outputs_id = ''\s+\{\{ end \}\}",
        )
        self.assertIn("same Microsoft Entra tenant", module)
        self.assertIn("userAssignedIdentities: union(", module)
        self.assertIn("empty(provisioner_identity_outputs_id)", module)
        self.assertIn("'${provisioner_identity_outputs_id}': { }", module)
        self.assertIn("patch-api-provisioner-identity.py", regeneration)
        for directory in PRESERVED_INFRA_DIRECTORIES:
            self.assertIn(directory, regeneration)
        self.assertNotIn("dashboard", module.lower())
        self.assertNotIn("WEBSITE_ENABLE_ASPIRE_OTEL_SIDECAR", module)
        self.assertLess(
            regeneration.index("trap restore_preserved_infra EXIT"),
            regeneration.index('mv "infra/$relative_path"'),
        )

    def test_operator_documentation_preserves_identity_and_deployment_boundaries(self) -> None:
        documentation = APP_SERVICE_DOC.read_text()

        self.assertIn("AZURE_PROVISIONER_IDENTITY_ID", documentation)
        self.assertIn("AZURE_API_EGRESS_SUBNET_ID", documentation)
        self.assertIn("same Microsoft Entra tenant", documentation)
        self.assertIn("restarts the app", documentation)
        self.assertIn("AZURE_CLIENT_ID` remains", documentation)
        self.assertIn("keyVaultReferenceIdentity` remains", documentation)
        self.assertIn("existing classic `DOCKER` deployment mode", documentation)
        self.assertIn("Do not convert it to `SITECONTAINERS`", documentation)

    def test_regeneration_patch_reconstructs_generated_module_idempotently(self) -> None:
        module = API_MODULE.read_text()
        generated = self.generated_api_module()

        with tempfile.TemporaryDirectory() as temporary:
            fixture = Path(temporary) / "infra" / "api" / "api-website.module.bicep"
            fixture.parent.mkdir(parents=True)
            fixture.write_text(generated)
            for _ in range(2):
                result = subprocess.run(
                    [sys.executable, str(PATCH_API_IDENTITY)],
                    cwd=temporary,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(result.returncode, 0, "identity patch helper should be idempotent")
            self.assertEqual(fixture.read_text(), module)

    def test_regeneration_patch_restores_the_provisioner_parameter_block_idempotently(self) -> None:
        template = API_PARAMETERS.read_text()
        provisioner_block = re.compile(r'\{\{ if index \.Env "AZURE_PROVISIONER_IDENTITY_ID" \}\}.*?\{\{ end \}\}\n', re.DOTALL)
        egress_block = re.compile(r'\{\{ if index \.Env "AZURE_API_EGRESS_SUBNET_ID" \}\}.*?\{\{ end \}\}\n', re.DOTALL)
        regenerated = egress_block.sub("", provisioner_block.sub("", template, count=1), count=1)
        self.assertNotIn("provisioner_identity_outputs_id", regenerated)
        self.assertNotIn("api_egress_subnet_id", regenerated)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            module_fixture = root / "infra" / "api" / "api-website.module.bicep"
            module_fixture.parent.mkdir(parents=True)
            module_fixture.write_text(self.generated_api_module())
            template_fixture = root / "src" / "Hosting" / "ElsaControl.AppHost" / "infra" / "api" / "api.tmpl.bicepparam"
            template_fixture.parent.mkdir(parents=True)
            template_fixture.write_text(regenerated)
            for _ in range(2):
                result = subprocess.run([sys.executable, str(PATCH_API_IDENTITY)], cwd=temporary, capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode, 0, "patch helper should be idempotent")
            self.assertEqual(template_fixture.read_text(), template)
            self.assertEqual(1, template_fixture.read_text().count("param provisioner_identity_outputs_id = ''"))

    def test_regeneration_rejects_unknown_catalog_authentication_without_partial_write(self) -> None:
        generated = self.generated_api_module().replace("Active Directory Default", "Unexpected Authentication")
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Path(temporary) / "infra" / "api" / "api-website.module.bicep"
            fixture.parent.mkdir(parents=True)
            fixture.write_text(generated)
            result = subprocess.run([sys.executable, str(PATCH_API_IDENTITY)], cwd=temporary, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(fixture.read_text(), generated)

    def test_regeneration_rejects_an_ambiguous_egress_anchor_without_partial_write(self) -> None:
        generated = self.generated_api_module().replace("      numberOfWorkers: 1\n", "      numberOfWorkers: 2\n", 1)
        self.assertNotIn("api_egress_subnet_id", generated)
        with tempfile.TemporaryDirectory() as temporary:
            fixture = Path(temporary) / "infra" / "api" / "api-website.module.bicep"
            fixture.parent.mkdir(parents=True)
            fixture.write_text(generated)
            result = subprocess.run([sys.executable, str(PATCH_API_IDENTITY)], cwd=temporary, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("egress", result.stderr)
            self.assertEqual(fixture.read_text(), generated)

    def test_regeneration_writes_nothing_when_the_parameter_template_is_malformed(self) -> None:
        generated = self.generated_api_module()
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            module_fixture = root / "infra" / "api" / "api-website.module.bicep"
            module_fixture.parent.mkdir(parents=True)
            module_fixture.write_text(generated)
            template_fixture = root / "src" / "Hosting" / "ElsaControl.AppHost" / "infra" / "api" / "api.tmpl.bicepparam"
            template_fixture.parent.mkdir(parents=True)
            template_fixture.write_text("using './api-website.module.bicep'\n")
            result = subprocess.run([sys.executable, str(PATCH_API_IDENTITY)], cwd=temporary, capture_output=True, text=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(module_fixture.read_text(), generated, "module must not be written when the template fails")
            self.assertEqual(template_fixture.read_text(), "using './api-website.module.bicep'\n")

    def run_regeneration_fixture(self, mode: str) -> tuple[subprocess.CompletedProcess[str], Path]:
        """Run regeneration in a disposable project with a fake azd/az pair."""

        temporary = Path(tempfile.mkdtemp(prefix="api-infra-regeneration-"))
        self.addCleanup(shutil.rmtree, temporary, ignore_errors=True)
        (temporary / "temp").mkdir()
        (temporary / "dev").mkdir()
        (temporary / "infra").mkdir()
        shutil.copy2(REGENERATE_INFRA, temporary / "dev" / "regenerate-infra.sh")
        shutil.copy2(PATCH_API_IDENTITY, temporary / "dev" / "patch-api-provisioner-identity.py")
        for relative_path in PRESERVED_INFRA_DIRECTORIES:
            directory = temporary / "infra" / relative_path
            directory.mkdir(parents=True)
            (directory / "manual.marker").write_text(relative_path)

        (temporary / "generated-api-website.module.bicep").write_text(self.generated_api_module())
        (temporary / "collision.marker").write_text("generated-collision")
        fake_bin = temporary / "bin"
        fake_bin.mkdir()
        (fake_bin / "azd").write_text(
            "#!/bin/sh\n"
            "set -eu\n"
            "if [ \"${FAKE_AZD_MODE}\" = failure ]; then exit 19; fi\n"
            "mkdir -p infra/api\n"
            "cp generated-api-website.module.bicep infra/api/api-website.module.bicep\n"
            "if [ \"${FAKE_AZD_MODE}\" = collision ]; then\n"
            "  mkdir -p infra/azure-production\n"
            "  cp collision.marker infra/azure-production/generated.marker\n"
            "fi\n"
        )
        (fake_bin / "az").write_text(
            "#!/bin/sh\n"
            "[ \"${1-}\" = bicep ] && [ \"${2-}\" = build ] || exit 41\n"
        )
        for command in (fake_bin / "azd", fake_bin / "az"):
            command.chmod(0o755)

        environment = os.environ.copy()
        environment["TMPDIR"] = str(temporary / "temp")
        environment["FAKE_AZD_MODE"] = mode
        environment["PATH"] = f"{fake_bin}{os.pathsep}{environment['PATH']}"
        result = subprocess.run(
            [str(temporary / "dev" / "regenerate-infra.sh")],
            cwd=temporary,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        return result, temporary

    @staticmethod
    def assert_manual_directories(test_case: unittest.TestCase, project: Path) -> None:
        for relative_path in PRESERVED_INFRA_DIRECTORIES:
            marker = project / "infra" / relative_path / "manual.marker"
            test_case.assertTrue(marker.exists(), f"manual directory was not restored: {relative_path}")
            test_case.assertEqual(marker.read_text(), relative_path)

    def test_regeneration_success_restores_all_manual_authority_directories(self) -> None:
        result, project = self.run_regeneration_fixture("success")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assert_manual_directories(self, project)
        self.assertIn("param provisioner_identity_outputs_id string = ''", (project / "infra/api/api-website.module.bicep").read_text())

    def test_regeneration_failure_restores_all_manual_authority_directories(self) -> None:
        result, project = self.run_regeneration_fixture("failure")

        self.assertNotEqual(result.returncode, 0)
        self.assert_manual_directories(self, project)

    def test_regeneration_collision_fails_without_overwriting_manual_authority(self) -> None:
        result, project = self.run_regeneration_fixture("collision")

        self.assertNotEqual(result.returncode, 0)
        for relative_path in PRESERVED_INFRA_DIRECTORIES[1:]:
            marker = project / "infra" / relative_path / "manual.marker"
            self.assertEqual(marker.read_text(), relative_path)
        self.assertEqual(
            (project / "infra/azure-production/generated.marker").read_text(),
            "generated-collision",
        )
        match = re.search(r"Preserved infrastructure remains at (?P<path>[^\n]+)", result.stderr)
        self.assertIsNotNone(match)
        self.assertEqual(
            (Path(match.group("path")) / "azure-production/manual.marker").read_text(),
            "azure-production",
        )


if __name__ == "__main__":
    unittest.main()
