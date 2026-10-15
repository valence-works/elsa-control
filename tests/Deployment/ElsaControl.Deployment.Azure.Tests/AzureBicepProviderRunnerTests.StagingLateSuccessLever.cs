using ElsaControl.Deployment.Azure;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed partial class AzureBicepProviderRunnerTests
{
    private static readonly Guid LateSuccessAllowedWorkspace = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid LateSuccessOtherWorkspace = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid LateSuccessSmokeWorkspace = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    [Fact]
    public void Late_success_options_reject_a_wait_under_fifteen_seconds_or_over_five_minutes()
    {
        var under = new StagingLateSuccessLeverOptions { Enabled = true, WorkloadWait = TimeSpan.FromSeconds(14) };
        var over = new StagingLateSuccessLeverOptions { Enabled = true, WorkloadWait = TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1) };
        var malformed = new StagingLateSuccessLeverOptions { Enabled = true, AllowedWorkspaceIds = ["not-a-guid"] };

        Assert.Contains(under.Validate(), error => error.Contains("WorkloadWait", StringComparison.Ordinal));
        Assert.Contains(over.Validate(), error => error.Contains("WorkloadWait", StringComparison.Ordinal));
        Assert.Contains(malformed.Validate(), error => error.Contains("AllowedWorkspaceIds[0]", StringComparison.Ordinal));
        Assert.Empty(new StagingLateSuccessLeverOptions { Enabled = true, WorkloadWait = TimeSpan.FromSeconds(15) }.Validate());
        Assert.Empty(new StagingLateSuccessLeverOptions { Enabled = true, WorkloadWait = TimeSpan.FromMinutes(5) }.Validate());
    }

    [Fact]
    public void Late_success_options_refuse_the_smoke_owner_workspace_even_when_allowlisted()
    {
        var options = new StagingLateSuccessLeverOptions
        {
            Enabled = true,
            AllowedWorkspaceIds = [LateSuccessSmokeWorkspace.ToString("D")],
            SmokeOwnerWorkspaceId = LateSuccessSmokeWorkspace.ToString("D")
        };

        Assert.True(options.IsSmokeOwnerWorkspace(LateSuccessSmokeWorkspace));
        Assert.False(options.AllowsWorkspace(LateSuccessSmokeWorkspace));
        Assert.False(ArmedLever(smoke: LateSuccessSmokeWorkspace).AppliesTo(LateSuccessSmokeWorkspace));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Late_success_override_is_not_applied_when_a_gate_signal_is_missing(
        bool enabled,
        bool pipelineTest,
        bool stripeTest)
    {
        var result = await RunLateSuccessWorkloadAsync(
            ArmedLever(enabled: enabled, pipelineTest: pipelineTest, stripeTest: stripeTest),
            LateSuccessAllowedWorkspace,
            "Running", "Running", "Succeeded");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
    }

    [Fact]
    public async Task Late_success_override_is_not_applied_for_a_workspace_that_is_not_allowlisted()
    {
        var result = await RunLateSuccessWorkloadAsync(
            ArmedLever(),
            LateSuccessOtherWorkspace,
            "Running", "Running", "Succeeded");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
    }

    [Fact]
    public async Task Late_success_override_is_not_applied_for_the_smoke_owner_workspace()
    {
        var result = await RunLateSuccessWorkloadAsync(
            ArmedLever(smoke: LateSuccessSmokeWorkspace),
            LateSuccessSmokeWorkspace,
            "Running", "Running", "Succeeded");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
    }

    [Fact]
    public async Task Billing_or_recovery_flags_never_arm_the_late_success_override()
    {
        var billingOrRecoveryOnly = new StagingLateSuccessLever(
            new StagingLateSuccessLeverOptions
            {
                Enabled = false,
                AllowedWorkspaceIds = [LateSuccessAllowedWorkspace.ToString("D")],
                WorkloadWait = TimeSpan.FromSeconds(30)
            },
            isPipelineTargetTest: true,
            isStripeTestMode: true);

        var result = await RunLateSuccessWorkloadAsync(
            billingOrRecoveryOnly,
            LateSuccessAllowedWorkspace,
            "Running", "Running", "Succeeded");

        Assert.False(billingOrRecoveryOnly.AppliesTo(LateSuccessAllowedWorkspace));
        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
    }

    [Fact]
    public async Task Allowlisted_workspace_parks_at_the_lever_wait_without_cancel_or_resubmit()
    {
        var (result, process) = await RunLateSuccessWorkloadDetailedAsync(
            ArmedLever(),
            LateSuccessAllowedWorkspace,
            "Running", "Running", "Succeeded");

        Assert.Equal(AzureProviderRunnerOutcome.Uncertain, result.Outcome);
        Assert.Equal(ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded, result.Code);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
        Assert.Equal(1, result.Diagnostics.Count(diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode));
        Assert.Single(process.Calls, IsDeploymentSubmission);
        Assert.DoesNotContain(process.Calls, call => call.Contains("cancel") || call.Contains("delete"));
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Equal(diagnostic.Code, diagnostic.Message);
            Assert.DoesNotContain(LateSuccessAllowedWorkspace.ToString("D"), diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task Non_allowlisted_workspace_waits_the_full_configured_budget_and_never_parks()
    {
        var (result, process) = await RunLateSuccessWorkloadDetailedAsync(
            ArmedLever(),
            LateSuccessOtherWorkspace,
            "Running", "Running", "Succeeded");

        Assert.Equal(AzureProviderRunnerOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded);
        Assert.Single(process.Calls, IsDeploymentSubmission);
    }

    [Fact]
    public async Task Foundation_wait_and_command_timeout_stay_unchanged_for_an_allowlisted_workspace()
    {
        var process = NewFoundationProcess();
        process.Deployment(FoundationOutputs(), "Running", "Running", "Succeeded");
        var originalTimeout = _fixture.Options.CommandTimeout;
        var originalFoundation = _fixture.Options.FoundationDeploymentTimeout;
        var lever = ArmedLever();
        var clock = new SteppingTimeProvider(TimeSpan.FromSeconds(20));
        var command = _fixture.Command(AzureProviderRunnerStep.Foundation) with
        {
            Context = _fixture.Context with { WorkspaceId = LateSuccessAllowedWorkspace }
        };

        var result = await new AzureBicepProviderRunner(
                _fixture.Options, _fixture.Scope, process, timeProvider: clock, lateSuccessLever: lever)
            .RunAsync(command);

        Assert.True(result.Outcome == AzureProviderRunnerOutcome.Completed, $"{result.Code}: {result.Message}");
        Assert.Equal(originalTimeout, _fixture.Options.CommandTimeout);
        Assert.Equal(originalFoundation, _fixture.Options.FoundationDeploymentTimeout);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == StagingLateSuccessLeverDefaults.AppliedCode);
        Assert.Equal(_fixture.Options.WorkloadDeploymentTimeout,
            lever.WorkloadWaitLimit(_fixture.Options.WorkloadDeploymentTimeout, LateSuccessOtherWorkspace));
        Assert.Equal(TimeSpan.FromSeconds(30),
            lever.WorkloadWaitLimit(_fixture.Options.WorkloadDeploymentTimeout, LateSuccessAllowedWorkspace));
    }

    [Theory]
    [InlineData(AzureProviderRunnerStep.AcrPull)]
    [InlineData(AzureProviderRunnerStep.SeedSecrets)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCreate)]
    [InlineData(AzureProviderRunnerStep.SqlBootstrapScript)]
    [InlineData(AzureProviderRunnerStep.SqlFirewallCleanup)]
    [InlineData(AzureProviderRunnerStep.Health)]
    [InlineData(AzureProviderRunnerStep.Promotion)]
    [InlineData(AzureProviderRunnerStep.Cleanup)]
    public void Late_success_wait_limit_is_unused_for_non_workload_steps(AzureProviderRunnerStep step)
    {
        var lever = ArmedLever();
        Assert.NotEqual(AzureProviderRunnerStep.Workload, step);
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            lever.WorkloadWaitLimit(_fixture.Options.WorkloadDeploymentTimeout, LateSuccessAllowedWorkspace));
        Assert.Equal(_fixture.Options.CommandTimeout, _fixture.Options.CommandTimeout);
    }

    private static StagingLateSuccessLever ArmedLever(
        bool enabled = true,
        bool pipelineTest = true,
        bool stripeTest = true,
        Guid? smoke = null,
        TimeSpan? wait = null) =>
        new(
            new StagingLateSuccessLeverOptions
            {
                Enabled = enabled,
                AllowedWorkspaceIds = [LateSuccessAllowedWorkspace.ToString("D"), LateSuccessSmokeWorkspace.ToString("D")],
                SmokeOwnerWorkspaceId = smoke?.ToString("D"),
                WorkloadWait = wait ?? TimeSpan.FromSeconds(30)
            },
            pipelineTest,
            stripeTest);

    private async Task<AzureProviderRunnerResult> RunLateSuccessWorkloadAsync(
        StagingLateSuccessLever lever,
        Guid workspaceId,
        params string[] states)
    {
        var (result, _) = await RunLateSuccessWorkloadDetailedAsync(lever, workspaceId, states);
        return result;
    }

    private async Task<(AzureProviderRunnerResult Result, FakeCommandProcess Process)> RunLateSuccessWorkloadDetailedAsync(
        StagingLateSuccessLever lever,
        Guid workspaceId,
        params string[] states)
    {
        var process = new FakeCommandProcess();
        process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        process.Success(args => args.Contains("resource") && args.Contains("list"), "0");
        process.Deployment(WorkloadOutputs(), states);
        process.Success(args => args.Contains("sql") && args.Contains("server") && args.Contains("list"), "1");
        process.Success(args => args.Contains("ad-admin") && args.Contains("list"), "[{\"login\":\"proof-bootstrap\",\"sid\":\"11111111-1111-1111-1111-111111111111\"}]");
        process.Success(args => args.Contains("ad-only-auth") && args.Contains("enable"));
        var clock = new SteppingTimeProvider(TimeSpan.FromSeconds(20));
        var command = _fixture.Command(AzureProviderRunnerStep.Workload, RegistryReadyResources()) with
        {
            Context = _fixture.Context with { WorkspaceId = workspaceId }
        };
        var result = await new AzureBicepProviderRunner(
                _fixture.Options, _fixture.Scope, process, timeProvider: clock, lateSuccessLever: lever)
            .RunAsync(command);
        return (result, process);
    }
}
