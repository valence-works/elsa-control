"""Shared safe-result contract for the host-side rehearsal scripts (the probe ships as a single file and repeats these)."""

from __future__ import annotations

import re

SCHEMA = "elsa-control.catalog-rehearsal/v1"
PHASES = ("candidate", "previous")
SAFE_ID = re.compile(r"^[A-Za-z0-9._+:-]{1,128}$")
MIGRATION = re.compile(r"^[0-9]{14}_[A-Za-z0-9_]+$")
COUNTS = ("Accounts", "Organizations", "Workspaces", "ElsaInstances", "DeploymentRuns", "AzureProviderOperations")
COUNT_FIELDS = (
    "previewColumnCount", "baselinePreviewColumnCount", "duplicateTargetGroupCount", "duplicateOperationGroupCount",
    "billingProviderEventNullStateCount", "foreignKeyIntegrityViolationCount", "checkConstraintIntegrityViolationCount",
    "orphanProviderAssignmentCount", "orphanOperationTransitionCount",
)
BOOL_FIELDS = (
    "commonCountsEqual", "permissionChecks", "principalChecks", "integrityChecks", "indexChecks",
    "recoveryObservationColumnsValid", "recoveryObservationForeignKeysValid", "recoveryObservationNaturalKeyIndexValid",
    "recoveryObservationAppendOnlyTriggerValid", "recoveryRequestColumnsValid", "attemptedStepColumnValid",
    "billingProviderEventsPresent", "providerAssignmentSchemaPresent",
)
SAFE_FIELDS = frozenset(
    ("schema", "phase", "result", "code", "healthChecks", "migrationIds", "baselineMigrationIds", "baselineCounts",
     "postCounts", "bakedImageId", "buildNumber", "rehearsalGroupName", *COUNT_FIELDS, *BOOL_FIELDS)
)


def group_name_valid(name: object, phase: str) -> bool:
    """Group names are bound to their phase prefix so a result can never be attributed to the other phase."""
    prefix = f"catalog-rehearsal-{phase}"
    return isinstance(name, str) and bool(SAFE_ID.fullmatch(name)) and (name == prefix or name.startswith(prefix + "-"))
