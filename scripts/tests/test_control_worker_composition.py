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
ARM_ID = re.compile(r"^/subscriptions/[0-9a-f-]{36}(/resourceGroups/[A-Za-z0-9._()-]+)?(/providers/[A-Za-z0-9./_-]+)+$")
ORIGIN = re.compile(r"^https://[a-z0-9.-]+$")
TOKEN = re.compile(r"^[A-Za-z0-9._@#-]{1,128}$")


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
                self.assertTrue(
                    GUID.match(value) or ARM_ID.match(value) or ORIGIN.match(value) or TOKEN.match(value),
                    f"{name} does not look like an identifier")
                self.assertNotRegex(value, r"[=;]")

    def test_only_the_named_decisions_are_pending(self):
        self.assertEqual({
            "SqlBootstrapIp": "#310",
            "ReleaseFeedServiceIndex": "#311",
            "ReleaseVerificationClientId": "#311",
            "ReleaseVerificationBlobRedirectHost": "#311",
            "ReleaseProducerSignatureSubject": "#311",
            "ReleaseProducerOidcIssuer": "#311",
        }, self.pending)

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
        self.overrides = {"SqlBootstrapIp": "203.0.113.10", "ReleaseFeedServiceIndex": "https://api.nuget.org/v3/index.json"}

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
            path.write_text(json.dumps({"parameters": {"X": "Server=tcp:a;Password=b"}}))
            with self.assertRaises(renderer.CompositionError):
                renderer.load_parameters(path)

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
            self.assertEqual(0, renderer.main(["workers", "--output", str(Path(directory) / "w.json"),
                                              "--set", "SqlBootstrapIp=203.0.113.10",
                                              "--set", "ReleaseFeedServiceIndex=https://api.nuget.org/v3/index.json"]))
            self.assertEqual(2, renderer.main(["workers", "--output", str(Path(directory) / "w.json"),
                                              "--set", "ProvisionerClientId=00000000-0000-0000-0000-000000000000",
                                              "--set", "SqlBootstrapIp=203.0.113.10"]))
            self.assertEqual(0, renderer.main(["rollback", "--output", str(Path(directory) / "r.json")]))


if __name__ == "__main__":
    unittest.main()
