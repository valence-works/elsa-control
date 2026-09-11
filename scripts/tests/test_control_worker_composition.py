#!/usr/bin/env python3
"""Contract tests for the checked-in Control API worker composition (#315)."""
import importlib.util
import json
import re
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "render-worker-settings.py"
spec = importlib.util.spec_from_file_location("render_worker_settings", SCRIPT)
renderer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(renderer)

GUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")


class CompositionFilesTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workers = renderer.load_template(renderer.WORKER_TEMPLATE)
        cls.verification = renderer.load_template(renderer.VERIFICATION_TEMPLATE)
        cls.resolved, cls.pending = renderer.load_parameters(renderer.PRODUCTION_PARAMETERS)

    def test_worker_template_enables_all_three_workers_and_the_v1_scope(self):
        for key in renderer.WORKER_ENABLE_KEYS:
            self.assertEqual("true", self.workers[key])
        self.assertEqual("true", self.workers["Deployment__AzureProvider__Runner__Enabled"])
        self.assertEqual("false", self.workers["Deployment__AzureProvider__Runner__DisposableProofMode"])
        self.assertEqual("westeurope", self.workers["Deployment__AzureProvider__Runner__TargetScope__Location"])
        self.assertEqual("unrestricted", self.workers["RuntimeBuilder__InstancePlans__DefaultEgress"])

    def test_worker_template_uses_only_the_three_provider_owned_secret_instructions(self):
        secrets = {self.workers[f"Deployment__AzureProvider__Secrets__{i}__Name"]:
                   self.workers[f"Deployment__AzureProvider__Secrets__{i}__Reference"] for i in range(3)}
        self.assertEqual({
            "database:connectionstring": "secret://azure-managed/sql-connection",
            "identity:signingkey": "secret://azure-managed/identity-signing-key",
            "admin:password": "secret://azure-managed/admin-password",
        }, secrets)
        self.assertFalse(any(key.startswith("Deployment__AzureProvider__Secrets__3") for key in self.workers))
        self.assertFalse(any(key.endswith("__Value") for key in self.workers))

    def test_worker_template_leaves_image_owned_tool_paths_to_the_image(self):
        self.assertTrue(renderer.IMAGE_OWNED_KEYS.isdisjoint(self.workers))

    def test_every_placeholder_is_declared_and_every_parameter_is_used(self):
        referenced = set()
        for template in (self.workers, self.verification):
            for value in template.values():
                referenced.update(renderer.PLACEHOLDER.findall(value))
        declared = set(self.resolved) | set(self.pending)
        self.assertEqual(referenced, declared)

    def test_production_parameters_are_non_secret_identifiers(self):
        for name, value in self.resolved.items():
            with self.subTest(name=name):
                self.assertTrue(any(shape.match(value) for shape in renderer.PARAMETER_SHAPES),
                                f"{name} does not look like an identifier")
                self.assertNotRegex(value, r"[=;]")

    def test_only_the_named_decisions_are_pending(self):
        self.assertEqual({"SqlBootstrapIp": "#310"}, self.pending)

    def test_release_verification_binds_the_decided_producer_and_the_api_identity(self):
        self.assertEqual(
            "https://github.com/valence-works/elsa-production-image/.github/workflows/build-and-push.yml@refs/heads/main",
            self.resolved["ReleaseProducerSignatureSubject"])
        self.assertEqual("https://token.actions.githubusercontent.com", self.resolved["ReleaseProducerOidcIssuer"])
        self.assertEqual("c5055d7d-d66d-468d-8984-077214496243", self.resolved["ReleaseVerificationClientId"])
        self.assertTrue(self.resolved["ReleaseVerificationBlobRedirectHost"].endswith(".blob.core.windows.net"))
        self.assertNotIn("*", self.resolved["ReleaseVerificationBlobRedirectHost"])
        rendered = renderer.render(self.verification, self.resolved, self.pending)
        self.assertEqual("valenceruntimeimages.azurecr.io", rendered["ReleaseCatalog__Verification__RegistryHost"])

    def test_production_scope_binds_the_customer_workload_subscription_and_the_governed_registry(self):
        self.assertEqual("a54cd7b1-3d13-48ce-9dce-5ae013142c85", self.resolved["WorkloadSubscriptionId"])
        self.assertEqual("rg-elsa-cloud-workloads-platform-prod-weu", self.resolved["WorkloadResourceGroupName"])
        self.assertEqual("8e23037a-420f-4ad0-9594-9d194de29e84", self.resolved["RegistrySubscriptionId"])
        self.assertEqual("valenceruntimeimages", self.resolved["RegistryName"])
        self.assertNotEqual(self.resolved["WorkloadSubscriptionId"], self.resolved["RegistrySubscriptionId"])
        for name in ("RegistryDeploymentMetadataRoleDefinitionId", "RegistryDeploymentMetadataRoleAssignmentId",
                     "RegistryRoleAdministrationAssignmentId"):
            self.assertTrue(self.resolved[name].startswith(f"/subscriptions/{self.resolved['RegistrySubscriptionId']}/"))
        self.assertIn("/resourceGroups/rg-valence-runtime/providers/Microsoft.ContainerRegistry/registries/valenceruntimeimages/",
                      self.resolved["RegistryRoleAdministrationAssignmentId"])

    def test_sql_bootstrap_identity_is_the_provisioner(self):
        self.assertEqual("mi-elsa-cloud-provisioner-prod-weu", self.resolved["ProvisionerIdentityName"])
        self.assertTrue(GUID.match(self.resolved["ProvisionerClientId"]))
        self.assertTrue(GUID.match(self.resolved["ProvisionerPrincipalId"]))
        self.assertNotEqual(self.resolved["ProvisionerClientId"], self.resolved["ProvisionerPrincipalId"])


class RenderTests(unittest.TestCase):
    def setUp(self):
        self.workers = renderer.load_template(renderer.WORKER_TEMPLATE)
        self.resolved, self.pending = renderer.load_parameters(renderer.PRODUCTION_PARAMETERS)
        self.overrides = {"SqlBootstrapIp": "203.0.113.10"}

    def test_production_render_is_blocked_while_a_decision_is_pending(self):
        with self.assertRaises(renderer.CompositionError) as raised:
            renderer.render(self.workers, self.resolved, self.pending)
        self.assertIn("#310", str(raised.exception))
        self.assertNotIn("ada5e428", str(raised.exception))

    def test_render_with_resolved_decisions_produces_only_template_keys(self):
        rendered = renderer.render(self.workers, self.resolved, self.pending, self.overrides)
        self.assertEqual(set(self.workers), set(rendered))
        self.assertFalse(any("${" in value for value in rendered.values()))
        self.assertEqual("203.0.113.10", rendered["Deployment__AzureProvider__Runner__SqlBootstrapIp"])

    def test_render_refuses_half_enabled_workers(self):
        template = dict(self.workers)
        template["Deployment__AzureProvider__WorkerEnabled"] = "false"
        with self.assertRaises(renderer.CompositionError):
            renderer.render(template, self.resolved, self.pending, self.overrides)

    def test_render_refuses_disposable_proof_mode(self):
        template = dict(self.workers)
        template["Deployment__AzureProvider__Runner__DisposableProofMode"] = "true"
        with self.assertRaises(renderer.CompositionError):
            renderer.render(template, self.resolved, self.pending, self.overrides)

    def test_render_refuses_unknown_placeholders(self):
        template = dict(self.workers)
        template["ControlPlane__Origin"] = "${Nope}"
        with self.assertRaises(renderer.CompositionError):
            renderer.render(template, self.resolved, self.pending, self.overrides)

    def test_template_loader_refuses_raw_secret_values_and_image_owned_keys(self):
        with tempfile.TemporaryDirectory() as directory:
            for key in ("Deployment__AzureProvider__Secrets__0__Value", "Deployment__AzureProvider__Runner__AzureCliPath",
                        "ConnectionStrings__Catalog"):
                path = Path(directory) / "t.json"
                path.write_text(json.dumps({key: "x"}))
                with self.assertRaises(renderer.CompositionError, msg=key):
                    renderer.load_template(path)

    def test_parameter_loader_refuses_credential_shaped_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "p.json"
            for value in ("Server=tcp:a;Password=b", "P@ssw0rd123", "sk-abc123XYZ", "QUJDREVGR0hJSktMTU5PUA==",
                          "4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71"):
                path.write_text(json.dumps({"parameters": {"X": value}}))
                with self.assertRaises(renderer.CompositionError, msg=value):
                    renderer.load_parameters(path)
            for value in ("ada5e428-c5d8-4daf-b7f9-9f2c79d23815", "mi-elsa-cloud-provisioner-prod-weu", "203.0.113.10",
                          "https://api.nuget.org/v3/index.json", "valenceruntimeimages.azurecr.io"):
                path.write_text(json.dumps({"parameters": {"X": value}}))
                self.assertEqual({"X": value}, renderer.load_parameters(path)[0], value)

    def test_payload_shape_matches_az_webapp_appsettings_and_is_private(self):
        rendered = renderer.render(self.workers, self.resolved, self.pending, self.overrides)
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "out.json"
            renderer.write_payload(renderer.to_app_settings(rendered), output)
            self.assertEqual(0o600, output.stat().st_mode & 0o777)
            payload = json.loads(output.read_text())
        self.assertEqual({"name", "value", "slotSetting"}, set(payload[0]))
        self.assertTrue(all(entry["slotSetting"] is False for entry in payload))

    def test_rollback_disables_exactly_the_three_switches(self):
        payload = renderer.load_rollback()
        self.assertEqual(set(renderer.WORKER_ENABLE_KEYS), {entry["name"] for entry in payload})

    def test_cli_status_and_pending_exit_code(self):
        self.assertEqual(0, renderer.main(["status"]))
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(2, renderer.main(["workers", "--output", str(Path(directory) / "w.json")]))
            self.assertFalse((Path(directory) / "w.json").exists())
            self.assertEqual(0, renderer.main(["release-verification", "--output", str(Path(directory) / "v.json")]))
            self.assertEqual(0, renderer.main(["rollback", "--output", str(Path(directory) / "r.json")]))

    def test_cli_has_no_value_override_so_only_the_checked_in_parameters_reach_production(self):
        for argv in (["workers", "--output", "x.json", "--set", "SqlBootstrapIp=203.0.113.10"],
                     ["workers", "--output", "x.json", "--parameters", "other.json"]):
            with self.assertRaises(SystemExit):
                renderer.main(argv)


if __name__ == "__main__":
    unittest.main()
