using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderDeleteRecoverySupportTests
{
    [Theory]
    [InlineData(AzureProviderOperationStatus.Running, AzureProviderAssignmentState.Deleted)]
    [InlineData(AzureProviderOperationStatus.Running, AzureProviderAssignmentState.Unknown)]
    [InlineData(AzureProviderOperationStatus.RecoveryRequired, AzureProviderAssignmentState.Deleted)]
    [InlineData(AzureProviderOperationStatus.RecoveryRequired, AzureProviderAssignmentState.Unknown)]
    public void Verified_cleanup_accepts_only_the_two_durable_pending_states(
        AzureProviderOperationStatus status,
        AzureProviderAssignmentState assignmentState)
    {
        var (operation, assignment) = Fixture(status, assignmentState);

        Assert.True(AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(operation, assignment));
    }

    [Theory]
    [InlineData("operation-inventory")]
    [InlineData("assignment-inventory")]
    [InlineData("endpoint")]
    [InlineData("wrong-phase")]
    [InlineData("attempted-step")]
    [InlineData("operation-assignment")]
    [InlineData("last-operation")]
    [InlineData("assignment-workspace")]
    [InlineData("assignment-organization")]
    [InlineData("assignment-instance")]
    [InlineData("operation-workspace")]
    [InlineData("operation-organization")]
    [InlineData("operation-instance")]
    [InlineData("target")]
    [InlineData("invalid-metadata")]
    [InlineData("terminal-operation")]
    [InlineData("active-assignment")]
    [InlineData("wrong-action")]
    [InlineData("wrong-lifecycle-action")]
    [InlineData("invalid-scope")]
    public void Verified_cleanup_rejects_unbound_or_nonempty_snapshots(string mutation)
    {
        var (operation, assignment) = Fixture(
            AzureProviderOperationStatus.RecoveryRequired,
            AzureProviderAssignmentState.Unknown);
        var alternate = Guid.Parse("99999999-9999-9999-9999-999999999999");

        (operation, assignment) = mutation switch
        {
            "operation-inventory" => (operation with
            {
                Resources = operation.Resources with { WorkloadResourceId = "/owned/workload" }
            }, assignment),
            "assignment-inventory" => (operation, assignment with
            {
                Resources = assignment.Resources with { WorkloadResourceId = "/owned/workload" }
            }),
            "endpoint" => (operation with { Endpoint = "https://runtime.example" }, assignment),
            "wrong-phase" => (operation with { Phase = AzureProviderOperationPhase.CleanupSubmitted }, assignment),
            "attempted-step" => (operation with { AttemptedStep = AzureProviderRunnerStep.Cleanup }, assignment),
            "operation-assignment" => (operation with { ProviderAssignmentId = alternate }, assignment),
            "last-operation" => (operation, assignment with { LastOperationId = alternate }),
            "assignment-workspace" => (operation, assignment with { WorkspaceId = alternate }),
            "assignment-organization" => (operation, assignment with { OrganizationId = alternate }),
            "assignment-instance" => (operation, assignment with { InstanceId = alternate }),
            "operation-workspace" => (operation with { WorkspaceId = alternate }, assignment),
            "operation-organization" => (operation with { OrganizationId = alternate }, assignment),
            "operation-instance" => (operation with { InstanceId = alternate }, assignment),
            "target" => (operation with { TargetKey = "foreign-workload" }, assignment),
            "invalid-metadata" => (operation with { PersistedMetadataInvalid = true }, assignment),
            "terminal-operation" => (operation with { Status = AzureProviderOperationStatus.Succeeded }, assignment),
            "active-assignment" => (operation, assignment with { State = AzureProviderAssignmentState.Deleting }),
            "wrong-action" => (operation with { Action = AzureProviderOperationAction.Reconcile }, assignment),
            "wrong-lifecycle-action" => (operation with { LifecycleAction = ElsaInstanceOperationAction.Create }, assignment),
            "invalid-scope" => (operation with { ProviderScopeFingerprint = "invalid" },
                assignment with { ProviderScopeFingerprint = "invalid" }),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };

        Assert.False(AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(operation, assignment));
    }

    [Fact]
    public void Verified_cleanup_accepts_a_template_only_scope_rotation()
    {
        var (operation, assignment) = Fixture(
            AzureProviderOperationStatus.RecoveryRequired,
            AzureProviderAssignmentState.Unknown);
        assignment = assignment with { ProviderScopeFingerprint = new string('b', 64) };

        Assert.True(AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(operation, assignment));
    }

    [Fact]
    public void Verified_cleanup_rejects_null_operation_or_assignment()
    {
        var (operation, assignment) = Fixture(
            AzureProviderOperationStatus.RecoveryRequired,
            AzureProviderAssignmentState.Deleted);

        Assert.False(AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(null, assignment));
        Assert.False(AzureProviderDeleteRecoverySupport.IsVerifiedCleanupEligible(operation, null));
    }

    private static (AzureProviderOperation Operation, AzureProviderResourceAssignment Assignment) Fixture(
        AzureProviderOperationStatus status,
        AzureProviderAssignmentState assignmentState)
    {
        var workspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var organizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var instanceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var operationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var assignmentId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        const string workloadName = "e333333333333333";
        const string resourceGroupName = "elsa-instance-rg";
        var scopeFingerprint = new string('a', 64);
        var now = DateTimeOffset.Parse("2026-09-06T08:00:00Z");
        var operation = new AzureProviderOperation(
            operationId,
            workspaceId,
            workloadName,
            AzureProviderOperationAction.Delete,
            "delete-operation",
            new string('b', 64),
            "operation-identity",
            new string('c', 64),
            new string('d', 64),
            "5.0.0",
            "5.0",
            "combined",
            "Dedicated",
            "westeurope",
            "registry.example/runtime",
            "sha256:" + new string('e', 64),
            null,
            null,
            status,
            AzureProviderOperationPhase.CleanupVerified,
            CheckpointSequence: 4,
            AttemptNumber: 2,
            Version: 9,
            Resources: new(resourceGroupName),
            Endpoint: null,
            Health: AzureProviderHealth.Unknown,
            Diagnostics: [],
            WorkerId: null,
            LeaseExpiresAt: null,
            HeartbeatAt: null,
            CreatedAt: now,
            UpdatedAt: now,
            CompletedAt: null,
            ProviderScopeFingerprint: scopeFingerprint,
            OrganizationId: organizationId,
            InstanceId: instanceId,
            LifecycleAction: ElsaInstanceOperationAction.Delete,
            ProviderAssignmentId: assignmentId);
        var assignment = new AzureProviderResourceAssignment(
            assignmentId,
            workspaceId,
            organizationId,
            instanceId,
            scopeFingerprint,
            AzureProviderResourceAssignmentNaming.CurrentVersion,
            "subscription-1",
            resourceGroupName,
            workloadName,
            new string('f', 64),
            "westeurope",
            assignmentState,
            new(resourceGroupName),
            operationId,
            2,
            now,
            now);
        return (operation, assignment);
    }
}
