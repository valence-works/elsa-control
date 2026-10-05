namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureNamedDeploymentFreshnessTests
{
    [Fact]
    public void Freshness_accepts_a_timestamp_at_or_after_the_step_start_minus_skew()
    {
        var started = DateTimeOffset.Parse("2026-09-24T00:33:00Z");

        Assert.True(AzureNamedDeploymentFreshness.IsFresh(started, started));
        Assert.True(AzureNamedDeploymentFreshness.IsFresh(started - TimeSpan.FromMinutes(1), started));
        Assert.False(AzureNamedDeploymentFreshness.IsFresh(started - TimeSpan.FromMinutes(1) - TimeSpan.FromSeconds(1), started));
        Assert.False(AzureNamedDeploymentFreshness.IsFresh(null, started));
    }

    [Fact]
    public void Parked_row_freshness_falls_back_to_status_changed_at_before_updated_at()
    {
        var armSucceeded = DateTimeOffset.Parse("2026-09-24T00:48:56Z");
        var statusChangedAt = DateTimeOffset.Parse("2026-09-24T00:48:18Z");
        var updatedAt = DateTimeOffset.Parse("2026-09-27T21:29:00Z");

        Assert.Equal(statusChangedAt, AzureNamedDeploymentFreshness.FreshnessBaseline(null, statusChangedAt, updatedAt));
        Assert.Equal(updatedAt, AzureNamedDeploymentFreshness.FreshnessBaseline(null, null, updatedAt));
        Assert.True(AzureNamedDeploymentFreshness.IsFresh(
            armSucceeded, AzureNamedDeploymentFreshness.FreshnessBaseline(null, statusChangedAt, updatedAt)));
        Assert.True(AzureNamedDeploymentFreshness.IsFresh(
            armSucceeded,
            AzureNamedDeploymentFreshness.FreshnessBaseline(null, armSucceeded + AzureNamedDeploymentFreshness.TimestampSkew, updatedAt)));
        Assert.False(AzureNamedDeploymentFreshness.IsFresh(
            armSucceeded,
            AzureNamedDeploymentFreshness.FreshnessBaseline(
                null, armSucceeded + AzureNamedDeploymentFreshness.TimestampSkew + TimeSpan.FromSeconds(1), updatedAt)));
    }

    [Fact]
    public void Arm_read_backoff_starts_at_one_minute_and_caps_at_five()
    {
        Assert.Equal(60, AzureNamedDeploymentFreshness.NextBackoffSeconds(0));
        Assert.Equal(120, AzureNamedDeploymentFreshness.NextBackoffSeconds(60));
        Assert.Equal(240, AzureNamedDeploymentFreshness.NextBackoffSeconds(120));
        Assert.Equal(300, AzureNamedDeploymentFreshness.NextBackoffSeconds(240));
        Assert.Equal(300, AzureNamedDeploymentFreshness.NextBackoffSeconds(300));
    }

    [Fact]
    public void Auto_resume_backoff_is_exponential_from_the_persisted_count()
    {
        Assert.Equal(60, AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(0));
        Assert.Equal(120, AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(1));
        Assert.Equal(240, AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(2));
        Assert.Equal(300, AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(3));
        Assert.Equal(300, AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(8));
        Assert.Equal(60, AzureNamedDeploymentFreshness.BackoffSecondsForAutoResumeCount(-1));
    }

    [Fact]
    public void Arm_read_is_due_when_no_prior_observation_or_backoff_elapsed()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");

        Assert.True(AzureNamedDeploymentFreshness.IsArmReadDue(now, null, 0));
        Assert.False(AzureNamedDeploymentFreshness.IsArmReadDue(now, now.AddSeconds(-30), 60));
        Assert.True(AzureNamedDeploymentFreshness.IsArmReadDue(now, now.AddSeconds(-60), 60));
    }

    [Fact]
    public void Operator_forced_arm_read_shares_the_sixty_second_floor()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");

        Assert.True(AzureNamedDeploymentFreshness.IsOperatorForcedArmReadDue(now, null));
        Assert.False(AzureNamedDeploymentFreshness.IsOperatorForcedArmReadDue(now, now.AddSeconds(-59)));
        Assert.True(AzureNamedDeploymentFreshness.IsOperatorForcedArmReadDue(now, now.AddSeconds(-60)));
    }

    [Fact]
    public void Confirmed_completed_resume_requires_the_attempted_step()
    {
        Assert.True(AzureNamedDeploymentFreshness.IsConfirmedCompletedResume(
            AzureProviderRunnerStep.Workload, AzureProviderRunnerStep.Workload));
        Assert.False(AzureNamedDeploymentFreshness.IsConfirmedCompletedResume(
            AzureProviderRunnerStep.SeedSecrets, AzureProviderRunnerStep.AcrPull));
    }
}
