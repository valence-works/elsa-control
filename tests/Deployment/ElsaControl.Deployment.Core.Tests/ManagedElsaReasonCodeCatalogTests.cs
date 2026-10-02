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
        }

        Assert.Equal(ManagedElsaReasonCodeCatalog.DefinedCodes.Count, ManagedElsaReasonCodeCatalog.ByCode.Count);
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
    [InlineData(ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderReconciliationAmbiguous, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderIdentityBindingMissing, ManagedElsaReasonClass.NeedsPerson)]
    [InlineData(ManagedElsaReasonCodeCatalog.StagingLeverRecoveryRequired, ManagedElsaReasonClass.NeedsPerson)]
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
}
