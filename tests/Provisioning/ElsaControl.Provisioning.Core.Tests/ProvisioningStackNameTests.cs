namespace ElsaControl.Provisioning.Core.Tests;

public sealed class ProvisioningStackNameTests
{
    [Theory]
    [InlineData("acme-prod", "acme-prod")]
    [InlineData("Acme Prod", "Acme-Prod")]
    [InlineData("org/slug", "org-slug")]
    [InlineData("--alpha--", "alpha")]
    public void Canonicalizes_to_pulumi_charset(string input, string expected) =>
        Assert.Equal(expected, ProvisioningStackName.Canonicalize(input));
}
