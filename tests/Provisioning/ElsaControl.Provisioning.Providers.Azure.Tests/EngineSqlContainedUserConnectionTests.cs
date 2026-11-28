using ElsaControl.Provisioning.Providers.Azure.Programs;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

public sealed class EngineSqlContainedUserConnectionTests
{
    private const string Server = "sql-engine-x.database.windows.net";

    [Fact]
    public void The_grant_authenticates_as_the_provisioner_identity_when_its_client_id_is_known()
    {
        var clientId = "d57a6279-2806-4289-9442-d862b53451a1";

        var connection = EngineSqlContainedUser.ConnectionString(Server, "Elsa", clientId);

        Assert.Contains("Authentication=Active Directory Managed Identity;", connection, StringComparison.Ordinal);
        Assert.Contains($"User Id={clientId};", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("Active Directory Default", connection, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void The_grant_falls_back_to_the_default_credential_without_a_valid_client_id(string? clientId)
    {
        var connection = EngineSqlContainedUser.ConnectionString(Server, "Elsa", clientId);

        Assert.Contains("Authentication=Active Directory Default;", connection, StringComparison.Ordinal);
        Assert.DoesNotContain("User Id=", connection, StringComparison.Ordinal);
    }
}
