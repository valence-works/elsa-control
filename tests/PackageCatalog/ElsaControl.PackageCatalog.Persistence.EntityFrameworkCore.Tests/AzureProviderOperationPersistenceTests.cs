using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore.Tests;

public sealed class AzureProviderOperationPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _workspaceId = Guid.NewGuid();

    public AzureProviderOperationPersistenceTests()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
        db.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Azure operation workspace" });
        db.SaveChanges();
    }

    [Fact]
    public void Runnable_queue_has_a_global_polling_index()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(AzureProviderOperationEntity))!;
        var index = Assert.Single(entity.GetIndexes(), candidate =>
            candidate.Properties.Select(property => property.Name).SequenceEqual(
                ["Status", "LeaseExpiresAt", "UpdatedAt", "Id"]));

        Assert.Equal("IX_AzureProviderOperations_Status_LeaseExpiresAt_UpdatedAt_Id", index.GetDatabaseName());
    }

    [Fact]
    public async Task Create_is_idempotent_and_transitions_are_append_only()
    {
        var request = Request();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var first = await store.CreateOrGetWithResultAsync(request, DateTimeOffset.UtcNow);
        var second = await store.CreateOrGetWithResultAsync(request, DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal(first.Operation.Id, second.Operation.Id);
        var accepted = Assert.Single(await store.ListTransitionsAsync(_workspaceId, first.Operation.Id));
        Assert.Equal("Azure provider operation accepted.", accepted.Message);
    }

    [Theory]
    [InlineData(AzureProviderOperationPhase.FoundationSubmitted)]
    [InlineData(AzureProviderOperationPhase.FoundationReady)]
    public async Task Checkpoint_and_finalize_update_the_bound_assignment_atomically(AzureProviderOperationPhase phase)
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignment = await Assignment(store, organizationId, instanceId, now);
        var operation = await store.CreateOrGetAsync(Request() with
        {
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignment.Id
        }, now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now, operation.Version));
        var resources = new AzureProviderResourceReferences(
            assignment.ResourceGroupName,
            FoundationDeploymentId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/foundation");

        var checkpointed = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId,
            operation.Id,
            "lease",
            new(phase, "foundation.checkpointed", "ignored", resources, null, AzureProviderHealth.Unknown, []),
            now,
            claimed.Version));
        var during = Assert.IsType<AzureProviderResourceAssignment>(await ((IAzureProviderResourceAssignmentStore)store).GetAsync(_workspaceId, assignment.Id));
        Assert.Equal(AzureProviderAssignmentState.Provisioning, during.State);
        Assert.Equal(resources.FoundationDeploymentId, during.Resources.FoundationDeploymentId);
        Assert.Equal(operation.Id, during.LastOperationId);

        // SeedSecrets runs only after FoundationSubmitted has been committed. A fresh
        // context must see the exact operation link used by durable secret authorization.
        using (var reloadedDb = CreateContext())
        {
            var reloadedStore = new AzureProviderOperationStore(reloadedDb);
            var authorization = Assert.IsType<AzureSecretAuthorization>(await
                new DurableAzureSecretAuthorizationStore(reloadedStore, reloadedStore)
                    .GetAsync(_workspaceId, assignment.Id));
            Assert.Equal(operation.Id, authorization.Assignment.LastOperationId);
            Assert.Equal(operation.Id, authorization.Operation.Id);
            Assert.Equal(phase, authorization.Operation.Phase);
            Assert.Equal(AzureProviderOperationStatus.Running, authorization.Operation.Status);
        }

        _ = Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
            _workspaceId, operation.Id, "lease", AzureProviderOperationStatus.Succeeded, "operation.succeeded", now, checkpointed.Version));
        var active = Assert.IsType<AzureProviderResourceAssignment>(await ((IAzureProviderResourceAssignmentStore)store).GetAsync(_workspaceId, assignment.Id));
        Assert.Equal(AzureProviderAssignmentState.Active, active.State);
        Assert.Null(active.DeletedAt);
    }

    [Fact]
    public async Task Failed_step_diagnostics_round_trip_through_checkpoint_and_finalization()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        AzureProviderOperation operation;
        AzureProviderOperation checkpointed;
        var expectedCodes = new[]
        {
            "azure.step.acr-pull.failed",
            "azure.step.acr-pull.process.non-zero-exit"
        };

        using (var db = CreateContext())
        {
            var store = new AzureProviderOperationStore(db);
            var assignment = await Assignment(store, organizationId, instanceId, now);
            operation = await store.CreateOrGetAsync(Request() with
            {
                TargetKey = "diagnostic-round-trip",
                IdempotencyKey = "diagnostic-round-trip",
                OrganizationId = organizationId,
                InstanceId = instanceId,
                LifecycleAction = ElsaInstanceOperationAction.Reconcile,
                ProviderAssignmentId = assignment.Id
            }, now);
            var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
                _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now, operation.Version));

            checkpointed = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
                _workspaceId,
                operation.Id,
                "lease",
                new(
                    AzureProviderOperationPhase.FoundationSubmitted,
                    expectedCodes[0],
                    "The provider step failed.",
                    new(ResourceGroupName: assignment.ResourceGroupName),
                    null,
                    AzureProviderHealth.Unknown,
                    [
                        new(expectedCodes[0], "untrusted step details"),
                        new(expectedCodes[1], "untrusted process output")
                    ]),
                now,
                claimed.Version));

            var finalized = Assert.IsType<AzureProviderOperation>(await store.FinalizeAsync(
                _workspaceId,
                operation.Id,
                "lease",
                AzureProviderOperationStatus.Failed,
                "azure.step.failed",
                now.AddSeconds(1),
                checkpointed.Version));
            Assert.Equal(AzureProviderOperationStatus.Failed, finalized.Status);
        }

        using (var reloadedDb = CreateContext())
        {
            var reloadedStore = new AzureProviderOperationStore(reloadedDb);
            var persisted = Assert.IsType<AzureProviderOperation>(await reloadedStore.GetAsync(_workspaceId, operation.Id));

            Assert.Equal(AzureProviderOperationStatus.Failed, persisted.Status);
            Assert.Equal(expectedCodes, persisted.Diagnostics.Select(diagnostic => diagnostic.Code));
            Assert.All(persisted.Diagnostics, diagnostic => Assert.Equal(diagnostic.Code, diagnostic.Message));
            Assert.DoesNotContain(persisted.Diagnostics, diagnostic => diagnostic.Message.Contains("untrusted", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(persisted.Diagnostics, diagnostic => diagnostic.Message.Contains("password", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("instance")]
    public async Task Finalize_rejects_an_assignment_with_a_mismatched_owner(string mismatch)
    {
        var now = DateTimeOffset.UtcNow;
        var assignmentOrganizationId = Guid.NewGuid();
        var assignmentInstanceId = Guid.NewGuid();
        var operationOrganizationId = mismatch == "organization" ? Guid.NewGuid() : assignmentOrganizationId;
        var operationInstanceId = mismatch == "instance" ? Guid.NewGuid() : assignmentInstanceId;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignment = await Assignment(store, assignmentOrganizationId, assignmentInstanceId, now);
        var operation = await store.CreateOrGetAsync(Request() with
        {
            TargetKey = $"mismatched-{mismatch}",
            IdempotencyKey = $"mismatched-{mismatch}",
            OrganizationId = operationOrganizationId,
            InstanceId = operationInstanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignment.Id
        }, now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease",
            AzureProviderOperationStatus.Succeeded,
            "operation.succeeded",
            now,
            claimed.Version));

        Assert.Equal("The Azure provider assignment binding is invalid.", exception.Message);
        var persistedOperation = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operation.Id));
        Assert.Equal(AzureProviderOperationStatus.Running, persistedOperation.Status);
        Assert.Equal(claimed.Version, persistedOperation.Version);
        Assert.Equal("worker", persistedOperation.WorkerId);
        var persistedAssignment = Assert.IsType<AzureProviderResourceAssignment>(await ((IAzureProviderResourceAssignmentStore)store).GetAsync(_workspaceId, assignment.Id));
        Assert.Equal(AzureProviderAssignmentState.Reserved, persistedAssignment.State);
        Assert.Null(persistedAssignment.LastOperationId);
        Assert.Equal(assignment.Version, persistedAssignment.Version);
    }

    [Fact]
    public async Task Checkpoint_rejects_a_foreign_resource_group_before_a_later_context_save()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignment = await Assignment(store, organizationId, instanceId, now);
        var operation = await store.CreateOrGetAsync(Request() with
        {
            TargetKey = "foreign-resource-group",
            IdempotencyKey = "foreign-resource-group",
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignment.Id
        }, now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckpointAsync(
            _workspaceId,
            operation.Id,
            "lease",
            new(
                AzureProviderOperationPhase.FoundationReady,
                "foundation.ready",
                "Ready.",
                new(ResourceGroupName: "rg-foreign"),
                null,
                AzureProviderHealth.Unknown,
                []),
            now,
            claimed.Version));

        Assert.Equal("The Azure provider assignment binding is invalid.", exception.Message);

        // The failed checkpoint must leave no staged tracked mutation behind for a later save.
        await db.SaveChangesAsync();
        using var reloadedDb = CreateContext();
        var reloadedStore = new AzureProviderOperationStore(reloadedDb);
        var persistedOperation = Assert.IsType<AzureProviderOperation>(await reloadedStore.GetAsync(_workspaceId, operation.Id));
        Assert.Equal(AzureProviderOperationStatus.Running, persistedOperation.Status);
        Assert.Equal(claimed.Version, persistedOperation.Version);
        Assert.Equal(new AzureProviderResourceReferences(), persistedOperation.Resources);

        var persistedAssignment = Assert.IsType<AzureProviderResourceAssignment>(await
            ((IAzureProviderResourceAssignmentStore)reloadedStore).GetAsync(_workspaceId, assignment.Id));
        Assert.Equal(AzureProviderAssignmentState.Reserved, persistedAssignment.State);
        Assert.Equal(assignment.ResourceGroupName, persistedAssignment.ResourceGroupName);
        Assert.Equal(assignment.Resources, persistedAssignment.Resources);
        Assert.Null(persistedAssignment.LastOperationId);
        Assert.Equal(assignment.Version, persistedAssignment.Version);
    }

    [Fact]
    public async Task Cleanup_verified_checkpoint_clears_inventory_but_preserves_assignment_resource_group()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignment = await Assignment(store, organizationId, instanceId, now);
        var operation = await store.CreateOrGetAsync(Request() with
        {
            TargetKey = "cleanup-assignment-group",
            IdempotencyKey = "cleanup-assignment-group",
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Delete,
            ProviderAssignmentId = assignment.Id
        }, now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var resources = new AzureProviderResourceReferences(
            ResourceGroupName: assignment.ResourceGroupName.ToUpperInvariant(),
            FoundationDeploymentId: $"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/foundation");

        var submitted = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId,
            operation.Id,
            "lease",
            new(AzureProviderOperationPhase.CleanupSubmitted, "cleanup.submitted", "Submitted.", resources, null, AzureProviderHealth.Unknown, []),
            now,
            claimed.Version));
        var verified = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId,
            operation.Id,
            "lease",
            new(AzureProviderOperationPhase.CleanupVerified, "cleanup.verified", "Verified.", new(), null, AzureProviderHealth.Unknown, [], ReplaceResources: true),
            now.AddSeconds(1),
            submitted.Version));

        Assert.Equal(assignment.ResourceGroupName, verified.Resources.ResourceGroupName);
        Assert.Null(verified.Resources.FoundationDeploymentId);
        var persistedAssignment = Assert.IsType<AzureProviderResourceAssignment>(await
            ((IAzureProviderResourceAssignmentStore)store).GetAsync(_workspaceId, assignment.Id));
        Assert.Equal(AzureProviderAssignmentState.Deleted, persistedAssignment.State);
        Assert.Equal(assignment.ResourceGroupName, persistedAssignment.ResourceGroupName);
        Assert.Equal(new AzureProviderResourceReferences(ResourceGroupName: assignment.ResourceGroupName), persistedAssignment.Resources);
    }

    [Fact]
    public async Task Cleanup_rehydrates_group_only_checkpoint_before_and_after_delete_finalization()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var lifecycleOperationId = Guid.NewGuid();
        var workloadName = $"e{instanceId:N}"[..16];
        var operationId = Guid.Empty;
        var leaseVersion = 0L;
        var leaseToken = "cleanup-lease";
        AzureProviderResourceAssignment assignment;

        // Use the real EF store to create the assignment and to persist the exact
        // checkpoint shape that cleanup observes after inventory has been cleared.
        using (var db = CreateContext())
        {
            var store = new AzureProviderOperationStore(db);
            assignment = await ((IAzureProviderResourceAssignmentStore)store).CreateOrGetAsync(
                new(
                    _workspaceId,
                    organizationId,
                    instanceId,
                    new string('a', 64),
                    "11111111-1111-1111-1111-111111111111",
                    "rg-elsa",
                    workloadName,
                    "westeurope"),
                now);
            var operation = await store.CreateOrGetAsync(Request() with
            {
                TargetKey = workloadName,
                Action = AzureProviderOperationAction.Delete,
                IdempotencyKey = $"elsa-instance-operation:{lifecycleOperationId:D}:delete",
                OrganizationId = organizationId,
                InstanceId = instanceId,
                LifecycleAction = ElsaInstanceOperationAction.Delete,
                ProviderAssignmentId = assignment.Id,
                ProviderScopeFingerprint = new string('a', 64)
            }, now);
            operationId = operation.Id;
            var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
                _workspaceId, operation.Id, "cleanup-worker", leaseToken, TimeSpan.FromMinutes(1), now));
            var submitted = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
                _workspaceId,
                operation.Id,
                leaseToken,
                new(
                    AzureProviderOperationPhase.CleanupSubmitted,
                    "cleanup.submitted",
                    "Submitted.",
                    new(
                        ResourceGroupName: assignment.ResourceGroupName,
                        FoundationDeploymentId: $"/subscriptions/{assignment.SubscriptionId}/resourceGroups/{assignment.ResourceGroupName}/providers/Microsoft.Resources/deployments/foundation"),
                    null,
                    AzureProviderHealth.Unknown,
                    []),
                now,
                claimed.Version));
            var checkpointed = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
                _workspaceId,
                operation.Id,
                leaseToken,
                new(
                    AzureProviderOperationPhase.CleanupVerified,
                    "cleanup.verified",
                    "Verified.",
                    new(),
                    null,
                    AzureProviderHealth.Unknown,
                    [],
                    ReplaceResources: true),
                now.AddSeconds(1),
                submitted.Version));
            leaseVersion = checkpointed.Version;
        }

        var options = new AzureElsaInstanceProviderOptions
        {
            Enabled = true,
            TemplateFingerprint = new string('b', 64),
            ProviderScopeFingerprint = new string('a', 64),
            SubscriptionId = "11111111-1111-1111-1111-111111111111",
            ResourceGroupNamePrefix = "rg-elsa"
        };
        var cleanupRequest = new ElsaInstanceCleanupRequest(
            _workspaceId,
            instanceId,
            lifecycleOperationId,
            1,
            null,
            new ElsaPlacementAssignmentReference(assignment.Id.ToString("D")),
            null);

        static Task<ElsaInstanceCleanupObservation> ObserveCleanupAsync(
            AzureProviderOperationStore store,
            AzureElsaInstanceProviderOptions options,
            ElsaInstanceCleanupRequest request) =>
            new AzureElsaInstanceProvider(
                new AzureProviderOperationService(store), store, store, options: options)
                .CleanupAsync(request);

        // Rehydrate both assignment and operation through a new context while the
        // provider operation is still Running after CleanupVerified. This is the
        // narrow checkpoint/finalization window and must remain deferred, not recovery.
        using (var beforeFinalizeDb = CreateContext())
        {
            var store = new AzureProviderOperationStore(beforeFinalizeDb);
            var persisted = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operationId));
            Assert.Equal(organizationId, persisted.OrganizationId);
            Assert.Equal(instanceId, persisted.InstanceId);
            Assert.Equal(assignment.Id, persisted.ProviderAssignmentId);
            Assert.Equal(AzureProviderOperationAction.Delete, persisted.Action);
            Assert.Equal(ElsaInstanceOperationAction.Delete, persisted.LifecycleAction);
            Assert.Equal($"elsa-instance-operation:{lifecycleOperationId:D}:delete", persisted.IdempotencyKey);
            Assert.Equal(workloadName, persisted.TargetKey);
            Assert.Equal(new string('a', 64), persisted.ProviderScopeFingerprint);
            var observation = await ObserveCleanupAsync(store, options, cleanupRequest);

            Assert.Equal(ElsaInstanceCleanupObservationKind.InProgress, observation.Kind);
            Assert.Equal("deletion.provider-cleanup-pending", observation.DiagnosticCode);
        }

        using (var finalizeDb = CreateContext())
        {
            var store = new AzureProviderOperationStore(finalizeDb);
            Assert.Equal(
                AzureProviderOperationStatus.Succeeded,
                (await store.FinalizeAsync(
                    _workspaceId,
                    operationId,
                    leaseToken,
                    AzureProviderOperationStatus.Succeeded,
                    "cleanup.succeeded",
                    now.AddSeconds(2),
                    leaseVersion))?.Status);
        }

        // A second fresh context must see the immutable assignment group retained
        // by FinalizeAsync and confirm deletion without submitting another provider call.
        using (var afterFinalizeDb = CreateContext())
        {
            var store = new AzureProviderOperationStore(afterFinalizeDb);
            var afterFinalizeRequest = cleanupRequest with { AttemptNumber = 2 };
            var observation = await ObserveCleanupAsync(store, options, afterFinalizeRequest);

            Assert.Equal(ElsaInstanceCleanupObservationKind.ConfirmedAbsent, observation.Kind);
            Assert.Equal("deletion.provider-confirmed-absent", observation.DiagnosticCode);
            var persistedAssignment = Assert.IsType<AzureProviderResourceAssignment>(await
                ((IAzureProviderResourceAssignmentStore)store).GetAsync(_workspaceId, assignment.Id));
            Assert.Equal(AzureProviderAssignmentState.Deleted, persistedAssignment.State);
            Assert.Equal(assignment.ResourceGroupName, persistedAssignment.ResourceGroupName);
            Assert.Equal(new AzureProviderResourceReferences(assignment.ResourceGroupName), persistedAssignment.Resources);
        }
    }

    [Fact]
    public async Task Commercial_denial_is_authorized_and_held_in_the_same_operation_CAS()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var assignment = await Assignment(store, organizationId, instanceId, now);
        var operation = await store.CreateOrGetAsync(Request() with
        {
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignment.Id
        }, now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));

        var authorization = await store.AuthorizeAsync(
            _workspaceId,
            operation.Id,
            "lease",
            new DenyingCommercialGate(),
            now,
            claimed.Version);

        Assert.NotNull(authorization);
        Assert.False(authorization!.Decision.Allowed);
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, authorization.Decision.Code);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, authorization.Operation.Status);
        Assert.Null(authorization.Operation.CompletedAt);
        Assert.Null(authorization.Operation.WorkerId);
        Assert.Null(authorization.Operation.LeaseExpiresAt);
        Assert.Contains(
            await store.ListTransitionsAsync(_workspaceId, operation.Id),
            transition => transition.Code == ElsaInstanceCommercialOperation.LifecycleConstrained);
    }

    [Fact]
    public async Task Bound_safe_exit_supersedes_held_provider_operation_before_new_operation_executes()
    {
        var now = DateTimeOffset.UtcNow;
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var assignment = await Assignment(store, organizationId, instanceId, now);
        var held = await store.CreateOrGetAsync(Request() with
        {
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignment.Id
        }, now);
        var claimedHeld = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, held.Id, "worker-held", "lease-held", TimeSpan.FromMinutes(1), now));
        var authorization = await store.AuthorizeAsync(
            _workspaceId, held.Id, "lease-held", new DenyingCommercialGate(), now, claimedHeld.Version);
        Assert.Equal(AzureProviderOperationStatus.EntitlementHeld, authorization?.Operation.Status);

        var safeExit = await store.CreateOrGetAsync(Request() with
        {
            IdempotencyKey = "safe-stop",
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Stop,
            ProviderAssignmentId = assignment.Id
        }, now.AddMinutes(1));

        Assert.Equal(AzureProviderOperationStatus.Accepted, safeExit.Status);
        var superseded = await store.GetAsync(_workspaceId, held.Id);
        Assert.Equal(AzureProviderOperationStatus.Cancelled, superseded?.Status);
        Assert.Contains(
            await store.ListTransitionsAsync(_workspaceId, held.Id),
            transition => transition.Code == ElsaInstanceCommercialOperation.EntitlementSafeExitSuperseded);

        var claimedSafeExit = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, safeExit.Id, "worker-safe-exit", "lease-safe-exit", TimeSpan.FromMinutes(1), now.AddMinutes(1), safeExit.Version));
        var completedSafeExit = await store.FinalizeAsync(
            _workspaceId, safeExit.Id, "lease-safe-exit", AzureProviderOperationStatus.Succeeded,
            "azure.operation.succeeded", now.AddMinutes(1), claimedSafeExit.Version);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, completedSafeExit?.Status);
    }

    [Fact]
    public async Task Unknown_workspace_is_rejected_by_catalog_foreign_key()
    {
        using var db = CreateContext();
        var request = Request() with { WorkspaceId = Guid.NewGuid() };
        await Assert.ThrowsAsync<DbUpdateException>(() => new AzureProviderOperationStore(db).CreateOrGetAsync(request, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Equivalent_active_identity_converges_even_with_a_new_request_key()
    {
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var first = await store.CreateOrGetAsync(Request(), DateTimeOffset.UtcNow);
        var second = await store.CreateOrGetAsync(Request() with { IdempotencyKey = "replayed-request" }, DateTimeOffset.UtcNow);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public async Task Different_active_plan_cannot_claim_the_same_target()
    {
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var first = await store.CreateOrGetAsync(Request(), DateTimeOffset.UtcNow);

        var conflict = await Assert.ThrowsAsync<AzureProviderOperationConflictException>(() =>
            store.CreateOrGetAsync(Request() with
            {
                IdempotencyKey = "different-plan",
                PlanFingerprint = new('f', 64)
            }, DateTimeOffset.UtcNow));

        Assert.Equal(first.Id, conflict.Operation.Id);
        Assert.Equal(first.TargetKey, conflict.Operation.TargetKey);
    }

    [Fact]
    public async Task Delete_cannot_be_created_while_reconcile_is_active_for_the_same_target()
    {
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var first = await store.CreateOrGetAsync(Request(), DateTimeOffset.UtcNow);

        var conflict = await Assert.ThrowsAsync<AzureProviderOperationConflictException>(() =>
            store.CreateOrGetAsync(Request() with
            {
                Action = AzureProviderOperationAction.Delete,
                IdempotencyKey = "delete-request"
            }, DateTimeOffset.UtcNow));

        Assert.Equal(first.Id, conflict.Operation.Id);
        Assert.Equal(AzureProviderOperationAction.Reconcile, conflict.Operation.Action);
    }

    [Fact]
    public async Task Claim_checkpoint_finalize_and_stale_recovery_preserve_history()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = await store.ClaimAsync(_workspaceId, operation.Id, "worker-1", "lease-1", TimeSpan.FromMinutes(1), now);
        Assert.NotNull(claimed);
        Assert.Null(await store.ClaimAsync(_workspaceId, operation.Id, "worker-2", "lease-2", TimeSpan.FromMinutes(1), now));

        var checkpoint = await store.CheckpointAsync(_workspaceId, operation.Id, "lease-1", new(
            AzureProviderOperationPhase.FoundationReady, "foundation.ready", "hunter2",
            new(ResourceGroupName: "rg-safe", FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.Resources/deployments/deployment-1"), null, AzureProviderHealth.Unknown,
            [new("foundation.note", "even-more-sensitive")]), now);
        var savedCheckpoint = Assert.IsType<AzureProviderOperation>(checkpoint);
        Assert.Equal(AzureProviderOperationPhase.FoundationReady, savedCheckpoint.Phase);
        Assert.Equal("foundation.note", Assert.Single(savedCheckpoint.Diagnostics).Message);
        var replay = await store.CheckpointAsync(_workspaceId, operation.Id, "lease-1", new(
            AzureProviderOperationPhase.FoundationReady, "foundation.ready", "hunter2",
            new(ResourceGroupName: "rg-safe", FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.Resources/deployments/deployment-1"), null, AzureProviderHealth.Unknown,
            [new("foundation.note", "different-unpersisted-detail")]), now);
        Assert.Equal(savedCheckpoint.Version, replay?.Version);
        var completed = await store.FinalizeAsync(_workspaceId, operation.Id, "lease-1", AzureProviderOperationStatus.Succeeded, "operation.succeeded", now, savedCheckpoint.Version);
        Assert.Equal(AzureProviderOperationStatus.Succeeded, completed?.Status);
        Assert.Null(await store.FinalizeAsync(_workspaceId, operation.Id, "wrong-lease", AzureProviderOperationStatus.Succeeded, "operation.succeeded", now));
        Assert.Equal(completed?.Id, (await store.FinalizeAsync(_workspaceId, operation.Id, "lease-1", AzureProviderOperationStatus.Succeeded, "operation.succeeded", now, savedCheckpoint.Version))?.Id);
        Assert.Null(await store.FinalizeAsync(_workspaceId, operation.Id, "lease-1", AzureProviderOperationStatus.Succeeded, "operation.different", now, savedCheckpoint.Version));
        Assert.Equal(4, (await store.ListTransitionsAsync(_workspaceId, operation.Id)).Count);
        Assert.DoesNotContain(await store.ListTransitionsAsync(_workspaceId, operation.Id), x => x.Message.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expired_lease_requires_recovery_and_rejects_old_lease()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        await store.ClaimAsync(_workspaceId, operation.Id, "worker-1", "lease-1", TimeSpan.FromMinutes(1), now);
        Assert.Equal(1, await store.RecoverStaleAsync(now.AddMinutes(2)));
        var recovered = await store.GetAsync(_workspaceId, operation.Id);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, recovered?.Status);
        Assert.Null(await store.ClaimAsync(_workspaceId, operation.Id, "worker-2", "lease-2", TimeSpan.FromMinutes(1), now.AddMinutes(2)));
        Assert.NotNull(await store.ClaimRecoveryAsync(_workspaceId, operation.Id, "worker-2", "lease-2", TimeSpan.FromMinutes(1), now.AddMinutes(2), recovered!.Version));
        Assert.Null(await store.CheckpointAsync(_workspaceId, operation.Id, "lease-1", new(
            AzureProviderOperationPhase.FoundationReady, "late", "Late.", new(), null, AzureProviderHealth.Unknown, []), now.AddMinutes(2)));
        Assert.Null(recovered?.CompletedAt);
        var transitions = await store.ListTransitionsAsync(_workspaceId, operation.Id);
        Assert.Contains(transitions, x => x.Code == "operation.recovery.required");
        Assert.Contains(transitions, x => x.Code == "operation.recovery.claimed");
    }

    [Fact]
    public async Task Provider_worker_preserves_stale_operation_until_explicit_provider_observation()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var organizationId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var assignment = await Assignment(store, organizationId, instanceId, now);
        var request = Request() with
        {
            ReleaseManifestDigest = "sha256:" + new string('d', 64),
            ReleaseManifestSignatureDigest = "sha256:" + new string('e', 64),
            ReleaseManifestReference = "oci://evidence.example/manifest",
            ReleaseManifestSignatureReference = "oci://evidence.example/signature",
            SqlWorkflowPackageVersion = "3.8.0",
            SqlQuartzPackageVersion = "3.8.0",
            SecretReferences = new Dictionary<string, string>
            {
                ["database:connectionstring"] = "secret://vault/database",
                ["identity:signingkey"] = "secret://vault/signingkey",
                ["admin:password"] = "secret://vault/admin"
            },
            OrganizationId = organizationId,
            InstanceId = instanceId,
            LifecycleAction = ElsaInstanceOperationAction.Reconcile,
            ProviderAssignmentId = assignment.Id
        };
        var operation = await store.CreateOrGetAsync(request, now);
        _ = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker-stale", "lease-stale", TimeSpan.FromMinutes(1), now));
        Assert.Equal(1, await store.RecoverStaleAsync(now.AddMinutes(2)));

        var runner = new RecoveringCompletingRunner();
        var worker = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, runner, new FixedTimeProvider(now.AddMinutes(2))),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(now.AddMinutes(2)));

        Assert.Equal(0, await worker.ProcessOnceAsync());
        var firstRecovery = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operation.Id));
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, firstRecovery.Status);
        Assert.Equal(operation.RequestHash, firstRecovery.RequestHash);
        Assert.Equal(1, firstRecovery.AttemptNumber);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Latest_active_reconcile_includes_recovery_required_operation_without_resource_references()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var request = Request() with { ProviderScopeFingerprint = new string('d', 64) };
        var operation = await store.CreateOrGetAsync(request, now);
        Assert.NotNull(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker-1", "lease-1", TimeSpan.FromMinutes(1), now));
        Assert.Equal(1, await store.RecoverStaleAsync(now.AddMinutes(2)));

        var active = await store.GetLatestActiveReconcileAsync(
            _workspaceId, request.TargetKey.ToUpperInvariant(), $" {request.ProviderScopeFingerprint!.ToUpperInvariant()} ");

        Assert.NotNull(active);
        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, active.Status);
        Assert.Equal(new AzureProviderResourceReferences(), active.Resources);
    }

    [Fact]
    public async Task Latest_reconcile_includes_precheckpoint_operation_without_resource_references()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);

        var latest = await store.GetLatestReconcileAsync(
            _workspaceId, Request().TargetKey.ToUpperInvariant(), providerScopeFingerprint: null);

        Assert.NotNull(latest);
        Assert.Equal(operation.Id, latest.Id);
        Assert.Equal(AzureProviderOperationAction.Reconcile, latest.Action);
        Assert.Equal(new AzureProviderResourceReferences(), latest.Resources);
    }

    [Fact]
    public async Task Recovery_claim_only_accepts_recovery_required_operations()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);

        Assert.Null(await store.ClaimRecoveryAsync(
            _workspaceId, operation.Id, "recovery-worker", "recovery-lease", TimeSpan.FromMinutes(1), now));
        Assert.NotNull(await store.ClaimAsync(
            _workspaceId, operation.Id, "normal-worker", "normal-lease", TimeSpan.FromMinutes(1), now));
    }

    [Fact]
    public async Task Transition_sequences_use_the_operation_version()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var heartbeat = Assert.IsType<AzureProviderOperation>(await store.HeartbeatAsync(
            _workspaceId, operation.Id, "lease", TimeSpan.FromMinutes(1), now.AddSeconds(1), claimed.Version));
        var checkpoint = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, operation.Id, "lease",
            new(AzureProviderOperationPhase.FoundationReady, "foundation.ready", "Ready.", new(), null, AzureProviderHealth.Unknown, []),
            now.AddSeconds(2), heartbeat.Version));

        var transitions = await store.ListTransitionsAsync(_workspaceId, operation.Id);
        Assert.Equal([operation.Version, claimed.Version, checkpoint.Version], transitions.Select(x => x.Sequence));
    }

    [Fact]
    public async Task Checkpoints_with_distinct_codes_preserve_distinct_transitions()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var state = new AzureProviderResourceReferences(ResourceGroupName: "rg-safe");
        var first = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, operation.Id, "lease",
            new(AzureProviderOperationPhase.FoundationReady, "foundation.ready", "Ready.", state, null, AzureProviderHealth.Unknown, []),
            now.AddSeconds(1), claimed.Version));
        var second = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, operation.Id, "lease",
            new(AzureProviderOperationPhase.FoundationReady, "foundation.verified", "Verified.", state, null, AzureProviderHealth.Unknown, []),
            now.AddSeconds(2), first.Version));

        var transitions = await store.ListTransitionsAsync(_workspaceId, operation.Id);
        Assert.Contains(transitions, x => x.Code == "foundation.ready");
        Assert.Contains(transitions, x => x.Code == "foundation.verified");
        Assert.Equal(first.Version + 1, second.Version);
    }

    [Fact]
    public async Task Unrestorable_plan_is_terminal_and_value_free_with_compare_and_set()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);

        var failed = await store.MarkUnrestorableAsync(_workspaceId, operation.Id, now, operation.Version);

        Assert.Equal(AzureProviderOperationStatus.Failed, failed?.Status);
        Assert.Equal(now, failed?.CompletedAt);
        var transition = Assert.Single(await store.ListTransitionsAsync(_workspaceId, operation.Id), x => x.Code == "azure.plan.unrestorable");
        Assert.Equal("The persisted provider plan cannot be restored.", transition.Message);
        Assert.Empty(await store.ListRunnableAsync(now, 10));
        Assert.Null(await store.MarkUnrestorableAsync(_workspaceId, operation.Id, now, operation.Version));
    }

    [Fact]
    public async Task Unrestorable_recovery_operation_remains_reserved_for_operator_reconciliation()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        Assert.NotNull(await store.ClaimAsync(_workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        Assert.Equal(1, await store.RecoverStaleAsync(now.AddMinutes(2)));
        var recovery = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operation.Id));

        var blocked = await store.MarkUnrestorableAsync(_workspaceId, operation.Id, now.AddMinutes(2), recovery.Version);

        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, blocked?.Status);
        Assert.Null(blocked?.CompletedAt);
        Assert.Empty(await store.ListRunnableAsync(now.AddMinutes(2), 10));
        await Assert.ThrowsAsync<AzureProviderOperationConflictException>(() =>
            store.CreateOrGetAsync(Request() with { IdempotencyKey = "different-plan", PlanFingerprint = new('f', 64) }, now.AddMinutes(2)));
    }

    [Fact]
    public async Task Recovery_required_finalization_remains_reserved_and_requires_explicit_recovery()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId,
            operation.Id,
            "worker",
            "lease",
            TimeSpan.FromMinutes(1),
            now));

        var finalized = await store.FinalizeAsync(
            _workspaceId,
            operation.Id,
            "lease",
            AzureProviderOperationStatus.RecoveryRequired,
            "azure.operation.recovery-required",
            now,
            claimed.Version);

        Assert.Equal(AzureProviderOperationStatus.RecoveryRequired, finalized?.Status);
        Assert.Null(finalized?.CompletedAt);
        Assert.Empty(await store.ListRunnableAsync(now.AddMinutes(2), 10));
        Assert.NotNull(await store.ClaimRecoveryAsync(
            _workspaceId,
            operation.Id,
            "worker",
            "recovery-lease",
            TimeSpan.FromMinutes(1),
            now.AddMinutes(2),
            finalized!.Version));
    }

    [Fact]
    public async Task Recovery_required_rows_cannot_fill_the_batch_before_runnable_work()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        foreach (var targetKey in new[] { "recovery-a", "recovery-b" })
        {
            var recovery = await store.CreateOrGetAsync(Request() with
            {
                TargetKey = targetKey,
                IdempotencyKey = targetKey
            }, now);
            var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
                _workspaceId, recovery.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
            Assert.NotNull(await store.FinalizeAsync(
                _workspaceId, recovery.Id, "lease",
                AzureProviderOperationStatus.RecoveryRequired,
                "azure.operation.recovery-required",
                now,
                claimed.Version));
        }

        var runnableOperation = await store.CreateOrGetAsync(Request() with
        {
            TargetKey = "runnable",
            IdempotencyKey = "runnable"
        }, now.AddSeconds(1));

        var runnable = await store.ListRunnableAsync(now.AddMinutes(2), 2);

        var onlyRunnable = Assert.Single(runnable);
        Assert.Equal(runnableOperation.Id, onlyRunnable.Id);
        Assert.DoesNotContain(runnable, operation => operation.Status == AzureProviderOperationStatus.RecoveryRequired);
    }

    [Fact]
    public async Task New_reconcile_inherits_the_latest_durable_resource_snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var first = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, first.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var checkpoint = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, first.Id, "lease",
            new(AzureProviderOperationPhase.WorkloadReady, "workload.ready", "Ready.",
                new(ResourceGroupName: "rg", WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.App/containerApps/app"),
                "https://workload.example.test", AzureProviderHealth.Healthy, []),
            now, claimed.Version));
        Assert.NotNull(await store.FinalizeAsync(
            _workspaceId, first.Id, "lease", AzureProviderOperationStatus.Succeeded, "operation.succeeded", now, checkpoint.Version));

        var next = await store.CreateOrGetAsync(
            Request() with { IdempotencyKey = "next-reconcile", PlanFingerprint = new('f', 64) },
            now.AddMinutes(1));

        Assert.Equal("rg", next.Resources.ResourceGroupName);
        Assert.Equal("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.App/containerApps/app", next.Resources.WorkloadResourceId);
    }

    [Fact]
    public async Task New_scope_never_inherits_resource_handles_from_an_old_scope()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var first = await store.CreateOrGetAsync(Request() with { ProviderScopeFingerprint = new string('a', 64) }, now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, first.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var checkpoint = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, first.Id, "lease",
            new(AzureProviderOperationPhase.WorkloadReady, "workload.ready", "Ready.",
                new(ResourceGroupName: "rg", WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.App/containerApps/app"),
                null, AzureProviderHealth.Unknown, []),
            now, claimed.Version));
        Assert.NotNull(await store.FinalizeAsync(
            _workspaceId, first.Id, "lease", AzureProviderOperationStatus.Succeeded, "operation.succeeded", now, checkpoint.Version));

        var next = await store.CreateOrGetAsync(
            Request() with
            {
                IdempotencyKey = "new-scope",
                PlanFingerprint = new string('f', 64),
                ProviderScopeFingerprint = new string('b', 64)
            },
            now.AddMinutes(1));

        Assert.Equal(new AzureProviderResourceReferences(), next.Resources);
    }

    [Fact]
    public async Task Resource_reference_snapshot_round_trips_all_provider_resources()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var resources = new AzureProviderResourceReferences(
            ResourceGroupName: "rg",
            FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Resources/deployments/foundation",
            WorkloadDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Resources/deployments/workload",
            WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.App/containerApps/workload",
            WorkloadRevisionName: "workload--000001",
            StableTrafficRevisionName: "workload--000001",
            WorkloadIdentityResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/workload",
            WorkloadIdentityClientId: "11111111-1111-1111-1111-111111111111",
            WorkloadIdentityPrincipalId: "22222222-2222-2222-2222-222222222222",
            KeyVaultResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/workload",
            KeyVaultUri: "https://workload.vault.azure.net/",
            SqlServerResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Sql/servers/workload",
            SqlServerFqdn: "workload.database.windows.net",
            ContainerAppsEnvironmentResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.App/managedEnvironments/workload",
            RegistryResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.ContainerRegistry/registries/workload",
            AcrPullDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.Resources/deployments/acr-pull",
            AcrPullRoleAssignmentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg/providers/Microsoft.ContainerRegistry/registries/workload/providers/Microsoft.Authorization/roleAssignments/33333333-3333-3333-3333-333333333333");

        var saved = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, operation.Id, "lease",
            new(AzureProviderOperationPhase.WorkloadReady, "workload.ready", "Ready.", resources, null,
                AzureProviderHealth.Healthy, []), now, claimed.Version));

        Assert.Equal(resources, saved.Resources);
        var reloaded = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operation.Id));
        Assert.Equal(resources, reloaded.Resources);
    }

    [Fact]
    public async Task Legacy_rows_with_null_provider_resource_columns_remain_readable()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE AzureProviderOperations
            SET WorkloadIdentityResourceId = NULL,
                WorkloadIdentityClientId = NULL,
                WorkloadIdentityPrincipalId = NULL,
                KeyVaultResourceId = NULL,
                KeyVaultUri = NULL,
                SqlServerResourceId = NULL,
                SqlServerFqdn = NULL,
                ContainerAppsEnvironmentResourceId = NULL,
                RegistryResourceId = NULL,
                AcrPullDeploymentId = NULL,
                AcrPullRoleAssignmentId = NULL
            WHERE Id = {operation.Id}
            """);

        var reloaded = Assert.IsType<AzureProviderOperation>(await store.GetAsync(_workspaceId, operation.Id));
        Assert.Null(reloaded.Resources.WorkloadIdentityResourceId);
        Assert.Null(reloaded.Resources.WorkloadIdentityClientId);
        Assert.Null(reloaded.Resources.WorkloadIdentityPrincipalId);
        Assert.Null(reloaded.Resources.KeyVaultResourceId);
        Assert.Null(reloaded.Resources.KeyVaultUri);
        Assert.Null(reloaded.Resources.SqlServerResourceId);
        Assert.Null(reloaded.Resources.SqlServerFqdn);
        Assert.Null(reloaded.Resources.ContainerAppsEnvironmentResourceId);
        Assert.Null(reloaded.Resources.RegistryResourceId);
        Assert.Null(reloaded.Resources.AcrPullDeploymentId);
        Assert.Null(reloaded.Resources.AcrPullRoleAssignmentId);
    }

    [Fact]
    public async Task Reference_only_checkpoint_preserves_existing_endpoint_and_health()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now));
        var observed = Assert.IsType<AzureProviderOperation>(await store.CheckpointAsync(
            _workspaceId, operation.Id, "lease",
            new(AzureProviderOperationPhase.HealthVerified, "health.verified", "Verified.", new(),
                " HTTPS://Workload.Example.Test:443/ ", AzureProviderHealth.Healthy, []),
            now, claimed.Version));

        var preserved = await store.CheckpointAsync(
            _workspaceId, operation.Id, "lease",
            new(AzureProviderOperationPhase.TrafficPromoted, "traffic.restored", "Restored.", new(),
                null, AzureProviderHealth.Unknown, []),
            now.AddSeconds(1), observed.Version);

        Assert.Equal("https://workload.example.test", preserved?.Endpoint);
        Assert.Equal(AzureProviderHealth.Healthy, preserved?.Health);
    }

    [Theory]
    [InlineData("DiagnosticsJson", "{")]
    [InlineData("DiagnosticsJson", "null")]
    [InlineData("DiagnosticsJson", "[{\"Code\":\"azure.step\",\"Message\":\"password=top-secret\"}]")]
    [InlineData("DiagnosticsJson", "[{\"Code\":\"azure.step\",\"Message\":\"line1\\nline2\"}]")]
    [InlineData("SecretReferencesJson", "{")]
    [InlineData("SecretReferencesJson", "null")]
    [InlineData("SecretReferencesJson", "{\"database\":null}")]
    [InlineData("SecretReferencesJson", "{\"database\":\"secret://vault/database?token=unsafe\"}")]
    public async Task Malformed_persisted_metadata_is_marked_unrestorable_without_escaping(string column, string json)
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request() with
        {
            ReleaseManifestDigest = "sha256:" + new string('d', 64),
            ReleaseManifestSignatureDigest = "sha256:" + new string('e', 64),
            ReleaseManifestReference = "oci://release-manifest.example/manifest",
            ReleaseManifestSignatureReference = "oci://release-manifest.example/signature"
        }, now);
        if (column == "DiagnosticsJson")
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET DiagnosticsJson = {json} WHERE Id = {operation.Id}");
        else
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AzureProviderOperations SET SecretReferencesJson = {json} WHERE Id = {operation.Id}");

        var runnable = Assert.Single(await store.ListRunnableAsync(now, 10));
        Assert.True(runnable.PersistedMetadataInvalid);
        Assert.Empty(runnable.Diagnostics);
        var mutableReferences = Assert.IsAssignableFrom<IDictionary<string, string>>(runnable.SafeSecretReferences);
        Assert.Throws<NotSupportedException>(() => mutableReferences.Add("database", "secret://vault/database"));
        Assert.Null(new PersistedAzureProviderPlanSource().Resolve(runnable));

        var worker = new AzureProviderOperationWorker(
            store,
            new AzureProviderExecutor(store, new NeverCalledRunner()),
            new PersistedAzureProviderPlanSource(),
            new FixedTimeProvider(now));
        Assert.Equal(0, await worker.ProcessOnceAsync());

        var failed = await store.GetAsync(_workspaceId, operation.Id);
        Assert.Equal(AzureProviderOperationStatus.Failed, failed?.Status);
        Assert.NotNull(failed);
        Assert.Empty(failed!.Diagnostics);
        var transition = Assert.Single(await store.ListTransitionsAsync(_workspaceId, operation.Id), x => x.Code == "azure.plan.unrestorable");
        Assert.Equal("The persisted provider plan cannot be restored.", transition.Message);
    }

    [Fact]
    public async Task Concurrent_claim_has_one_winner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"elsa-azure-operation-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite($"Data Source={path}").Options;
            await using (var seed = new CatalogDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Workspaces.Add(new Workspace { Id = _workspaceId, Name = "Azure operation workspace" });
                await seed.SaveChangesAsync();
                var operation = await new AzureProviderOperationStore(seed).CreateOrGetAsync(Request(), DateTimeOffset.UtcNow);
                await using var first = new CatalogDbContext(options);
                await using var second = new CatalogDbContext(options);
                var firstClaim = new AzureProviderOperationStore(first).ClaimAsync(_workspaceId, operation.Id, "worker-a", "lease-a", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
                var secondClaim = new AzureProviderOperationStore(second).ClaimAsync(_workspaceId, operation.Id, "worker-b", "lease-b", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
                var claims = await Task.WhenAll(firstClaim, secondClaim);
                Assert.Equal(1, claims.Count(x => x is not null));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Claim_replay_requires_the_same_unexpired_lease()
    {
        var now = DateTimeOffset.UtcNow;
        using var db = CreateContext();
        var store = new AzureProviderOperationStore(db);
        var operation = await store.CreateOrGetAsync(Request(), now);
        var claimed = Assert.IsType<AzureProviderOperation>(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now, operation.Version));

        var replay = await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now.AddSeconds(30), operation.Version);

        Assert.Equal(claimed.Version, replay?.Version);
        Assert.Null(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "lease", TimeSpan.FromMinutes(1), now.AddMinutes(2), operation.Version));
        Assert.Null(await store.ClaimAsync(
            _workspaceId, operation.Id, "worker", "different-lease", TimeSpan.FromMinutes(1), now.AddSeconds(30), operation.Version));
        Assert.Equal(2, (await store.ListTransitionsAsync(_workspaceId, operation.Id)).Count);
    }

    [Fact]
    public async Task Stale_recovery_wins_against_heartbeat_checkpoint_and_finalize_versions()
    {
        using var firstDb = CreateContext();
        using var secondDb = CreateContext();
        var first = new AzureProviderOperationStore(firstDb);
        var second = new AzureProviderOperationStore(secondDb);
        foreach (var suffix in new[] { "heartbeat", "checkpoint", "finalize" })
        {
            var operation = await first.CreateOrGetAsync(Request() with { TargetKey = "workload-" + suffix, IdempotencyKey = suffix }, DateTimeOffset.UtcNow);
            var claimed = await first.ClaimAsync(_workspaceId, operation.Id, "worker-a", "lease-" + suffix, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow);
            Assert.NotNull(claimed);
            Assert.Equal(1, await second.RecoverStaleAsync(DateTimeOffset.UtcNow.AddMinutes(2)));
            if (suffix == "heartbeat")
                Assert.Null(await first.HeartbeatAsync(_workspaceId, operation.Id, "lease-heartbeat", TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow.AddMinutes(2), claimed!.Version));
            else if (suffix == "checkpoint")
                Assert.Null(await first.CheckpointAsync(_workspaceId, operation.Id, "lease-checkpoint", new(AzureProviderOperationPhase.FoundationReady, "checkpoint.ready", "Ready.", new(), null, AzureProviderHealth.Unknown, []), DateTimeOffset.UtcNow.AddMinutes(2), claimed!.Version));
            else
                Assert.Null(await first.FinalizeAsync(_workspaceId, operation.Id, "lease-finalize", AzureProviderOperationStatus.Succeeded, "operation.succeeded", DateTimeOffset.UtcNow.AddMinutes(2), claimed!.Version));
        }
    }

    private CatalogDbContext CreateContext() => new(new DbContextOptionsBuilder<CatalogDbContext>().UseRetryingSqlite(_connection).Options);

    private async Task<AzureProviderResourceAssignment> Assignment(
        AzureProviderOperationStore store,
        Guid organizationId,
        Guid instanceId,
        DateTimeOffset now) =>
        await ((IAzureProviderResourceAssignmentStore)store).CreateOrGetAsync(
            new(
                _workspaceId,
                organizationId,
                instanceId,
                new string('a', 64),
                "11111111-1111-1111-1111-111111111111",
                "rg-elsa",
                $"e{instanceId:N}"[..16],
                "westeurope"),
            now);

    private AzureProviderOperationRequest Request() => new(
        _workspaceId, "workload-a", AzureProviderOperationAction.Reconcile, "request-1",
        new('a', 64), new('b', 64), "3.8.0", "3.8", "combined", "Dedicated", "westeurope",
        "valenceruntimeimages.azurecr.io/runtime-combined", "sha256:" + new string('c', 64));

    private sealed class DenyingCommercialGate : IElsaInstanceCommercialGate
    {
        public Task<ElsaInstanceCommercialGateDecision> EvaluateAsync(
            Guid organizationId,
            ElsaInstanceOperationAction action,
            int? activeInstanceCount = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElsaInstanceCommercialGateDecision(
                false,
                ElsaInstanceCommercialOperation.LifecycleConstrained,
                "The organization subscription does not permit managed-instance changes."));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class NeverCalledRunner : IAzureProviderRunner
    {
        public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default) =>
            throw new Xunit.Sdk.XunitException("The provider runner must not be called for malformed persisted metadata.");
    }

    private sealed class RecoveringCompletingRunner : IAzureProviderRunner
    {
        public List<AzureProviderRunnerCommand> Commands { get; } = [];

        public Task<AzureProviderRunnerResult> RunAsync(AzureProviderRunnerCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var resources = CompleteResources();
            if (Commands.Count == 1)
                return Task.FromResult(new AzureProviderRunnerResult(
                    AzureProviderRunnerOutcome.Uncertain,
                    AzureProviderOperationPhase.Planned,
                    resources,
                    AzureProviderHealth.Unknown,
                    null,
                    [],
                    "azure.step.uncertain",
                    "The provider result is uncertain."));

            var observed = command.Step is AzureProviderRunnerStep.Health or AzureProviderRunnerStep.Promotion;
            var phase = command.Step switch
            {
                AzureProviderRunnerStep.Foundation => AzureProviderOperationPhase.FoundationSubmitted,
                AzureProviderRunnerStep.AcrPull or AzureProviderRunnerStep.SeedSecrets => AzureProviderOperationPhase.FoundationSubmitted,
                AzureProviderRunnerStep.SqlBootstrap => AzureProviderOperationPhase.FoundationReady,
                AzureProviderRunnerStep.Workload => AzureProviderOperationPhase.WorkloadReady,
                AzureProviderRunnerStep.Health => AzureProviderOperationPhase.HealthVerified,
                AzureProviderRunnerStep.Promotion => AzureProviderOperationPhase.TrafficPromoted,
                _ => AzureProviderOperationPhase.CleanupVerified
            };
            return Task.FromResult(new AzureProviderRunnerResult(
                AzureProviderRunnerOutcome.Completed,
                phase,
                resources,
                observed ? AzureProviderHealth.Healthy : AzureProviderHealth.Unknown,
                observed ? "https://workload.example.test" : null,
                [],
                "azure.step.completed",
                "The provider step completed."));
        }

        private static AzureProviderResourceReferences CompleteResources() => new(
            ResourceGroupName: "rg-safe",
            FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.Resources/deployments/foundation",
            WorkloadDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.Resources/deployments/workload",
            WorkloadResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.App/containerApps/workload-a",
            WorkloadRevisionName: "workload-a--candidate",
            StableTrafficRevisionName: "workload-a--stable",
            WorkloadIdentityResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.ManagedIdentity/userAssignedIdentities/workload-a",
            WorkloadIdentityClientId: "22222222-2222-2222-2222-222222222222",
            WorkloadIdentityPrincipalId: "33333333-3333-3333-3333-333333333333",
            KeyVaultResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.KeyVault/vaults/workload-a",
            KeyVaultUri: "https://workload-a.vault.azure.net/",
            SqlServerResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.Sql/servers/workload-a",
            SqlServerFqdn: "workload-a.database.windows.net",
            ContainerAppsEnvironmentResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-safe/providers/Microsoft.App/managedEnvironments/workload-a",
            RegistryResourceId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry/providers/Microsoft.ContainerRegistry/registries/workload",
            AcrPullDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry/providers/Microsoft.Resources/deployments/acr-pull",
            AcrPullRoleAssignmentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/registry/providers/Microsoft.ContainerRegistry/registries/workload/providers/Microsoft.Authorization/roleAssignments/44444444-4444-4444-4444-444444444444");
    }

    public void Dispose() => _connection.Dispose();
}
