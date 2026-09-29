using ElsaControl.Api.OrganizationBilling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class StagingRecoveryLifecycleLeverOptionsTests
{
    private static readonly Guid InstanceId = Guid.Parse("30000000-0000-0000-0000-000000000033");
    private static readonly Guid SmokeOwnerInstanceId = Guid.Parse("30000000-0000-0000-0000-000000000099");

    [Fact]
    public void Disabled_options_refuse_allowlisted_instances()
    {
        var options = new StagingRecoveryLifecycleLeverOptions
        {
            Enabled = false,
            AllowedInstanceIds = [InstanceId.ToString("D")]
        };

        Assert.False(options.AllowsInstance(InstanceId));
    }

    [Fact]
    public void Enabled_options_accept_allowlisted_instances_only()
    {
        var options = new StagingRecoveryLifecycleLeverOptions
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
        var options = new StagingRecoveryLifecycleLeverOptions
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
        var validator = new StagingRecoveryLifecycleLeverConfigurationValidator(Options.Create(
            new StagingRecoveryLifecycleLeverOptions
            {
                Enabled = false,
                AllowedInstanceIds = ["not-a-guid"]
            }));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Validator_fails_closed_on_malformed_allowlist_ids_when_enabled()
    {
        var validator = new StagingRecoveryLifecycleLeverConfigurationValidator(Options.Create(
            new StagingRecoveryLifecycleLeverOptions
            {
                Enabled = true,
                AllowedInstanceIds = ["not-a-guid"]
            }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("AllowedInstanceIds[0]", error.Message, StringComparison.Ordinal);
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
                configuration.GetValue<bool>($"{StagingRecoveryLifecycleLeverOptions.ConfigurationSection}:Enabled"),
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
