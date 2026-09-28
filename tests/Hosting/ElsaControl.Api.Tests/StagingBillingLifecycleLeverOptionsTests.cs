using ElsaControl.Api.OrganizationBilling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class StagingBillingLifecycleLeverOptionsTests
{
    private static readonly Guid OrganizationId = Guid.Parse("20000000-0000-0000-0000-000000000022");

    [Fact]
    public void Disabled_options_refuse_allowlisted_orgs()
    {
        var options = new StagingBillingLifecycleLeverOptions
        {
            Enabled = false,
            AllowedOrganizationIds = [OrganizationId.ToString("D")]
        };

        Assert.False(options.AllowsOrganization(OrganizationId));
    }

    [Fact]
    public void Enabled_options_accept_allowlisted_orgs_only()
    {
        var options = new StagingBillingLifecycleLeverOptions
        {
            Enabled = true,
            AllowedOrganizationIds = [OrganizationId.ToString("D")]
        };

        Assert.True(options.AllowsOrganization(OrganizationId));
        Assert.False(options.AllowsOrganization(Guid.NewGuid()));
        Assert.False(options.AllowsOrganization(Guid.Empty));
    }

    [Fact]
    public async Task Validator_skips_allowlist_shape_while_the_lever_is_off()
    {
        var validator = new StagingBillingLifecycleLeverConfigurationValidator(Options.Create(
            new StagingBillingLifecycleLeverOptions
            {
                Enabled = false,
                AllowedOrganizationIds = ["not-a-guid"]
            }));

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Validator_fails_closed_on_malformed_allowlist_ids_when_enabled()
    {
        var validator = new StagingBillingLifecycleLeverConfigurationValidator(Options.Create(
            new StagingBillingLifecycleLeverOptions
            {
                Enabled = true,
                AllowedOrganizationIds = ["not-a-guid"]
            }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));

        Assert.Contains("AllowedOrganizationIds[0]", error.Message, StringComparison.Ordinal);
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
                configuration.GetValue<bool>($"{StagingBillingLifecycleLeverOptions.ConfigurationSection}:Enabled"),
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
