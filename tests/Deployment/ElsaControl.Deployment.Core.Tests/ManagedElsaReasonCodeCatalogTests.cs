using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedElsaReasonCodeCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_enumerates_every_defined_lifecycle_and_park_constant()
    {
        Assert.NotEmpty(ManagedElsaReasonCodeCatalog.DefinedCodes);
        Assert.Equal(
            ManagedElsaReasonCodeCatalog.DefinedCodes.Count,
            ManagedElsaReasonCodeCatalog.DefinedCodes.Distinct(StringComparer.Ordinal).Count());

        foreach (var code in ManagedElsaReasonCodeCatalog.DefinedCodes)
        {
            Assert.True(ManagedElsaReasonCodeCatalog.TryGet(code, out var entry), code);
            Assert.Equal(code, entry!.Code);
            Assert.Equal(ManagedElsaReasonCodeCatalog.Classify(code), entry.Class);
            Assert.True(
                ManagedElsaReasonLiteralScanner.IsCataloguedFamily(code),
                "Declared prefixes must cover catalog family " + code);
            Assert.True(
                ManagedElsaReasonLiteralScanner.ProductionReasonLiteral.IsMatch("\"" + code + "\""),
                "Scanner must cover catalog family " + code);
        }

        Assert.Contains("deletion.", ManagedElsaReasonLiteralScanner.ReasonFamilyPrefixes, StringComparer.Ordinal);
        Assert.Contains("assignment.rebind.", ManagedElsaReasonLiteralScanner.ReasonFamilyPrefixes, StringComparer.Ordinal);
        Assert.Equal(ManagedElsaReasonCodeCatalog.DefinedCodes.Count, ManagedElsaReasonCodeCatalog.ByCode.Count);
    }

    [Fact]
    public void Catalog_contains_every_reason_literal_production_writes()
    {
        var missing = ManagedElsaReasonLiteralScanner.FindUncataloguedInProduction(FindRepoRoot());
        Assert.True(missing.Count == 0, "Uncatalogued production reason literals:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Completeness_scan_fails_on_a_planted_uncatalogued_literal()
    {
        const string plantedRecovery = "azure.recovery.planted-uncatalogued";
        const string plantedDeletion = "deletion.planted-uncatalogued";
        const string plantedRebind = "assignment.rebind.planted-uncatalogued";
        Assert.False(ManagedElsaReasonCodeCatalog.TryGet(plantedRecovery, out _));
        Assert.False(ManagedElsaReasonCodeCatalog.TryGet(plantedDeletion, out _));
        Assert.False(ManagedElsaReasonCodeCatalog.TryGet(plantedRebind, out _));
        Assert.Equal(plantedRecovery, Assert.Single(ManagedElsaReasonLiteralScanner.FindUncatalogued(
            $"operation.ReconciliationDiagnosticCode = \"{plantedRecovery}\";")));
        Assert.Equal(plantedDeletion, Assert.Single(ManagedElsaReasonLiteralScanner.FindUncatalogued(
            $"operation.ReconciliationDiagnosticCode = \"{plantedDeletion}\";")));
        Assert.Equal(plantedRebind, Assert.Single(ManagedElsaReasonLiteralScanner.FindUncatalogued(
            $"operation.ReconciliationDiagnosticCode = \"{plantedRebind}\";")));
        Assert.Empty(ManagedElsaReasonLiteralScanner.FindUncatalogued(
            $"operation.ReconciliationDiagnosticCode = \"{ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted}\";"));
    }

    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted, ManagedElsaReasonClass.HealthyHandOff)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationInProgress, ManagedElsaReasonClass.HealthyHandOff)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationConverged, ManagedElsaReasonClass.HealthyHandOff)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationUnknown, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationUnavailable, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationHealthUnknown, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationRetrySafe, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeClaimConflict, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentFailed, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeAccepted, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadInProgress, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadObserved, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzurePromotionUncertain, ManagedElsaReasonClass.AutoResuming)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryFoundationOutputsInvalid, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadOutputsInvalid, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoverySeedSecretsPartial, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationAmbiguous, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderIdentityBindingMissing, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.StagingLeverRecoveryRequired, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionLocalAbsent, ManagedElsaReasonClass.HealthyHandOff)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderConfirmedAbsent, ManagedElsaReasonClass.HealthyHandOff)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderUnavailable, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionAzureProviderUnavailable, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderCleanupPending, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderUncertain, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionRecoveryAuthorityUnavailable, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionRecoveryPlanUnavailable, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionCorrelationInvalid, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderCleanupFailed, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionProviderAmbiguous, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.DeletionPredecessorRecoverySuperseded, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.AssignmentRebindOperationsInFlight, ManagedElsaReasonClass.Temporary)]
    [InlineData(ManagedElsaReasonCodeCatalog.AssignmentRebindPlacementMismatch, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.AssignmentRebindAmbiguous, ManagedElsaReasonClass.NeedsPerson)]
    public void Catalogued_codes_have_the_architect_class(string code, ManagedElsaReasonClass expected)
    {
        Assert.Equal(expected, ManagedElsaReasonCodeCatalog.Classify(code));
    }

    [Fact]
    public void Healthy_hand_off_never_requires_a_human()
    {
        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted, Now, Now + TimeSpan.FromHours(1)));
        var clock = ManagedElsaReasonClock.Advance(
            null, ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted, null, null, Now, restartClock: false);
        Assert.Equal(Now, clock.ReasonEnteredAt);
        Assert.Null(clock.RequiresHumanAt);
    }

    [Fact]
    public void Auto_resuming_and_temporary_require_a_human_only_after_the_named_window()
    {
        var inside = Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter - TimeSpan.FromTicks(1);
        var atBound = Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter;

        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.AzureDeploymentFailed, Now, inside));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.AzureDeploymentFailed, Now, atBound));
        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, Now, inside));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, Now, atBound));
        Assert.Equal(TimeSpan.FromMinutes(10), ManagedElsaReasonCodeCatalog.HumanRequiredAfter);
        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, null, Now));
        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable, Now, inside));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable, Now, atBound));
    }

    [Fact]
    public void Select_current_reason_prefers_operation_fields_and_keeps_uncertain_owned()
    {
        Assert.Equal(
            ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadInProgress,
            ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                ManagedElsaReasonCodeCatalog.ProviderReconciliationRetrySafe,
                ManagedElsaReasonCodeCatalog.AzureRecoveryWorkloadInProgress,
                ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted));
        Assert.Equal(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
            ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
                ManagedElsaReasonCodeCatalog.ProviderReconciliationInProgress,
                ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted));
        Assert.Equal(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted,
            ManagedElsaReasonCodeCatalog.SelectCurrentReason(
                null, null, ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted));
        Assert.Null(ManagedElsaReasonCodeCatalog.SelectCurrentReason(null, null, "Worker heartbeat became stale."));
    }

    [Fact]
    public void Exhausted_and_unknown_codes_require_a_human_immediately()
    {
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, Now, Now));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman("provider.recovery.never-seen", Now, Now));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(null, null, Now, Now));
        var clock = ManagedElsaReasonClock.Advance(
            null, "provider.recovery.never-seen", null, null, Now, restartClock: false);
        Assert.Equal(Now, clock.RequiresHumanAt);
    }

    [Fact]
    public void Clock_does_not_reset_when_the_class_stays_the_same()
    {
        var first = ManagedElsaReasonClock.Advance(
            null, ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, null, null, Now, restartClock: false);
        var switched = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
            ManagedElsaReasonCodeCatalog.ProviderReconciliationUnknown,
            first.ReasonEnteredAt,
            first.RequiresHumanAt,
            Now.AddMinutes(5),
            restartClock: false);
        var atBound = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.ProviderReconciliationUnknown,
            ManagedElsaReasonCodeCatalog.ProviderReconciliationUnavailable,
            switched.ReasonEnteredAt,
            switched.RequiresHumanAt,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter,
            restartClock: false);

        Assert.Equal(Now, switched.ReasonEnteredAt);
        Assert.Null(switched.RequiresHumanAt);
        Assert.Equal(Now, atBound.ReasonEnteredAt);
        Assert.Equal(Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter, atBound.RequiresHumanAt);
    }

    [Fact]
    public void Missing_reason_entered_at_starts_the_clock_now()
    {
        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, null, Now.AddMinutes(30)));
        var stamped = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain,
            reasonEnteredAt: null,
            requiresHumanAt: null,
            Now.AddMinutes(3),
            restartClock: false);

        Assert.Equal(Now.AddMinutes(3), stamped.ReasonEnteredAt);
        Assert.Null(stamped.RequiresHumanAt);
    }

    [Fact]
    public void Flapping_azure_observations_keep_the_deferred_clock()
    {
        var first = ManagedElsaReasonClock.Advance(
            null, ManagedElsaReasonCodeCatalog.AzureDeploymentFailed, null, null, Now, restartClock: false);
        var flapped = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.AzureDeploymentFailed,
            ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable,
            first.ReasonEnteredAt,
            first.RequiresHumanAt,
            Now.AddMinutes(5),
            restartClock: false);
        var back = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.AzureRecoveryObservationUnavailable,
            ManagedElsaReasonCodeCatalog.AzureRecoveryObservationInProgress,
            flapped.ReasonEnteredAt,
            flapped.RequiresHumanAt,
            Now.AddMinutes(8),
            restartClock: false);
        var atBound = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.AzureRecoveryObservationInProgress,
            ManagedElsaReasonCodeCatalog.AzureDeploymentFailed,
            back.ReasonEnteredAt,
            back.RequiresHumanAt,
            Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter,
            restartClock: false);

        Assert.Equal(Now, first.ReasonEnteredAt);
        Assert.Null(first.RequiresHumanAt);
        Assert.Equal(Now, flapped.ReasonEnteredAt);
        Assert.Null(flapped.RequiresHumanAt);
        Assert.Equal(Now, back.ReasonEnteredAt);
        Assert.Null(back.RequiresHumanAt);
        Assert.Equal(Now, atBound.ReasonEnteredAt);
        Assert.Equal(Now + ManagedElsaReasonCodeCatalog.HumanRequiredAfter, atBound.RequiresHumanAt);
    }

    [Fact]
    public void Resume_or_recover_restarts_the_clock_and_clears_the_flag()
    {
        var parked = ManagedElsaReasonClock.Advance(
            null, ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, null, null, Now, restartClock: false);
        var recovered = ManagedElsaReasonClock.Advance(
            ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted,
            nextCode: null,
            parked.ReasonEnteredAt,
            parked.RequiresHumanAt,
            Now.AddMinutes(3),
            restartClock: true);

        Assert.Equal(Now, parked.RequiresHumanAt);
        Assert.Equal(Now.AddMinutes(3), recovered.ReasonEnteredAt);
        Assert.Null(recovered.RequiresHumanAt);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
