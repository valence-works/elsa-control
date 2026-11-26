using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Billing.Stripe;
using ElsaControl.Deployment.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class StagingProvisioningHealthLeverOptionsTests
{
    private static readonly Guid InstanceId = Guid.Parse("50000000-0000-0000-0000-000000000055");
    private static readonly Guid SmokeOwnerInstanceId = Guid.Parse("50000000-0000-0000-0000-000000000099");

    [Fact]
    public void Disabled_options_refuse_allowlisted_instances()
    {
        var options = new StagingProvisioningHealthLeverOptions
        {
            Enabled = false,
            AllowedInstanceIds = [InstanceId.ToString("D")]
        };

        Assert.False(options.AllowsInstance(InstanceId));
    }

    [Fact]
    public void Enabled_options_accept_allowlisted_instances_only()
    {
        var options = new StagingProvisioningHealthLeverOptions
        {
            Enabled = true,
            AllowedInstanceIds = [InstanceId.ToString("D")]
        };

        Assert.True(options.AllowsInstance(InstanceId));
        Assert.False(options.AllowsInstance(Guid.NewGuid()));
        Assert.False(options.AllowsInstance(Guid.Empty));
    }

    [Fact]
    public void Smoke_owner_instance_is_refused_even_when_allowlisted()
    {
        var options = new StagingProvisioningHealthLeverOptions
        {
            Enabled = true,
            AllowedInstanceIds = [SmokeOwnerInstanceId.ToString("D")],
            SmokeOwnerInstanceId = SmokeOwnerInstanceId.ToString("D")
        };

        Assert.True(options.IsSmokeOwnerInstance(SmokeOwnerInstanceId));
        Assert.False(options.AllowsInstance(SmokeOwnerInstanceId));
    }

    [Fact]
    public async Task Validator_skips_allowlist_shape_while_the_lever_is_off()
    {
        var validator = CreateValidator(
            new StagingProvisioningHealthLeverOptions
            {
                Enabled = false,
                AllowedInstanceIds = ["not-a-guid"]
            },
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_live_harness" });

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Validator_fails_closed_on_malformed_allowlist_ids_when_enabled()
    {
        var validator = CreateValidator(
            new StagingProvisioningHealthLeverOptions
            {
                Enabled = true,
                AllowedInstanceIds = ["not-a-guid"]
            },
            TestStripe());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("AllowedInstanceIds[0]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validator_fails_closed_when_enabled_without_stripe_test_mode()
    {
        var validator = CreateValidator(
            new StagingProvisioningHealthLeverOptions
            {
                Enabled = true,
                AllowedInstanceIds = [InstanceId.ToString("D")]
            },
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_live_harness" });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("test-mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk_live", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(InstanceId.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Factory_requires_its_own_flag_and_a_stripe_test_key()
    {
        var options = new StagingProvisioningHealthLeverOptions
        {
            Enabled = true,
            AllowedInstanceIds = [InstanceId.ToString("D")]
        };
        var armed = StagingProvisioningHealthLeverFactory.Create(
            options,
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_test_harness" });
        var liveKey = StagingProvisioningHealthLeverFactory.Create(
            options,
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_live_harness" });
        var flagOff = StagingProvisioningHealthLeverFactory.Create(
            new StagingProvisioningHealthLeverOptions
            {
                Enabled = false,
                AllowedInstanceIds = [InstanceId.ToString("D")]
            },
            new StripeBillingOptions { Enabled = true, SecretKey = "sk_test_harness" });

        Assert.True(armed.AppliesTo(InstanceId));
        Assert.False(liveKey.AppliesTo(InstanceId));
        Assert.False(flagOff.AppliesTo(InstanceId));
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
                configuration.GetValue<bool>($"{StagingProvisioningHealthLeverOptions.ConfigurationSection}:Enabled"),
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

    private static StagingProvisioningHealthLeverConfigurationValidator CreateValidator(
        StagingProvisioningHealthLeverOptions options,
        StripeBillingOptions stripe) =>
        new(Options.Create(options), Options.Create(stripe));

    private static StripeBillingOptions TestStripe() =>
        new() { Enabled = true, SecretKey = "sk_test_harness" };
}
