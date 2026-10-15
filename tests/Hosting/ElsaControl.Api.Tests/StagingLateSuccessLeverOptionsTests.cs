using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class StagingLateSuccessLeverOptionsTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("40000000-0000-0000-0000-000000000044");
    private static readonly Guid SmokeOwnerWorkspaceId = Guid.Parse("40000000-0000-0000-0000-000000000099");

    [Fact]
    public void Disabled_options_refuse_allowlisted_workspaces()
    {
        var options = new StagingLateSuccessLeverOptions
        {
            Enabled = false,
            AllowedWorkspaceIds = [WorkspaceId.ToString("D")]
        };

        Assert.False(options.AllowsWorkspace(WorkspaceId));
    }

    [Fact]
    public void Enabled_options_accept_allowlisted_workspaces_only()
    {
        var options = new StagingLateSuccessLeverOptions
        {
            Enabled = true,
            AllowedWorkspaceIds = [WorkspaceId.ToString("D")]
        };

        Assert.True(options.AllowsWorkspace(WorkspaceId));
        Assert.False(options.AllowsWorkspace(Guid.NewGuid()));
        Assert.False(options.AllowsWorkspace(Guid.Empty));
    }

    [Fact]
    public async Task Validator_skips_shape_while_the_lever_is_off()
    {
        var validator = new StagingLateSuccessLeverConfigurationValidator(Options.Create(
            new StagingLateSuccessLeverOptions
            {
                Enabled = false,
                AllowedWorkspaceIds = ["not-a-guid"],
                WorkloadWait = TimeSpan.FromSeconds(1)
            }));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Validator_fails_closed_on_malformed_allowlist_and_wait_when_enabled()
    {
        var allowlist = new StagingLateSuccessLeverConfigurationValidator(Options.Create(
            new StagingLateSuccessLeverOptions
            {
                Enabled = true,
                AllowedWorkspaceIds = ["not-a-guid"]
            }));
        var wait = new StagingLateSuccessLeverConfigurationValidator(Options.Create(
            new StagingLateSuccessLeverOptions
            {
                Enabled = true,
                WorkloadWait = TimeSpan.FromSeconds(14)
            }));

        var allowlistError = await Assert.ThrowsAsync<InvalidOperationException>(() => allowlist.StartAsync(CancellationToken.None));
        var waitError = await Assert.ThrowsAsync<InvalidOperationException>(() => wait.StartAsync(CancellationToken.None));

        Assert.Contains("AllowedWorkspaceIds[0]", allowlistError.Message, StringComparison.Ordinal);
        Assert.Contains("WorkloadWait", waitError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_requires_pipeline_test_and_stripe_test_and_its_own_flag()
    {
        var options = new StagingLateSuccessLeverOptions
        {
            Enabled = true,
            AllowedWorkspaceIds = [WorkspaceId.ToString("D")]
        };
        var armed = StagingLateSuccessLeverFactory.Create(
            options,
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_test_harness", ExpectedMode = StripeBillingOptions.TestMode });
        var liveMode = StagingLateSuccessLeverFactory.Create(
            options,
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_test_harness", ExpectedMode = StripeBillingOptions.LiveMode });
        var liveKey = StagingLateSuccessLeverFactory.Create(
            options,
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_live_harness", ExpectedMode = StripeBillingOptions.TestMode });

        Assert.True(armed.AppliesTo(WorkspaceId));
        Assert.False(liveMode.AppliesTo(WorkspaceId));
        Assert.False(liveKey.AppliesTo(WorkspaceId));
    }

    [Fact]
    public void No_committed_appsettings_file_enables_the_lever()
    {
        var files = Directory.EnumerateFiles(FindRepositoryRoot(), "appsettings*.json", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build();
            Assert.False(
                configuration.GetValue<bool>($"{StagingLateSuccessLeverOptions.ConfigurationSection}:Enabled"),
                path);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ElsaControl.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
