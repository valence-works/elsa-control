#!/usr/bin/env python3
"""Catalog migration rehearsal probe sidecar.

Runs inside the no-ingress ACI group beside the exact API image. It audits the retained clone before the
API starts, releases the start barrier, waits for two stable loopback health responses bound to the expected
baked source id and build number, audits the clone again, stops the API and prints exactly one value-free
JSON result line. Raw SQL rows, API output, connection material and error text are never printed.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

SCHEMA = "elsa-control.catalog-rehearsal/v1"
COUNT_TABLES = ("Accounts", "Organizations", "Workspaces", "ElsaInstances", "DeploymentRuns", "AzureProviderOperations")
ACTIVE_STATUSES = "('Accepted', 'Queued', 'EntitlementHeld', 'Running', 'RecoveryRequired')"
RECOVERY_OBSERVATION_COLUMNS = (
    "Id", "OrganizationId", "WorkspaceId", "InstanceId", "LifecycleOperationId", "LifecycleAction",
    "ObservedLifecycleAttemptNumber", "ObservedInstanceVersion", "ProviderOperationId", "ProviderAssignmentId",
    "ProviderOperationIdentity", "ProviderRequestHash", "ProviderAttemptNumber", "ProviderVersion",
    "ProviderCheckpointSequence", "TargetKey", "ProviderScopeFingerprint", "ResolvedPlanId", "ResolvedPlanSchemaVersion",
    "ResolvedPlanUri", "ResolvedPlanContentHash", "ProviderPlanFingerprint", "ProviderTemplateFingerprint",
    "CompletedStep", "ObservedPhase", "ObservedHealth", "ResourceFingerprint", "PostconditionFingerprint",
    "NaturalKey", "RecordDigest", "ObservedAt", "CreatedAt",
)
RECOVERY_OBSERVATION_FOREIGN_KEYS = (
    "FK_AzureProviderRecoveryObservations_AzureProviderOperations_ProviderOperationId",
    "FK_AzureProviderRecoveryObservations_AzureProviderResourceAssignments_ProviderAssignmentId",
    "FK_AzureProviderRecoveryObservations_ElsaInstanceOperations_LifecycleOperationId",
    "FK_AzureProviderRecoveryObservations_Workspaces_WorkspaceId",
)
RECOVERY_REQUEST_COLUMNS = (
    "ObservedInstanceVersion", "ObservedLifecycleAttemptNumber", "RecoveryObservationDigest",
    "RecoveryObservationReference", "AzureDeleteRecoveryAuthority",
)
MIGRATION_RE = re.compile(r"^[0-9]{14}_[A-Za-z0-9_]+$")
SAFE_ID_RE = re.compile(r"^[A-Za-z0-9._+:-]{1,128}$")


def env(name: str, default: str = "") -> str:
    return os.environ.get(name, default)


def int_env(name: str, default: int, low: int, high: int) -> int:
    try:
        value = int(env(name, str(default)))
    except ValueError:
        return default
    return value if low <= value <= high else default


BARRIER_PATH = Path(env("REHEARSAL_BARRIER_PATH", "/rehearsal") or "/rehearsal")
PHASE = env("REHEARSAL_PHASE")
result: dict[str, object] = {
    "schema": SCHEMA,
    "phase": PHASE if PHASE in ("candidate", "previous") else "unknown",
    "result": "failed",
    "code": "not-started",
    "healthChecks": 0,
    "migrationIds": [],
    "baselineMigrationIds": [],
    "previewColumnCount": 0,
    "baselinePreviewColumnCount": 0,
    "duplicateTargetGroupCount": 0,
    "duplicateOperationGroupCount": 0,
    "billingProviderEventNullStateCount": 0,
    "billingProviderEventsPresent": False,
    "foreignKeyIntegrityViolationCount": 0,
    "checkConstraintIntegrityViolationCount": 0,
    "orphanProviderAssignmentCount": 0,
    "orphanOperationTransitionCount": 0,
    "providerAssignmentSchemaPresent": False,
    "recoveryObservationColumnsValid": False,
    "recoveryObservationForeignKeysValid": False,
    "recoveryObservationNaturalKeyIndexValid": False,
    "recoveryObservationAppendOnlyTriggerValid": False,
    "recoveryRequestColumnsValid": False,
    "attemptedStepColumnValid": False,
    "commonCountsEqual": False,
    "permissionChecks": False,
    "principalChecks": False,
    "integrityChecks": False,
    "indexChecks": False,
    "baselineCounts": {},
    "postCounts": {},
    "bakedImageId": "unknown",
    "buildNumber": "unknown",
    "rehearsalGroupName": "unknown",
}


def wait_for_api_exit() -> None:
    """Keep the probe alive until a started API records its own exit code.

    Azure Container Instances terminates the remaining containers of a group when one exits, so a probe
    that exits first makes the API wrapper report 143 whatever the API would have returned. The wrapper
    stops the API within 15 seconds of stop-api (then kills it), so the bound leaves margin."""
    if not BARRIER_PATH.joinpath("start-api").exists():
        return
    deadline = time.monotonic() + int(os.environ.get("REHEARSAL_API_STOP_WAIT_SECONDS", "45"))
    while not BARRIER_PATH.joinpath("api-exited").exists() and time.monotonic() < deadline:
        time.sleep(0.5)


def finish(code: str, passed: bool = False) -> None:
    result["result"] = "passed" if passed else "failed"
    result["code"] = code
    try:
        BARRIER_PATH.joinpath("stop-api").touch(mode=0o600, exist_ok=True)
    except OSError:
        pass
    wait_for_api_exit()
    sys.stdout.write(json.dumps(result, sort_keys=True, separators=(",", ":")) + "\n")
    sys.stdout.flush()
    raise SystemExit(0 if passed else 1)


class Sql:
    """Bounded sqlcmd boundary using the container's managed identity; rows come back as text cells only."""

    def __init__(self) -> None:
        self.sqlcmd = env("SQLCMD_PATH", "/usr/local/bin/sqlcmd")
        self.server = env("CATALOG_SERVER")
        self.database = env("CATALOG_DATABASE")
        self.client_id = env("CATALOG_MI_CLIENT_ID")
        self.resume_delay = int(os.environ.get("REHEARSAL_SQL_RESUME_DELAY_SECONDS", "45"))

    # A serverless database that auto-paused answers the first login with error 40613 while it
    # resumes; the retained rehearsal clone pauses after an hour idle. Retry only that signal.
    RESUME_ATTEMPTS = 4

    def rows(self, query: str, timeout: int = 120) -> list[list[str]] | None:
        command = [
            self.sqlcmd, "-S", f"tcp:{self.server},1433", "-d", self.database,
            "--authentication-method", "ActiveDirectoryManagedIdentity", "-U", self.client_id,
            "-N", "true", "-h", "-1", "-W", "-s", "\t", "-b", "-l", "30", "-t", str(timeout), "-Q", "SET NOCOUNT ON; " + query,
        ]
        for attempt in range(self.RESUME_ATTEMPTS):
            try:
                completed = subprocess.run(command, capture_output=True, text=True, timeout=timeout + 60, check=False)
            except (OSError, subprocess.TimeoutExpired):
                return None
            if completed.returncode == 0:
                return [line.split("\t") for line in completed.stdout.splitlines() if line.strip()]
            if not self.resuming(completed.stdout + completed.stderr) or attempt + 1 == self.RESUME_ATTEMPTS:
                return None
            time.sleep(self.resume_delay)
        return None

    @staticmethod
    def resuming(output: str) -> bool:
        return re.search(r"\b(?:Error|Msg) 40613\b", output) is not None

    def scalar(self, query: str) -> int | None:
        rows = self.rows(query)
        if rows is None or len(rows) != 1 or len(rows[0]) != 1:
            return None
        try:
            return int(rows[0][0].strip())
        except ValueError:
            return None

    def column(self, query: str) -> list[str] | None:
        rows = self.rows(query)
        if rows is None or any(len(row) != 1 for row in rows):
            return None
        return [row[0].strip() for row in rows]


def object_exists(sql: Sql, table: str, column: str | None = None) -> bool | None:
    """True/False when the catalog answered, None when the query boundary failed."""
    if column is None:
        count = sql.scalar(f"SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{table}'")
    else:
        count = sql.scalar(
            f"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'"
        )
    return None if count is None else count == 1


def all_columns_present(sql: Sql, table: str, columns: tuple[str, ...]) -> bool | None:
    names = ", ".join(f"'{name}'" for name in columns)
    count = sql.scalar(
        f"SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = '{table}' AND COLUMN_NAME IN ({names})"
    )
    return None if count is None else count == len(columns)


def filtered_unique_index_valid(sql: Sql, name: str) -> bool | None:
    count = sql.scalar(
        "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.AzureProviderOperations') "
        f"AND name = '{name}' AND is_unique = 1 AND has_filter = 1 AND filter_definition LIKE '%EntitlementHeld%' "
        "AND filter_definition LIKE '%Accepted%' AND filter_definition LIKE '%Queued%' AND filter_definition LIKE '%Running%' "
        "AND filter_definition LIKE '%RecoveryRequired%'"
    )
    return None if count is None else count == 1


# Every enabled foreign key, including composite ones: rows whose referencing columns are all non-null
# but match no referenced row.
FOREIGN_KEY_VIOLATIONS = """
DECLARE @violations bigint = 0, @sql nvarchar(max), @count bigint;
DECLARE fk CURSOR LOCAL FAST_FORWARD FOR
  SELECT N'SELECT @c = COUNT(*) FROM ' + QUOTENAME(cs.name) + N'.' + QUOTENAME(ct.name) + N' AS c WHERE ' +
         STRING_AGG(N'c.' + QUOTENAME(cc.name) + N' IS NOT NULL', N' AND ') +
         N' AND NOT EXISTS (SELECT 1 FROM ' + QUOTENAME(ps.name) + N'.' + QUOTENAME(pt.name) + N' AS p WHERE ' +
         STRING_AGG(N'p.' + QUOTENAME(pc.name) + N' = c.' + QUOTENAME(cc.name), N' AND ') + N')'
  FROM sys.foreign_keys fk
  JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
  JOIN sys.tables ct ON ct.object_id = fk.parent_object_id JOIN sys.schemas cs ON cs.schema_id = ct.schema_id
  JOIN sys.columns cc ON cc.object_id = fkc.parent_object_id AND cc.column_id = fkc.parent_column_id
  JOIN sys.tables pt ON pt.object_id = fk.referenced_object_id JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
  JOIN sys.columns pc ON pc.object_id = fkc.referenced_object_id AND pc.column_id = fkc.referenced_column_id
  WHERE fk.is_disabled = 0
  GROUP BY fk.object_id, cs.name, ct.name, ps.name, pt.name;
OPEN fk; FETCH NEXT FROM fk INTO @sql;
WHILE @@FETCH_STATUS = 0 BEGIN
  EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @count OUTPUT; SET @violations += @count;
  FETCH NEXT FROM fk INTO @sql;
END
CLOSE fk; DEALLOCATE fk;
SELECT @violations;
"""

CHECK_CONSTRAINT_VIOLATIONS = """
DECLARE @violations bigint = 0, @sql nvarchar(max), @count bigint;
DECLARE ck CURSOR LOCAL FAST_FORWARD FOR
  SELECT N'SELECT @c = COUNT(*) FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' WHERE NOT (' + c.definition + N')'
  FROM sys.check_constraints c JOIN sys.tables t ON t.object_id = c.parent_object_id JOIN sys.schemas s ON s.schema_id = t.schema_id
  WHERE c.is_disabled = 0;
OPEN ck; FETCH NEXT FROM ck INTO @sql;
WHILE @@FETCH_STATUS = 0 BEGIN
  EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @count OUTPUT; SET @violations += @count;
  FETCH NEXT FROM ck INTO @sql;
END
CLOSE ck; DEALLOCATE ck;
SELECT @violations;
"""


def audit(sql: Sql, prefix: str) -> str | None:
    """Populate result fields for one audit pass ("baseline" or "post"); return a stable failure code or None."""
    ids_key, preview_key, counts_key = (
        ("baselineMigrationIds", "baselinePreviewColumnCount", "baselineCounts") if prefix == "baseline"
        else ("migrationIds", "previewColumnCount", "postCounts")
    )
    migrations = sql.column("SELECT MigrationId FROM dbo.__EFMigrationsHistory ORDER BY MigrationId")
    if migrations is None or any(not MIGRATION_RE.fullmatch(item) for item in migrations):
        return f"{prefix}-migration-query-failed"
    result[ids_key] = migrations
    preview = sql.scalar(
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo' AND COLUMN_NAME = 'PreviewManifestDigest' "
        "AND TABLE_NAME IN ('ElsaInstances', 'ElsaInstanceIntentRevisions')"
    )
    if preview is None:
        return f"{prefix}-schema-query-failed"
    result[preview_key] = preview
    counts: dict[str, int] = {}
    for table in COUNT_TABLES:
        count = sql.scalar(f"SELECT COUNT(*) FROM dbo.{table}")
        if count is None:
            return f"{prefix}-count-query-failed"
        counts[table] = count
    result[counts_key] = counts
    duplicate_targets = sql.scalar(
        "SELECT COUNT(*) FROM (SELECT WorkspaceId, TargetKey FROM dbo.AzureProviderOperations "
        f"WHERE Status IN {ACTIVE_STATUSES} GROUP BY WorkspaceId, TargetKey HAVING COUNT(*) > 1) AS d"
    )
    duplicate_operations = sql.scalar(
        "SELECT COUNT(*) FROM (SELECT WorkspaceId, TargetKey, OperationIdentity FROM dbo.AzureProviderOperations "
        f"WHERE Status IN {ACTIVE_STATUSES} GROUP BY WorkspaceId, TargetKey, OperationIdentity HAVING COUNT(*) > 1) AS d"
    )
    if duplicate_targets is None or duplicate_operations is None:
        return f"{prefix}-duplicate-query-failed"
    result["duplicateTargetGroupCount"] = duplicate_targets
    result["duplicateOperationGroupCount"] = duplicate_operations
    if duplicate_targets or duplicate_operations:
        return f"{prefix}-duplicate-active-operations"
    billing_present = object_exists(sql, "BillingProviderEvents")
    if billing_present is None:
        return f"{prefix}-schema-query-failed"
    result["billingProviderEventsPresent"] = billing_present
    if billing_present:
        null_states = sql.scalar("SELECT COUNT(*) FROM dbo.BillingProviderEvents WHERE State IS NULL")
        if null_states is None:
            return f"{prefix}-billing-query-failed"
        result["billingProviderEventNullStateCount"] = null_states
    return None


def integrity(sql: Sql) -> str | None:
    fk = sql.scalar(FOREIGN_KEY_VIOLATIONS)
    ck = sql.scalar(CHECK_CONSTRAINT_VIOLATIONS)
    assignment_schema = object_exists(sql, "AzureProviderOperations", "ProviderAssignmentId")
    assignments_table = object_exists(sql, "AzureProviderResourceAssignments")
    if fk is None or ck is None or assignment_schema is None or assignments_table is None:
        return "integrity-query-failed"
    result["foreignKeyIntegrityViolationCount"] = fk
    result["checkConstraintIntegrityViolationCount"] = ck
    result["providerAssignmentSchemaPresent"] = assignment_schema and assignments_table
    orphan_assignments = 0
    if result["providerAssignmentSchemaPresent"]:
        orphan_assignments = sql.scalar(
            "SELECT COUNT(*) FROM dbo.AzureProviderOperations o WHERE o.ProviderAssignmentId IS NOT NULL "
            "AND NOT EXISTS (SELECT 1 FROM dbo.AzureProviderResourceAssignments a WHERE a.Id = o.ProviderAssignmentId)"
        )
    orphan_transitions = sql.scalar(
        "SELECT COUNT(*) FROM dbo.AzureProviderOperationTransitions t "
        "WHERE NOT EXISTS (SELECT 1 FROM dbo.AzureProviderOperations o WHERE o.Id = t.OperationId)"
    )
    if orphan_assignments is None or orphan_transitions is None:
        return "integrity-query-failed"
    result["orphanProviderAssignmentCount"] = orphan_assignments
    result["orphanOperationTransitionCount"] = orphan_transitions
    result["integrityChecks"] = fk == 0 and ck == 0 and orphan_assignments == 0 and orphan_transitions == 0
    return None


def schema_contract(sql: Sql) -> str | None:
    checks = {
        "recoveryObservationColumnsValid": all_columns_present(sql, "AzureProviderRecoveryObservations", RECOVERY_OBSERVATION_COLUMNS),
        "recoveryObservationForeignKeysValid": None,
        "recoveryObservationNaturalKeyIndexValid": None,
        "recoveryObservationAppendOnlyTriggerValid": None,
        "recoveryRequestColumnsValid": all_columns_present(sql, "ElsaInstanceRecoveryRequests", RECOVERY_REQUEST_COLUMNS),
        "attemptedStepColumnValid": None,
    }
    fk_names = ", ".join(f"'{name}'" for name in RECOVERY_OBSERVATION_FOREIGN_KEYS)
    fk_count = sql.scalar(f"SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN ({fk_names})")
    checks["recoveryObservationForeignKeysValid"] = None if fk_count is None else fk_count == len(RECOVERY_OBSERVATION_FOREIGN_KEYS)
    natural = sql.scalar(
        "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.AzureProviderRecoveryObservations') "
        "AND name = 'IX_AzureProviderRecoveryObservations_WorkspaceId_NaturalKey' AND is_unique = 1"
    )
    checks["recoveryObservationNaturalKeyIndexValid"] = None if natural is None else natural == 1
    trigger = sql.scalar(
        "SELECT COUNT(*) FROM sys.triggers tr JOIN sys.trigger_events te ON te.object_id = tr.object_id "
        "WHERE tr.name = 'TR_AzureProviderRecoveryObservations_AppendOnly' AND tr.is_instead_of_trigger = 1 AND tr.is_disabled = 0 "
        "AND te.type_desc IN ('UPDATE', 'DELETE')"
    )
    checks["recoveryObservationAppendOnlyTriggerValid"] = None if trigger is None else trigger == 2
    checks["attemptedStepColumnValid"] = object_exists(sql, "AzureProviderOperations", "AttemptedStep")
    if any(value is None for value in checks.values()):
        return "schema-contract-query-failed"
    result.update(checks)
    return None


def indexes_and_permissions(sql: Sql) -> str | None:
    target = filtered_unique_index_valid(sql, "IX_AzureProviderOperations_WorkspaceId_TargetKey")
    operation = filtered_unique_index_valid(sql, "IX_AzureProviderOperations_WorkspaceId_TargetKey_OperationIdentity")
    permissions = sql.scalar(
        "SELECT HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'CREATE TABLE') + HAS_PERMS_BY_NAME(NULL, 'DATABASE', 'ALTER')"
    )
    principal_name = env("CATALOG_MI_PRINCIPAL_NAME")
    principal = None
    if SAFE_ID_RE.fullmatch(principal_name or ""):
        principal = sql.scalar(
            f"SELECT CASE WHEN USER_NAME() = '{principal_name}' THEN 1 ELSE 0 END + "
            f"(SELECT COUNT(*) FROM sys.database_principals WHERE name = '{principal_name}' AND type IN ('E', 'X'))"
        )
    if target is None or operation is None or permissions is None or principal is None:
        return "authority-query-failed"
    result["indexChecks"] = target and operation
    result["permissionChecks"] = permissions == 2
    result["principalChecks"] = principal == 2
    return None


def health_ok(payload: object, expected_image: str, expected_build: str) -> bool:
    return (
        isinstance(payload, dict)
        and payload.get("status") == "ok"
        and payload.get("imageId") == expected_image
        and payload.get("buildNumber") == expected_build
    )


def wait_for_health() -> str | None:
    curl = env("CURL_PATH", "/usr/bin/curl")
    expected_image = env("EXPECTED_IMAGE_ID")
    expected_build = env("EXPECTED_BUILD_NUMBER")
    deadline = time.monotonic() + int_env("API_START_TIMEOUT_SECONDS", 900, 30, 3600)
    interval = int_env("HEALTH_SAMPLE_SECONDS", 2, 1, 30)
    stable = 0
    while time.monotonic() < deadline:
        if BARRIER_PATH.joinpath("api-exited").exists():
            return "api-exited-before-health"
        try:
            completed = subprocess.run(
                [curl, "--silent", "--max-time", "10", "--output", "-", "--write-out", "\n%{http_code}", "http://127.0.0.1:8080/health"],
                capture_output=True, text=True, timeout=20, check=False,
            )
            body, _, status = completed.stdout.rpartition("\n")
            payload = json.loads(body) if completed.returncode == 0 and status == "200" else None
        except (OSError, subprocess.TimeoutExpired, ValueError):
            payload = None
        if health_ok(payload, expected_image, expected_build):
            stable += 1
            if stable >= 2:
                result["healthChecks"] = 2
                result["bakedImageId"] = expected_image
                result["buildNumber"] = expected_build
                return None
        else:
            stable = 0
        time.sleep(interval)
    return "api-health-timeout"


def main() -> None:
    group = env("REHEARSAL_GROUP_NAME")
    if result["phase"] == "unknown" or not SAFE_ID_RE.fullmatch(group or ""):
        finish("inputs-invalid")
    result["rehearsalGroupName"] = group
    if not SAFE_ID_RE.fullmatch(env("EXPECTED_IMAGE_ID") or "") or not SAFE_ID_RE.fullmatch(env("EXPECTED_BUILD_NUMBER") or ""):
        finish("inputs-invalid")
    expected_ids = sorted(env("EXPECTED_MIGRATION_IDS").split())
    baseline_expected = int_env("EXPECTED_BASELINE_MIGRATIONS", 0, 1, 9999)
    target_expected = int_env("EXPECTED_MIGRATIONS", 0, 1, 9999)
    baseline_preview_expected = int_env("EXPECTED_BASELINE_PREVIEW_COLUMNS", -1, 0, 99)
    target_preview_expected = int_env("EXPECTED_PREVIEW_COLUMNS", -1, 0, 99)
    if len(expected_ids) != target_expected or any(not MIGRATION_RE.fullmatch(item) for item in expected_ids):
        finish("inputs-invalid")
    sql = Sql()
    code = audit(sql, "baseline")
    if code:
        finish(code)
    baseline_ids = result["baselineMigrationIds"]
    if len(baseline_ids) != baseline_expected or baseline_ids != expected_ids[: len(baseline_ids)]:
        finish("baseline-migrations-unexpected")
    if result["baselinePreviewColumnCount"] != baseline_preview_expected:
        finish("baseline-preview-columns-unexpected")
    code = indexes_and_permissions(sql)
    if code:
        finish(code)
    if not (result["principalChecks"] and result["permissionChecks"]):
        finish("baseline-authority-invalid")
    BARRIER_PATH.joinpath("start-api").touch(mode=0o600, exist_ok=True)
    code = wait_for_health()
    if code:
        # Audit the clone even on failure so the safe result still records what happened to the schema.
        audit(sql, "post")
        integrity(sql)
        schema_contract(sql)
        finish(code)
    for step in (lambda: audit(sql, "post"), lambda: integrity(sql), lambda: schema_contract(sql), lambda: indexes_and_permissions(sql)):
        code = step()
        if code:
            finish(code)
    if result["migrationIds"] != expected_ids:
        finish("post-migrations-unexpected")
    if result["previewColumnCount"] != target_preview_expected:
        finish("post-preview-columns-unexpected")
    if not result["integrityChecks"] or not result["indexChecks"] or not result["permissionChecks"] or not result["principalChecks"]:
        finish("post-authority-or-integrity-invalid")
    if result["billingProviderEventsPresent"] and result["billingProviderEventNullStateCount"]:
        finish("billing-event-state-null")
    if not all(
        result[key]
        for key in (
            "providerAssignmentSchemaPresent", "recoveryObservationColumnsValid", "recoveryObservationForeignKeysValid",
            "recoveryObservationNaturalKeyIndexValid", "recoveryObservationAppendOnlyTriggerValid",
            "recoveryRequestColumnsValid", "attemptedStepColumnValid",
        )
    ):
        finish("post-migration-contract-invalid")
    if result["postCounts"] != result["baselineCounts"]:
        finish("common-counts-changed")
    result["commonCountsEqual"] = True
    finish("ok", passed=True)


if __name__ == "__main__":
    with open(os.devnull, "w") as devnull:
        sys.stderr = devnull
        try:
            main()
        except SystemExit:
            raise
        except Exception:
            finish("probe-failed")
