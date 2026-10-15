using System.Security.Claims;
using ElsaControl.Api.Authentication;

namespace ElsaControl.Api.Tests;

public sealed class ControlRateLimitIdentityTests
{
    [Fact]
    public void Multi_tenant_session_without_tid_still_partitions_by_issuer_and_subject()
    {
        const string issuer = "https://login.microsoftonline.com/0b6f5c4e-2d8a-4f3b-9c1e-7a5d3e2f1b0c/v2.0";
        const string subject = "subject-without-tid";
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("iss", issuer),
                new Claim("sub", subject)
            ],
            ControlIdentityDefaults.Scheme));
        var controlIdentity = new ControlIdentityOptions
        {
            Provider = ControlIdentityProviderKind.MicrosoftEntra,
            Entra = new ControlEntraIdentityOptions { MultiTenant = true }
        };
        var cloudAccount = new CloudAccountIdentityOptions();
        var hubAccount = new HubAccountIdentityOptions();

        Assert.True(controlIdentity.IsEntraMultiTenant);
        Assert.Null(ControlIdentityReader.MapIdentity(user, controlIdentity, cloudAccount, hubAccount));
        Assert.Equal(
            ControlRateLimitIdentity.Format(issuer, subject),
            ControlRateLimitIdentity.PartitionKey(user, controlIdentity, cloudAccount, hubAccount));
    }

    [Fact]
    public void Unauthenticated_principal_has_no_partition_key()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var key = ControlRateLimitIdentity.PartitionKey(
            user,
            new ControlIdentityOptions { Issuer = "https://control.test" },
            new CloudAccountIdentityOptions(),
            new HubAccountIdentityOptions());

        Assert.Null(key);
    }
}
