#!/usr/bin/env python3
"""Contract tests for the checked-in Control API worker composition (#315)."""
import importlib.util
import json
import re
import tempfile
import unittest
from unittest import mock
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
        self.assertEqual("true", self.workers[renderer.HEALTH_MONITOR_KEY])
        self.assertEqual("true", self.workers["Deployment__AzureProvider__Runner__Enabled"])
        self.assertEqual("false", self.workers["Deployment__AzureProvider__Runner__DisposableProofMode"])
        self.assertEqual("360", self.workers["Deployment__AzureProvider__Runner__CleanupObservationAttempts"])
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

    def test_staging_profile_declares_the_same_authority_without_copying_production_values(self):
        staging, pending = renderer.load_parameters(renderer.STAGING_PARAMETERS)
        self.assertEqual(set(self.resolved), set(staging) | set(pending))
        self.assertEqual({}, pending)
        self.assertEqual(renderer.STAGING_CONTROL_ORIGIN, staging["ControlPlaneOrigin"])
        renderer.validate_staging_authority(staging, self.resolved)
        self.assertEqual("00:45:00", renderer.STAGING_WORKER_SETTINGS["Deployment__AzureProvider__Runner__CommandTimeout"])
        self.assertEqual("1", renderer.STAGING_WORKER_SETTINGS["Deployment__AzureProvider__BatchSize"])

    def test_production_parameters_are_non_secret_identifiers(self):
        for name, value in self.resolved.items():
            with self.subTest(name=name):
                shapes = (renderer.NAMED_PARAMETER_SHAPES[name],) if name in renderer.NAMED_PARAMETER_SHAPES else renderer.PARAMETER_SHAPES
                self.assertTrue(any(shape.match(value) for shape in shapes), f"{name} does not look like an identifier")
                self.assertNotRegex(value, r"[=;]")

    def test_only_the_named_decisions_are_pending(self):
        self.assertEqual({}, self.pending)

    def test_sql_bootstrap_address_is_the_control_egress_nat_address(self):
        # Output of infra/control-egress (#310): the single static address all API egress uses.
        self.assertEqual("9.160.165.252", self.resolved["SqlBootstrapIp"])

    def test_release_verification_binds_the_decided_producer_and_the_api_identity(self):
        self.assertEqual(
            "https://github.com/valence-works/elsa-production-image/.github/workflows/build-and-push.yml@refs/heads/main",
            self.resolved["ReleaseProducerSignatureSubject"])
        self.assertEqual("https://token.actions.githubusercontent.com", self.resolved["ReleaseProducerOidcIssuer"])
        self.assertEqual("c5055d7d-d66d-468d-8984-077214496243", self.resolved["ReleaseVerificationClientId"])
        self.assertEqual("becmanaged36.blob.core.windows.net", self.resolved["ReleaseVerificationBlobRedirectHost"])
        self.assertEqual("https://api.nuget.org/v3/index.json", self.resolved["ReleaseFeedServiceIndex"])
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
        self.overrides = {}

    def test_render_is_blocked_while_a_decision_is_pending(self):
        resolved = {name: value for name, value in self.resolved.items() if name != "SqlBootstrapIp"}
        with self.assertRaises(renderer.CompositionError) as raised:
            renderer.render(self.workers, resolved, {"SqlBootstrapIp": "#310"})
        self.assertIn("#310", str(raised.exception))
        self.assertNotIn("ada5e428", str(raised.exception))

    def test_production_render_succeeds_with_every_decision_made(self):
        rendered = renderer.render(self.workers, self.resolved, self.pending)
        self.assertEqual("9.160.165.252", rendered["Deployment__AzureProvider__Runner__SqlBootstrapIp"])

    def test_render_with_resolved_decisions_produces_only_template_keys(self):
        rendered = renderer.render(self.workers, self.resolved, self.pending, self.overrides)
        self.assertEqual(set(self.workers), set(rendered))
        self.assertFalse(any("${" in value for value in rendered.values()))
        self.assertEqual("9.160.165.252", rendered["Deployment__AzureProvider__Runner__SqlBootstrapIp"])

    def test_render_refuses_half_enabled_workers(self):
        template = dict(self.workers)
        template["Deployment__AzureProvider__WorkerEnabled"] = "false"
        with self.assertRaises(renderer.CompositionError):
            renderer.render(template, self.resolved, self.pending, self.overrides)

    def test_render_refuses_a_health_monitor_without_the_workers(self):
        template = {key: value for key, value in self.workers.items() if key not in renderer.WORKER_ENABLE_KEYS}
        with self.assertRaises(renderer.CompositionError) as raised:
            renderer.render(template, self.resolved, self.pending, self.overrides)
        self.assertIn("health monitor", str(raised.exception))
        template[renderer.HEALTH_MONITOR_KEY] = "false"
        renderer.render(template, self.resolved, self.pending, self.overrides)

    def test_render_refuses_a_non_boolean_health_monitor_switch(self):
        template = dict(self.workers)
        template[renderer.HEALTH_MONITOR_KEY] = "yes"
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

    def test_signer_identity_accepts_only_an_exact_github_actions_workflow_ref(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "p.json"
            for value in ("https://example.com/signer", "https://github.com/valence-works/elsa-production-image",
                          "https://github.com/valence-works/*/.github/workflows/build.yml@refs/heads/main"):
                path.write_text(json.dumps({"parameters": {"ReleaseProducerSignatureSubject": value}}))
                with self.assertRaises(renderer.CompositionError, msg=value):
                    renderer.load_parameters(path)
            path.write_text(json.dumps({"parameters": {"ReleaseProducerOidcIssuer": "https://issuer.example"}}))
            with self.assertRaises(renderer.CompositionError):
                renderer.load_parameters(path)

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

    def test_rollback_disables_exactly_the_three_switches_and_the_health_monitor(self):
        payload = renderer.load_rollback()
        self.assertEqual({*renderer.WORKER_ENABLE_KEYS, renderer.HEALTH_MONITOR_KEY}, {entry["name"] for entry in payload})
        self.assertTrue(all(entry["value"] == "false" for entry in payload))

    def test_rollback_that_leaves_the_health_monitor_on_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "rollback.json"
            path.write_text(json.dumps([{"name": key, "value": "false", "slotSetting": False}
                                        for key in renderer.WORKER_ENABLE_KEYS]))
            with self.assertRaises(renderer.CompositionError):
                renderer.load_rollback(path)

    def test_staging_handoff_profile_contains_only_the_approved_non_secret_settings(self):
        settings = renderer.load_staging_handoff()
        self.assertEqual(renderer.HANDOFF_SETTINGS_KEYS, set(settings))
        self.assertEqual("true", settings[renderer.HANDOFF_ENABLE_KEY])
        self.assertEqual(renderer.STAGING_CONTROL_ORIGIN, settings["ManagedElsa__Handoff__Issuer"])
        self.assertEqual(renderer.STAGING_CLOUD_CONTINUATION_URL,
                         settings["ManagedElsa__Handoff__CloudContinuationUrl"])
        self.assertFalse(any("PrivateKey" in key or "Pem" in key for key in settings))

    def test_staging_handoff_profile_rejects_scope_drift_and_unapproved_keys(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "handoff.json"
            settings = renderer.load_staging_handoff()
            for key, value in (("ManagedElsa__Handoff__Issuer", "https://cloud.elsaworkflows.io"),
                               ("ManagedElsa__Handoff__CloudContinuationUrl", "https://production.example/dashboard"),
                               (renderer.HANDOFF_ENABLE_KEY, "false")):
                changed = dict(settings)
                changed[key] = value
                path.write_text(json.dumps(changed))
                with self.assertRaises(renderer.CompositionError):
                    renderer.load_staging_handoff(path)
            with_key = dict(settings)
            with_key["ManagedElsa__Handoff__ActivePrivateKeyPem"] = "must-not-be-rendered"
            path.write_text(json.dumps(with_key))
            with self.assertRaises(renderer.CompositionError):
                renderer.load_staging_handoff(path)
            for key, value in (("$private", "must-not-be-rendered"),
                               ("ManagedElsa__Handoff__Issuer", None)):
                changed = dict(settings)
                changed[key] = value
                path.write_text(json.dumps(changed))
                with self.assertRaises(renderer.CompositionError):
                    renderer.load_staging_handoff(path)

    def test_handoff_rollback_disables_only_the_handoff_switch(self):
        payload = renderer.load_handoff_rollback()
        self.assertEqual([{"name": renderer.HANDOFF_ENABLE_KEY, "value": "false", "slotSetting": False}], payload)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "handoff-rollback.json"
            for invalid in ([], [{"name": renderer.HANDOFF_ENABLE_KEY, "value": "true", "slotSetting": False}],
                            [{"name": renderer.HANDOFF_ENABLE_KEY, "value": "false", "slotSetting": False},
                             {"name": "ManagedElsa__Handoff__ActivePrivateKeyPem", "value": "x", "slotSetting": False}]):
                path.write_text(json.dumps(invalid))
                with self.assertRaises(renderer.CompositionError):
                    renderer.load_handoff_rollback(path)

    def test_cli_status_and_pending_exit_code(self):
        self.assertEqual(0, renderer.main(["status"]))
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(0, renderer.main(["workers", "--output", str(Path(directory) / "w.json")]))
            self.assertEqual(0, renderer.main(["release-verification", "--output", str(Path(directory) / "v.json")]))
            self.assertEqual(0, renderer.main(["rollback", "--output", str(Path(directory) / "r.json")]))
            handoff = Path(directory) / "h.json"
            self.assertEqual(0, renderer.main(["handoff", "--output", str(handoff)]))
            self.assertEqual(0, renderer.main(["handoff-rollback", "--output", str(Path(directory) / "hr.json")]))
            self.assertEqual(renderer.HANDOFF_SETTINGS_KEYS,
                             {entry["name"] for entry in json.loads(handoff.read_text())})

    def test_staging_worker_payload_bounds_concurrency_and_cold_start_commands(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "staging.json"
            self.assertEqual(0, renderer.main(["workers", "--environment", "staging", "--output", str(output)]))
            settings = {entry["name"]: entry["value"] for entry in json.loads(output.read_text())}
        self.assertEqual("1", settings["Deployment__AzureProvider__BatchSize"])
        self.assertEqual("00:45:00", settings["Deployment__AzureProvider__Runner__CommandTimeout"])

    def test_cli_has_no_value_override_so_only_the_checked_in_parameters_reach_production(self):
        for argv in (["workers", "--output", "x.json", "--set", "SqlBootstrapIp=203.0.113.10"],
                     ["workers", "--output", "x.json", "--parameters", "other.json"]):
            with self.assertRaises(SystemExit):
                renderer.main(argv)

    def test_staging_renderer_fails_closed_when_authority_is_incomplete(self):
        with tempfile.TemporaryDirectory() as directory:
            parameters = json.loads(renderer.STAGING_PARAMETERS.read_text())
            parameters["parameters"]["ProvisionerClientId"] = {"pending": "#561"}
            pending_file = Path(directory) / "pending.json"
            pending_file.write_text(json.dumps(parameters))
            with mock.patch.object(renderer, "STAGING_PARAMETERS", pending_file):
                self.assertEqual(0, renderer.main(["status", "--environment", "staging"]))
                for command in ("workers", "release-verification"):
                    output = Path(directory) / f"{command}.json"
                    self.assertEqual(2, renderer.main([command, "--environment", "staging", "--output", str(output)]))
                    self.assertFalse(output.exists())

    def test_staging_authority_rejects_production_identity_scope_and_origin(self):
        staging = dict(self.resolved)
        staging.update({
            "WorkloadSubscriptionId": "11111111-1111-4111-8111-111111111111",
            "ProvisionerClientId": "22222222-2222-4222-8222-222222222222",
            "ProvisionerPrincipalId": "33333333-3333-4333-8333-333333333333",
            "ProvisionerIdentityName": "mi-elsa-cloud-provisioner-stage-weu",
            "SqlBootstrapIp": "203.0.113.15",
            "WorkloadResourceGroupName": "rg-elsa-cloud-workloads-platform-staging-weu",
            "ReleaseVerificationClientId": "44444444-4444-4444-8444-444444444444",
            "RegistryDeploymentMetadataRoleAssignmentId": "stage-metadata-assignment",
            "RegistryRoleAdministrationAssignmentId": "stage-registry-assignment",
            "ControlPlaneOrigin": renderer.STAGING_CONTROL_ORIGIN,
        })
        renderer.validate_staging_authority(staging, self.resolved)
        for name in ("WorkloadSubscriptionId", "ProvisionerClientId", "ProvisionerPrincipalId",
                     "ProvisionerIdentityName", "SqlBootstrapIp", "WorkloadResourceGroupName",
                     "ReleaseVerificationClientId",
                     "RegistryDeploymentMetadataRoleAssignmentId", "RegistryRoleAdministrationAssignmentId",
                     "ControlPlaneOrigin"):
            with self.subTest(name=name):
                invalid = dict(staging)
                invalid[name] = self.resolved[name]
                with self.assertRaises(renderer.CompositionError):
                    renderer.validate_staging_authority(invalid, self.resolved)
        invalid = dict(staging)
        invalid["RegistrySubscriptionId"] = staging["WorkloadSubscriptionId"]
        with self.assertRaises(renderer.CompositionError):
            renderer.validate_staging_authority(invalid, self.resolved)
        incomplete = dict(staging)
        del incomplete["ProvisionerClientId"]
        with self.assertRaises(renderer.CompositionError):
            renderer.validate_staging_authority(incomplete, self.resolved)


if __name__ == "__main__":
    unittest.main()
