using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Azure;
using Xunit;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureProviderRecoveryObservationContractTests
{
    [Theory]
    [InlineData("target")]
    [InlineData("operation")]
    public void Observation_rejects_noncanonical_identity_token_casing(string field)
    {
        var observation = CreateObservation();
        observation = field == "target"
            ? observation with { TargetKey = observation.TargetKey.ToUpperInvariant() }
            : observation with { ProviderOperationIdentity = observation.ProviderOperationIdentity.ToUpperInvariant() };

        Assert.Throws<ArgumentException>(observation.Validate);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("scope")]
    [InlineData("plan")]
    [InlineData("template")]
    [InlineData("resources")]
    [InlineData("postcondition")]
    public void Observation_rejects_noncanonical_fingerprint_casing(string field)
    {
        var uppercase = new string('A', 64);
        var observation = CreateObservation();
        observation = field switch
        {
            "request" => observation with { ProviderRequestHash = uppercase },
            "scope" => observation with { ProviderScopeFingerprint = uppercase },
            "plan" => observation with { ProviderPlanFingerprint = uppercase },
            "template" => observation with { ProviderTemplateFingerprint = uppercase },
            "resources" => observation with { ResourceFingerprint = uppercase },
            "postcondition" => observation with { PostconditionFingerprint = uppercase },
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<ArgumentException>(observation.Validate);
    }

    [Fact]
    public void Natural_key_ignores_polling_time_but_binds_changed_authority_tuple()
    {
        var observation = CreateObservation();
        var unchangedPoll = observation with { ObservedAt = observation.ObservedAt.AddMinutes(5) };
        var changedAuthority = observation with { ProviderVersion = observation.ProviderVersion + 1 };

        Assert.Equal(observation.ComputeNaturalKey(), unchangedPoll.ComputeNaturalKey());
        Assert.Equal(
            observation.ComputeRecordDigest(RecordId),
            unchangedPoll.ComputeRecordDigest(RecordId));
        Assert.NotEqual(observation.ComputeNaturalKey(), changedAuthority.ComputeNaturalKey());
        Assert.NotEqual(
            observation.ComputeRecordDigest(RecordId),
            changedAuthority.ComputeRecordDigest(RecordId));
    }

    [Fact]
    public void Record_digest_is_bound_to_record_id()
    {
        var observation = CreateObservation();

        Assert.NotEqual(
            observation.ComputeRecordDigest(RecordId),
            observation.ComputeRecordDigest(Guid.Parse("77777777-7777-7777-7777-777777777777")));
    }

    [Fact]
    public void Legacy_phase_ordinals_are_preserved_and_recovery_phases_are_explicitly_ordered()
    {
        var legacyPhases = new[]
        {
            AzureProviderOperationPhase.Planned,
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderOperationPhase.FoundationReady,
            AzureProviderOperationPhase.WorkloadSubmitted,
            AzureProviderOperationPhase.WorkloadReady,
            AzureProviderOperationPhase.HealthVerified,
            AzureProviderOperationPhase.TrafficPromoted,
            AzureProviderOperationPhase.CleanupSubmitted,
            AzureProviderOperationPhase.CleanupVerified
        };

        Assert.Equal(Enumerable.Range(0, 9), legacyPhases.Select(phase => (int)phase));
        Assert.True(AzureProviderOperationPhaseOrdering.Compare(
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderOperationPhase.FoundationObserved) < 0);
        Assert.True(AzureProviderOperationPhaseOrdering.Compare(
            AzureProviderOperationPhase.FoundationObserved,
            AzureProviderOperationPhase.AcrPullObserved) < 0);
        Assert.True(AzureProviderOperationPhaseOrdering.Compare(
            AzureProviderOperationPhase.AcrPullObserved,
            AzureProviderOperationPhase.SeedSecretsObserved) < 0);
        Assert.True(AzureProviderOperationPhaseOrdering.Compare(
            AzureProviderOperationPhase.SeedSecretsObserved,
            AzureProviderOperationPhase.SqlFirewallReady) < 0);
        Assert.True(AzureProviderOperationPhaseOrdering.Compare(
            AzureProviderOperationPhase.SqlFirewallReady,
            AzureProviderOperationPhase.SqlBootstrapReady) < 0);
        Assert.True(AzureProviderOperationPhaseOrdering.Compare(
            AzureProviderOperationPhase.SqlBootstrapReady,
            AzureProviderOperationPhase.FoundationReady) < 0);
    }

    [Fact]
    public void Recovery_boundary_requires_the_observed_step_and_phase_to_match()
    {
        Assert.True(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.SqlFirewallReady));
        Assert.True(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.SeedSecretsObserved,
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.SqlFirewallReady));
        Assert.True(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.SqlFirewallReady,
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.SqlBootstrapReady));
        Assert.True(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlFirewallCleanup,
            AzureProviderOperationPhase.SqlBootstrapReady,
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.SqlBootstrapReady));
        Assert.True(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            null,
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderRunnerStep.Foundation,
            AzureProviderOperationPhase.FoundationObserved));

        Assert.False(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.FoundationSubmitted,
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.SqlBootstrapReady));
        Assert.False(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.SqlFirewallReady,
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.FoundationReady));
        Assert.False(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlFirewallCleanup,
            AzureProviderOperationPhase.SqlBootstrapReady,
            AzureProviderRunnerStep.SqlBootstrapScript,
            AzureProviderOperationPhase.SqlFirewallReady));
        Assert.False(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.FoundationObserved,
            AzureProviderRunnerStep.SqlFirewallCreate,
            AzureProviderOperationPhase.SqlFirewallReady));
        Assert.False(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.SqlBootstrap,
            AzureProviderOperationPhase.FoundationReady,
            AzureProviderRunnerStep.SqlBootstrap,
            AzureProviderOperationPhase.FoundationReady));
        Assert.False(AzureProviderRecoveryObservationSupport.IsCompatibleBoundary(
            AzureProviderRunnerStep.Workload,
            AzureProviderOperationPhase.WorkloadSubmitted,
            AzureProviderRunnerStep.Workload,
            AzureProviderOperationPhase.WorkloadReady));
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCreate, AzureProviderOperationPhase.SqlFirewallReady)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrapScript, AzureProviderOperationPhase.SqlBootstrapReady)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCleanup, AzureProviderOperationPhase.FoundationReady)]
    public void Sql_recovery_steps_have_explicit_phase_mapping(
        AzureProviderRunnerStep step,
        AzureProviderOperationPhase expectedPhase)
    {
        Assert.Equal(expectedPhase, AzureProviderRecoveryObservationSupport.RecoveryPhase(step));
    }

    [Fact]
    public void Checkpoint_validation_rejects_unknown_phase_and_attempted_step()
    {
        var checkpoint = new AzureProviderCheckpoint(
            AzureProviderOperationPhase.FoundationSubmitted,
            "azure.step.attempted",
            "The Azure lifecycle step was marked before its remote call.",
            new(),
            null,
            AzureProviderHealth.Unknown,
            [],
            AttemptedStep: AzureProviderRunnerStep.Foundation);

        Assert.Throws<ArgumentException>(() => AzureProviderOperationValidation.ValidateCheckpoint(
            checkpoint with { Phase = (AzureProviderOperationPhase)999 }));
        Assert.Throws<ArgumentException>(() => AzureProviderOperationValidation.ValidateCheckpoint(
            checkpoint with { AttemptedStep = (AzureProviderRunnerStep)999 }));
    }

    [Fact]
    public void Observation_validation_accepts_confirmed_and_uncertain_shapes_but_rejects_mixed_values()
    {
        var confirmed = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Confirmed,
            AzureProviderRunnerStep.Foundation,
            new(),
            AzureProviderHealth.Unknown,
            null,
            "provider.recovery.foundation-observed",
            "The retained foundation postcondition was observed.");
        var uncertain = new AzureProviderRecoveryObservation(
            AzureProviderRecoveryObservationKind.Unknown,
            null,
            new(),
            AzureProviderHealth.Unknown,
            null,
            "provider.recovery.unknown",
            "The retained provider state remains uncertain.");

        confirmed.Validate();
        uncertain.Validate();
        var confirmedWithoutStep = confirmed with { CompletedStep = null };
        var uncertainWithStep = uncertain with { CompletedStep = AzureProviderRunnerStep.Foundation };
        var healthyUncertainty = uncertain with { Health = AzureProviderHealth.Healthy };
        Assert.Throws<ArgumentException>(() => confirmedWithoutStep.Validate());
        Assert.Throws<ArgumentException>(() => uncertainWithStep.Validate());
        Assert.Throws<ArgumentException>(() => healthyUncertainty.Validate());

        var invalidKind = confirmed with { Kind = (AzureProviderRecoveryObservationKind)999 };
        var exception = Assert.Throws<ArgumentException>(invalidKind.Validate);
        Assert.Equal("The Azure recovery observation is invalid.", exception.Message);
        Assert.Null(exception.ParamName);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("organization")]
    [InlineData("workspace")]
    [InlineData("instance")]
    [InlineData("last-operation")]
    [InlineData("target")]
    public void Recovery_request_rejects_assignment_tuple_mismatch_before_observation(string mismatch)
    {
        var request = CreateRecoveryRequest();
        var assignment = request.Assignment!;
        assignment = mismatch switch
        {
            "id" => assignment with { Id = Guid.Parse("77777777-7777-7777-7777-777777777777") },
            "organization" => assignment with { OrganizationId = Guid.Parse("88888888-8888-8888-8888-888888888888") },
            "workspace" => assignment with { WorkspaceId = Guid.Parse("99999999-9999-9999-9999-999999999999") },
            "instance" => assignment with { InstanceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa") },
            "last-operation" => assignment with { LastOperationId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") },
            "target" => assignment with { WorkloadName = "another-instance" },
            _ => throw new InvalidOperationException()
        };

        var invalidRequest = request with { Assignment = assignment };
        Assert.Throws<InvalidOperationException>(invalidRequest.Validate);
    }

    [Fact]
    public void Recovery_request_accepts_a_template_only_scope_rotation()
    {
        var request = CreateRecoveryRequest();
        var rebound = request with
        {
            Assignment = request.Assignment! with { ProviderScopeFingerprint = new string('c', 64) }
        };

        rebound.Validate();
    }

    [Fact]
    public void Recovery_request_keeps_original_elsa_lifecycle_action_distinct_from_provider_action()
    {
        var request = CreateRecoveryRequest();

        request.Validate();
        Assert.Equal(AzureProviderOperationAction.Reconcile, request.Operation.Action);
        Assert.Equal(ElsaInstanceOperationAction.Create, request.Operation.LifecycleAction);
    }

    [Fact]
    public void Recovery_request_keeps_nullable_assignment_for_observer_fail_closed_validation()
    {
        var request = CreateRecoveryRequest() with { Assignment = null };

        request.Validate();
        Assert.Null(request.Assignment);
    }

    [Fact]
    public void Recovery_request_rejects_a_noncanonical_plan_fingerprint()
    {
        var request = CreateRecoveryRequest();
        var invalidRequest = request with
        {
            Plan = request.Plan with { Fingerprint = request.Plan.Fingerprint.ToUpperInvariant() }
        };

        Assert.Throws<InvalidOperationException>(invalidRequest.Validate);
    }

    [Fact]
    public void Recovery_request_rejects_uppercase_operation_and_plan_fingerprints()
    {
        var request = CreateRecoveryRequest();
        var uppercaseFingerprint = request.Plan.Fingerprint.ToUpperInvariant();
        var invalidRequest = request with
        {
            Operation = request.Operation with { PlanFingerprint = uppercaseFingerprint },
            Plan = request.Plan with { Fingerprint = uppercaseFingerprint }
        };

        Assert.Throws<InvalidOperationException>(invalidRequest.Validate);
    }

    [Fact]
    public void Recovery_request_rejects_provider_plan_field_mismatch_even_when_fingerprint_matches()
    {
        var request = CreateRecoveryRequest();
        var invalidRequest = request with
        {
            Plan = request.Plan with { ImageDigest = new string('f', 64) }
        };

        Assert.Throws<InvalidOperationException>(invalidRequest.Validate);
    }

    [Theory]
    [InlineData("group")]
    [InlineData("foundation")]
    [InlineData("vault")]
    public void Resource_fingerprint_rejects_unsafe_references_before_hashing(string field)
    {
        var resources = field switch
        {
            "group" => new AzureProviderResourceReferences(ResourceGroupName: "proof-rg\nother"),
            "foundation" => new AzureProviderResourceReferences(
                FoundationDeploymentId: "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/proof-rg/providers/Microsoft.Resources/deployments/foundation?unexpected=true"),
            "vault" => new AzureProviderResourceReferences(KeyVaultUri: "https://proof.vault.azure.net/?unexpected=true"),
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<ArgumentException>(() => AzureProviderRecoveryObservationRecord.ComputeResourceFingerprint(resources));
    }

    [Fact]
    public void Observation_record_validation_rejects_unknown_phase_or_completed_step()
    {
        var observation = CreateObservation();

        var unknownPhase = observation with
        {
            ObservedPhase = (AzureProviderOperationPhase)999
        };
        var unknownStep = observation with
        {
            CompletedStep = (AzureProviderRunnerStep)999
        };
        Assert.Throws<ArgumentException>(() => unknownPhase.Validate());
        Assert.Throws<ArgumentException>(() => unknownStep.Validate());
    }

    [Fact]
    public void Observation_binding_accepts_distinct_recovery_and_observation_ids()
    {
        CreateBinding().Validate();
    }

    [Theory]
    [InlineData("bad scope", "recovery-key", 2, 4)]
    [InlineData("instances/operations", "recovery:key", 2, 4)]
    [InlineData("instances/operations", "recovery-key", 3, 4)]
    [InlineData("instances/operations", "recovery-key", 2, 3)]
    public void Observation_binding_rejects_core_envelope_invariants(
        string scope,
        string key,
        int acceptedAttempt,
        int acceptedVersion)
    {
        var binding = CreateBinding() with
        {
            IdempotencyScope = scope,
            IdempotencyKey = key,
            AcceptedLifecycleAttemptNumber = acceptedAttempt,
            AcceptedInstanceVersion = acceptedVersion
        };

        Assert.Throws<ArgumentException>(binding.Validate);
    }

    [Theory]
    [InlineData("https://control.example.test/api/workspaces/22222222-2222-2222-2222-222222222222/instances/33333333-3333-3333-3333-333333333333/resolved-plans/plan-1?token=secret")]
    [InlineData("https://control.example.test/api/workspaces/22222222-2222-2222-2222-222222222222/instances/33333333-3333-3333-3333-333333333333/resolved-plans/plan-1#fragment")]
    [InlineData("https://user:secret@control.example.test/api/workspaces/22222222-2222-2222-2222-222222222222/instances/33333333-3333-3333-3333-333333333333/resolved-plans/plan-1")]
    [InlineData("http://control.example.test/api/workspaces/22222222-2222-2222-2222-222222222222/instances/33333333-3333-3333-3333-333333333333/resolved-plans/plan-1")]
    [InlineData("https://control.example.test/plans/plan-1")]
    public void Observation_record_validation_rejects_unsafe_resolved_plan_uris(string planUri)
    {
        Assert.Throws<ArgumentException>(() => (CreateObservation() with { ResolvedPlanUri = planUri }).Validate());
    }

    private static readonly Guid RecordId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    private static AzureProviderRecoveryObservationRecord CreateObservation() => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        ElsaInstanceOperationAction.Reconcile,
        1,
        3,
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        "operation-identity-1",
        new string('a', 64),
        1,
        7,
        3,
        Guid.Parse("66666666-6666-6666-6666-666666666666"),
        "elsa-instance",
        new string('b', 64),
        "plan-1",
        1,
        "https://control.example.test/api/workspaces/22222222-2222-2222-2222-222222222222/instances/33333333-3333-3333-3333-333333333333/resolved-plans/plan-1",
        "sha256:" + new string('c', 64),
        new string('d', 64),
        new string('e', 64),
        AzureProviderRunnerStep.Foundation,
        AzureProviderOperationPhase.FoundationObserved,
        AzureProviderHealth.Unknown,
        new string('f', 64),
        new string('0', 64),
        DateTimeOffset.Parse("2026-09-06T08:00:00Z"));

    private static AzureProviderRecoveryObservationBinding CreateBinding()
    {
        var digest = "sha256:" + new string('a', 64);
        return new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            1,
            3,
            2,
            4,
            "instances/operations",
            "recovery-key",
            new string('b', 64),
            ElsaInstanceProviderRecoveryObservationReference.Create(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), digest),
            digest);
    }

    private static AzureProviderRecoveryRequest CreateRecoveryRequest()
    {
        var organizationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var workspaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var instanceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var operationId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var assignmentId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var planFingerprint = new string('d', 64);
        var scopeFingerprint = new string('b', 64);
        var plan = new AzureWorkloadPlan(
            "elsa-instance",
            "westeurope",
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "valenceruntimeimages.azurecr.io/runtime-combined",
            new string('e', 64),
            "oci://valenceruntimeimages.azurecr.io/runtime-manifest@sha256:" + new string('f', 64),
            "sha256:" + new string('f', 64),
            "oci://valenceruntimeimages.azurecr.io/runtime-signature@sha256:" + new string('0', 64),
            "sha256:" + new string('0', 64),
            new Dictionary<string, string>(),
            planFingerprint,
            "3.8.0",
            "3.8.0");
        var operation = new AzureProviderOperation(
            operationId,
            workspaceId,
            plan.WorkloadName,
            AzureProviderOperationAction.Reconcile,
            "elsa-instance-operation:" + operationId.ToString("D"),
            new string('a', 64),
            "operation-identity-1",
            planFingerprint,
            new string('1', 64),
            "3.8.0",
            "3.8",
            "combined",
            "Dedicated",
            "westeurope",
            plan.ImageRepository,
            "sha256:" + plan.ImageDigest,
            plan.ReleaseManifestDigest,
            plan.ReleaseManifestSignatureDigest,
            AzureProviderOperationStatus.RecoveryRequired,
            AzureProviderOperationPhase.FoundationSubmitted,
            3,
            1,
            7,
            new(),
            null,
            AzureProviderHealth.Unknown,
            [],
            "worker-1",
            null,
            null,
            DateTimeOffset.Parse("2026-09-06T08:00:00Z"),
            DateTimeOffset.Parse("2026-09-06T08:00:00Z"),
            null,
            ReleaseManifestReference: plan.ReleaseManifestReference,
            ReleaseManifestSignatureReference: plan.ReleaseManifestSignatureReference,
            SecretReferences: plan.SecretReferences,
            SqlWorkflowPackageVersion: plan.SqlWorkflowPackageVersion,
            SqlQuartzPackageVersion: plan.SqlQuartzPackageVersion,
            ProviderScopeFingerprint: scopeFingerprint,
            OrganizationId: organizationId,
            InstanceId: instanceId,
            LifecycleAction: ElsaInstanceOperationAction.Create,
            ProviderAssignmentId: assignmentId,
            AttemptedStep: AzureProviderRunnerStep.Foundation);
        var assignment = new AzureProviderResourceAssignment(
            assignmentId,
            workspaceId,
            organizationId,
            instanceId,
            scopeFingerprint,
            AzureProviderResourceAssignmentNaming.CurrentVersion,
            "subscription-1",
            "elsa-instance-rg",
            plan.WorkloadName,
            new string('2', 64),
            "westeurope",
            AzureProviderAssignmentState.Unknown,
            new(),
            operationId,
            2,
            DateTimeOffset.Parse("2026-09-06T08:00:00Z"),
            DateTimeOffset.Parse("2026-09-06T08:00:00Z"));

        return new(operation, plan, assignment);
    }
}
