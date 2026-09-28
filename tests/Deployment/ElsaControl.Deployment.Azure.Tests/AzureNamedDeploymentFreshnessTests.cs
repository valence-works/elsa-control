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
    public void Plan_fingerprint_rejects_a_different_sha256_and_accepts_bicep_unique_string()
    {
        var plan = new string('a', 64);

        Assert.True(AzureNamedDeploymentFreshness.MatchesPlanFingerprint(plan, plan));
        Assert.True(AzureNamedDeploymentFreshness.MatchesPlanFingerprint("abc123uniquestr", plan));
        Assert.False(AzureNamedDeploymentFreshness.MatchesPlanFingerprint(new string('f', 64), plan));
        Assert.False(AzureNamedDeploymentFreshness.MatchesPlanFingerprint(null, plan));
        Assert.False(AzureNamedDeploymentFreshness.MatchesPlanFingerprint("", plan));
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
    public void Arm_read_is_due_when_no_prior_observation_or_backoff_elapsed()
    {
        var now = DateTimeOffset.Parse("2026-09-24T00:48:18Z");

        Assert.True(AzureNamedDeploymentFreshness.IsArmReadDue(now, null, 0));
        Assert.False(AzureNamedDeploymentFreshness.IsArmReadDue(now, now.AddSeconds(-30), 60));
        Assert.True(AzureNamedDeploymentFreshness.IsArmReadDue(now, now.AddSeconds(-60), 60));
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
