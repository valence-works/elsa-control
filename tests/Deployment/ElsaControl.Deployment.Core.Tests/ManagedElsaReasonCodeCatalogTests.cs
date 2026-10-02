using ElsaControl.Deployment.Core.Instances;
using Xunit;

namespace ElsaControl.Deployment.Core.Tests;

public sealed class ManagedElsaReasonCodeCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_is_one_table_keyed_by_code()
    {
        Assert.NotEmpty(ManagedElsaReasonCodeCatalog.ByCode);
        Assert.Equal(
            ManagedElsaReasonCodeCatalog.ByCode.Count,
            ManagedElsaReasonCodeCatalog.ByCode.Values.Select(entry => entry.Code).Distinct(StringComparer.Ordinal).Count());

        foreach (var (key, entry) in ManagedElsaReasonCodeCatalog.ByCode)
        {
            Assert.Equal(key, entry.Code);
            Assert.True(ManagedElsaReasonCodeCatalog.TryGet(entry.Code, out var found));
            Assert.Same(entry, found);
        }
    }

    [Fact]
    public void Submission_uncertain_is_the_only_age_bounded_row()
    {
        var uncertain = Assert.Single(
            ManagedElsaReasonCodeCatalog.ByCode.Values,
            entry => entry.UncertainHealthyWindow is not null);

        Assert.Equal(ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, uncertain.Code);
        Assert.True(uncertain.RequiresHuman);
        Assert.Equal(ManagedElsaReasonCodeCatalog.SubmissionUncertainHealthyWindow, uncertain.UncertainHealthyWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), uncertain.UncertainHealthyWindow);
    }

    [Theory]
    [InlineData(ManagedElsaReasonCodeCatalog.ProviderSubmissionAccepted)]
    [InlineData(ElsaInstanceProviderReconciliationService.InProgressCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.HealthUnknownCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.UnavailableCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.UnknownCode)]
    public void Catalogued_non_human_reasons_do_not_require_a_human(string reason)
    {
        Assert.True(ManagedElsaReasonCodeCatalog.TryGet(reason, out var entry));
        Assert.False(entry!.RequiresHuman);
        Assert.Null(entry.UncertainHealthyWindow);
        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(reason, null, Now, Now));
    }

    [Theory]
    [InlineData(ElsaInstanceProviderReconciliationService.AutoResumeExhaustedCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.FailedCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.HealthFailedCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.AmbiguousCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.CorrelationMismatchCode)]
    [InlineData(ElsaInstanceProviderReconciliationService.RetrySafeCode)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentFailed)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded)]
    [InlineData(ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled)]
    public void Catalogued_human_required_reasons_require_a_human(string reason)
    {
        Assert.True(ManagedElsaReasonCodeCatalog.TryGet(reason, out var entry));
        Assert.True(entry!.RequiresHuman);
        Assert.Null(entry.UncertainHealthyWindow);
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(reason, null, Now, Now));
    }

    [Fact]
    public void Submission_uncertain_requires_a_human_only_after_the_named_window()
    {
        var origin = Now;
        var inside = origin + ManagedElsaReasonCodeCatalog.SubmissionUncertainHealthyWindow - TimeSpan.FromTicks(1);
        var atBound = origin + ManagedElsaReasonCodeCatalog.SubmissionUncertainHealthyWindow;

        Assert.False(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, null, origin, inside));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, null, origin, atBound));
    }

    [Fact]
    public void Unknown_or_missing_reason_fails_safe_to_human_required()
    {
        Assert.False(ManagedElsaReasonCodeCatalog.TryGet("provider.recovery.never-seen", out _));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(null, null, Now, Now));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman("provider.recovery.never-seen", null, Now, Now));
        Assert.True(ManagedElsaReasonCodeCatalog.RequiresHuman(
            ManagedElsaReasonCodeCatalog.ProviderSubmissionUncertain, null, parkedAt: null, Now));
    }
}
