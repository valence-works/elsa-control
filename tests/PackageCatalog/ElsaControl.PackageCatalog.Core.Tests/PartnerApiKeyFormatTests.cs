using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class PartnerApiKeyFormatTests
{
    [Fact]
    public void Issued_token_parses_and_does_not_print_the_secret()
    {
        var issued = PartnerApiKeyFormat.Issue("test");

        Assert.True(PartnerApiKeyFormat.TryParse(issued.Token, out var parsed));
        Assert.Equal(issued.Environment, parsed.Environment);
        Assert.Equal(issued.KeyId, parsed.KeyId);
        Assert.Equal(issued.Secret, parsed.Secret);
        Assert.DoesNotContain(issued.Secret, issued.ToString(), StringComparison.Ordinal);
        Assert.Equal(PartnerApiKeyFormat.SecretHashLength, PartnerApiKeyFormat.HashSecret(issued.Secret).Length);
    }

    [Fact]
    public void Secret_may_contain_base64url_underscores()
    {
        var token = "ecp_test_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_abcdefghijklmnopqrstuvwxyz0123456789_-__";

        Assert.True(PartnerApiKeyFormat.TryParse(token, out var parsed));
        Assert.Equal("abcdefghijklmnopqrstuvwxyz0123456789_-__", parsed.Secret);
        Assert.DoesNotContain(parsed.Secret, parsed.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ecp_test_ab")]
    [InlineData("ecp_TEST_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_abcdefghijklmnopqrstuvwxyz0123456789_-")]
    [InlineData("ecp_test_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_secret_extra")]
    public void Invalid_tokens_do_not_parse(string? token)
    {
        Assert.False(PartnerApiKeyFormat.TryParse(token, out _));
    }

    [Fact]
    public void Scope_normalization_rejects_unknown_values_without_echoing_them()
    {
        Assert.False(PartnerApiKeyScopes.TryNormalize(["engines:read", "billing:write"], out _, out var error));
        Assert.Equal("One or more scopes are not allowed.", error);
        Assert.DoesNotContain("billing:write", error, StringComparison.Ordinal);
    }
}
