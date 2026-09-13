using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Abstractions.Tests;

public sealed class ElsaInstanceContractTests
{
    [Theory]
    [InlineData("3.8")]
    [InlineData("3.9")]
    [InlineData("3.10")]
    [InlineData("4.0")]
    [InlineData("4.1")]
    [InlineData("5.0")]
    [InlineData("future-line")]
    public void Release_lines_are_catalog_data(string releaseLine)
    {
        var intent = InstanceIntent(releaseLine, requestedVersion: releaseLine + ".7");

        Assert.Equal(releaseLine, intent.Release.ReleaseLine);
        Assert.Equal(releaseLine + ".7", intent.Release.RequestedVersion);
    }

    [Theory]
    [InlineData("3.8.0-preview.5413+build.1")]
    [InlineData("3.8.0-preview.5567-build.153")]
    [InlineData("future-release-line")]
    public void Catalog_values_support_future_prerelease_and_build_labels(string value)
    {
        Assert.Equal(value.ToLowerInvariant(), ElsaInstanceValue.Catalog(value, nameof(value)));
    }

    [Theory]
    [InlineData("not a catalog")]
    [InlineData("catalog/value")]
    [InlineData("catalog:value")]
    public void Catalog_values_reject_unsafe_or_unbounded_payloads(string value)
    {
        Assert.Throws<ArgumentException>(() => ElsaInstanceValue.Catalog(value, nameof(value)));
        Assert.Throws<ArgumentException>(() => ElsaInstanceValue.Catalog(new string('a', 129), nameof(value)));
    }

    [Theory]
    [InlineData("combined")]
    [InlineData("server-studio")]
    public void Application_intent_accepts_catalog_topologies(string topologyId)
    {
        var intent = InstanceIntent(application: new ElsaApplicationIntent(topologyId, "starter", [new("Beta", "true")], "approved"));

        Assert.Equal(topologyId, intent.Application.TopologyId);
        Assert.Equal(ElsaFeatureOverrideKind.Catalog, intent.Application.FeatureOverrides["beta"].Kind);
        Assert.Equal("true", intent.Application.FeatureOverrides["beta"].Value);
    }

    [Fact]
    public void Intent_hash_is_stable_for_equivalent_override_order()
    {
        var left = InstanceIntent(application: new ElsaApplicationIntent("combined", "starter", [new("zeta", "2"), new("alpha", "1")], "approved"));
        var right = InstanceIntent(application: new ElsaApplicationIntent("combined", "starter", [new("alpha", "1"), new("zeta", "2")], "approved"));

        Assert.Equal(left.ComputeCanonicalHash(), right.ComputeCanonicalHash());
        Assert.Equal(left.ComputeCanonicalJson(), right.ComputeCanonicalJson());
    }

    [Fact]
    public void Intent_hash_changes_when_placement_or_lifecycle_intent_changes()
    {
        var intent = InstanceIntent();

        var changedPlacement = intent with
        {
            Placement = intent.Placement with { RegionCode = "northeurope" }
        };
        var stopped = intent with
        {
            DesiredLifecycle = ElsaDesiredLifecycle.Stopped
        };

        Assert.NotEqual(intent.ComputeCanonicalHash(), changedPlacement.ComputeCanonicalHash());
        Assert.NotEqual(intent.ComputeCanonicalHash(), stopped.ComputeCanonicalHash());
    }

    [Fact]
    public void Null_preview_consent_preserves_the_legacy_canonical_release_bytes()
    {
        var intent = InstanceIntent(releaseLine: "3.8", requestedVersion: "3.8.7");

        const string legacyJson = "{\"application\":{\"configurationShapeRevisionId\":null,\"featureOverrides\":{},\"featurePresetId\":\"starter\",\"packagePolicy\":\"valence-approved\",\"topologyId\":\"combined\"},\"desiredLifecycle\":\"Running\",\"placement\":{\"capacityProfile\":\"standard-small\",\"domainOutcome\":\"managed\",\"isolationProfile\":\"dedicated\",\"networkOutcome\":\"public\",\"regionCode\":\"westeurope\",\"targetMode\":\"managed\"},\"release\":{\"channel\":\"stable\",\"distributionId\":\"valence-runtime\",\"majorMigrations\":\"explicit-migration\",\"minorUpdates\":\"explicit-approval\",\"patchUpdates\":\"automatic-within-minor\",\"releaseLine\":\"3.8\",\"requestedVersion\":\"3.8.7\"}}";
        Assert.Equal(legacyJson, intent.ComputeCanonicalJson());
        Assert.Equal("fa0972faa780088c54b4774d529f76ab213e625dc2a67f3c35548f1a889804f2", intent.ComputeCanonicalHash());
        Assert.DoesNotContain("previewManifestDigest", intent.ComputeCanonicalJson(), StringComparison.Ordinal);
        Assert.Equal(intent.ComputeCanonicalHash(), (intent with
        {
            Release = new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.7")
        }).ComputeCanonicalHash());
    }

    [Fact]
    public void Preview_consent_is_strict_and_participates_in_the_intent_revision_hash()
    {
        const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var withoutConsent = InstanceIntent(releaseLine: "3.8", requestedVersion: "3.8.7");
        var withConsent = withoutConsent with
        {
            Release = new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.7", previewManifestDigest: digest)
        };

        Assert.Equal(digest, withConsent.Release.PreviewManifestDigest);
        Assert.Contains("\"previewManifestDigest\":\"" + digest + "\"", withConsent.ComputeCanonicalJson(), StringComparison.Ordinal);
        Assert.NotEqual(withoutConsent.ComputeCanonicalHash(), withConsent.ComputeCanonicalHash());
        Assert.Throws<ArgumentException>(() => new ElsaReleaseIntent("valence-runtime", "3.8", previewManifestDigest: digest));
        Assert.Throws<ArgumentException>(() => new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.7", previewManifestDigest: digest.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => new ElsaReleaseIntent("valence-runtime", "3.8", "3.8.7", previewManifestDigest: "sha256:short"));
    }

    [Fact]
    public void Feature_overrides_are_typed_and_canonical_hash_preserves_type()
    {
        var boolean = InstanceIntent(application: new ElsaApplicationIntent(
            "combined", featureOverrides: new Dictionary<string, ElsaFeatureOverride> { ["flag"] = ElsaFeatureOverride.FromBoolean(true) }));
        var catalog = InstanceIntent(application: new ElsaApplicationIntent(
            "combined", featureOverrides: new Dictionary<string, ElsaFeatureOverride> { ["flag"] = ElsaFeatureOverride.FromCatalog("true") }));
        var number = InstanceIntent(application: new ElsaApplicationIntent(
            "combined", featureOverrides: new Dictionary<string, ElsaFeatureOverride> { ["limit"] = ElsaFeatureOverride.FromNumber("1.0") }));
        var reordered = InstanceIntent(application: new ElsaApplicationIntent(
            "combined", featureOverrides: new Dictionary<string, ElsaFeatureOverride>
            {
                ["zeta"] = ElsaFeatureOverride.FromNumber(2),
                ["alpha"] = ElsaFeatureOverride.FromBoolean(false)
            }));
        var ordered = InstanceIntent(application: new ElsaApplicationIntent(
            "combined", featureOverrides: new Dictionary<string, ElsaFeatureOverride>
            {
                ["alpha"] = ElsaFeatureOverride.FromBoolean(false),
                ["zeta"] = ElsaFeatureOverride.FromNumber(2)
            }));

        Assert.NotEqual(boolean.ComputeCanonicalHash(), catalog.ComputeCanonicalHash());
        Assert.Equal(ElsaFeatureOverrideKind.Number, number.Application.FeatureOverrides["limit"].Kind);
        Assert.Equal(reordered.ComputeCanonicalHash(), ordered.ComputeCanonicalHash());
        Assert.Throws<ArgumentException>(() => ElsaFeatureOverride.FromCatalog("free text"));
        Assert.Throws<ArgumentException>(() => ElsaFeatureOverride.FromCatalog("secret=value"));
        Assert.Throws<ArgumentException>(() => ElsaFeatureOverride.FromNumber("NaN"));
    }

    [Fact]
    public void Identity_binding_uses_lowercase_canonical_guid_and_fixed_callback_path()
    {
        var id = Guid.Parse("550E8400-E29B-41D4-A716-446655440000");
        var binding = ElsaInstanceIdentityBinding.Create(id, "https://Customer.Example.test/");

        Assert.Equal("urn:elsa:instance:550e8400-e29b-41d4-a716-446655440000", binding.Audience);
        Assert.Equal("https://customer.example.test/managed-elsa/handoff/callback", binding.CanonicalCallbackUri);
        Assert.True(binding.Matches(id, binding.Audience, binding.CanonicalCallbackUri, binding.BindingVersion));
    }

    [Fact]
    public void Identity_binding_rotation_increments_version_and_invalidates_old_callback()
    {
        var id = Guid.NewGuid();
        var original = ElsaInstanceIdentityBinding.Create(id, "https://old.example.test");

        var rotated = original.Rotate("https://new.example.test");

        Assert.Equal(original.BindingVersion + 1, rotated.BindingVersion);
        Assert.Equal(original.Audience, rotated.Audience);
        Assert.False(rotated.Matches(id, original.Audience, original.CanonicalCallbackUri, original.BindingVersion));
        Assert.True(rotated.Matches(id, rotated.Audience, rotated.CanonicalCallbackUri, rotated.BindingVersion));
    }

    [Fact]
    public void Identity_binding_rejects_cross_instance_and_stale_values()
    {
        var id = Guid.NewGuid();
        var binding = ElsaInstanceIdentityBinding.Create(id, "https://example.test");

        Assert.False(binding.Matches(Guid.NewGuid(), binding.Audience, binding.CanonicalCallbackUri, binding.BindingVersion));
        Assert.False(binding.Matches(id, binding.Audience, binding.CanonicalCallbackUri, binding.BindingVersion + 1));
        Assert.False(binding.Matches(id, "urn:elsa:instance:" + Guid.NewGuid().ToString("D"), binding.CanonicalCallbackUri, binding.BindingVersion));
    }

    [Fact]
    public void Instance_accepts_only_an_identity_binding_for_its_own_id()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Pending);
        var binding = ElsaInstanceIdentityBinding.Create(instance.Id, "https://example.test");

        var bound = instance.AttachIdentityBinding(binding);

        Assert.Equal(binding, bound.IdentityBinding);
        Assert.Throws<ArgumentException>(() => instance.AttachIdentityBinding(
            ElsaInstanceIdentityBinding.Create(Guid.NewGuid(), "https://other.example.test")));
    }

    [Fact]
    public void Rename_rejects_display_names_over_256_characters()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Pending);

        Assert.Throws<ArgumentException>(() => instance.Rename(new string('n', 257)));
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    public void Localhost_callback_origin_can_use_http(string origin)
    {
        var binding = ElsaInstanceIdentityBinding.Create(Guid.NewGuid(), origin);

        Assert.StartsWith(origin + "/managed-elsa/handoff/callback", binding.CanonicalCallbackUri);
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("https://*.example.test")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test/control")]
    [InlineData("https://example.test?redirect=elsewhere")]
    [InlineData("https://example.test/#fragment")]
    public void Callback_origin_rejects_non_canonical_or_unsafe_values(string origin)
    {
        Assert.Throws<ArgumentException>(() => ElsaInstanceIdentityBinding.Create(Guid.NewGuid(), origin));
    }

    [Fact]
    public void Lifecycle_state_machine_requires_valid_observed_transitions()
    {
        Assert.Equal(ElsaObservedLifecycle.Provisioning,
            ElsaInstanceStateMachine.Transition(ElsaObservedLifecycle.Pending, ElsaObservedLifecycle.Provisioning));
        Assert.Equal(ElsaObservedLifecycle.Ready,
            ElsaInstanceStateMachine.Transition(ElsaObservedLifecycle.Provisioning, ElsaObservedLifecycle.Ready));

        Assert.Throws<InvalidOperationException>(() => ElsaInstanceStateMachine.Transition(
            ElsaObservedLifecycle.Deleted, ElsaObservedLifecycle.Ready));
        Assert.Throws<InvalidOperationException>(() => ElsaInstanceStateMachine.Transition(
            ElsaObservedLifecycle.Ready, ElsaObservedLifecycle.Stopped));
    }

    [Fact]
    public void Delete_is_explicit_and_only_cleanup_can_project_deleted()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);

        var requested = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete);

        Assert.Equal(ElsaDesiredLifecycle.Deleting, requested.Instance.Intent.DesiredLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Deleting, requested.Instance.ObservedLifecycle);
        Assert.Equal(ElsaInstanceOperationState.Accepted, requested.Operation.State);

        var deleted = ElsaInstanceStateMachine.FinalizeDeletion(requested.Instance, DateTimeOffset.UtcNow);

        Assert.Equal(ElsaObservedLifecycle.Deleted, deleted.ObservedLifecycle);
        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.Request(
            deleted, ElsaInstanceOperationAction.Start));
    }

    [Theory]
    [InlineData(ElsaInstanceOperationState.Accepted)]
    [InlineData(ElsaInstanceOperationState.Queued)]
    [InlineData(ElsaInstanceOperationState.Running)]
    [InlineData(ElsaInstanceOperationState.RecoveryRequired)]
    public void Delete_waits_behind_an_active_or_uncertain_operation(ElsaInstanceOperationState state)
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);
        var active = OperationFor(instance, state);

        var result = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete, active);

        Assert.Equal(ElsaInstanceOperationState.WaitingForPriorOperation, result.Operation.State);
        Assert.Equal(ElsaDesiredLifecycle.Deleting, result.Instance.DesiredLifecycle);
        Assert.Equal(instance.Id, active.InstanceId);
        Assert.True(active.HoldsReservation);
    }

    [Fact]
    public void Waiting_delete_is_the_durable_successor_and_cannot_be_duplicated()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);
        var prior = OperationFor(instance, ElsaInstanceOperationState.Running);
        var waiting = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete, prior);

        Assert.False(waiting.Operation.HoldsReservation);
        Assert.True(ElsaInstanceOperationGuard.IsBlocking(waiting.Operation.State));
        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.Request(
            waiting.Instance, ElsaInstanceOperationAction.Delete, waiting.Operation));
    }

    [Fact]
    public void Only_delete_operations_can_wait_for_a_prior_operation()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);
        var reconcile = ElsaInstanceOperation.Create(instance.Id, ElsaInstanceOperationAction.Reconcile,
            "instance/reconcile", "operation-key", Hash('a'), instance.Version);

        Assert.Throws<InvalidOperationException>(() =>
            reconcile.TransitionTo(ElsaInstanceOperationState.WaitingForPriorOperation));
        Assert.Equal(
            ElsaInstanceOperationState.WaitingForPriorOperation,
            OperationFor(instance, ElsaInstanceOperationState.WaitingForPriorOperation).State);
    }

    [Fact]
    public void Delete_cannot_create_a_second_successor_after_cleanup_has_started()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);
        var first = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete);

        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.Request(
            first.Instance, ElsaInstanceOperationAction.Delete, first.Operation));
    }

    [Theory]
    [InlineData(ElsaObservedLifecycle.Pending, ElsaObservedLifecycle.Deleting)]
    [InlineData(ElsaObservedLifecycle.Provisioning, ElsaObservedLifecycle.Deleting)]
    [InlineData(ElsaObservedLifecycle.Updating, ElsaObservedLifecycle.Deleting)]
    [InlineData(ElsaObservedLifecycle.Stopping, ElsaObservedLifecycle.Deleting)]
    [InlineData(ElsaObservedLifecycle.Unknown, ElsaObservedLifecycle.Unknown)]
    public void Delete_records_terminal_intent_without_bypassing_uncertain_observation(
        ElsaObservedLifecycle observed,
        ElsaObservedLifecycle expectedObserved)
    {
        var instance = CreateInstance(observed);

        var result = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete);

        Assert.Equal(ElsaDesiredLifecycle.Deleting, result.Instance.Intent.DesiredLifecycle);
        Assert.Equal(expectedObserved, result.Instance.ObservedLifecycle);
    }

    [Fact]
    public void Unknown_observation_never_projects_ready_without_reconciliation()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Provisioning);
        var unknown = ElsaInstanceStateMachine.Report(instance, ElsaObservedLifecycle.Unknown);

        Assert.Equal(ElsaObservedLifecycle.Unknown, unknown.ObservedLifecycle);
        Assert.Throws<InvalidOperationException>(() => ElsaInstanceStateMachine.Transition(
            ElsaObservedLifecycle.Unknown, ElsaObservedLifecycle.Ready));

        var provisioning = ElsaInstanceStateMachine.Request(unknown, ElsaInstanceOperationAction.Reconcile).Instance;
        var ready = ElsaInstanceStateMachine.Report(provisioning, ElsaObservedLifecycle.Ready);
        Assert.Equal(ElsaInstanceHealth.Healthy, ready.Health);
    }

    [Fact]
    public void Recover_reuses_the_recovery_required_operation_and_increments_attempt()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Failed);
        var recoveryRequired = OperationFor(instance, ElsaInstanceOperationState.RecoveryRequired);

        var result = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Recover, recoveryRequired);

        Assert.Equal(recoveryRequired.Id, result.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, result.Operation.State);
        Assert.Equal(recoveryRequired.AttemptNumber + 1, result.Operation.AttemptNumber);
    }

    [Fact]
    public void Recovering_delete_reuses_operation_without_reconciling_to_provisioning()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Unknown);
        var delete = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete);
        var recoveryRequired = delete.Operation
            .TransitionTo(ElsaInstanceOperationState.Queued)
            .TransitionTo(ElsaInstanceOperationState.Running)
            .TransitionTo(ElsaInstanceOperationState.RecoveryRequired);

        var result = ElsaInstanceStateMachine.Request(
            delete.Instance, ElsaInstanceOperationAction.Recover, recoveryRequired);

        Assert.Equal(recoveryRequired.Id, result.Operation.Id);
        Assert.Equal(ElsaInstanceOperationState.Queued, result.Operation.State);
        Assert.Equal(ElsaObservedLifecycle.Unknown, result.Instance.ObservedLifecycle);
        Assert.Equal(ElsaDesiredLifecycle.Deleting, result.Instance.DesiredLifecycle);
    }

    [Fact]
    public void Retry_is_state_gated_to_failed_or_degraded_observations()
    {
        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.Request(
            CreateInstance(ElsaObservedLifecycle.Ready), ElsaInstanceOperationAction.Retry));
        Assert.Equal(ElsaObservedLifecycle.Provisioning,
            ElsaInstanceStateMachine.Request(CreateInstance(ElsaObservedLifecycle.Failed), ElsaInstanceOperationAction.Retry)
                .Instance.ObservedLifecycle);
    }

    [Fact]
    public void Stop_and_start_change_desired_intent_before_remote_observation_catches_up()
    {
        var ready = CreateInstance(ElsaObservedLifecycle.Ready);
        var stopping = ElsaInstanceStateMachine.Request(ready, ElsaInstanceOperationAction.Stop).Instance;

        Assert.Equal(ElsaDesiredLifecycle.Stopped, stopping.Intent.DesiredLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Stopping, stopping.ObservedLifecycle);

        var stopped = ElsaInstanceStateMachine.Report(stopping, ElsaObservedLifecycle.Stopped);
        var starting = ElsaInstanceStateMachine.Request(stopped, ElsaInstanceOperationAction.Start).Instance;

        Assert.Equal(ElsaDesiredLifecycle.Running, starting.Intent.DesiredLifecycle);
        Assert.Equal(ElsaObservedLifecycle.Provisioning, starting.ObservedLifecycle);
    }

    [Fact]
    public void Deleted_observation_requires_delete_intent()
    {
        var ready = CreateInstance(ElsaObservedLifecycle.Ready);

        Assert.Throws<InvalidOperationException>(() => ElsaInstanceStateMachine.FinalizeDeletion(ready, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Recovery_required_operation_remains_an_active_concurrency_guard()
    {
        var operation = ElsaInstanceOperation.Create(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Reconcile,
            "workspace/instances/1/reconcile",
            "key-1",
            Hash('a'),
            expectedVersion: 4)
            .TransitionTo(ElsaInstanceOperationState.Queued)
            .TransitionTo(ElsaInstanceOperationState.Running)
            .TransitionTo(ElsaInstanceOperationState.RecoveryRequired);

        Assert.True(operation.HoldsReservation);
        Assert.True(ElsaInstanceOperationGuard.IsConflict(operation, "key-2", Hash('b')));
        Assert.False(ElsaInstanceOperationGuard.IsConflict(operation, "key-1", Hash('a')));
        Assert.Throws<InvalidOperationException>(() => operation.TransitionTo(ElsaInstanceOperationState.Queued));
    }

    [Fact]
    public void Invalid_enum_values_fail_closed_in_transition_guards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ElsaInstanceStateMachine.CanTransition(
            (ElsaObservedLifecycle)999, (ElsaObservedLifecycle)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElsaInstanceOperation.CanTransition(
            (ElsaInstanceOperationState)999, (ElsaInstanceOperationState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElsaInstanceOperationGuard.IsActive(
            (ElsaInstanceOperationState)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElsaInstanceOperationGuard.IsBlocking(
            (ElsaInstanceOperationState)999));
        Assert.False(ElsaInstanceOperationGuard.IsActive(ElsaInstanceOperationState.WaitingForPriorOperation));
        Assert.True(ElsaInstanceOperationGuard.IsBlocking(ElsaInstanceOperationState.WaitingForPriorOperation));
    }

    [Fact]
    public void Duplicate_operation_key_replays_only_for_the_same_request_hash()
    {
        var original = ElsaInstanceOperation.Create(
            Guid.NewGuid(),
            ElsaInstanceOperationAction.Stop,
            "workspace/instances/1/stop",
            "key-1",
            Hash('a'),
            expectedVersion: 2);

        Assert.False(ElsaInstanceOperationGuard.IsConflict(original, "key-1", Hash('a')));
        Assert.True(ElsaInstanceOperationGuard.IsConflict(original, "key-1", Hash('b')));
        var completed = original.TransitionTo(ElsaInstanceOperationState.Queued)
            .TransitionTo(ElsaInstanceOperationState.Running)
            .TransitionTo(ElsaInstanceOperationState.Succeeded);
        Assert.True(ElsaInstanceOperationGuard.IsConflict(completed, "key-1", Hash('b')));
        Assert.False(ElsaInstanceOperationGuard.IsConflict(completed, "key-2", Hash('b')));
        Assert.True(ElsaInstanceOperationGuard.IsConflict(original, "workspace/instances/1/start", "key-2", Hash('b')));
        Assert.False(ElsaInstanceOperationGuard.IsConflict(completed, "workspace/instances/1/stop", "key-1", Hash('a')));
    }

    [Fact]
    public void Stale_expected_version_is_rejected_before_accepting_intent()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);

        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.WithIntent(
            instance,
            InstanceIntent(application: new ElsaApplicationIntent("server-studio")),
            expectedVersion: instance.Version + 1));
    }

    [Fact]
    public void Intent_updates_are_guarded_by_operation_reservation_and_transition_rules()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);
        var patchIntent = InstanceIntent("3.8", "3.8.1");
        var minorIntent = InstanceIntent("3.9", "3.9.0");
        var majorIntent = InstanceIntent("4.0", "4.0.0");

        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.WithIntent(
            instance, instance.Intent with { DesiredLifecycle = ElsaDesiredLifecycle.Stopped }, instance.Version));
        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.WithIntent(instance, minorIntent, instance.Version));
        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.WithIntent(instance, majorIntent, instance.Version));

        var active = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Stop);
        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.WithIntent(
            instance, patchIntent, instance.Version, active.Operation));

        var patched = ElsaInstanceStateMachine.Request(
            instance, ElsaInstanceOperationAction.UpdateIntent, requestedIntent: patchIntent);
        Assert.Equal("3.8.1", patched.Instance.Intent.Release.RequestedVersion);
        Assert.Equal(instance.Version + 1, patched.Instance.Version);

        var approvedMinor = ElsaInstanceStateMachine.Request(
            instance, ElsaInstanceOperationAction.ApproveMinorUpgrade, requestedIntent: minorIntent);
        Assert.Equal("3.9", approvedMinor.Instance.Intent.Release.ReleaseLine);

        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.Request(
            instance, ElsaInstanceOperationAction.MajorMigration, requestedIntent: majorIntent));
        var migrated = ElsaInstanceStateMachine.Request(
            instance, ElsaInstanceOperationAction.MajorMigration, requestedIntent: majorIntent, migrationAuthorized: true);
        Assert.Equal("4.0", migrated.Instance.Intent.Release.ReleaseLine);
    }

    [Fact]
    public void Waiting_for_a_prior_operation_can_leave_the_waiting_state()
    {
        var operation = ElsaInstanceOperation.Create(
            Guid.NewGuid(), ElsaInstanceOperationAction.Delete, "scope", "key", Hash('a'), 1)
            .TransitionTo(ElsaInstanceOperationState.WaitingForPriorOperation);

        Assert.Equal(ElsaInstanceOperationState.Queued,
            operation.TransitionTo(ElsaInstanceOperationState.Queued).State);
        Assert.Equal(ElsaInstanceOperationState.Cancelled,
            operation.TransitionTo(ElsaInstanceOperationState.Cancelled).State);
        Assert.Throws<InvalidOperationException>(() => ElsaInstanceOperation.Create(
            Guid.NewGuid(), ElsaInstanceOperationAction.Reconcile, "scope", "key", Hash('a'), 1)
            .TransitionTo(ElsaInstanceOperationState.WaitingForPriorOperation));
    }

    [Fact]
    public void Operation_identity_values_are_bounded_and_canonical()
    {
        var instanceId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => ElsaInstanceOperation.Create(
            Guid.Empty, ElsaInstanceOperationAction.Reconcile, "scope", "key", Hash('a'), 1));
        Assert.Throws<ArgumentException>(() => ElsaInstanceOperation.Create(
            instanceId, ElsaInstanceOperationAction.Reconcile, "scope", "key", Hash('a'), 1, Guid.Empty));
        Assert.Throws<ArgumentException>(() => ElsaInstanceOperation.Create(
            instanceId, ElsaInstanceOperationAction.Reconcile, "scope with spaces", "key", Hash('a'), 1));
        Assert.Throws<ArgumentException>(() => ElsaInstanceOperation.Create(
            instanceId, ElsaInstanceOperationAction.Reconcile, "scope", "key", "hash", 1));

        var operation = ElsaInstanceOperation.Create(instanceId, ElsaInstanceOperationAction.Reconcile,
            "scope", "key", Hash('A'), 1);
        Assert.Equal(Hash('a'), operation.RequestHash);
    }

    [Fact]
    public void Foreign_active_operation_cannot_be_replayed_for_this_instance()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);
        var foreign = OperationFor(new ElsaInstance(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "Other", "other", InstanceIntent()), ElsaInstanceOperationState.Running);

        Assert.Throws<ElsaInstanceStateConflictException>(() => ElsaInstanceStateMachine.Request(
            instance, ElsaInstanceOperationAction.Reconcile, foreign));
    }

    [Theory]
    [InlineData("3.8", "3.8", "3.8.1", ElsaReleaseTransitionKind.Patch, false, true)]
    [InlineData("3.8", "3.9", "3.9.0", ElsaReleaseTransitionKind.Minor, false, false)]
    [InlineData("3.8", "3.9", "3.9.0", ElsaReleaseTransitionKind.Minor, true, true)]
    [InlineData("3.8", "4.0", "4.0.0", ElsaReleaseTransitionKind.Major, false, false)]
    [InlineData("3.8", "4.0", "4.0.0", ElsaReleaseTransitionKind.Major, true, true)]
    public void Release_transition_rules_distinguish_patch_minor_and_major(
        string currentLine,
        string targetLine,
        string targetVersion,
        ElsaReleaseTransitionKind expectedKind,
        bool explicitApproval,
        bool allowed)
    {
        var current = new ElsaReleaseSelection("valence-runtime", currentLine, currentLine + ".0", "stable");
        var target = new ElsaReleaseSelection("valence-runtime", targetLine, targetVersion, "stable");

        var transition = ElsaReleaseTransitionRules.Classify(current, target);

        Assert.Equal(expectedKind, transition.Kind);
        Assert.Equal(allowed, transition.IsAllowed(
            minorApproved: expectedKind == ElsaReleaseTransitionKind.Minor && explicitApproval,
            migrationAuthorized: expectedKind == ElsaReleaseTransitionKind.Major && explicitApproval));
    }

    [Fact]
    public void Preview_build_suffix_is_a_same_line_patch_not_a_pin_fallback()
    {
        var current = new ElsaReleaseSelection("valence-runtime", "3.8", "3.8.0-preview.5567", "preview");
        var target = new ElsaReleaseSelection("valence-runtime", "3.8", "3.8.0-preview.5567-build.153", "preview");

        var transition = ElsaReleaseTransitionRules.Classify(current, target);

        Assert.Equal(ElsaReleaseTransitionKind.Patch, transition.Kind);
        Assert.True(transition.IsAllowed(minorApproved: false, migrationAuthorized: false));
        Assert.NotEqual(current.Version, target.Version);
    }

    [Fact]
    public void AttachResolvedPlan_replaces_an_existing_pin_projection_with_the_build_suffix_release()
    {
        var pinPlan = new ElsaResolvedPlanReference(
            "release-61a8b8179b13f1b9b0560c3eb8723c4a98f69930b9fff02c0b7c5c3f0d56f4d7",
            1,
            Digest('a'),
            "https://control.example.test/api/workspaces/11111111-1111-1111-1111-111111111111/instances/22222222-2222-2222-2222-222222222222/resolved-plans/release-61a8b8179b13f1b9b0560c3eb8723c4a98f69930b9fff02c0b7c5c3f0d56f4d7");
        var buildPlan = new ElsaResolvedPlanReference(
            "release-3935507cf7ef56b895bb6c32d0fb7cd2d607981470c06e15cf1d01f10d529837",
            1,
            Digest('b'),
            "https://control.example.test/api/workspaces/11111111-1111-1111-1111-111111111111/instances/22222222-2222-2222-2222-222222222222/resolved-plans/release-3935507cf7ef56b895bb6c32d0fb7cd2d607981470c06e15cf1d01f10d529837");
        var pinRelease = new ElsaCurrentResolvedRelease(
            pinPlan,
            "valence-runtime",
            "3.8",
            "3.8.0-preview.5567",
            Digest('c'),
            [new ElsaComponentDigest("combined", Digest('d'))]);
        var buildRelease = new ElsaCurrentResolvedRelease(
            buildPlan,
            "valence-runtime",
            "3.8",
            "3.8.0-preview.5567-build.153",
            Digest('e'),
            [new ElsaComponentDigest("combined", Digest('f'))]);
        var instance = ElsaInstance.Hydrate(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.NewGuid(),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Dogfood2",
            "dogfood2",
            InstanceIntent("3.8", "3.8.0-preview.5567-build.153"),
            ElsaObservedLifecycle.Ready,
            ElsaInstanceHealth.Healthy,
            2,
            resolvedPlanReference: pinPlan,
            currentResolvedRelease: pinRelease);

        var replaced = instance.AttachResolvedPlan(buildPlan, buildRelease);

        Assert.Equal(buildPlan, replaced.ResolvedPlanReference);
        Assert.Equal("3.8.0-preview.5567-build.153", replaced.CurrentResolvedRelease!.Version);
        Assert.NotEqual(pinPlan, replaced.ResolvedPlanReference);
        Assert.Equal(instance.Version, replaced.Version);
        Assert.Equal(instance.ObservedLifecycle, replaced.ObservedLifecycle);
    }

    [Fact]
    public void Requested_version_must_belong_to_selected_release_line()
    {
        Assert.Throws<ArgumentException>(() => InstanceIntent("3.8", requestedVersion: "3.9.0"));
    }

    [Fact]
    public void Distribution_change_is_major_even_when_release_line_is_unchanged()
    {
        var current = new ElsaReleaseSelection("valence-runtime", "3.8", "3.8.0", "stable");
        var target = new ElsaReleaseSelection("other-distribution", "3.8", "3.8.1", "stable");

        var transition = ElsaReleaseTransitionRules.Classify(current, target);

        Assert.Equal(ElsaReleaseTransitionKind.Major, transition.Kind);
        Assert.False(transition.IsAllowed(minorApproved: true, migrationAuthorized: false));
        Assert.True(transition.IsAllowed(minorApproved: false, migrationAuthorized: true));
    }

    [Fact]
    public void Public_copy_initializers_cannot_bypass_intent_validation()
    {
        var intent = InstanceIntent();
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);

        Assert.Throws<ArgumentNullException>(() => intent with { Release = null! });
        Assert.Throws<ArgumentNullException>(() => intent with { Application = null! });
        Assert.Throws<ArgumentException>(() => intent.Application with { TopologyId = "\t" });
        Assert.Throws<ArgumentNullException>(() => intent.Application with { FeatureOverrides = null! });
        Assert.Throws<ArgumentException>(() => intent.Placement with { RegionCode = "region\r\n" });
        Assert.Throws<ArgumentOutOfRangeException>(() => intent with { DesiredLifecycle = (ElsaDesiredLifecycle)999 });
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.Name))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.Version))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.ObservedLifecycle))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.Health))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.IdentityBinding))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.Intent))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void Safe_observed_references_validate_immutably_and_project_on_instance()
    {
        var revisionId = new ElsaDesiredStateRevisionId("revision_01");
        var operationId = new ElsaLastOperationId("operation_01");
        var plan = new ElsaResolvedPlanReference("plan_01", 1, Digest('a'), "https://control.example.test/api/plans/plan_01");
        var release = new ElsaCurrentResolvedRelease(
            plan,
            "Valence-Runtime",
            "3.8",
            "3.8.0-preview.5413",
            Digest('b'),
            [new ElsaComponentDigest("studio", Digest('d')), new ElsaComponentDigest("combined", Digest('c'))]);
        var deployment = new ElsaCurrentDeploymentReference("deployment_01", "revision_01", "https://runtime.example.test/");
        var placement = new ElsaPlacementAssignmentReference("assignment_01");
        var tenant = new ElsaTenantReference("tenant_01", "urn:elsa:tenant:tenant_01");
        var instance = ElsaInstance.Hydrate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Claims", "claims", InstanceIntent(),
            ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Unknown, 1,
            desiredStateRevisionId: revisionId,
            resolvedPlanReference: plan,
            currentResolvedRelease: release,
            currentDeploymentReference: deployment,
            placementAssignmentReference: placement,
            elsaTenantReference: tenant,
            lastOperationId: operationId);

        Assert.Equal(revisionId, instance.DesiredStateRevisionId);
        Assert.Equal(plan, instance.ResolvedPlanReference);
        Assert.Equal(plan.PlanUri, instance.CurrentResolvedRelease!.PlanUri);
        Assert.Equal("combined", instance.CurrentResolvedRelease.ComponentDigests[0].ComponentId);
        Assert.Equal("https://runtime.example.test", instance.CurrentDeploymentReference!.EndpointUri);
        Assert.Equal("assignment_01", instance.PlacementAssignmentReference!.Value);
        Assert.Equal("tenant_01", instance.ElsaTenantReference!.TenantId);
        Assert.Equal(operationId, instance.LastOperationId);

        Assert.Throws<ArgumentException>(() => ElsaInstance.Hydrate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Claims", "claims", InstanceIntent(),
            ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Unknown, 1,
            desiredStateRevisionId: new ElsaDesiredStateRevisionId()));
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.DesiredStateRevisionId))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.CurrentResolvedRelease))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void Managed_handoff_belongs_to_a_verified_deployment_origin()
    {
        Assert.False(new ElsaCurrentDeploymentReference("deployment_01", endpointUri: "https://runtime.example.test").ManagedHandoff);
        Assert.True(new ElsaCurrentDeploymentReference("deployment_01", endpointUri: "https://runtime.example.test", managedHandoff: true).ManagedHandoff);
        Assert.Throws<ArgumentException>(() => new ElsaCurrentDeploymentReference("deployment_01", managedHandoff: true));
    }

    [Fact]
    public void Managed_deployment_endpoint_is_a_canonical_typed_https_origin()
    {
        var deployment = new ElsaCurrentDeploymentReference(
            "deployment_01", endpointUri: " HTTPS://Runtime.Example.Test:443/ ");

        Assert.Equal(new ElsaManagedEndpointOrigin("https://runtime.example.test"), deployment.EndpointOrigin);
        Assert.Equal("https://runtime.example.test", deployment.EndpointUri);
    }

    [Theory]
    [InlineData("http://runtime.example.test")]
    [InlineData("https:///runtime")]
    [InlineData("https://user:password@runtime.example.test")]
    [InlineData("https://runtime.example.test/runtime")]
    [InlineData("https://runtime.example.test?token=value")]
    [InlineData("https://runtime.example.test#fragment")]
    [InlineData("https://*.example.test")]
    [InlineData("https://runtime.example.test/\r\nsecret")]
    public void Managed_deployment_endpoint_rejects_non_origins(string endpoint)
    {
        Assert.False(ElsaManagedEndpointOrigin.TryCreate(endpoint, out _));
        Assert.Throws<ArgumentException>(() =>
            new ElsaCurrentDeploymentReference("deployment_01", endpointUri: endpoint));
    }

    [Theory]
    [InlineData("md5:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Safe_plan_and_release_references_require_sha256_digests(string digest)
    {
        Assert.Throws<ArgumentException>(() => new ElsaResolvedPlanReference(
            "plan_01", 1, digest, "https://control.example.test/api/plans/plan_01"));
        Assert.Throws<ArgumentException>(() => new ElsaComponentDigest("combined", digest));
    }

    [Theory]
    [InlineData("https://control.example.test/api/plans/plan_01?token=secret")]
    [InlineData("https://user:password@control.example.test/api/plans/plan_01")]
    [InlineData("http://control.example.test/api/plans/plan_01")]
    [InlineData("/api/plans/plan_01")]
    public void Plan_reference_rejects_non_absolute_or_unsafe_api_uris(string planUri)
    {
        Assert.Throws<ArgumentException>(() => new ElsaResolvedPlanReference("plan_01", 1, Digest('a'), planUri));
    }

    [Theory]
    [InlineData("https://control.example.test/api/plans/../secret")]
    [InlineData("https://control.example.test/api/plans/%2e%2e/secret")]
    [InlineData("https://control.example.test/api//plans/plan_01")]
    public void Plan_reference_rejects_ambiguous_or_traversal_paths(string planUri)
    {
        Assert.Throws<ArgumentException>(() => new ElsaResolvedPlanReference("plan_01", 1, Digest('a'), planUri));
    }

    [Fact]
    public void Safe_reference_lengths_and_component_count_are_bounded()
    {
        Assert.Throws<ArgumentException>(() => new ElsaResolvedPlanReference(
            new string('p', 129), 1, Digest('a'), "https://control.example.test/api/plans/plan_01"));
        Assert.Throws<ArgumentException>(() => new ElsaCurrentDeploymentReference(new string('d', 129)));

        var tooMany = Enumerable.Range(0, 257)
            .Select(x => new ElsaComponentDigest("component-" + x, Digest('a')));
        var plan = new ElsaResolvedPlanReference("plan_01", 1, Digest('a'), "https://control.example.test/api/plans/plan_01");
        Assert.Throws<ArgumentException>(() => new ElsaCurrentResolvedRelease(
            plan, "valence-runtime", "3.8", "3.8.0", Digest('b'), tooMany));
    }

    [Theory]
    [InlineData("deployment/azure/resource")]
    [InlineData("deployment?secret=1")]
    [InlineData("deployment\n")]
    public void Deployment_and_assignment_references_reject_provider_or_unsafe_values(string value)
    {
        Assert.Throws<ArgumentException>(() => new ElsaCurrentDeploymentReference(value));
        Assert.Throws<ArgumentException>(() => new ElsaPlacementAssignmentReference(value));
    }

    [Fact]
    public void Current_release_requires_matching_line_and_deduplicated_components()
    {
        var plan = new ElsaResolvedPlanReference("plan_01", 1, Digest('a'), "https://control.example.test/api/plans/plan_01");

        Assert.Throws<ArgumentException>(() => new ElsaCurrentResolvedRelease(
            plan, "valence-runtime", "3.8", "3.8.0", Digest('b'), []));
        Assert.Throws<ArgumentException>(() => new ElsaCurrentResolvedRelease(
            plan, "valence-runtime", "3.8", "3.8.0", Digest('b'),
            [new ElsaComponentDigest("combined", Digest('c')), new ElsaComponentDigest("COMBINED", Digest('d'))]));
    }

    [Fact]
    public void Aggregate_keeps_plan_reference_and_current_release_identity_aligned()
    {
        var plan = new ElsaResolvedPlanReference("plan_01", 1, Digest('a'), "https://control.example.test/api/plans/plan_01");
        var release = CurrentRelease(plan);
        var instance = ElsaInstance.Hydrate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Claims", "claims", InstanceIntent(),
            ElsaObservedLifecycle.Ready, ElsaInstanceHealth.Unknown, 1,
            resolvedPlanReference: plan, currentResolvedRelease: release);
        var otherPlan = new ElsaResolvedPlanReference("plan_02", 1, Digest('b'), "https://control.example.test/api/plans/plan_02");

        Assert.Throws<ArgumentException>(() => ElsaInstance.Hydrate(
            instance.Id, instance.OrganizationId, instance.WorkspaceId, instance.Name, instance.Slug, instance.Intent,
            instance.ObservedLifecycle, instance.Health, instance.Version,
            resolvedPlanReference: plan, currentResolvedRelease: CurrentRelease(otherPlan)));
        Assert.Throws<ArgumentException>(() => ElsaInstance.Hydrate(
            instance.Id, instance.OrganizationId, instance.WorkspaceId, instance.Name, instance.Slug, instance.Intent,
            instance.ObservedLifecycle, instance.Health, instance.Version,
            resolvedPlanReference: otherPlan, currentResolvedRelease: release));
    }

    [Fact]
    public void Deletion_timestamp_cannot_be_forged_on_a_live_instance()
    {
        var instance = CreateInstance(ElsaObservedLifecycle.Ready);

        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.DeletedAt))!.SetMethod!.IsPublic);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.ObservedLifecycle))!.SetMethod!.IsPublic);
        Assert.Throws<ArgumentException>(() => ElsaInstance.Hydrate(
            instance.Id, instance.OrganizationId, instance.WorkspaceId, instance.Name, instance.Slug, instance.Intent,
            ElsaObservedLifecycle.Ready, instance.Health, instance.Version,
            deletedAt: DateTimeOffset.UtcNow));

        var deleting = ElsaInstanceStateMachine.Request(instance, ElsaInstanceOperationAction.Delete).Instance;
        var deleted = ElsaInstanceStateMachine.FinalizeDeletion(deleting, DateTimeOffset.UtcNow);
        Assert.NotNull(deleted.DeletedAt);
        Assert.False(typeof(ElsaInstance).GetProperty(nameof(ElsaInstance.DeletedAt))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void Generic_observation_cannot_project_a_deleted_tombstone()
    {
        var deleting = ElsaInstanceStateMachine.Request(
            CreateInstance(ElsaObservedLifecycle.Ready),
            ElsaInstanceOperationAction.Delete).Instance;

        Assert.Throws<InvalidOperationException>(() =>
            ElsaInstanceStateMachine.Report(deleting, ElsaObservedLifecycle.Deleted));
    }

    [Fact]
    public void Unknown_deletion_requires_the_explicit_finalization_boundary()
    {
        var unknown = ElsaInstanceStateMachine.Request(
            CreateInstance(ElsaObservedLifecycle.Unknown),
            ElsaInstanceOperationAction.Delete).Instance;

        Assert.False(ElsaInstanceStateMachine.CanTransition(
            ElsaObservedLifecycle.Unknown,
            ElsaObservedLifecycle.Deleted));
        var deletedAt = DateTimeOffset.UtcNow;
        var tombstone = ElsaInstanceStateMachine.FinalizeDeletion(unknown, deletedAt);
        Assert.Equal(ElsaObservedLifecycle.Deleted, tombstone.ObservedLifecycle);
        Assert.Equal(deletedAt, tombstone.DeletedAt);
    }

    [Fact]
    public void Ownership_requires_non_empty_organization_and_workspace_ids()
    {
        Assert.Throws<ArgumentException>(() => new ElsaInstance(
            Guid.NewGuid(),
            Guid.Empty,
            Guid.NewGuid(),
            "Claims",
            "claims",
            InstanceIntent()));
        Assert.Throws<ArgumentException>(() => new ElsaInstance(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.Empty,
            "Claims",
            "claims",
            InstanceIntent()));
    }

    [Fact]
    public void New_instances_cannot_start_with_deletion_intent_but_tombstones_can_rehydrate()
    {
        var deletingIntent = InstanceIntent() with { DesiredLifecycle = ElsaDesiredLifecycle.Deleting };
        Assert.Throws<ArgumentException>(() => new ElsaInstance(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Claims", "claims", deletingIntent));

        var tombstone = ElsaInstance.Hydrate(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Claims", "claims", deletingIntent,
            ElsaObservedLifecycle.Deleted, ElsaInstanceHealth.Unknown, 4,
            deletedAt: DateTimeOffset.UtcNow);
        Assert.Equal(ElsaObservedLifecycle.Deleted, tombstone.ObservedLifecycle);
        Assert.NotNull(tombstone.DeletedAt);
    }

    [Fact]
    public void Ownership_check_requires_both_organization_and_workspace()
    {
        var organizationId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var instance = new ElsaInstance(Guid.NewGuid(), organizationId, workspaceId, "Claims", "Claims Prod", InstanceIntent());

        Assert.True(instance.BelongsTo(organizationId, workspaceId));
        Assert.False(instance.BelongsTo(Guid.NewGuid(), workspaceId));
        Assert.False(instance.BelongsTo(organizationId, Guid.NewGuid()));
        Assert.Equal("Claims", instance.Name);
        Assert.Equal("claims-prod", instance.Slug);
    }

    private static ElsaInstance CreateInstance(ElsaObservedLifecycle observed)
    {
        return ElsaInstance.Hydrate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Claims", "claims", InstanceIntent(),
            observed, ElsaInstanceHealth.Unknown, 1);
    }

    private static ElsaInstanceIntent InstanceIntent(
        string releaseLine = "3.8",
        string? requestedVersion = null,
        ElsaApplicationIntent? application = null) => new(
        new ElsaReleaseIntent("valence-runtime", releaseLine, requestedVersion, "stable", "automatic-within-minor", "explicit-approval", "explicit-migration"),
        application ?? new ElsaApplicationIntent("combined", "starter", new Dictionary<string, ElsaFeatureOverride>(), "valence-approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static string Digest(char value) => "sha256:" + new string(value, 64);

    private static string Hash(char value) => new string(value, 64);

    private static ElsaInstanceOperation OperationFor(ElsaInstance instance, ElsaInstanceOperationState state)
    {
        var action = state == ElsaInstanceOperationState.WaitingForPriorOperation
            ? ElsaInstanceOperationAction.Delete
            : ElsaInstanceOperationAction.Reconcile;
        var operation = ElsaInstanceOperation.Create(instance.Id, action,
            "instance/reconcile", "operation-key", Hash('a'), instance.Version);
        return state switch
        {
            ElsaInstanceOperationState.Accepted => operation,
            ElsaInstanceOperationState.WaitingForPriorOperation => operation
                .TransitionTo(ElsaInstanceOperationState.WaitingForPriorOperation),
            ElsaInstanceOperationState.Queued => operation.TransitionTo(ElsaInstanceOperationState.Queued),
            ElsaInstanceOperationState.Running => operation.TransitionTo(ElsaInstanceOperationState.Queued)
                .TransitionTo(ElsaInstanceOperationState.Running),
            ElsaInstanceOperationState.RecoveryRequired => operation.TransitionTo(ElsaInstanceOperationState.Queued)
                .TransitionTo(ElsaInstanceOperationState.Running)
                .TransitionTo(ElsaInstanceOperationState.RecoveryRequired),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
    }

    private static ElsaCurrentResolvedRelease CurrentRelease(ElsaResolvedPlanReference plan) => new(
        plan, "valence-runtime", "3.8", "3.8.0", Digest('c'), [new ElsaComponentDigest("combined", Digest('d'))]);
}
