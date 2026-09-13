using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ElsaInstanceLifecycleWorkerTests
{
    private static readonly Guid OrganizationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid WorkspaceId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

    [Fact]
    public void Provider_submission_requires_a_non_empty_organization_binding()
    {
        var submission = new ElsaInstanceProviderSubmission(
            WorkspaceId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            ElsaDesiredLifecycle.Running,
            null!,
            null!);

        Assert.Throws<InvalidOperationException>(() => submission.Validate());
    }

    [Fact]
    public async Task Resolves_accepted_work_and_commits_one_queued_run_atomically()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-prod", "create-1"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance) with
        { ExpectedPlanDigest = SuccessfulResolution(WorkspaceId, accepted.Instance.Id).Reference!.ContentHash });

        var worker = new ElsaInstanceLifecycleWorker(store, new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)), new StaticTimeProvider(Now));

        var result = await worker.ProcessAvailableAsync("lifecycle-worker-1");

        var operation = Assert.Single(store.Operations);
        var instance = Assert.Single(store.Instances);
        var run = Assert.Single(store.DeploymentRuns);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, Assert.Single(result.Results).Outcome);
        Assert.Equal(ElsaInstanceOperationState.Queued, operation.State);
        Assert.Equal(SuccessfulResolution(WorkspaceId, accepted.Instance.Id).Reference, instance.ResolvedPlanReference);
        Assert.NotNull(instance.CurrentResolvedRelease);
        Assert.Equal("5.0", instance.CurrentResolvedRelease!.ReleaseLine);
        Assert.Equal("5.0.0-preview.1", instance.CurrentResolvedRelease.Version);
        Assert.Equal(ElsaInstanceOperationState.Queued, run.Operation.State);
        Assert.Equal(accepted.Instance.Id, run.InstanceId);
        Assert.Single(store.ResolvedPlans);
        Assert.Equal(0, result.ProviderInvocations);
    }

    [Fact]
    public async Task Reviewed_plan_drift_fails_before_creating_a_run_or_submitting_to_a_provider()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var accepted = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now))
            .CreateAsync(CreateRequest("reviewed-engine", "reviewed-engine"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance) with
        { ExpectedPlanDigest = "sha256:" + new string('0', 64) });
        var provider = new RecordingSubmissionPort();
        var worker = new ElsaInstanceLifecycleWorker(store,
            new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
            new StaticTimeProvider(Now), provider, store);

        var result = await worker.ProcessAvailableAsync("review-worker");

        Assert.Equal(0, result.ProviderInvocations);
        Assert.Empty(provider.Submissions);
        Assert.Empty(store.DeploymentRuns);
        Assert.Equal("provisioning.plan-changed", Assert.Single(result.Results).FailureCode);
    }

    [Fact]
    public async Task Provider_submission_happens_after_reservation_and_leaves_one_reconciliation_target()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-provider", "create-provider"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var provider = new RecordingSubmissionPort();

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                provider,
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(1, result.ProviderInvocations);
        Assert.Equal(accepted.Operation.Id, Assert.Single(provider.Submissions).OperationId);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);
        Assert.Equal(WorkspaceDeploymentRunStatus.RecoveryRequired, Assert.Single(store.DeploymentRuns).Run.Status);
        var pending = await store.ListPendingProviderOperationsAsync(16);
        Assert.Equal(accepted.Operation.Id, Assert.Single(pending).OperationId);
        Assert.Null(pending.Single().Submission);
    }

    [Fact]
    public async Task Worker_time_commercial_denial_holds_the_run_without_provider_submission()
    {
        var store = new EntitlementHoldingLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-entitlement-held", "create-entitlement-held"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var provider = new RecordingSubmissionPort();

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                provider,
                store)
            .ProcessAvailableAsync("lifecycle-worker-entitlement");

        var held = Assert.Single(result.Results);
        Assert.Equal(ElsaInstanceOperationState.EntitlementHeld, held.Operation.State);
        Assert.Equal(ElsaInstanceCommercialOperation.LifecycleConstrained, held.FailureCode);
        Assert.Equal(0, result.ProviderInvocations);
        Assert.Empty(provider.Submissions);
        Assert.True(store.HoldPersisted);
        Assert.Equal(ElsaInstanceOperationState.EntitlementHeld, Assert.Single(store.PersistedOperations).State);
    }

    [Fact]
    public async Task Uncertain_provider_submission_is_recoverable_without_inserting_another_run()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-provider-uncertain", "create-provider-uncertain"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var provider = new RecordingSubmissionPort(throwOnSubmit: true);

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                provider,
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(1, result.ProviderInvocations);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, Assert.Single(store.Operations).State);
        Assert.Equal("provider.submission.uncertain", Assert.Single(store.DeploymentRuns).Run.RecoveryReason);
        Assert.Single(store.DeploymentRuns);
        Assert.NotNull((await store.ListPendingProviderOperationsAsync(16)).Single().Submission);
    }

    [Fact]
    public async Task Deterministic_provider_rejection_does_not_create_an_uncertain_handoff()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-provider-rejected", "create-provider-rejected"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var provider = new RecordingSubmissionPort(
            throwOnSubmit: true,
            failureKind: ElsaInstanceProviderSubmissionFailureKind.Rejected);

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                provider,
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(1, result.ProviderInvocations);
        Assert.Equal(ElsaInstanceOperationState.Queued, Assert.Single(store.Operations).State);
        Assert.Equal(WorkspaceDeploymentRunStatus.Queued, Assert.Single(store.DeploymentRuns).Run.Status);
        Assert.NotNull((await store.ListPendingProviderOperationsAsync(16)).Single().Submission);
    }

    [Fact]
    public async Task Unknown_reconciliation_preserves_uncertain_submission_for_a_later_replay()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-provider-uncertain-reconcile", "create-provider-uncertain-reconcile"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));

        await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                new RecordingSubmissionPort(throwOnSubmit: true),
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        var before = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.NotNull(before.Submission);

        var result = await new ElsaInstanceProviderReconciliationService(
                store,
                new UnknownReconciliationPort(),
                new StaticTimeProvider(Now.AddMinutes(1)))
            .ReconcileAsync(WorkspaceId, accepted.Operation.Id);

        Assert.Equal(ElsaInstanceProviderReconciliationOutcome.RecoveryRequired, result.Outcome);
        Assert.False(result.RetrySafe);
        var after = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.NotNull(after.Submission);
        Assert.Equal("provider.submission.uncertain", Assert.Single(store.DeploymentRuns).Run.RecoveryReason);
    }

    [Fact]
    public async Task Successful_replay_upgrades_uncertain_handoff_and_stops_future_submission_replays()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-provider-replay", "create-provider-replay"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));

        var provider = new RecordingSubmissionPort(throwOnSubmit: true);
        await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                provider,
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        var pendingBeforeReplay = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.NotNull(pendingBeforeReplay.Submission);

        await store.CommitProviderSubmissionAsync(new(
            WorkspaceId,
            accepted.Instance.Id,
            accepted.Operation.Id,
            accepted.Operation.AttemptNumber,
            "provider-operation-replayed",
            Now.AddSeconds(1)));

        var pendingAfterReplay = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.Null(pendingAfterReplay.Submission);
        Assert.Equal("provider.submission.accepted", Assert.Single(store.DeploymentRuns).Run.RecoveryReason);
    }

    [Fact]
    public async Task Accepted_provider_submission_persists_the_opaque_placement_assignment()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-provider-assignment", "create-provider-assignment"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var assignmentId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee").ToString("D");

        await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                new RecordingSubmissionPort(placementAssignmentId: assignmentId),
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        var instance = Assert.IsType<ElsaInstance>(await store.GetInstanceAsync(WorkspaceId, accepted.Instance.Id));
        Assert.Equal(assignmentId, instance.PlacementAssignmentReference?.AssignmentId);
    }

    [Fact]
    public async Task Invalid_deleting_provider_submission_is_not_reconstructed_for_replay()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var instance = ElsaInstance.Hydrate(
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            OrganizationId,
            WorkspaceId,
            "claims-provider-deleting",
            "claims-provider-deleting",
            Intent() with { DesiredLifecycle = ElsaDesiredLifecycle.Deleting },
            ElsaObservedLifecycle.Unknown,
            ElsaInstanceHealth.Unknown,
            1);
        var operation = ElsaInstanceOperation.Create(
            instance.Id,
            ElsaInstanceOperationAction.Create,
            ElsaInstanceLifecycleService.CreateIdempotencyScope,
            "create-provider-deleting",
            instance.ComputeCanonicalIntentHash(),
            instance.Version,
            acceptedAt: Now);
        var outbox = new ElsaInstanceLifecycleOutboxMessage(
            Guid.NewGuid(), WorkspaceId, instance.Id, operation.Id,
            ElsaInstanceOperationAction.Create, operation.RequestHash, Now);
        var accepted = await store.CommitAcceptedAsync(null, instance, operation, outbox);
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));

        await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)),
                new StaticTimeProvider(Now),
                new RecordingSubmissionPort(),
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        var pending = Assert.Single(await store.ListPendingProviderOperationsAsync(16));
        Assert.Null(pending.Submission);
    }

    [Fact]
    public async Task In_memory_finalization_uses_store_clock_for_lease_expiry()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now.AddMinutes(6)));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-clock", "create-clock"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var item = await store.TryClaimNextAsync("lifecycle-worker-1", Now)
            ?? throw new InvalidOperationException("Expected a claimed work item.");
        var resolution = SuccessfulResolution(WorkspaceId, accepted.Instance.Id);
        var resolvedInstance = item.Instance.AttachResolvedPlan(resolution.Reference!, resolution.CurrentResolvedRelease!);
        var commit = new ElsaInstanceLifecycleResolutionCommit(
            item.Outbox.WorkspaceId,
            item.Outbox.InstanceId,
            item.Operation.Id,
            item.Outbox.Id,
            item.Outbox.RequestHash,
            "lifecycle-worker-1",
            item.Operation.TransitionTo(ElsaInstanceOperationState.Queued),
            resolvedInstance,
            new ElsaInstanceLifecycleResolvedPlan(
                resolution.Reference!,
                ResolvedElsaApplicationPlanSerialization.Serialize(resolution.Plan!)),
            item.Resolution.DeploymentTarget,
            Now.AddMinutes(1),
            item.LeaseToken,
            item.LeaseVersion);

        var error = await Assert.ThrowsAsync<ElsaInstanceLifecycleConflictException>(
            () => store.CommitResolvedAsync(commit));

        Assert.Equal("Lifecycle work item is no longer owned by this worker.", error.Message);
        Assert.Equal(ElsaInstanceOperationState.Accepted, Assert.Single(store.Operations).State);
        Assert.Empty(store.DeploymentRuns);
    }

    [Fact]
    public async Task Resolution_failure_fails_operation_without_creating_a_run()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-failed", "create-failed"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var failure = ElsaInstancePlanResolutionResult.Failed(
            [new("error", "catalog.unavailable", "Required catalog metadata could not be loaded.", "catalog")]);

        var result = await new ElsaInstanceLifecycleWorker(store, new RecordingResolver(failure), new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, Assert.Single(result.Results).Outcome);
        Assert.Equal(ElsaInstanceOperationState.Failed, Assert.Single(store.Operations).State);
        Assert.Empty(store.DeploymentRuns);
        var recordedFailure = Assert.Single(store.Failures);
        Assert.Equal("catalog.unavailable", recordedFailure.Code);
        Assert.Equal("Lifecycle plan resolution was rejected.", recordedFailure.Summary);
    }

    [Fact]
    public async Task Missing_resolution_input_fails_with_an_actionable_code()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        await service.CreateAsync(CreateRequest("claims-missing-input", "create-missing-input"));

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, Guid.NewGuid())),
                new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, Assert.Single(result.Results).Outcome);
        Assert.Equal(ElsaInstanceOperationState.Failed, Assert.Single(store.Operations).State);
        Assert.Empty(store.DeploymentRuns);
        var recordedFailure = Assert.Single(store.Failures);
        Assert.Equal("resolution.input-unavailable", recordedFailure.Code);
        Assert.Equal(
            "Lifecycle resolution input could not be reconstructed from the catalog projection.",
            recordedFailure.Summary);
    }

    [Fact]
    public async Task Resolver_exceptions_fail_with_an_actionable_code()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-resolver-throw", "create-resolver-throw"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new ThrowingResolver(),
                new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, Assert.Single(result.Results).Outcome);
        Assert.Equal("resolution.unexpected", Assert.Single(store.Failures).Code);
        Assert.Empty(store.DeploymentRuns);
    }

    [Fact]
    public async Task Unsafe_resolved_plan_fails_before_run_reservation_or_provider_submission()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-unsafe", "create-unsafe"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var baseline = SuccessfulResolution(WorkspaceId, accepted.Instance.Id);
        var unsafePlan = baseline.Plan! with
        {
            Packages =
            [
                new(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    "Customer.SecretPackage",
                    "1.0.0",
                    "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                    ["elsa.server"],
                    [],
                    ResolvedExtensionClass.ArbitraryCustomer,
                    "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
            ]
        };
        var reference = new ElsaResolvedPlanReference(
            baseline.Reference!.PlanId,
            baseline.Reference.SchemaVersion,
            ResolvedElsaApplicationPlanSerialization.ComputeContentHash(unsafePlan),
            baseline.Reference.PlanUri);
        var current = baseline.CurrentResolvedRelease!;
        var unsafeResolution = baseline with
        {
            Plan = unsafePlan,
            Reference = reference,
            CurrentResolvedRelease = new(
                reference,
                current.DistributionId,
                current.ReleaseLine,
                current.Version,
                current.ManifestDigest,
                current.ComponentDigests)
        };
        var provider = new RecordingSubmissionPort();

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(unsafeResolution),
                new StaticTimeProvider(Now),
                provider,
                store)
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, Assert.Single(result.Results).Outcome);
        Assert.Equal(ElsaInstanceOperationState.Failed, Assert.Single(store.Operations).State);
        Assert.Empty(store.ResolvedPlans);
        Assert.Empty(store.DeploymentRuns);
        Assert.Empty(provider.Submissions);
        Assert.Equal("resolution.commit-invalid", Assert.Single(store.Failures).Code);
    }

    [Fact]
    public async Task A_bad_item_does_not_stop_the_worker_from_continuing_the_queue()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var first = await service.CreateAsync(CreateRequest("claims-bad", "create-bad"));
        var second = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now.AddSeconds(1)))
            .CreateAsync(CreateRequest("claims-good", "create-good"));
        store.RegisterResolutionInput(first.Operation.Id, ResolutionInput(first.Instance));
        store.RegisterResolutionInput(second.Operation.Id, ResolutionInput(second.Instance));
        var resolver = new QueueResolver(
            ElsaInstancePlanResolutionResult.Failed(
                [new("error", "plan.invalid", "Resolved application plan values are invalid.", "plan")]),
            SuccessfulResolution(WorkspaceId, second.Instance.Id));

        var result = await new ElsaInstanceLifecycleWorker(store, resolver, new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(1, result.Results.Count(x => x.Outcome == ElsaInstanceLifecycleWorkerOutcome.Failed));
        Assert.Equal(1, result.Results.Count(x => x.Outcome == ElsaInstanceLifecycleWorkerOutcome.Queued));
        Assert.Equal(ElsaInstanceOperationState.Failed, store.Operations.Single(x => x.Id == first.Operation.Id).State);
        Assert.Equal(ElsaInstanceOperationState.Queued, store.Operations.Single(x => x.Id == second.Operation.Id).State);
        Assert.Single(store.DeploymentRuns);
        Assert.Equal(2, resolver.Calls);
    }

    [Fact]
    public async Task A_malformed_item_is_failed_and_the_next_item_is_still_processed()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var first = await service.CreateAsync(CreateRequest("claims-malformed", "create-malformed"));
        var second = await service.CreateAsync(CreateRequest("claims-after-malformed", "create-after-malformed"));
        var malformed = ResolutionInput(first.Instance);
        store.RegisterResolutionInput(first.Operation.Id, malformed with
        {
            PlanRequest = malformed.PlanRequest with { WorkspaceId = Guid.NewGuid() }
        });
        store.RegisterResolutionInput(second.Operation.Id, ResolutionInput(second.Instance));

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new RecordingResolver(SuccessfulResolution(WorkspaceId, second.Instance.Id)),
                new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(2, result.Results.Count);
        var malformedResult = result.Results.Single(x => x.Operation.Id == first.Operation.Id);
        var nextResult = result.Results.Single(x => x.Operation.Id == second.Operation.Id);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Failed, malformedResult.Outcome);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, nextResult.Outcome);
        Assert.Equal("resolution.work-item-invalid", malformedResult.FailureCode);
        Assert.Equal(ElsaInstanceOperationState.Failed, store.Operations.Single(x => x.Id == first.Operation.Id).State);
        Assert.Equal(ElsaInstanceOperationState.Queued, store.Operations.Single(x => x.Id == second.Operation.Id).State);
        Assert.Single(store.DeploymentRuns);
    }

    [Fact]
    public async Task A_commit_lease_conflict_does_not_stop_the_worker_from_continuing_the_queue()
    {
        var (inner, first, second) = await CreateTwoAcceptedAsync("commit-race");
        var store = new ConflictOnceWorkerStore(inner, conflictOnCommit: true);

        var result = await new ElsaInstanceLifecycleWorker(
                store,
                new InstanceResolver(new Dictionary<Guid, ElsaInstancePlanResolutionResult>
                {
                    [first.Instance.Id] = SuccessfulResolution(WorkspaceId, first.Instance.Id),
                    [second.Instance.Id] = SuccessfulResolution(WorkspaceId, second.Instance.Id)
                }),
                new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(2, result.Results.Count);
        Assert.Equal("lifecycle.claim.conflict", result.Results.Single(x => x.Operation.Id == first.Operation.Id).FailureCode);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, result.Results.Single(x => x.Operation.Id == second.Operation.Id).Outcome);
        Assert.Single(inner.DeploymentRuns);
    }

    [Fact]
    public async Task A_failure_recording_lease_conflict_does_not_stop_the_worker_from_continuing_the_queue()
    {
        var (inner, first, second) = await CreateTwoAcceptedAsync("failure-race");
        var store = new ConflictOnceWorkerStore(inner, conflictOnFailure: true);
        var resolver = new InstanceResolver(new Dictionary<Guid, ElsaInstancePlanResolutionResult>
        {
            [first.Instance.Id] = ElsaInstancePlanResolutionResult.Failed(
                [new("error", "plan.invalid", "Resolved application plan values are invalid.", "plan")]),
            [second.Instance.Id] = SuccessfulResolution(WorkspaceId, second.Instance.Id)
        });

        var result = await new ElsaInstanceLifecycleWorker(store, resolver, new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(2, result.Results.Count);
        Assert.Equal("lifecycle.claim.conflict", result.Results.Single(x => x.Operation.Id == first.Operation.Id).FailureCode);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, result.Results.Single(x => x.Operation.Id == second.Operation.Id).Outcome);
        Assert.Single(inner.DeploymentRuns);
    }

    [Fact]
    public async Task Retrying_a_completed_item_is_idempotent_and_does_not_insert_another_run()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var accepted = await service.CreateAsync(CreateRequest("claims-retry", "create-retry"));
        store.RegisterResolutionInput(accepted.Operation.Id, ResolutionInput(accepted.Instance));
        var worker = new ElsaInstanceLifecycleWorker(store, new RecordingResolver(SuccessfulResolution(WorkspaceId, accepted.Instance.Id)), new StaticTimeProvider(Now));

        var first = await worker.ProcessAvailableAsync("lifecycle-worker-1");
        var second = await worker.ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Queued, Assert.Single(first.Results).Outcome);
        Assert.Empty(second.Results);
        Assert.Single(store.DeploymentRuns);
        Assert.Single(store.ResolvedPlans);
    }

    [Fact]
    public async Task Active_environment_reservation_conflict_fails_the_losing_operation_without_a_second_run()
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var first = await service.CreateAsync(CreateRequest("claims-reservation-1", "create-reservation-1"));
        var second = await service.CreateAsync(CreateRequest("claims-reservation-2", "create-reservation-2"));
        var environmentId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        store.RegisterResolutionInput(first.Operation.Id, ResolutionInput(first.Instance, environmentId));
        store.RegisterResolutionInput(second.Operation.Id, ResolutionInput(second.Instance, environmentId));

        var result = await new ElsaInstanceLifecycleWorker(store, new InstanceResolver(
                new Dictionary<Guid, ElsaInstancePlanResolutionResult>
                {
                    [first.Instance.Id] = SuccessfulResolution(WorkspaceId, first.Instance.Id),
                    [second.Instance.Id] = SuccessfulResolution(WorkspaceId, second.Instance.Id)
                }),
                new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        var losing = result.Results.Single(x => x.Outcome == ElsaInstanceLifecycleWorkerOutcome.Conflict);
        Assert.Equal(ElsaInstanceLifecycleWorkerOutcome.Conflict, losing.Outcome);
        Assert.Equal("run.reservation.conflict", losing.FailureCode);
        Assert.Equal(ElsaInstanceOperationState.Failed, losing.Operation.State);
        Assert.Single(result.Results, x => x.Outcome == ElsaInstanceLifecycleWorkerOutcome.Queued);
        Assert.Single(store.DeploymentRuns);
    }

    [Fact]
    public async Task Waiting_delete_is_not_claimed_or_resolved()
    {
        var authority = new InMemoryElsaInstanceDeleteConfirmationAuthority();
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now), authority);
        var service = new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now));
        var created = await service.CreateAsync(CreateRequest("claims-delete", "create-delete"));
        store.RegisterResolutionInput(created.Operation.Id, ResolutionInput(created.Instance));
        await new ElsaInstanceLifecycleWorker(store, new RecordingResolver(SuccessfulResolution(WorkspaceId, created.Instance.Id)), new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");
        var confirmationId = Guid.NewGuid();
        var actorAccountId = Guid.NewGuid();
        authority.Add(new ActionConfirmation(
            confirmationId,
            WorkspaceId,
            ConfirmationActionType.DeleteManagedInstance,
            created.Instance.Id.ToString("D"),
            actorAccountId,
            Now,
            Now.AddMinutes(5),
            null));
        var deletion = await service.DeleteAsync(new ElsaInstanceLifecycleRequest(
            WorkspaceId, created.Instance.Id, created.Instance.Version, "delete-1",
            DeleteConfirmationId: confirmationId, ActorAccountId: actorAccountId));
        store.RegisterResolutionInput(deletion.Operation.Id, ResolutionInput(deletion.Instance));

        var result = await new ElsaInstanceLifecycleWorker(store, new RecordingResolver(SuccessfulResolution(WorkspaceId, created.Instance.Id)), new StaticTimeProvider(Now))
            .ProcessAvailableAsync("lifecycle-worker-1");

        Assert.Empty(result.Results);
        Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation,
            store.Operations.Single(x => x.Id == deletion.Operation.Id).State);
        Assert.Single(store.DeploymentRuns);
    }

    private static ElsaInstanceCreateRequest CreateRequest(string slug, string key) =>
        new(OrganizationId, WorkspaceId, slug, slug, Intent(), key);

    private static async Task<(InMemoryElsaInstanceLifecycleStore Store,
        ElsaInstanceLifecycleAcceptance First,
        ElsaInstanceLifecycleAcceptance Second)> CreateTwoAcceptedAsync(string prefix)
    {
        var store = new InMemoryElsaInstanceLifecycleStore(new StaticTimeProvider(Now));
        var first = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now))
            .CreateAsync(CreateRequest($"{prefix}-first", $"{prefix}-first"));
        var second = await new ElsaInstanceLifecycleService(store, new StaticTimeProvider(Now.AddSeconds(1)))
            .CreateAsync(CreateRequest($"{prefix}-second", $"{prefix}-second"));
        store.RegisterResolutionInput(first.Operation.Id, ResolutionInput(first.Instance));
        store.RegisterResolutionInput(second.Operation.Id, ResolutionInput(second.Instance));
        return (store, first, second);
    }

    private static ElsaInstanceIntent Intent() => new(
        new("future-runtime", "5.0", "5.0.0-preview.1", "preview"),
        new("server-studio"),
        new("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static ElsaInstanceLifecycleResolutionInput ResolutionInput(ElsaInstance instance, Guid? environmentId = null) =>
        new(
            new ElsaInstancePlanResolutionRequest(
                instance.Intent,
                new(new("future-runtime", null, null, null), [], [], [], null),
                AdmittedManifest(),
                "plan_01J5WORKER",
                $"https://control.example.test/api/workspaces/{instance.WorkspaceId:D}/instances/{instance.Id:D}/resolved-plans/plan_01J5WORKER",
                instance.WorkspaceId),
            new(Guid.NewGuid(), environmentId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

    private static ReleaseManifestAdmissionResult AdmittedManifest()
    {
        var topology = new ReleaseManifestTopology(
            "server-studio",
            ["elsa.server"],
            [new("paid", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")],
            new Dictionary<string, string> { ["server"] = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
            new Dictionary<string, string>(),
            new("1", []),
            new(null, null, [], null));
        var manifest = new CommercialReleaseManifest(
            "1",
            new("future-runtime", "commercial", "5.0", "5.0.0-preview.1", "preview", "preview", new("https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "build", "1")),
            [topology]);
        return new(
            true,
            "https://example.test/manifests/5.0.0-preview.1.json",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            manifest,
            new("https://example.test/signatures/5.0.0-preview.1.sig", "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"),
            "paid",
            "server-studio",
            []);
    }

    private static ElsaInstancePlanResolutionResult SuccessfulResolution(Guid workspaceId, Guid instanceId)
    {
        var provisionalReference = new ElsaResolvedPlanReference(
            "plan_01J5WORKER",
            1,
            "sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            $"https://control.example.test/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/resolved-plans/plan_01J5WORKER");
        var plan = MinimalPlan(provisionalReference);
        var reference = new ElsaResolvedPlanReference(
            provisionalReference.PlanId,
            provisionalReference.SchemaVersion,
            ResolvedElsaApplicationPlanSerialization.ComputeContentHash(plan),
            provisionalReference.PlanUri);
        var current = new ElsaCurrentResolvedRelease(
            reference,
            "future-runtime",
            "5.0",
            "5.0.0-preview.1",
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            [new("server", "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]);
        return new(true, plan, reference, current, []);
    }

    private static ResolvedElsaApplicationPlan MinimalPlan(ElsaResolvedPlanReference reference) =>
        new(
            "1",
            new("future-runtime", "5.0", "5.0.0-preview.1", "https://example.test/repo", "0123456789abcdef0123456789abcdef01234567", "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"),
            new("server-studio", [new("server", ["server"], new("paid", "registry.example.test/elsa", "registry.example.test/elsa/server@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), ["elsa.server"], [], [])]),
            [],
            new([]),
            new([], []),
            new("public", "public", false, [], []),
            "dedicated",
            new("preview", "preview", "stable", "automatic-within-minor", "explicit-approval", "explicit-migration"),
            [],
            [new("release-manifest", "https://example.test/manifests/5.0.0-preview.1.json", "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc", "Verified release manifest.")]);

    private sealed class RecordingResolver(ElsaInstancePlanResolutionResult result) : IElsaInstancePlanResolver
    {
        public int Calls { get; private set; }

        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(ElsaInstancePlanResolutionRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingResolver : IElsaInstancePlanResolver
    {
        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(
            ElsaInstancePlanResolutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("resolver exploded");
    }

    private sealed class RecordingSubmissionPort(
        bool throwOnSubmit = false,
        ElsaInstanceProviderSubmissionFailureKind failureKind = ElsaInstanceProviderSubmissionFailureKind.OutcomeUnknown,
        string? placementAssignmentId = null)
        : IElsaInstanceProviderSubmissionPort
    {
        public List<ElsaInstanceProviderSubmission> Submissions { get; } = [];

        public Task<ElsaInstanceProviderSubmissionResult> SubmitAsync(
            ElsaInstanceProviderSubmission request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Validate();
            Submissions.Add(request);
            if (throwOnSubmit)
                throw new ElsaInstanceProviderSubmissionException(failureKind);
            return Task.FromResult(new ElsaInstanceProviderSubmissionResult(
                $"provider-operation-{request.OperationId:N}",
                Replayed: Submissions.Count > 1,
                placementAssignmentId));
        }
    }

    private sealed class EntitlementHoldingLifecycleStore(TimeProvider timeProvider)
        : IElsaInstanceLifecycleStore,
          IElsaInstanceLifecycleWorkerStore,
          IElsaInstanceProviderSubmissionStore,
          IElsaInstanceEntitlementHoldStore
    {
        private readonly InMemoryElsaInstanceLifecycleStore _inner = new(timeProvider);

        public IReadOnlyCollection<ElsaInstanceOperation> Operations => _inner.Operations;
        public IReadOnlyCollection<ElsaInstanceOperation> PersistedOperations => _persistedOperations.Values;
        public bool HoldPersisted { get; private set; }
        private readonly Dictionary<Guid, ElsaInstanceOperation> _persistedOperations = [];

        public void RegisterResolutionInput(Guid operationId, ElsaInstanceLifecycleResolutionInput input) =>
            _inner.RegisterResolutionInput(operationId, input);

        public Task<ElsaInstanceLifecycleAcceptance> CommitAcceptedAsync(
            ElsaInstance? existingInstance, ElsaInstance instance, ElsaInstanceOperation operation,
            ElsaInstanceLifecycleOutboxMessage outbox, CancellationToken cancellationToken = default) =>
            _inner.CommitAcceptedAsync(existingInstance, instance, operation, outbox, cancellationToken);

        public Task<ElsaInstance?> GetInstanceAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            _inner.GetInstanceAsync(workspaceId, instanceId, cancellationToken);

        public Task<ElsaInstanceOperation?> GetActiveOperationAsync(Guid workspaceId, Guid instanceId, CancellationToken cancellationToken = default) =>
            _inner.GetActiveOperationAsync(workspaceId, instanceId, cancellationToken);

        public Task<ElsaInstanceOperation?> FindOperationByKeyAsync(Guid workspaceId, string idempotencyKey, Guid? instanceId = null,
            ElsaInstanceOperationAction? action = null, string? idempotencyScope = null, CancellationToken cancellationToken = default) =>
            _inner.FindOperationByKeyAsync(workspaceId, idempotencyKey, instanceId, action, idempotencyScope, cancellationToken);

        public Task<ElsaInstanceLifecycleWorkItem?> TryClaimNextAsync(string workerId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            _inner.TryClaimNextAsync(workerId, now, cancellationToken);

        public Task<ElsaInstanceLifecycleWorkerResult> CommitResolvedAsync(ElsaInstanceLifecycleResolutionCommit commit, CancellationToken cancellationToken = default) =>
            _inner.CommitResolvedAsync(commit, cancellationToken);

        public Task<ElsaInstanceLifecycleWorkerResult> FailResolutionAsync(ElsaInstanceLifecycleResolutionFailure failure, CancellationToken cancellationToken = default) =>
            _inner.FailResolutionAsync(failure, cancellationToken);

        public Task<ElsaInstanceCommercialGateDecision> AuthorizeProviderSubmissionAsync(
            Guid workspaceId, Guid instanceId, Guid operationId, DateTimeOffset authorizedAt,
            CancellationToken cancellationToken = default)
        {
            HoldPersisted = true;
            var operation = Operations.Single(x => x.Id == operationId);
            _persistedOperations[operationId] = operation.TransitionTo(ElsaInstanceOperationState.EntitlementHeld);
            return Task.FromResult(new ElsaInstanceCommercialGateDecision(
                false,
                ElsaInstanceCommercialOperation.LifecycleConstrained,
                "The organization subscription does not permit managed-instance changes."));
        }

        public Task CommitProviderSubmissionAsync(ElsaInstanceProviderSubmissionCommit commit, CancellationToken cancellationToken = default) =>
            _inner.CommitProviderSubmissionAsync(commit, cancellationToken);

    }

    private sealed class UnknownReconciliationPort : IElsaInstanceProviderReconciliationPort
    {
        public Task<ElsaInstanceProviderObservation> ObserveAsync(
            ElsaInstanceProviderReconciliationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ElsaInstanceProviderObservation(
                ElsaInstanceProviderObservationKind.Unknown,
                ElsaObservedLifecycle.Unknown,
                ElsaInstanceProviderHealthGate.Unknown,
                request.OperationId,
                request.AttemptNumber,
                "provider-observation-unknown"));
    }

    private sealed class QueueResolver(params ElsaInstancePlanResolutionResult[] results) : IElsaInstancePlanResolver
    {
        private int _index;
        public int Calls => _index;

        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(ElsaInstancePlanResolutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(results[Math.Min(Interlocked.Increment(ref _index) - 1, results.Length - 1)]);
    }

    private sealed class InstanceResolver(IReadOnlyDictionary<Guid, ElsaInstancePlanResolutionResult> results) : IElsaInstancePlanResolver
    {
        public Task<ElsaInstancePlanResolutionResult> ResolveAsync(ElsaInstancePlanResolutionRequest request, CancellationToken cancellationToken = default)
        {
            var instanceId = request.PlanUri.Split('/', StringSplitOptions.RemoveEmptyEntries)[6];
            return Task.FromResult(results[Guid.Parse(instanceId)]);
        }
    }

    private sealed class ConflictOnceWorkerStore(
        IElsaInstanceLifecycleWorkerStore inner,
        bool conflictOnCommit = false,
        bool conflictOnFailure = false) : IElsaInstanceLifecycleWorkerStore
    {
        private int _commitConflictsRemaining = conflictOnCommit ? 1 : 0;
        private int _failureConflictsRemaining = conflictOnFailure ? 1 : 0;

        public Task<ElsaInstanceLifecycleWorkItem?> TryClaimNextAsync(
            string workerId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            inner.TryClaimNextAsync(workerId, now, cancellationToken);

        public Task<ElsaInstanceLifecycleWorkerResult> CommitResolvedAsync(
            ElsaInstanceLifecycleResolutionCommit commit,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _commitConflictsRemaining, 0) == 1)
                throw new ElsaInstanceLifecycleConflictException("Lease ownership changed.");
            return inner.CommitResolvedAsync(commit, cancellationToken);
        }

        public Task<ElsaInstanceLifecycleWorkerResult> FailResolutionAsync(
            ElsaInstanceLifecycleResolutionFailure failure,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failureConflictsRemaining, 0) == 1)
                throw new ElsaInstanceLifecycleConflictException("Lease ownership changed.");
            return inner.FailResolutionAsync(failure, cancellationToken);
        }
    }

    private sealed class StaticTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
