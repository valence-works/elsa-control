#!/usr/bin/env python3
"""Offline contract checks for the disposable Azure workload proof."""

from __future__ import annotations

import json
import re
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
PROOF = ROOT / "infra" / "azure-workload-proof"
MAIN = PROOF / "main.bicep"
RECOVERY_TARGET = PROOF / "recovery-target.bicep"
APP = PROOF / "modules" / "container-app.bicep"
ENVIRONMENT = PROOF / "modules" / "container-apps-environment.bicep"
ACR_ROLE = PROOF / "acr-pull-role.bicep"
VAULT = PROOF / "modules" / "key-vault.bicep"
SQL = PROOF / "modules" / "sql.bicep"
RUNBOOK = ROOT / "scripts" / "azure-workload-proof.sh"
RESTORE_RUNBOOK = ROOT / "scripts" / "azure-workload-restore-proof.sh"
RUNBOOK_LIB = ROOT / "scripts" / "lib" / "azure-workload-proof.sh"
REGENERATE_INFRA = ROOT / "dev" / "regenerate-infra.sh"


class AzureWorkloadProofTests(unittest.TestCase):
    def test_checked_in_shape(self) -> None:
        expected = {
            "main.bicep",
            "recovery-target.bicep",
            "acr-pull-role.bicep",
            "sql-bootstrap.sql",
            "modules/identity.bicep",
            "modules/key-vault.bicep",
            "modules/observability.bicep",
            "modules/sql.bicep",
            "modules/container-apps-environment.bicep",
            "modules/container-app.bicep",
        }
        actual = {str(path.relative_to(PROOF)) for path in PROOF.rglob("*") if path.is_file()}
        self.assertTrue(expected <= actual)

    def test_image_is_digest_only(self) -> None:
        source = MAIN.read_text()
        self.assertIn("param imageDigest string", source)
        self.assertIn("@minLength(64)", source)
        self.assertIn("@maxLength(64)", source)
        self.assertIn("@sha256:${toLower(imageDigest)}", source)
        self.assertNotRegex(source, r"param\s+imageTag\b")

    def test_sql_is_entra_only_and_bootstrap_is_explicit(self) -> None:
        source = SQL.read_text()
        self.assertIn("azureADOnlyAuthentication: true", source)
        self.assertIn("administratorType: 'ActiveDirectory'", source)
        self.assertIn("bootstrapObjectId", source)
        self.assertNotIn("administratorPassword", source)
        bootstrap = (PROOF / "sql-bootstrap.sql").read_text()
        self.assertIn("TYPE = E", bootstrap)
        self.assertIn("__WORKLOAD_IDENTITY_CLIENT_ID__", bootstrap)
        self.assertIn("DECLARE @clientId UNIQUEIDENTIFIER", bootstrap)
        self.assertIn("CONVERT(VARBINARY(16), @clientId)", bootstrap)
        self.assertNotIn("REPLACE('__WORKLOAD_IDENTITY_CLIENT_ID__'", bootstrap)
        self.assertNotIn("__WORKLOAD_IDENTITY_OBJECT_ID__", bootstrap)
        self.assertNotIn("Directory Readers", bootstrap)
        self.assertIn("db_ddladmin", bootstrap)

    def test_sql_restore_retention_is_explicit(self) -> None:
        source = SQL.read_text()
        main = MAIN.read_text()
        self.assertIn("backupShortTermRetentionPolicies@2023-08-01", source)
        self.assertIn("param shortTermRetentionDays int = 35", source)
        self.assertIn("param differentialBackupIntervalHours int = 12", source)
        self.assertIn("retentionDays: shortTermRetentionDays", source)
        self.assertIn("diffBackupIntervalInHours: differentialBackupIntervalHours", source)
        self.assertIn("output sqlShortTermRetentionDays int", main)

    def test_recovery_target_is_new_isolated_compute_without_a_second_source_database(self) -> None:
        source = RECOVERY_TARGET.read_text()
        self.assertIn("proof: '129'", source)
        self.assertIn("'recovery-role': 'target'", source)
        self.assertIn("param restoredDatabaseId string", source)
        self.assertIn("param recoveryPointDigest string", source)
        self.assertIn("module workloadIdentity", source)
        self.assertIn("module vault", source)
        self.assertIn("module containerEnvironment", source)
        self.assertIn("module workload", source)
        self.assertNotIn("modules/sql.bicep", source)
        self.assertNotIn("listKeys", source)
        output_lines = [line.lower() for line in source.splitlines() if line.lstrip().startswith("output ")]
        self.assertTrue(output_lines)
        for line in output_lines:
            self.assertFalse(any(token in line for token in ("secret", "password", "sharedkey", "connectionstring")))

    def test_restore_runbook_is_opt_in_restore_to_new_and_health_gated(self) -> None:
        source = RESTORE_RUNBOOK.read_text()
        self.assertIn("set -Eeuo pipefail", source)
        self.assertIn("umask 077", source)
        self.assertIn("DISPOSABLE_PROOF_APPLY", source)
        self.assertIn("mktemp -d", source)
        self.assertIn('database_template="$proof_dir/recovery-database.bicep"', source)
        self.assertIn('--name "$database_restore_deployment" --template-file "$database_template"', source)
        self.assertIn('restorePointUtc="$restore_point_utc"', source)
        self.assertNotIn("az sql db restore", source)
        self.assertIn("earliestRestoreDate", source)
        self.assertIn("provider-confirmed recovery cutoff does not contain the pre-point workflow", source)
        self.assertNotIn("recoverableDatabases", source)
        self.assertIn("--mode verify --absent-workflow-id", source)
        self.assertIn("healthBeforeEligibility:true", source)
        self.assertIn("trafficMutated:false", source)
        self.assertIn("cleanup_owned_role_assignment", source)
        self.assertIn("cleanup_source_secret_assignment", source)
        self.assertNotIn("az role assignment show", source)
        self.assertIn("az sql db delete", source)
        self.assertIn("wait_for_target_group_absence", source)
        self.assertIn("sourcePreserved:true", source)
        self.assertIn("source_healthy", source)
        self.assertNotIn("set -x", source)
        self.assertNotRegex(source, r"--password(?:=|\s)")

    def test_proof_expiry_default_is_derived_at_runtime(self) -> None:
        library = RUNBOOK_LIB.read_text()
        self.assertIn("default_expiry_utc()", library)
        for runbook in (RUNBOOK, RESTORE_RUNBOOK):
            source = runbook.read_text()
            self.assertIn('expiry_utc=""', source)
            self.assertIn('expiry_utc="${expiry_utc:-$(default_expiry_utc)}"', source)
            self.assertNotIn('expiry_utc="2026-09-02"', source)

        result = subprocess.run(
            ["bash", "-c", 'source "$1"; default_expiry_utc', "bash", str(RUNBOOK_LIB)],
            check=True,
            capture_output=True,
            text=True,
        )
        self.assertRegex(result.stdout.strip(), r"^\d{4}-\d{2}-\d{2}$")

    def test_managed_identity_roles_are_narrow(self) -> None:
        acr = ACR_ROLE.read_text()
        vault = VAULT.read_text()
        self.assertIn("targetScope = 'resourceGroup'", acr)
        self.assertIn("7f951dda-4ed3-4680-a7ca-43fe172d538d", acr)
        self.assertIn("4633458b-17de-408a-b874-0445c86b69e6", vault)
        self.assertIn("b86a8fe4-44ce-4948-aee5-eccb2c155cd7", vault)
        self.assertIn("enableRbacAuthorization: true", vault)

    def test_app_has_safe_ingress_revision_and_probes(self) -> None:
        source = APP.read_text()
        for expected in (
            "activeRevisionsMode: 'Multiple'",
            "targetPort: 8080",
            "allowInsecure: false",
            "latestRevision: true",
            "minReplicas: 0",
            "type: 'Startup'",
            "type: 'Readiness'",
            "type: 'Liveness'",
            "path: '/health'",
            "path: '/alive'",
            "identity: workloadIdentityId",
            "keyVaultUrl:",
        ):
            self.assertIn(expected, source)
        self.assertIn("workloadProfileType: 'Consumption'", ENVIRONMENT.read_text())
        self.assertIn("Nuplane__Setup__Feeds__0__Name", source)
        self.assertIn("Nuplane__Setup__Feeds__1__ServiceIndex", source)
        self.assertIn("Nuplane__Setup__Feeds__2__IncludePatterns__0", source)
        self.assertIn("Nuplane__Setup__Feeds__2__IncludePatterns__1", source)
        self.assertIn("CShells__Shells__Default__Features__DefaultAdminUser__AdminUsername", source)
        self.assertIn("CShells__Shells__Default__Features__DefaultAdminUser__AdminPassword", source)
        self.assertIn("secretRef: adminCredentialRef", source)
        self.assertIn("stableTrafficRevisionName", source)
        self.assertIn("revisionName: stableTrafficRevisionName", source)
        main_source = MAIN.read_text()
        self.assertIn("param sqlWorkflowPackageVersion string", main_source)
        self.assertIn("param sqlQuartzPackageVersion string", main_source)
        self.assertIn("sqlWorkflowPackageVersion: sqlWorkflowPackageVersion", main_source)
        self.assertIn("sqlQuartzPackageVersion: sqlQuartzPackageVersion", main_source)
        self.assertNotIn("param sqlWorkflowPackageVersion string =", main_source)
        self.assertNotIn("param sqlQuartzPackageVersion string =", main_source)

    def test_deterministic_fingerprint_and_required_tags(self) -> None:
        source = MAIN.read_text()
        self.assertIn("var planInput =", source)
        self.assertIn("template=${toLower(templateFingerprint)}", source)
        self.assertIn("var planFingerprint = uniqueString(planInput)", source)
        self.assertIn("empty(workloadRevisionSuffix)", source)
        self.assertIn("'plan-fingerprint': planFingerprint", source)
        self.assertIn("deploymentName string = take('elsa108-${proofName}-${take(planFingerprint, 12)}', 64)", source)
        self.assertIn("proof: '108'", source)
        self.assertIn("owner: owner", source)
        self.assertIn("expiry: expiryUtc", source)

    def test_outputs_are_secret_safe(self) -> None:
        source = MAIN.read_text()
        output_lines = [line.lower() for line in source.splitlines() if line.lstrip().startswith("output ")]
        self.assertTrue(output_lines)
        for line in output_lines:
            self.assertFalse(any(token in line for token in ("secret", "password", "sharedkey", "connectionstring")))
        self.assertNotIn("@secure()", source)

    def test_no_edge_or_network_expansion(self) -> None:
        source = "\n".join(path.read_text().lower() for path in PROOF.rglob("*.bicep"))
        self.assertNotIn("frontdoor", source)
        self.assertNotIn("virtualnetwork", source)
        self.assertNotIn("microsoft.network/virtualnetworks", source)

    def test_aspire_regeneration_preserves_manual_proof(self) -> None:
        source = REGENERATE_INFRA.read_text()
        self.assertIn("preserved_infra_paths", source)
        self.assertIn("azure-production", source)
        self.assertIn("azure-workload-proof", source)
        self.assertIn("azure-customer-subscription", source)
        self.assertIn('mv "infra/$relative_path"', source)
        self.assertIn("trap restore_preserved_infra EXIT", source)
        self.assertLess(source.index("preserved_infra_paths"), source.index("rm -rf infra"))

    def test_runbook_is_fail_closed(self) -> None:
        source = RUNBOOK.read_text()
        library = RUNBOOK_LIB.read_text()
        bootstrap = (ROOT / "scripts" / "bootstrap-github-azure.sh").read_text()
        combined_source = source + "\n" + library
        self.assertIn("DISPOSABLE_PROOF_APPLY:-", source)
        self.assertIn("what-if requires an existing resource group", source)
        self.assertIn("az group delete", source)
        self.assertIn("--sql-bootstrap-ip", source)
        self.assertIn("--authentication-method ActiveDirectoryDefault", source)
        self.assertIn("sqlcmd '-?'", source)
        self.assertIn("temporary_firewall_rule", source)
        self.assertIn("ensure_exact_sql_bootstrap_admin", source)
        self.assertIn("az sql server ad-admin create", source)
        self.assertIn("az sql server ad-only-auth enable", source)
        self.assertIn("Refusing to replace an unexpected SQL server administrator", source)
        self.assertIn("az sql server ad-admin list", combined_source)
        self.assertNotIn("az sql server ad-admin delete", combined_source)
        self.assertNotIn("az sql server ad-only-auth disable", combined_source)
        self.assertIn("ensure_exact_sql_bootstrap_admin 1", source)
        self.assertIn("ensure_exact_sql_bootstrap_admin 0", source)
        self.assertIn("governed SQL administrator could not be verified after workload deployment", source)
        self.assertIn("openssl rand -base64 48 | tr -d '\\r\\n'", source)
        self.assertIn('seed_secret_if_missing admin-password "$temp_dir/admin-password"', source)
        firewall_deletes = [
            line for line in source.splitlines()
            if "az sql server firewall-rule delete" in line
        ]
        self.assertEqual(0, len(firewall_deletes))
        library_firewall_deletes = [
            line for line in library.splitlines()
            if "az sql server firewall-rule delete" in line
        ]
        self.assertEqual(1, len(library_firewall_deletes))
        self.assertNotIn("--yes", library_firewall_deletes[0])
        self.assertIn("keyvault purge", combined_source)
        self.assertIn("Refusing to adopt unrelated resource group", source)
        self.assertIn("different bootstrap identity", source)
        self.assertIn("external ACR cleanup cannot be proven", source)
        self.assertIn("external ACR cleanup was incomplete", source)
        self.assertIn("registry-subscription", source)
        self.assertIn('proof-name="$proof_name"', source)
        self.assertIn("acr_role_ready", source)
        self.assertNotIn('--role AcrPull --output json', source)
        self.assertIn("external_context=", source)
        self.assertIn('external_deployment_suffix="$(sha256_text', source)
        self.assertIn("tags.acrDeployment", source)
        self.assertIn("delete_and_verify_firewall_rule", combined_source)
        role_list_lines = [
            line.strip()
            for line in combined_source.splitlines()
            if "az role assignment list" in line
        ]
        self.assertGreaterEqual(len(role_list_lines), 6)
        self.assertTrue(all("--all" in line for line in role_list_lines), role_list_lines)
        self.assertTrue(
            all(not ("--all" in line and "--scope" in line) for line in role_list_lines),
            role_list_lines,
        )
        self.assertIn('validate_direct_acr_pull_assignment "$registry_id" "$assignment_json" "$cleanup_principal_id"', source)
        self.assertIn('has_direct_acr_pull_assignment "$registry_id" "$identity_principal_id" "$role_assignments_json"', source)
        self.assertIn('[[ "$assignment_scope_lower" == "$registry_id_lower" ]]', library)
        self.assertIn('valid_role_assignment_id "$registry_id" "$assignment_id"', library)
        bootstrap_role_list_lines = [
            line.strip()
            for line in bootstrap.splitlines()
            if "az role assignment list" in line
        ]
        self.assertEqual(1, len(bootstrap_role_list_lines))
        self.assertNotIn("--all", bootstrap_role_list_lines[0])
        self.assertIn('--scope "$scope"', bootstrap_role_list_lines[0])
        self.assertIn("[?scope=='$scope']", bootstrap_role_list_lines[0])
        self.assertIn("tags.acrPrincipal", source)
        self.assertIn("tags.acrRegistryId", source)
        self.assertIn("properties.outputs.roleAssignmentId.value", source)
        self.assertIn("stored ACR assignment does not match this proof identity, scope, and role", source)
        self.assertIn("ACR deployment records could not be read", source)
        self.assertIn("ACR role assignments could not be read", source)
        self.assertIn("identity-scoped ACR assignments could not be read", source)
        self.assertIn("az deployment group delete", combined_source)
        self.assertIn("az group exists", source)
        self.assertIn("list-deleted", library)
        self.assertRegex(source, r"\[\[ \"\$\{DISPOSABLE_PROOF_APPLY:-\}\" == YES \]\]")
        validation = (ROOT / "scripts" / "validate-azure-workload-proof.sh").read_text()
        self.assertIn("Compiled main template SHA-256", validation)
        self.assertIn('templateFingerprint="$compiled_fingerprint"', validation)
        self.assertIn('template_fingerprint="$(az bicep build', source)
        self.assertIn('"templateFingerprint=$template_fingerprint"', source)
        self.assertIn('workloadRevisionSuffix="$workload_revision_suffix"', source)
        self.assertIn("resolve_workload_revision_suffix", source)
        self.assertIn("resolve_stable_traffic_revision", source)
        self.assertIn("candidate_healthy", source)
        self.assertIn("stable traffic was preserved", source)
        self.assertIn("promote_workload_revision", library)
        self.assertIn("verify_single_revision_traffic", library)
        self.assertIn("verify_workload_traffic", library)
        self.assertNotIn("remove_owned_sql_bootstrap_admin", library)
        self.assertIn("delete_and_verify_role_assignment", library)
        self.assertIn("valid_role_assignment_id", library)
        self.assertIn("validate_direct_acr_pull_assignment", library)
        self.assertIn("delete_and_verify_group_deployment", library)
        self.assertIn("wait_for_resource_group_absence", library)
        self.assertIn("verify_proof_resource_inventory", combined_source)
        self.assertIn("unowned resource", library)
        self.assertIn("purge_and_verify_deleted_vault", library)
        self.assertIn("stored ACR deployment has no valid role-assignment output", source)
        self.assertLess(
            source.index('delete_and_verify_role_assignment "$registry_subscription_id" "$registry_id" "$role_assignment_id"'),
            source.index('delete_and_verify_group_deployment "$registry_subscription_id" "$registry_resource_group"'),
        )
        self.assertIn("--retry-all-errors", library)
        self.assertIn("/revisions?api-version=2024-03-01", source)
        self.assertIn(".nextLink // empty", source)
        self.assertIn("az tag update", source)
        self.assertIn("--operation Merge", source)
        self.assertIn("tags.acrDeployment", source)

    def test_revision_suffix_selection_is_deterministic(self) -> None:
        def select(current: str, revisions: list[str]) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [
                    "bash",
                    "-c",
                    'source "$1"; select_workload_revision_suffix plan123 "$2" proof-app "$3"',
                    "test",
                    str(RUNBOOK_LIB),
                    current,
                    json.dumps(revisions),
                ],
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual("plan123", select("bad", []).stdout.strip())
        occupied = ["proof-app--plan123", "proof-app--plan123-r1"]
        self.assertEqual("plan123-r2", select("bad", occupied).stdout.strip())
        self.assertEqual("plan123-r2", select("plan123-r2", occupied).stdout.strip())
        self.assertEqual("plan123-r2", select("plan123-r2", occupied).stdout.strip())

        exhausted = ["proof-app--plan123", *(f"proof-app--plan123-r{i}" for i in range(1, 1000))]
        result = select("bad", exhausted)
        self.assertEqual(5, result.returncode)
        self.assertIn("No free deterministic recovery revision suffix", result.stderr)

    def test_existing_split_traffic_fails_closed_without_latest_ready_fallback(self) -> None:
        script = r'''
source "$1"
proof_name=proof
resource_group=proof-rg
az() {
  case "$*" in
    *"resource list"*) printf '1\n' ;;
    *"configuration.ingress.traffic"*) printf '[{"revisionName":"stable-revision","weight":50},{"revisionName":"candidate-revision","weight":50}]\n' ;;
    *"latestReadyRevisionName"*) printf 'candidate-revision\n' ;;
    *) printf 'unexpected az call: %s\n' "$*" >&2; return 1 ;;
  esac
}
resolve_stable_traffic_revision proof-rg proof-app
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn("command not found", result.stderr)
        self.assertNotIn("latestReadyRevisionName", result.stderr)

    def test_firewall_cleanup_waits_for_absence_after_uncertain_delete(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            state = Path(temp_dir) / "state"
            calls = Path(temp_dir) / "calls"
            state.write_text("0")
            script = r'''
source "$1"
STATE_FILE="$2"
CALLS_FILE="$3"
az() {
  printf '%s\n' "$*" >>"$CALLS_FILE"
  if [[ "$*" == *"firewall-rule delete"* ]]; then return 1; fi
  count="$(<"$STATE_FILE")"
  count=$((count + 1))
  printf '%s' "$count" >"$STATE_FILE"
  if (( count < 3 )); then
    printf '[{"name":"temporary-rule"}]\n'
  else
    printf '[]\n'
  fi
}
sleep() { :; }
delete_and_verify_firewall_rule proof-sub proof-rg proof-sql temporary-rule 4 0
'''
            result = subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), str(state), str(calls)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            call_lines = calls.read_text().splitlines()
            self.assertEqual(1, sum("firewall-rule delete" in line for line in call_lines))
            self.assertEqual(3, sum("firewall-rule list" in line for line in call_lines))

    def test_failed_promotion_restores_stable_traffic(self) -> None:
        script = r'''
source "$1"
traffic_state=stable
az() {
  case "$*" in
    *"containerapp show"*)
      if [[ "$traffic_state" == candidate ]]; then
        printf '[{"revisionName":"candidate-revision","weight":100}]\n'
      else
        printf '[{"revisionName":"stable-revision","weight":100},{"revisionName":"candidate-revision","weight":0}]\n'
      fi
      ;;
    *)
      printf 'az:%s\n' "$*"
      [[ "$*" == *"candidate-revision=100"* ]] && traffic_state=candidate
      [[ "$*" == *"stable-revision=100"* ]] && traffic_state=stable
      return 0
      ;;
  esac
}
curl() { return 1; }
promote_workload_revision proof-rg proof-app stable-revision candidate-revision https://proof.invalid
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(5, result.returncode)
        calls = [line for line in result.stdout.splitlines() if line.startswith("az:")]
        self.assertEqual(2, len(calls))
        self.assertIn("candidate-revision=100", calls[0])
        self.assertIn("stable-revision=100 candidate-revision=0", calls[1])
        self.assertIn("Restored stable traffic", result.stderr)

    def test_inventory_rejects_mixed_resource_groups(self) -> None:
        base = "/subscriptions/proof-sub/resourceGroups/proof-rg"
        owned = [
            {"id": f"{base}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/proof-identity",
             "type": "Microsoft.ManagedIdentity/userAssignedIdentities"},
            {"id": f"{base}/providers/Microsoft.KeyVault/vaults/proof-kv",
             "type": "Microsoft.KeyVault/vaults"},
            {"id": f"{base}/providers/Microsoft.Sql/servers/proof-sql",
             "type": "Microsoft.Sql/servers"},
            {"id": f"{base}/providers/Microsoft.OperationalInsights/workspaces/proof-logs",
             "type": "Microsoft.OperationalInsights/workspaces"},
            {"id": f"{base}/providers/Microsoft.App/managedEnvironments/proof-aca",
             "type": "Microsoft.App/managedEnvironments"},
            {"id": f"{base}/providers/Microsoft.App/containerApps/proof-app",
             "type": "Microsoft.App/containerApps"},
        ]
        vault_id = f"{base}/providers/Microsoft.KeyVault/vaults/proof-kv"
        assignments = [
            {"scope": vault_id, "principalId": "A1111111-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
             "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"},
            {"scope": vault_id, "principalId": "B2222222-BBBB-BBBB-BBBB-BBBBBBBBBBBB",
             "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/b86a8fe4-44ce-4948-aee5-eccb2c155cd7"},
        ]
        script = r'''
source "$1"
RESOURCE_JSON="$2"
ASSIGNMENTS_JSON="$3"
az() {
  case "$*" in
    *"resource list"*) printf '%s\n' "$RESOURCE_JSON" ;;
    *"role assignment list"*) printf '%s\n' "$ASSIGNMENTS_JSON" ;;
    *"resourceGroups/proof-rg"*) printf '[]\n' ;;
    *) return 1 ;;
  esac
}
verify_proof_resource_inventory proof-sub proof-rg proof a1111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa b2222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb
'''
        exact = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB), json.dumps(owned), json.dumps(assignments)],
            capture_output=True, text=True, check=False)
        self.assertEqual(0, exact.returncode, exact.stderr)

        mixed = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB), json.dumps(owned + [
                {"id": f"{base}/providers/Microsoft.Storage/storageAccounts/unrelated",
                 "type": "Microsoft.Storage/storageAccounts"}]), json.dumps(assignments)],
            capture_output=True, text=True, check=False)
        self.assertNotEqual(0, mixed.returncode)
        self.assertIn("unowned resource", mixed.stderr)

    def test_inventory_rejects_extra_child_scoped_vault_role_assignment(self) -> None:
        base = "/subscriptions/proof-sub/resourceGroups/proof-rg"
        owned = [
            {"id": f"{base}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/proof-identity",
             "type": "Microsoft.ManagedIdentity/userAssignedIdentities"},
            {"id": f"{base}/providers/Microsoft.KeyVault/vaults/proof-kv",
             "type": "Microsoft.KeyVault/vaults"},
            {"id": f"{base}/providers/Microsoft.Sql/servers/proof-sql",
             "type": "Microsoft.Sql/servers"},
            {"id": f"{base}/providers/Microsoft.OperationalInsights/workspaces/proof-logs",
             "type": "Microsoft.OperationalInsights/workspaces"},
            {"id": f"{base}/providers/Microsoft.App/managedEnvironments/proof-aca",
             "type": "Microsoft.App/managedEnvironments"},
            {"id": f"{base}/providers/Microsoft.App/containerApps/proof-app",
             "type": "Microsoft.App/containerApps"},
        ]
        vault_id = f"{base}/providers/Microsoft.KeyVault/vaults/proof-kv"
        assignments = [
            {"scope": vault_id, "principalId": "A1111111-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
             "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"},
            {"scope": vault_id, "principalId": "B2222222-BBBB-BBBB-BBBB-BBBBBBBBBBBB",
             "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/b86a8fe4-44ce-4948-aee5-eccb2c155cd7"},
            {"scope": f"{vault_id}/secrets/unrelated", "principalId": "C3333333-CCCC-CCCC-CCCC-CCCCCCCCCCCC",
             "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/4633458b-17de-408a-b874-0445c86b69e6"},
        ]
        script = r'''
source "$1"
RESOURCE_JSON="$2"
ASSIGNMENTS_JSON="$3"
az() {
  case "$*" in
    *"resource list"*) printf '%s\n' "$RESOURCE_JSON" ;;
    *"role assignment list"*) printf '%s\n' "$ASSIGNMENTS_JSON" ;;
    *"resourceGroups/proof-rg"*) printf '[]\n' ;;
    *) return 1 ;;
  esac
}
verify_proof_resource_inventory proof-sub proof-rg proof a1111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa b2222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB), json.dumps(owned), json.dumps(assignments)],
            capture_output=True, text=True, check=False)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("vault RBAC inventory is not exact", result.stderr)

    def test_fallback_acr_cleanup_accepts_only_direct_registry_role_assignments(self) -> None:
        registry = "/subscriptions/proof-sub/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr"
        role = f"{registry}/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001"
        direct = json.dumps({
            "id": role,
            "scope": registry,
            "principalId": "A1111111-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d",
        })
        inherited = json.dumps({
            "id": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000002",
            "scope": "/subscriptions/proof-sub",
            "principalId": "A1111111-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            "roleDefinitionId": "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d",
        })
        script = r'''
source "$1"
validate_direct_acr_pull_assignment "$2" "$3" A1111111-AAAA-AAAA-AAAA-AAAAAAAAAAAA
'''
        self.assertEqual(
            0,
            subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), registry, direct],
                capture_output=True, text=True, check=False).returncode)
        self.assertNotEqual(
            0,
            subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), registry, inherited],
                capture_output=True, text=True, check=False).returncode)

    def test_acr_readiness_accepts_only_direct_matching_assignment(self) -> None:
        registry = "/subscriptions/proof-sub/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr"
        principal = "A1111111-AAAA-AAAA-AAAA-AAAAAAAAAAAA"
        role = "/subscriptions/proof-sub/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d"
        direct = {"scope": registry, "principalId": principal, "roleDefinitionId": role}
        inherited = {"scope": "/subscriptions/proof-sub", "principalId": principal, "roleDefinitionId": role}
        wrong_principal = {"scope": registry, "principalId": "B2222222-BBBB-BBBB-BBBB-BBBBBBBBBBBB", "roleDefinitionId": role}
        script = r'''
source "$1"
has_direct_acr_pull_assignment "$2" "$3" "$4"
'''

        def check(assignments: list[dict[str, str]]) -> int:
            return subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), registry, principal, json.dumps(assignments)],
                capture_output=True, text=True, check=False).returncode

        self.assertEqual(0, check([inherited, direct]))
        self.assertNotEqual(0, check([inherited]))
        self.assertNotEqual(0, check([wrong_principal]))

    def test_uncertain_traffic_set_result_also_rolls_back(self) -> None:
        script = r'''
source "$1"
attempt=0
traffic_state=stable
az() {
  case "$*" in
    *"containerapp show"*)
      if [[ "$traffic_state" == stable ]]; then
        printf '[{"revisionName":"stable-revision","weight":100},{"revisionName":"candidate-revision","weight":0}]\n'
      else
        printf '[{"revisionName":"candidate-revision","weight":100}]\n'
      fi
      ;;
    *)
      attempt=$((attempt + 1))
      printf 'az:%s\n' "$*"
      [[ "$*" == *"stable-revision=100"* ]] && traffic_state=stable
      (( attempt > 1 ))
      ;;
  esac
}
curl() { echo 'curl must not run' >&2; return 99; }
promote_workload_revision proof-rg proof-app stable-revision candidate-revision https://proof.invalid
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(5, result.returncode)
        calls = [line for line in result.stdout.splitlines() if line.startswith("az:")]
        self.assertEqual(2, len(calls))
        self.assertNotIn("curl must not run", result.stderr)
        self.assertIn("uncertain result", result.stderr)
        self.assertIn("Restored stable traffic", result.stderr)

    def test_role_cleanup_waits_for_eventual_absence_after_uncertain_delete(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            state = Path(temp_dir) / "state"
            calls = Path(temp_dir) / "calls"
            state.write_text("0")
            script = r'''
source "$1"
STATE_FILE="$2"
CALLS_FILE="$3"
az() {
  printf '%s\n' "$*" >>"$CALLS_FILE"
  if [[ "$*" == *"role assignment delete"* ]]; then return 1; fi
  count="$(cat "$STATE_FILE")"
  count=$((count + 1))
  printf '%s' "$count" >"$STATE_FILE"
  if (( count < 3 )); then
    printf '[{"id":"/subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001"}]\n'
  else
    printf '[]\n'
  fi
}
sleep() { :; }
delete_and_verify_role_assignment proof-sub /subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr /subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001 4 0
'''
            result = subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), str(state), str(calls)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            call_lines = calls.read_text().splitlines()
            self.assertEqual(1, sum("role assignment delete" in line for line in call_lines))
            self.assertEqual(3, sum("role assignment list" in line for line in call_lines))
            self.assertTrue(all("--subscription proof-sub" in line for line in call_lines))

    def test_role_assignment_id_must_be_present_and_scoped_to_registry(self) -> None:
        registry = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.ContainerRegistry/registries/acr"
        valid = f"{registry}/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001"

        def validate(assignment_id: str) -> subprocess.CompletedProcess[str]:
            return subprocess.run(
                [
                    "bash",
                    "-c",
                    'source "$1"; valid_role_assignment_id "$2" "$3"',
                    "test",
                    str(RUNBOOK_LIB),
                    registry,
                    assignment_id,
                ],
                capture_output=True,
                text=True,
                check=False,
            )

        self.assertEqual(0, validate(valid).returncode)
        self.assertEqual(0, validate(valid.upper()).returncode)
        self.assertNotEqual(0, validate("").returncode)
        self.assertNotEqual(0, validate(valid.replace("/registries/acr/", "/registries/other/")).returncode)
        self.assertNotEqual(0, validate(valid.replace("Microsoft.ContainerRegistry", "MicrosoftXContainerRegistry")).returncode)

    def test_role_cleanup_fails_when_assignment_never_disappears(self) -> None:
        script = r'''
source "$1"
az() {
  if [[ "$*" == *"role assignment delete"* ]]; then return 0; fi
  printf '[{"id":"/subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001"}]\n'
}
sleep() { :; }
delete_and_verify_role_assignment proof-sub /subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr /subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001 2 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(1, result.returncode)
        self.assertIn("remained observable", result.stderr)

    def test_role_cleanup_fails_closed_on_invalid_list_shape(self) -> None:
        script = r'''
source "$1"
az() {
  if [[ "$*" == *"role assignment delete"* ]]; then return 0; fi
  printf '{"id":"not-an-array"}\n'
}
sleep() { echo 'sleep must not run' >&2; return 99; }
delete_and_verify_role_assignment proof-sub /subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr /subscriptions/proof/resourceGroups/registry-rg/providers/Microsoft.ContainerRegistry/registries/acr/providers/Microsoft.Authorization/roleAssignments/00000000-0000-0000-0000-000000000001 4 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("output was invalid", result.stderr)
        self.assertNotIn("sleep must not run", result.stderr)

    def test_external_deployment_cleanup_waits_for_absence(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            state = Path(temp_dir) / "state"
            calls = Path(temp_dir) / "calls"
            state.write_text("0")
            script = r'''
source "$1"
STATE_FILE="$2"
CALLS_FILE="$3"
az() {
  printf '%s\n' "$*" >>"$CALLS_FILE"
  if [[ "$*" == *"deployment group delete"* ]]; then return 1; fi
  count="$(cat "$STATE_FILE")"
  count=$((count + 1))
  printf '%s' "$count" >"$STATE_FILE"
  (( count < 3 )) && printf '[{"name":"proof-acr"}]\n' || printf '[]\n'
}
sleep() { :; }
delete_and_verify_group_deployment proof-sub proof-rg proof-acr 4 0
'''
            result = subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), str(state), str(calls)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("3", state.read_text())
            self.assertTrue(all("--subscription proof-sub" in line for line in calls.read_text().splitlines()))

    def test_external_deployment_cleanup_fails_closed_on_malformed_json(self) -> None:
        script = r'''
source "$1"
az() {
  if [[ "$*" == *"deployment group delete"* ]]; then return 0; fi
  printf 'not-json\n'
}
sleep() { echo 'sleep must not run' >&2; return 99; }
delete_and_verify_group_deployment proof-sub proof-rg proof-acr 4 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("output was invalid", result.stderr)
        self.assertNotIn("sleep must not run", result.stderr)

    def test_group_cleanup_waits_through_slow_scheduled_deletion(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            state = Path(temp_dir) / "state"
            state.write_text("0")
            script = r'''
source "$1"
STATE_FILE="$2"
az() {
  count="$(cat "$STATE_FILE")"
  count=$((count + 1))
  printf '%s' "$count" >"$STATE_FILE"
  (( count < 4 )) && printf 'true\n' || printf 'false\n'
}
sleep() { :; }
wait_for_resource_group_absence proof-sub proof-rg 5 0
'''
            result = subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), str(state)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("4", state.read_text())

    def test_group_cleanup_reports_bounded_timeout(self) -> None:
        script = r'''
source "$1"
az() { printf 'true\n'; }
sleep() { :; }
wait_for_resource_group_absence proof-sub proof-rg 2 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(1, result.returncode)
        self.assertIn("bounded deletion window", result.stderr)

    def test_group_cleanup_api_errors_fail_closed(self) -> None:
        script = r'''
source "$1"
az() { return 1; }
sleep() { :; }
wait_for_resource_group_absence proof-sub proof-rg 2 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("Azure CLI read failed", result.stderr)

    def test_group_cleanup_does_not_confuse_provider_not_found_with_absence(self) -> None:
        script = r'''
source "$1"
az() { printf 'ResourceGroupNotFound\n' >&2; return 1; }
wait_for_resource_group_absence proof-sub proof-rg 2 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("Azure CLI read failed", result.stderr)

    def test_vault_cleanup_api_errors_fail_closed(self) -> None:
        script = r'''
source "$1"
az() { return 1; }
sleep() { :; }
purge_and_verify_deleted_vault proof-sub proof-kv westeurope 2 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("Azure CLI read failed", result.stderr)

    def test_vault_cleanup_does_not_confuse_provider_not_found_with_absence(self) -> None:
        script = r'''
source "$1"
az() { printf 'ResourceNotFound\n' >&2; return 1; }
purge_and_verify_deleted_vault proof-sub proof-kv westeurope 2 0
'''
        result = subprocess.run(
            ["bash", "-c", script, "test", str(RUNBOOK_LIB)],
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("Azure CLI read failed", result.stderr)

    def test_vault_cleanup_purges_then_verifies_absence(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            state = Path(temp_dir) / "state"
            state.write_text("0")
            script = r'''
source "$1"
STATE_FILE="$2"
az() {
  if [[ "$*" == *"keyvault purge"* ]]; then return 1; fi
  count="$(cat "$STATE_FILE")"
  count=$((count + 1))
  printf '%s' "$count" >"$STATE_FILE"
  if (( count < 3 )); then
    printf '[{"name":"proof-kv","properties":{"location":"WestEurope"}}]\n'
  else
    printf '[]\n'
  fi
}
sleep() { :; }
purge_and_verify_deleted_vault proof-sub proof-kv westeurope 4 0
'''
            result = subprocess.run(
                ["bash", "-c", script, "test", str(RUNBOOK_LIB), str(state)],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("3", state.read_text())

    def test_invalid_vault_derived_proof_names_fail_before_azure(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            fake_az = Path(temp_dir) / "az"
            marker = Path(temp_dir) / "azure-was-called"
            fake_az.write_text(f"#!/usr/bin/env bash\ntouch '{marker}'\nexit 99\n")
            fake_az.chmod(0o700)
            env = os.environ.copy()
            env["PATH"] = f"{temp_dir}:{env['PATH']}"
            common = [
                str(RUNBOOK),
                "apply",
                "--resource-group", "rg-proof",
                "--image-digest", "a" * 64,
                "--registry-resource-group", "rg-acr",
                "--sql-bootstrap-object-id", "00000000-0000-0000-0000-000000000001",
                "--sql-bootstrap-login", "proof-user",
                "--sql-bootstrap-ip", "203.0.113.10",
            ]
            for invalid_name in ("1abc", "abc-", "ab--cd"):
                result = subprocess.run(
                    [*common, "--proof-name", invalid_name],
                    env=env,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(2, result.returncode)
                self.assertIn("proof name must be", result.stderr)
                self.assertFalse(marker.exists())


def valid_image_reference(repository: str, digest: str) -> bool:
    return bool(re.fullmatch(r"[a-z0-9./_-]+@sha256:[0-9a-f]{64}", f"{repository}@sha256:{digest}"))


class ImageReferenceTests(unittest.TestCase):
    def test_tags_and_short_digests_are_rejected_by_contract(self) -> None:
        digest = "a" * 64
        self.assertTrue(valid_image_reference("valenceruntimeimages.azurecr.io/runtime-combined", digest))
        self.assertFalse(valid_image_reference("valenceruntimeimages.azurecr.io/runtime-combined:latest", digest))
        self.assertFalse(valid_image_reference("valenceruntimeimages.azurecr.io/runtime-combined", "a" * 63))


if __name__ == "__main__":
    unittest.main()
