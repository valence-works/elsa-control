using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Tests;

public sealed class HubAccountIdentityConfigurationValidatorTests
{
    [Fact]
    public async Task Disabled_hub_path_does_not_require_issuer_or_audience()
    {
        var validator = CreateValidator(new HubAccountIdentityOptions(), "Production");

        await validator.StartAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("Production", null, "authenticated")]
    [InlineData("Production", "", "authenticated")]
    [InlineData("Development", "https://hub-account.test/auth/v1", "")]
    [InlineData("Testing", "not-a-uri", "authenticated")]
    [InlineData("Production", "http://hub-account.test/auth/v1", "authenticated")]
    [InlineData("Production", "https://hub-account.test/auth/v1?tenant=1", "authenticated")]
    public async Task Incomplete_or_unsafe_hub_configuration_is_refused(
        string environment, string? issuer, string audience)
    {
        var validator = CreateValidator(new HubAccountIdentityOptions
        {
            Enabled = true,
            Issuer = issuer,
            Audience = audience
        }, environment);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("Hub account JWT validation requires an HTTPS issuer, an audience, and published signing keys.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_allows_a_complete_hub_configuration_with_a_test_signing_key()
    {
        var validator = CreateValidator(new HubAccountIdentityOptions
        {
            Enabled = true,
            Issuer = "https://hub-account.test/auth/v1",
            Audience = "authenticated",
            TestSigningKey = ControlApiTestApplication.TestControlIdentitySigningKey
        }, "Testing");

        await validator.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Production_refuses_a_test_signing_key()
    {
        var validator = CreateValidator(new HubAccountIdentityOptions
        {
            Enabled = true,
            Issuer = "https://hub-account.test/auth/v1",
            Audience = "authenticated",
            TestSigningKey = ControlApiTestApplication.TestControlIdentitySigningKey
        }, "Production");

        await Assert.ThrowsAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("https://shared-account.test/auth/v1", "https://shared-account.test/auth/v1")]
    [InlineData("https://shared-account.test/auth/v1/", "https://shared-account.test/auth/v1")]
    [InlineData("https://Shared-Account.TEST/auth/v1", "https://shared-account.test/auth/v1")]
    [InlineData(" https://shared-account.test/auth/v1 ", "https://shared-account.test/auth/v1")]
    public async Task Enabled_hub_and_cloud_paths_refuse_the_same_issuer(string hubIssuer, string cloudIssuer)
    {
        var validator = CreateValidator(
            new HubAccountIdentityOptions
            {
                Enabled = true,
                Issuer = hubIssuer,
                Audience = "authenticated"
            },
            "Production",
            new CloudAccountIdentityOptions
            {
                Enabled = true,
                Issuer = cloudIssuer,
                Audience = "authenticated"
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        Assert.Contains("issuers must be different", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enabled_hub_and_cloud_paths_allow_distinct_issuers()
    {
        var validator = CreateValidator(
            new HubAccountIdentityOptions
            {
                Enabled = true,
                Issuer = "https://hub-account.test/auth/v1",
                Audience = "authenticated"
            },
            "Production",
            new CloudAccountIdentityOptions
            {
                Enabled = true,
                Issuer = "https://cloud-account.test/auth/v1",
                Audience = "authenticated"
            });

        await validator.StartAsync(CancellationToken.None);
    }

    private static HubAccountIdentityConfigurationValidator CreateValidator(
        HubAccountIdentityOptions options,
        string environment,
        CloudAccountIdentityOptions? cloud = null) =>
        new(
            Options.Create(options),
            Options.Create(cloud ?? new CloudAccountIdentityOptions()),
            new HostingEnvironment { EnvironmentName = environment });
}
