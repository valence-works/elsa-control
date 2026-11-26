using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Tests;

public sealed class ManagedElsaInstanceOverviewAllowedActionsTests
{
    private static readonly Guid OrganizationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_may_delete_a_ready_instance()
    {
        var decision = Overview(Instance(ElsaObservedLifecycle.Ready)).AllowedActions.Delete;

        Assert.True(decision.Allowed);
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void Owner_may_delete_a_failed_instance()
    {
        var overview = Overview(Instance(ElsaObservedLifecycle.Failed, ElsaInstanceHealth.Unreachable));

        Assert.False(overview.AllowedActions.Restart.Allowed);
        Assert.Equal(ManagedElsaInstanceOverviewEndpoints.InstanceFailedCode, overview.AllowedActions.Restart.ReasonCode);
        Assert.True(overview.AllowedActions.Delete.Allowed);
        Assert.Null(overview.AllowedActions.Delete.ReasonCode);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationState.Accepted)]
    [InlineData(ElsaInstanceOperationState.Queued)]
    [InlineData(ElsaInstanceOperationState.Running)]
    public void Owner_may_delete_while_a_create_is_still_active(ElsaInstanceOperationState createState)
    {
        var instance = Instance(ElsaObservedLifecycle.Provisioning);
        var create = Operation(instance.Id, ElsaInstanceOperationAction.Create, createState);
        var decision = Overview(instance, create).AllowedActions.Delete;

        Assert.True(decision.Allowed);
        Assert.Null(decision.ReasonCode);
        Assert.False(ManagedElsaInstanceOverviewEndpoints.HasBlockingCustomerDelete(create));
    }

    [Fact]
    public void Owner_may_delete_a_recovery_required_create()
    {
        var instance = Instance(ElsaObservedLifecycle.Provisioning);
        var create = Operation(instance.Id, ElsaInstanceOperationAction.Create, ElsaInstanceOperationState.RecoveryRequired);
        var overview = Overview(instance, create);

        Assert.True(overview.AllowedActions.Delete.Allowed);
        Assert.Null(overview.AllowedActions.Delete.ReasonCode);
        Assert.False(overview.AllowedActions.Restart.Allowed);
        Assert.Equal(
            ManagedElsaInstanceOverviewEndpoints.OperationActiveCode,
            overview.AllowedActions.Restart.ReasonCode);
    }

    [Fact]
    public void Deleting_intent_denies_delete_with_instance_deleting()
    {
        var instance = Instance(ElsaObservedLifecycle.Deleting, desired: ElsaDesiredLifecycle.Deleting);
        var decision = Overview(instance).AllowedActions.Delete;

        Assert.False(decision.Allowed);
        Assert.Equal(ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode, decision.ReasonCode);
    }

    [Fact]
    public void Deleted_instance_denies_delete_with_instance_deleting()
    {
        var instance = Instance(ElsaObservedLifecycle.Deleted, desired: ElsaDesiredLifecycle.Deleting);
        var decision = Overview(instance).AllowedActions.Delete;

        Assert.False(decision.Allowed);
        Assert.Equal(ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode, decision.ReasonCode);
    }

    [Theory]
    [InlineData(ElsaInstanceOperationState.Accepted)]
    [InlineData(ElsaInstanceOperationState.Queued)]
    [InlineData(ElsaInstanceOperationState.Running)]
    [InlineData(ElsaInstanceOperationState.WaitingForPriorOperation)]
    public void Blocking_delete_denies_with_operation_active(ElsaInstanceOperationState deleteState)
    {
        var instance = Instance(ElsaObservedLifecycle.Deleting, desired: ElsaDesiredLifecycle.Deleting);
        var delete = Operation(instance.Id, ElsaInstanceOperationAction.Delete, deleteState);
        var decision = Overview(instance, delete).AllowedActions.Delete;

        Assert.True(ManagedElsaInstanceOverviewEndpoints.HasBlockingCustomerDelete(delete));
        Assert.False(ManagedElsaInstanceOverviewEndpoints.IsResumableCustomerDelete(delete));
        Assert.False(decision.Allowed);
        Assert.Equal(ManagedElsaInstanceOverviewEndpoints.OperationActiveCode, decision.ReasonCode);
    }

    [Fact]
    public void Owner_may_delete_a_recovery_required_delete()
    {
        var instance = Instance(ElsaObservedLifecycle.Deleting, desired: ElsaDesiredLifecycle.Deleting);
        var delete = Operation(instance.Id, ElsaInstanceOperationAction.Delete, ElsaInstanceOperationState.RecoveryRequired);
        var decision = Overview(instance, delete).AllowedActions.Delete;

        Assert.True(ManagedElsaInstanceOverviewEndpoints.IsResumableCustomerDelete(delete));
        Assert.False(ManagedElsaInstanceOverviewEndpoints.HasBlockingCustomerDelete(delete));
        Assert.Null(ManagedElsaInstanceOverviewEndpoints.CustomerDeleteDenialCode(instance, delete));
        Assert.True(decision.Allowed);
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void Reader_is_denied_with_permission_required()
    {
        var decision = Overview(Instance(ElsaObservedLifecycle.Ready), role: WorkspaceRole.Reader)
            .AllowedActions.Delete;

        Assert.False(decision.Allowed);
        Assert.Equal(ManagedElsaInstanceOverviewEndpoints.PermissionRequiredCode, decision.ReasonCode);
    }

    [Fact]
    public void Source_admin_with_delete_grant_may_delete_a_ready_instance()
    {
        var decision = Overview(
            Instance(ElsaObservedLifecycle.Ready),
            role: WorkspaceRole.SourceAdmin,
            canDeletePermission: true).AllowedActions.Delete;

        Assert.True(decision.Allowed);
        Assert.Null(decision.ReasonCode);
    }

    [Fact]
    public void Source_admin_without_delete_grant_is_denied_with_permission_required()
    {
        var overview = Overview(
            Instance(ElsaObservedLifecycle.Ready),
            role: WorkspaceRole.SourceAdmin,
            canDeletePermission: false);
        var decision = overview.AllowedActions.Delete;

        Assert.True(overview.AllowedActions.Restart.Allowed);
        Assert.False(decision.Allowed);
        Assert.Equal(ManagedElsaInstanceOverviewEndpoints.PermissionRequiredCode, decision.ReasonCode);
    }

    [Fact]
    public void Commercial_gate_denial_is_surfaced_on_delete()
    {
        var gate = new ElsaInstanceCommercialGateDecision(
            false, ElsaInstanceCommercialOperation.EntitlementRequired, "Managed hosting is not enabled.");
        var decision = Overview(
            Instance(ElsaObservedLifecycle.Ready),
            deleteGate: gate).AllowedActions.Delete;

        Assert.False(decision.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.EntitlementRequired, decision.ReasonCode);
    }

    [Fact]
    public void Delete_denial_includes_a_reason_code_matching_other_actions()
    {
        var overview = Overview(Instance(ElsaObservedLifecycle.Ready), role: WorkspaceRole.Reader);
        var denied = overview.AllowedActions.Delete;
        var restart = overview.AllowedActions.Restart;

        Assert.False(denied.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(denied.ReasonCode));
        Assert.Equal(restart.ReasonCode, denied.ReasonCode);
        Assert.Equal(typeof(ManagedElsaInstanceOverviewActionDecisionResponse), denied.GetType());
        Assert.Equal(typeof(ManagedElsaInstanceOverviewActionDecisionResponse), restart.GetType());
    }

    private static ManagedElsaInstanceOverviewResponse Overview(
        ElsaInstance instance,
        ElsaInstanceOperationSummary? activeOperation = null,
        WorkspaceRole role = WorkspaceRole.Owner,
        ElsaInstanceCommercialGateDecision? deleteGate = null,
        bool? canDeletePermission = null) =>
        ManagedElsaInstanceOverviewProjection.ToOverview(
            instance,
            instance.WorkspaceId,
            role,
            canOpenPermission: role is not WorkspaceRole.Reader,
            canDeletePermission: canDeletePermission ?? role is WorkspaceRole.Owner or WorkspaceRole.SourceAdmin,
            identity: null,
            activeOperation,
            lastOperation: null,
            new ManagedLifecycleOperationalHealthResult(
                ManagedLifecycleOperationalHealthStatus.Healthy,
                ManagedLifecycleOperationalHealthDiagnosticCodes.Healthy,
                new string('a', 64),
                Now,
                []),
            ElsaInstanceCommercialGateDecision.Allow(),
            ElsaInstanceCommercialGateDecision.Allow(),
            deleteGate ?? ElsaInstanceCommercialGateDecision.Allow());

    private static ElsaInstance Instance(
        ElsaObservedLifecycle observed,
        ElsaInstanceHealth health = ElsaInstanceHealth.Healthy,
        ElsaDesiredLifecycle desired = ElsaDesiredLifecycle.Running) =>
        ElsaInstance.Hydrate(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            OrganizationId,
            WorkspaceId,
            "Overview runtime",
            "overview-runtime",
            new ElsaInstanceIntent(
                new ElsaReleaseIntent("valence-runtime", "3.8", requestedVersion: "3.8.4", channel: "stable"),
                new ElsaApplicationIntent(
                    "combined",
                    "starter",
                    new Dictionary<string, ElsaFeatureOverride> { ["replicas"] = ElsaFeatureOverride.FromNumber(3) },
                    "approved"),
                new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"),
                observed == ElsaObservedLifecycle.Deleted ? ElsaDesiredLifecycle.Deleting : desired),
            observed,
            health,
            2,
            deletedAt: observed == ElsaObservedLifecycle.Deleted ? Now : null,
            createdAt: Now,
            updatedAt: Now);

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
            null,
            null,
            null,
            null,
            null,
            null,
            null);
}
