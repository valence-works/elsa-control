using ElsaControl.Api.Authentication;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class PartnerApiConfigurationValidatorTests
{
    [Fact]
    public void Unconfigured_Production_defaults_to_live()
    {
        var options = new PartnerApiOptions();
        options.ApplyEnvironmentDefault(new StubHostEnvironment(Environments.Production));
        Assert.Equal(PartnerApiOptions.LiveEnvironment, options.Environment);
    }

    [Fact]
    public void Unconfigured_non_Production_defaults_to_test()
    {
        var options = new PartnerApiOptions();
        options.ApplyEnvironmentDefault(new StubHostEnvironment(Environments.Staging));
        Assert.Equal(PartnerApiOptions.TestEnvironment, options.Environment);
    }

    [Fact]
    public void Production_rejects_a_non_live_environment()
    {
        var errors = Validate(
            Environments.Production,
            new PartnerApiOptions { Environment = PartnerApiOptions.TestEnvironment });

        Assert.Contains(errors, error => error.Contains("must be live", StringComparison.Ordinal));
    }

    [Fact]
    public void Production_accepts_live()
    {
        var errors = Validate(
            Environments.Production,
            new PartnerApiOptions { Environment = PartnerApiOptions.LiveEnvironment });

        Assert.Empty(errors);
    }

    [Fact]
    public void Testing_may_use_test_keys()
    {
        var errors = Validate(
            "Testing",
            new PartnerApiOptions { Environment = PartnerApiOptions.TestEnvironment });

        Assert.Empty(errors);
    }

    private static string[] Validate(string environmentName, PartnerApiOptions options) =>
        new PartnerApiConfigurationValidator(
                new StubHostEnvironment(environmentName),
                Options.Create(options))
            .Validate()
            .ToArray();

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ElsaControl.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
