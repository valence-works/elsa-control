#!/usr/bin/env python3
"""Compile and inspect the Control API Key Vault template (#831)."""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
LIB = ROOT / "scripts" / "lib"
if str(LIB) not in sys.path:
    sys.path.insert(0, str(LIB))
MAIN = ROOT / "infra/api-key-vault/api-key-vault.module.bicep"
README = ROOT / "infra/api-key-vault/README.md"
CHECK_SCRIPT = ROOT / "scripts/check-key-vault-references.sh"
WEBAPP_ID = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test/providers/Microsoft.Web/sites/api-test"
STAGING_WEBAPP_ID = "/subscriptions/8e23037a-420f-4ad0-9594-9d194de29e84/resourceGroups/rg-valence-control-staging/providers/Microsoft.Web/sites/api-tud53zotij43k"
STAGING_EXCLUDED = (
    "Billing__Stripe__WebhookSources__0__SigningSecret",
    "Billing__Stripe__WebhookSources__1__SigningSecret",
)
SECRETS_USER = "4633458b-17de-408a-b874-0445c86b69e6"


class ApiKeyVaultInfrastructureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        az = shutil.which("az")
        if az is None:
            raise RuntimeError("Azure CLI with Bicep is required for API Key Vault contract tests")
        result = subprocess.run(
            [az, "bicep", "build", "--file", str(MAIN), "--stdout"],
            capture_output=True,
            text=True,
            check=False,
            timeout=120,
        )
        if result.returncode:
            raise AssertionError(result.stderr)
        cls.template = json.loads(result.stdout)
        cls.by_type: dict[str, list[dict]] = {}
        for resource in cls.template["resources"]:
            cls.by_type.setdefault(resource["type"], []).append(resource)
        cls.source = MAIN.read_text()

    def test_vault_uses_rbac_soft_delete_purge_protection_and_a_firewall(self) -> None:
        vaults = self.by_type["Microsoft.KeyVault/vaults"]
        self.assertEqual(1, len(vaults))
        props = vaults[0]["properties"]
        self.assertTrue(props["enableRbacAuthorization"])
        self.assertTrue(props["enableSoftDelete"])
        self.assertTrue(props["enablePurgeProtection"])
        self.assertEqual(90, props["softDeleteRetentionInDays"])
        self.assertEqual("Enabled", props["publicNetworkAccess"])
        acls = props["networkAcls"]
        self.assertEqual("Deny", acls["defaultAction"])
        self.assertEqual("None", acls["bypass"])
        self.assertIn("virtualNetworkRules", acls)
        self.assertNotIn("ignoreMissingVnetServiceEndpoint", self.source)
        self.assertNotIn("ignoreMissingVnetServiceEndpoint", json.dumps(self.template))
        self.assertIn("ipRules", acls)
        self.assertNotIn("Microsoft.KeyVault/vaults/secrets", json.dumps(self.template))

    def test_role_assignment_is_exactly_secrets_user_for_the_api_identity(self) -> None:
        assignments = self.by_type["Microsoft.Authorization/roleAssignments"]
        self.assertEqual(1, len(assignments))
        self.assertEqual(SECRETS_USER, self.template["variables"]["keyVaultSecretsUserRoleId"])
        assignment = assignments[0]
        props = assignment["properties"]
        self.assertEqual("ServicePrincipal", props["principalType"])
        self.assertIn("variables('keyVaultSecretsUserRoleId')", props["roleDefinitionId"])
        self.assertIn("Microsoft.KeyVault/vaults", assignment["scope"])
        self.assertIn("parameters('apiIdentityPrincipalId')", props["principalId"])
        self.assertNotIn("deployIdentityPrincipalId", self.source)
        self.assertNotIn("b86a8fe4-44ce-4948-aee5-eccb2c155cd7", json.dumps(props))
        self.assertEqual(2, len(self.template["resources"]))
        self.assertEqual(
            {
                "Microsoft.KeyVault/vaults",
                "Microsoft.Authorization/roleAssignments",
            },
            set(self.by_type),
        )

    def test_readme_records_operator_write_path_and_no_ci_secret_write(self) -> None:
        readme = README.read_text()
        self.assertIn("operator writes", readme.lower())
        self.assertIn("never writes a secret value", readme.lower())
        self.assertIn("Authentication-ApiKey", readme)
        self.assertIn("Key Vault Secrets User", readme)
        self.assertIn("secrets/getSecret", readme)
        self.assertIn("no Key Vault data role", readme)
        self.assertIn("configreferences", readme)
        self.assertNotIn("can use the GitHub deploy identity", readme)
        self.assertIn("Key Vault Contributor", readme)
        self.assertIn("subnets/joinViaServiceEndpoint/action", readme)
        self.assertIn("only management-plane read on the", readme)

    def test_is_key_vault_reference_accepts_only_versionless_secret_uri(self) -> None:
        from key_vault_app_settings import is_key_vault_reference

        valid = (
            "@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/Billing-Stripe-SecretKey)"
        )
        self.assertTrue(is_key_vault_reference(valid))
        self.assertTrue(is_key_vault_reference(f"  {valid}  "))
        self.assertFalse(is_key_vault_reference("@Microsoft.KeyVault(foo)"))
        self.assertFalse(is_key_vault_reference("@Microsoft.KeyVault(SecretUri=foo)"))
        self.assertFalse(
            is_key_vault_reference(
                "@Microsoft.KeyVault(SecretUri=https://kv.example.vault.azure.net/secrets/X/0123456789abcdef0123456789abcdef)"
            )
        )
        self.assertFalse(is_key_vault_reference("@Microsoft.KeyVault(VaultName=kv;SecretName=X)"))
        self.assertFalse(is_key_vault_reference("sk_live_not_a_reference"))
        self.assertFalse(is_key_vault_reference(None))

    def test_guard_keys_match_the_python_secret_setting_map(self) -> None:
        from key_vault_app_settings import SECRET_APP_SETTINGS

        guard = (ROOT / "src/Hosting/ElsaControl.Api/Hosting/UnresolvedKeyVaultReferenceGuard.cs").read_text()
        expected = {name.replace("__", ":") for name in SECRET_APP_SETTINGS}
        found = set()
        for line in guard.splitlines():
            stripped = line.strip().strip(',').strip('"')
            if stripped.startswith("Authentication:") or stripped.startswith("Billing:") or stripped.startswith("ManagedElsa:"):
                found.add(stripped)
        self.assertEqual(expected, found)


    def test_optional_bff_credential_settings_are_separate_from_the_guard_keys(self) -> None:
        from key_vault_app_settings import (
            OPTIONAL_SECRET_APP_SETTINGS,
            REQUIRED_SECRET_APP_SETTING_NAMES,
            SECRET_APP_SETTING_NAMES,
            SECRET_APP_SETTINGS,
        )

        self.assertEqual(
            {
                "RateLimiting__BffCredentials__Sources__0__Secret": "RateLimiting-BffCredentials-cloud-Secret",
                "RateLimiting__BffCredentials__Sources__0__PreviousSecret": "RateLimiting-BffCredentials-cloud-PreviousSecret",
                "RateLimiting__BffCredentials__Sources__1__Secret": "RateLimiting-BffCredentials-hub-Secret",
                "RateLimiting__BffCredentials__Sources__1__PreviousSecret": "RateLimiting-BffCredentials-hub-PreviousSecret",
            },
            OPTIONAL_SECRET_APP_SETTINGS,
        )
        self.assertEqual(tuple(SECRET_APP_SETTINGS), REQUIRED_SECRET_APP_SETTING_NAMES)
        self.assertEqual(set(SECRET_APP_SETTINGS) | set(OPTIONAL_SECRET_APP_SETTINGS), set(SECRET_APP_SETTING_NAMES))
        self.assertFalse(set(SECRET_APP_SETTINGS) & set(OPTIONAL_SECRET_APP_SETTINGS))
        for secret in OPTIONAL_SECRET_APP_SETTINGS.values():
            self.assertIn(f"'{secret}'", self.source)
        guard = (ROOT / "src/Hosting/ElsaControl.Api/Hosting/UnresolvedKeyVaultReferenceGuard.cs").read_text()
        self.assertNotIn("RateLimiting:", guard)
        # The feature validator refuses an unresolved reference while the section is enabled.
        validator = (ROOT / "src/Hosting/ElsaControl.Api/Authentication/ControlRateLimitBffCredential.cs").read_text()
        self.assertIn("UnresolvedKeyVaultReferenceGuard.ReferencePrefix", validator)

    def run_check(
        self,
        properties: dict,
        plain_names: tuple[str, ...] = (),
        payload: dict | None = None,
        webapp_id: str = WEBAPP_ID,
    ) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as temporary:
            fake_az = Path(temporary) / "az"
            fake_az.write_text(
                "#!/usr/bin/env bash\n"
                "set -euo pipefail\n"
                "case \"$1 $2\" in\n"
                "  'webapp show') printf '%s\\n' \"$FAKE_WEBAPP_ID\" ;;\n"
                "  'rest --method') printf '%s\\n' \"$FAKE_REFERENCES\" ;;\n"
                "  'webapp config') printf '%s\\n' \"$FAKE_SETTING_NAMES\" ;;\n"
                "  'resource show') printf 'false\\n' ;;\n"
                "  *) exit 41 ;;\n"
                "esac\n"
            )
            fake_az.chmod(0o755)
            environment = os.environ.copy()
            environment["PATH"] = f"{temporary}{os.pathsep}{environment['PATH']}"
            environment["FAKE_WEBAPP_ID"] = webapp_id
            # The ApiKVReferenceCollection shape that api-version 2022-03-01 returns.
            environment["FAKE_REFERENCES"] = json.dumps(payload if payload is not None else {
                "value": [
                    {
                        "id": f"{webapp_id}/config/configreferences/appsettings/{name}",
                        "name": name,
                        "type": "Microsoft.Web/sites/config/configreferences",
                        "properties": entry,
                    }
                    for name, entry in properties.items()
                ]
            })
            # The site's setting names: every reference plus any plain-value setting.
            environment["FAKE_SETTING_NAMES"] = "\n".join([*properties, *plain_names])
            return subprocess.run(
                [str(CHECK_SCRIPT), "--resource-group", webapp_id.split("/")[4], "--name", webapp_id.rsplit("/", 1)[-1]],
                cwd=ROOT,
                env=environment,
                capture_output=True,
                text=True,
                check=False,
                timeout=60,
            )

    def test_staging_checker_requires_six_and_refuses_per_source_secrets(self) -> None:
        from key_vault_app_settings import SECRET_APP_SETTINGS

        resolved = {name: {"status": "Resolved"} for name in SECRET_APP_SETTINGS if name not in STAGING_EXCLUDED}
        result = self.run_check(resolved, webapp_id=STAGING_WEBAPP_ID)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        for name in STAGING_EXCLUDED:
            self.assertIn(f"{name}\tNotSet", result.stdout)
        self.assertIn("basicPublishingCredentialsPolicies/ftp\tallow=false", result.stdout)

        for name in STAGING_EXCLUDED:
            with self.subTest(name=name, state="reference"):
                result = self.run_check(resolved | {name: {"status": "Resolved"}}, webapp_id=STAGING_WEBAPP_ID)
                self.assertEqual(2, result.returncode)
                self.assertIn(f"{name}\tUnexpectedSetting", result.stdout)
            with self.subTest(name=name, state="plain"):
                result = self.run_check(resolved, plain_names=(name,), webapp_id=STAGING_WEBAPP_ID)
                self.assertEqual(2, result.returncode)
                self.assertIn(f"{name}\tUnexpectedSetting", result.stdout)

        resolved.pop("Authentication__ApiKey")
        result = self.run_check(resolved, webapp_id=STAGING_WEBAPP_ID)
        self.assertEqual(2, result.returncode)
        self.assertIn("Authentication__ApiKey\tMissing", result.stdout)

    def test_six_secret_exception_does_not_apply_to_production_or_other_subscriptions(self) -> None:
        from key_vault_app_settings import SECRET_APP_SETTINGS

        resolved = {name: {"status": "Resolved"} for name in SECRET_APP_SETTINGS if name not in STAGING_EXCLUDED}
        targets = (
            STAGING_WEBAPP_ID.replace("rg-valence-control-staging", "rg-valence-control-prod").replace("api-tud53zotij43k", "api-m5uymkuaf222o"),
            STAGING_WEBAPP_ID.replace("8e23037a-420f-4ad0-9594-9d194de29e84", "00000000-0000-0000-0000-000000000000"),
        )
        for target in targets:
            with self.subTest(target=target):
                missing = self.run_check(resolved, webapp_id=target)
                self.assertEqual(2, missing.returncode)
                for name in STAGING_EXCLUDED:
                    self.assertIn(f"{name}\tMissing", missing.stdout)
                full = self.run_check(resolved | {name: {"status": "Resolved"} for name in STAGING_EXCLUDED}, webapp_id=target)
                self.assertEqual(0, full.returncode, full.stdout + full.stderr)

    def test_check_script_reports_optional_settings_without_requiring_them(self) -> None:
        from key_vault_app_settings import OPTIONAL_SECRET_APP_SETTINGS, SECRET_APP_SETTINGS

        resolved = {name: {"status": "Resolved"} for name in SECRET_APP_SETTINGS}
        off = self.run_check(resolved)
        self.assertEqual(0, off.returncode, off.stdout + off.stderr)
        for name in OPTIONAL_SECRET_APP_SETTINGS:
            self.assertIn(f"{name}\tNotSet", off.stdout)
        self.assertIn("basicPublishingCredentialsPolicies/scm\tallow=false", off.stdout)

        cloud_secret = "RateLimiting__BffCredentials__Sources__0__Secret"
        on = self.run_check(resolved | {cloud_secret: {"status": "Resolved"}})
        self.assertEqual(0, on.returncode, on.stdout + on.stderr)
        self.assertIn(f"{cloud_secret}\tResolved", on.stdout)

        broken = self.run_check(resolved | {cloud_secret: {"status": "SecretNotFound"}})
        self.assertEqual(2, broken.returncode, broken.stdout + broken.stderr)
        self.assertIn(f"{cloud_secret}\tSecretNotFound", broken.stdout)

        # A secret setting stored as a plain value is not a reference and fails.
        plain = self.run_check(resolved, plain_names=(cloud_secret,))
        self.assertEqual(2, plain.returncode, plain.stdout + plain.stderr)
        self.assertIn(f"{cloud_secret}\tNotAReference", plain.stdout)

        required_missing = dict(resolved)
        required_missing.pop("Authentication__ApiKey")
        missing = self.run_check(required_missing)
        self.assertEqual(2, missing.returncode, missing.stdout + missing.stderr)
        self.assertIn("Authentication__ApiKey\tMissing", missing.stdout)

        # The pre-fix parser read a top-level "properties" map, which the API never returns.
        legacy = self.run_check(resolved, payload={"properties": resolved})
        self.assertNotEqual(0, legacy.returncode, legacy.stdout + legacy.stderr)
        self.assertIn("reference status payload is invalid", legacy.stderr)

        paged = self.run_check(resolved, payload={"value": [], "nextLink": "https://example.test/next"})
        self.assertNotEqual(0, paged.returncode, paged.stdout + paged.stderr)
        self.assertIn("reference status payload is paged", paged.stderr)


PLAIN_SECRET_VALUES = {
    "Authentication__ApiKey": "plain-admin-api-key-fixture",
    "Billing__Stripe__SecretKey": "rk_live_plainModeFixture",
    "Billing__Stripe__WebhookSigningSecret": "whsec_plainModeFixture",
}


def reference(name: str) -> str:
    return f"@Microsoft.KeyVault(SecretUri=https://ecapikvtest.vault.azure.net/secrets/{name.replace('__', '-')})"


class KeyVaultModeDecisionTests(unittest.TestCase):
    """The shared plain/reference/mixed classifier and the mode table (#865)."""

    def _refusal(self, values: dict[str, str], *, secrets_written: bool = False) -> str:
        from key_vault_app_settings import KeyVaultModeError, select_secret_settings_mode

        with self.assertRaises(KeyVaultModeError) as raised:
            select_secret_settings_mode(values, secrets_written=secrets_written)
        reason = str(raised.exception)
        for value in PLAIN_SECRET_VALUES.values():
            self.assertNotIn(value, reason)
        self.assertNotIn("SecretUri", reason)
        return reason

    def test_classifier_returns_plain_reference_or_mixed_for_present_listed_names(self) -> None:
        from key_vault_app_settings import MIXED, PLAIN, REFERENCE, classify_secret_settings

        references = {name: reference(name) for name in PLAIN_SECRET_VALUES}
        mixed = dict(PLAIN_SECRET_VALUES, Billing__Stripe__SecretKey=reference("Billing__Stripe__SecretKey"))

        self.assertEqual(PLAIN, classify_secret_settings({}))
        self.assertEqual(PLAIN, classify_secret_settings({"Billing__Stripe__Enabled": "true"}))
        self.assertEqual(PLAIN, classify_secret_settings(PLAIN_SECRET_VALUES))
        self.assertEqual(REFERENCE, classify_secret_settings(references))
        self.assertEqual(MIXED, classify_secret_settings(mixed))
        # Unlisted names never change the classification.
        self.assertEqual(REFERENCE, classify_secret_settings(references | {"Unlisted__Secret": "plain"}))

    def test_malformed_reference_counts_as_a_reference_and_is_named_as_malformed(self) -> None:
        from key_vault_app_settings import REFERENCE, classify_secret_settings

        values = {"Billing__Stripe__SecretKey": "@Microsoft.KeyVault(foo)"}
        self.assertEqual(REFERENCE, classify_secret_settings(values))

        reason = self._refusal(values)
        self.assertIn("malformed Key Vault reference: Billing__Stripe__SecretKey", reason)
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", reason)
        self.assertNotIn("@Microsoft.KeyVault(foo)", reason)

    def test_mode_table(self) -> None:
        from key_vault_app_settings import KEY_VAULT_MODE, PRE_CUTOVER_MODE, select_secret_settings_mode

        references = {name: reference(name) for name in PLAIN_SECRET_VALUES}
        mixed = dict(PLAIN_SECRET_VALUES, Billing__Stripe__SecretKey=reference("Billing__Stripe__SecretKey"))

        self.assertEqual(PRE_CUTOVER_MODE, select_secret_settings_mode(PLAIN_SECRET_VALUES, secrets_written=False))
        self.assertEqual(PRE_CUTOVER_MODE, select_secret_settings_mode({}, secrets_written=False))

        reason = self._refusal(references)
        self.assertIn("classification: reference", reason)
        self.assertIn("Billing__Stripe__SecretKey", reason)
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", reason)

        reason = self._refusal(mixed)
        self.assertIn("mixed", reason)
        self.assertIn("references: Billing__Stripe__SecretKey;", reason)
        self.assertIn("plain: Authentication__ApiKey, Billing__Stripe__WebhookSigningSecret", reason)
        self.assertIn("API_KEY_VAULT_SECRETS_WRITTEN=true", reason)

        # With the variable set the strict #863 checks decide; values are not read.
        for values in (references, mixed, PLAIN_SECRET_VALUES):
            self.assertEqual(KEY_VAULT_MODE, select_secret_settings_mode(values, secrets_written=True))

    def test_secrets_written_is_a_case_insensitive_true(self) -> None:
        from key_vault_app_settings import key_vault_secrets_written

        self.assertTrue(key_vault_secrets_written({"API_KEY_VAULT_SECRETS_WRITTEN": "true"}))
        self.assertTrue(key_vault_secrets_written({"API_KEY_VAULT_SECRETS_WRITTEN": "TRUE"}))
        self.assertTrue(key_vault_secrets_written({"API_KEY_VAULT_SECRETS_WRITTEN": "True"}))
        self.assertFalse(key_vault_secrets_written({}))
        self.assertFalse(key_vault_secrets_written({"API_KEY_VAULT_SECRETS_WRITTEN": ""}))
        self.assertFalse(key_vault_secrets_written({"API_KEY_VAULT_SECRETS_WRITTEN": "false"}))
        self.assertFalse(key_vault_secrets_written({"API_KEY_VAULT_SECRETS_WRITTEN": "1"}))

    def test_optional_bff_credential_settings_do_not_change_the_mode(self) -> None:
        # #862 x #865: an opt-in BFF credential written as a reference must not
        # turn a plain pre-cutover site mixed. Key Vault mode still requires it
        # to be a reference.
        from key_vault_app_settings import (
            KeyVaultModeError,
            PLAIN,
            PRE_CUTOVER_MODE,
            classify_secret_settings,
            require_secret_settings_are_all_references,
            select_secret_settings_mode,
        )

        bff_secret = "RateLimiting__BffCredentials__Sources__1__Secret"
        for bff_value in (reference(bff_secret), "plain-bff-credential-fixture"):
            values = PLAIN_SECRET_VALUES | {bff_secret: bff_value}
            self.assertEqual(PLAIN, classify_secret_settings(values))
            self.assertEqual(PRE_CUTOVER_MODE, select_secret_settings_mode(values, secrets_written=False))

        references = {name: reference(name) for name in PLAIN_SECRET_VALUES}
        require_secret_settings_are_all_references(references | {bff_secret: reference(bff_secret)})
        with self.assertRaises(KeyVaultModeError) as raised:
            require_secret_settings_are_all_references(references | {bff_secret: "plain-bff-credential-fixture"})
        self.assertIn(f"plain: {bff_secret}", str(raised.exception))
        self.assertNotIn("plain-bff-credential-fixture", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
