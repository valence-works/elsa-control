using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedElsaHumanRequiredParkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(ManagedElsaHumanRequiredPark.ProviderSubmissionAccepted)]
    [InlineData(ElsaInstanceProviderReconciliationService.InProgressCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.HealthUnknownCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.UnavailableCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.UnknownCode)]
    public void Healthy_and_transient_reasons_do_not_require_a_human(string reason)
    {
        Assert.False(ManagedElsaHumanRequiredPark.RequiresHuman(reason, null, Now, Now));
        Assert.NotEqual(
            ManagedElsaRecoveryParkKind.HumanRequired,
            ManagedElsaHumanRequiredPark.Classify(reason, null, Now, Now));
    }

    [Theory]
    [InlineData(ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.FailedCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.HealthFailedCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.AmbiguousCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.CorrelationMismatchCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.RetrySafeCode)]
    [InlineData(ManagedElsaHumanRequiredPark.AzureDeploymentFailed)]
    [InlineData(ManagedElsaHumanRequiredPark.AzureDeploymentWaitExceeded)]
    [InlineData(ManagedElsaHumanRequiredPark.AzureDeploymentCanceled)]
    public void Human_required_reasons_require_a_human(string reason)
    {
        Assert.True(ManagedElsaHumanRequiredPark.RequiresHuman(reason, null, Now, Now));
        Assert.Equal(
            ManagedElsaRecoveryParkKind.HumanRequired,
            ManagedElsaHumanRequiredPark.Classify(reason, null, Now, Now));
        Assert.Contains(reason, (IReadOnlySet<string>)ManagedElsaHumanRequiredPark.HumanRequiredReasons);
    }

    [Fact]
    public void Submission_uncertain_requires_a_human_only_after_the_named_window()
    {
        var origin = Now;
        var inside = origin + ManagedElsaHumanRequiredPark.SubmissionUncertainHealthyWindow - TimeSpan.FromTicks(1);
        var atBound = origin + ManagedElsaHumanRequiredPark.SubmissionUncertainHealthyWindow;

        Assert.False(ManagedElsaHumanRequiredPark.RequiresHuman(
            ManagedElsaHumanRequiredPark.ProviderSubmissionUncertain, null, origin, inside));
        Assert.True(ManagedElsaHumanRequiredPark.RequiresHuman(
            ManagedElsaHumanRequiredPark.ProviderSubmissionUncertain, null, origin, atBound));
        Assert.Equal(TimeSpan.FromMinutes(10), ManagedElsaHumanRequiredPark.SubmissionUncertainHealthyWindow);
    }

    [Fact]
    public void Unknown_or_missing_reason_fails_safe_to_human_required()
    {
        Assert.True(ManagedElsaHumanRequiredPark.RequiresHuman(null, null, Now, Now));
        Assert.True(ManagedElsaHumanRequiredPark.RequiresHuman("provider.recovery.never-seen", null, Now, Now));
        Assert.True(ManagedElsaHumanRequiredPark.RequiresHuman(
            ManagedElsaHumanRequiredPark.ProviderSubmissionUncertain, null, parkedAt: null, Now));
    }
}
