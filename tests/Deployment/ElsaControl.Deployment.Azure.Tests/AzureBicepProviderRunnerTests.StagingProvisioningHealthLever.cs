using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed partial class AzureBicepProviderRunnerTests
{
    private static readonly Guid HealthLeverAllowedInstance = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid HealthLeverOtherInstance = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid HealthLeverSmokeInstance = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    [Fact]
    public void Provisioning_health_options_refuse_the_smoke_owner_instance_even_when_allowlisted()
    {
        var options = new StagingProvisioningHealthLeverOptions
        {
            Enabled = true,
            AllowedInstanceIds = [HealthLeverSmokeInstance.ToString("D")],
            SmokeOwnerInstanceId = HealthLeverSmokeInstance.ToString("D")
        };

        Assert.True(options.IsSmokeOwnerInstance(HealthLeverSmokeInstance));
        Assert.False(options.AllowsInstance(HealthLeverSmokeInstance));
        Assert.False(ArmedHealthLever(smoke: HealthLeverSmokeInstance).AppliesTo(HealthLeverSmokeInstance));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Provisioning_health_inject_is_not_applied_when_a_gate_signal_is_missing(
        bool enabled,
        bool stripeTest)
    {
        var lever = ArmedHealthLever(enabled: enabled, stripeTest: stripeTest);
        lever.Arm(HealthLeverAllowedInstance, StagingProvisioningHealthFailureMode.CrashLoop);

        var result = await RunHealthWithLeverAsync(lever, HealthLeverAllowedInstance, revisionHealth: "Healthy");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingProvisioningHealthLeverDefaults.AppliedCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop);
    }

    [Fact]
    public async Task Provisioning_health_inject_is_not_applied_for_an_instance_that_is_not_allowlisted()
    {
        var lever = ArmedHealthLever();
        lever.Arm(HealthLeverAllowedInstance, StagingProvisioningHealthFailureMode.CrashLoop);

        var result = await RunHealthWithLeverAsync(lever, HealthLeverOtherInstance, revisionHealth: "Healthy");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingProvisioningHealthLeverDefaults.AppliedCode);
    }

    [Fact]
    public async Task Billing_or_recovery_or_late_success_flags_never_arm_the_health_inject()
    {
        var siblingOnly = new StagingProvisioningHealthLever(
            new StagingProvisioningHealthLeverOptions
            {
                Enabled = false,
                AllowedInstanceIds = [HealthLeverAllowedInstance.ToString("D")]
            },
            isStripeTestMode: true);
        siblingOnly.Arm(HealthLeverAllowedInstance, StagingProvisioningHealthFailureMode.CrashLoop);

        var result = await RunHealthWithLeverAsync(siblingOnly, HealthLeverAllowedInstance, revisionHealth: "Healthy");

        Assert.False(siblingOnly.AppliesTo(HealthLeverAllowedInstance));
        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingProvisioningHealthLeverDefaults.AppliedCode);
    }

    [Fact]
    public async Task Armed_crash_loop_fails_health_without_replica_observation()
    {
        var lever = ArmedHealthLever();
        Assert.Equal(
            StagingProvisioningHealthLeverOutcome.Armed,
            lever.Arm(HealthLeverAllowedInstance, StagingProvisioningHealthFailureMode.CrashLoop));

        var (result, process) = await RunHealthWithLeverDetailedAsync(
            lever, HealthLeverAllowedInstance, revisionHealth: "Healthy");

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal(AzureProviderHealth.Failed, result.Health);
        Assert.Equal(ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == ManagedElsaProvisioningFailureReason.AzureHealthCrashLoop);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == StagingProvisioningHealthLeverDefaults.AppliedCode);
        Assert.DoesNotContain(process.Calls, call => call.Contains("replica"));
        Assert.DoesNotContain("subscriptions/", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(diagnostic.Code, diagnostic.Message);
            Assert.DoesNotContain(HealthLeverAllowedInstance.ToString("D"), diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Armed_health_timeout_fails_health_without_waiting_for_the_observation_window()
    {
        var lever = ArmedHealthLever();
        Assert.Equal(
            StagingProvisioningHealthLeverOutcome.Armed,
            lever.Arm(HealthLeverAllowedInstance, StagingProvisioningHealthFailureMode.Timeout));

        var (result, process) = await RunHealthWithLeverDetailedAsync(
            lever, HealthLeverAllowedInstance, revisionHealth: "Healthy");

        Assert.Equal(AzureProviderRunnerOutcome.Failed, result.Outcome);
        Assert.Equal(ManagedElsaProvisioningFailureReason.AzureHealthTimeout, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == ManagedElsaProvisioningFailureReason.AzureHealthTimeout);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == StagingProvisioningHealthLeverDefaults.AppliedCode);
        Assert.DoesNotContain(process.Calls, call => call.Contains("replica"));
    }

    [Fact]
    public async Task Reset_clears_the_inject_so_a_healthy_revision_can_complete()
    {
        var lever = ArmedHealthLever();
        lever.Arm(HealthLeverAllowedInstance, StagingProvisioningHealthFailureMode.CrashLoop);
        Assert.Equal(StagingProvisioningHealthLeverOutcome.Reset, lever.Reset(HealthLeverAllowedInstance));

        var (result, process) = await RunHealthWithLeverDetailedAsync(
            lever, HealthLeverAllowedInstance, revisionHealth: "Healthy");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.Equal(AzureProviderHealth.Healthy, result.Health);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingProvisioningHealthLeverDefaults.AppliedCode);
        Assert.DoesNotContain(process.Calls, call => call.Contains("replica"));
    }

    private static StagingProvisioningHealthLever ArmedHealthLever(
        bool enabled = true,
        bool stripeTest = true,
        Guid? smoke = null) =>
        new(
            new StagingProvisioningHealthLeverOptions
            {
                Enabled = enabled,
                AllowedInstanceIds =
                [
                    HealthLeverAllowedInstance.ToString("D"),
                    HealthLeverSmokeInstance.ToString("D")
                ],
                SmokeOwnerInstanceId = smoke?.ToString("D")
            },
            isStripeTestMode: stripeTest);

    private async Task<AzureProviderRunnerResult> RunHealthWithLeverAsync(
        StagingProvisioningHealthLever lever,
        Guid instanceId,
        string revisionHealth)
    {
        var (result, _) = await RunHealthWithLeverDetailedAsync(lever, instanceId, revisionHealth);
        return result;
    }

    private async Task<(AzureProviderRunnerResult Result, FakeCommandProcess Process)> RunHealthWithLeverDetailedAsync(
        StagingProvisioningHealthLever lever,
        Guid instanceId,
        string revisionHealth)
    {
        var process = new FakeCommandProcess();
        process.Success(
            args => args.Contains("containerapp") && args.Contains("show") &&
                    args.Any(x => x.Contains("fqdn", StringComparison.Ordinal)),
            "proof-app.hash.azurecontainerapps.io");
        process.Success(args => args.Contains("containerapp") && args.Contains("revision") && args.Contains("show"), revisionHealth);
        process.Success(IsReplicaList, """[{"runningState":"Running","restartCounts":[0]}]""");
        var command = _fixture.Command(AzureProviderRunnerStep.Health, HealthResources()) with
        {
            Context = _fixture.Context with { InstanceId = instanceId }
        };

        var result = await new AzureBicepProviderRunner(
                _fixture.Options, _fixture.Scope, process, provisioningHealthLever: lever)
            .RunAsync(command);
        return (result, process);
    }
}
