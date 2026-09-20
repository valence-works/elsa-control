using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedElsaInstanceCustomerProjectionTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 4, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ElsaInstanceOperationState.Accepted, ElsaObservedLifecycle.Pending)]
    [InlineData(ElsaInstanceOperationState.Queued, ElsaObservedLifecycle.Pending)]
    [InlineData(ElsaInstanceOperationState.EntitlementHeld, ElsaObservedLifecycle.Pending)]
    [InlineData(ElsaInstanceOperationState.Running, ElsaObservedLifecycle.Provisioning)]
    [InlineData(ElsaInstanceOperationState.RecoveryRequired, ElsaObservedLifecycle.Provisioning)]
    public void Active_create_projects_a_known_phase_instead_of_unknown(
        ElsaInstanceOperationState operationState,
        ElsaObservedLifecycle expected)
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, operationState);

        var projected = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);
        var applied = ManagedElsaInstanceCustomerProjection.Apply(instance, operation);

        Assert.Equal(expected, projected);
        Assert.Equal(expected, applied.ObservedLifecycle);
        Assert.Equal(ElsaInstanceHealth.Unknown, applied.Health);
        Assert.Equal(instance.Version, applied.Version);
    }

    [Fact]
    public void Refresh_of_an_active_create_preserves_provisioning_and_does_not_invent_ready()
    {
        var instance = Instance(ElsaObservedLifecycle.Provisioning);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.RecoveryRequired);

        var first = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);
        var refreshed = ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation);

        Assert.Equal(ElsaObservedLifecycle.Provisioning, first);
        Assert.Equal(first, refreshed);
        Assert.NotEqual(ElsaObservedLifecycle.Ready, refreshed);
        Assert.NotEqual(ElsaObservedLifecycle.Failed, refreshed);
    }

    [Fact]
    public void Ready_outcome_comes_from_authoritative_instance_data()
    {
        var instance = Instance(ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Healthy);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Succeeded);

        Assert.Equal(ElsaObservedLifecycle.Ready,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
        Assert.Null(ManagedElsaInstanceCustomerProjection.UnavailableReason(
            canOpen: true, healthy: true, handoffConfigured: true, hasIdentity: true,
            ElsaObservedLifecycle.Ready));
    }

    [Fact]
    public void Terminal_failure_comes_from_authoritative_instance_data()
    {
        var instance = Instance(ElsaObservedLifecycle.Failed, ElsaInstanceHealth.Unreachable);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Failed);

        Assert.Equal(ElsaObservedLifecycle.Failed,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.FailedUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.Failed));
    }

    [Fact]
    public void Stale_health_keeps_ready_lifecycle_separate_from_endpoint_health()
    {
        var instance = Instance(ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Unknown);

        Assert.Equal(ElsaObservedLifecycle.Ready,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, activeOperation: null));
        Assert.Equal(ElsaInstanceHealth.Unknown, instance.Health);
        Assert.Equal(ManagedElsaInstanceCustomerProjection.GenericUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: true, hasIdentity: true,
                ElsaObservedLifecycle.Ready));
    }

    [Fact]
    public void Genuine_unknown_keeps_fail_safe_and_explains_refresh_recovery()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);

        Assert.Equal(ElsaObservedLifecycle.Unknown,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, activeOperation: null));
        Assert.Equal(ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.Unknown));
        Assert.Contains("Refresh", ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("recover", ManagedElsaInstanceCustomerProjection.UnknownUnavailableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Failed_operation_does_not_invent_failed_lifecycle_from_unknown()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.Failed);

        Assert.Equal(ElsaObservedLifecycle.Unknown,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
    }

    [Fact]
    public void Active_delete_does_not_invent_a_deleting_lifecycle_from_unknown()
    {
        var instance = Instance(ElsaObservedLifecycle.Unknown);
        var operation = Operation(instance.Id, ElsaInstanceOperationAction.Delete, ElsaInstanceOperationState.Running);

        Assert.Equal(ElsaObservedLifecycle.Unknown,
            ManagedElsaInstanceCustomerProjection.ProjectObservedLifecycle(instance, operation));
    }

    [Fact]
    public void In_progress_unavailable_reason_asks_for_refresh_without_claiming_ready()
    {
        Assert.Equal(ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason,
            ManagedElsaInstanceCustomerProjection.UnavailableReason(
                canOpen: true, healthy: false, handoffConfigured: false, hasIdentity: false,
                ElsaObservedLifecycle.Provisioning));
        Assert.DoesNotContain("Ready", ManagedElsaInstanceCustomerProjection.ProvisioningUnavailableReason, StringComparison.Ordinal);
    }

    private static ElsaInstance Instance(
        ElsaObservedLifecycle observed,
        ElsaInstanceHealth health = ElsaInstanceHealth.Unknown) =>
        ElsaInstance.Hydrate(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            OrganizationId,
            WorkspaceId,
            "Managed Elsa",
            "managed-elsa",
            new(
                new("commercial", "5.0", "5.0.1"),
                new("server-studio"),
                new("managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
            observed,
            health,
            4);

    private static ElsaInstanceOperationSummary Operation(
        Guid instanceId,
        ElsaInstanceOperationAction action,
        ElsaInstanceOperationState state) =>
        new(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            instanceId,
            action,
            state,
            1,
            1,
            Now,
            state is ElsaInstanceOperationState.Running or ElsaInstanceOperationState.RecoveryRequired ? Now : null,
            state is ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed ? Now : null,
            null,
            null,
            null,
            state == ElsaInstanceOperationState.Failed ? "provider.reconciliation.failed" : null,
            null,
            null);
}
