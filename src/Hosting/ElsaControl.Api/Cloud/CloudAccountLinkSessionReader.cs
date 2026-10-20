using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Cloud;

public static class CloudAccountLinkDefaults
{
    public const string CloudAuthorizationHeader = "X-Elsa-Cloud-Authorization";
    public const string HubAuthorizationHeader = "X-Elsa-Hub-Authorization";
}

public sealed record CloudAccountLinkSessions(
    TrustedWorkspaceIdentity CloudIdentity,
    TrustedWorkspaceIdentity HubIdentity,
    DateTimeOffset? CloudLastSignIn,
    DateTimeOffset? HubLastSignIn);

public sealed class CloudAccountLinkSessionReader(
    IOptions<CloudAccountIdentityOptions> cloudAccountOptions,
    IOptions<HubAccountIdentityOptions> hubAccountOptions)
{
    private readonly CloudAccountIdentityOptions _cloudAccountOptions = cloudAccountOptions.Value;
    private readonly HubAccountIdentityOptions _hubAccountOptions = hubAccountOptions.Value;

    public async Task<CloudAccountLinkSessions?> ReadAsync(HttpContext context)
    {
        var primary = await context.AuthenticateAsync(CloudAccountIdentityDefaults.SelectorScheme);

        var cloud = MapCloud(primary);
        var hub = MapHub(primary);

        cloud ??= await AuthenticateCompanionAsync(
            context,
            CloudAccountLinkDefaults.CloudAuthorizationHeader,
            CloudAccountIdentityDefaults.Scheme,
            MapCloud);
        hub ??= await AuthenticateCompanionAsync(
            context,
            CloudAccountLinkDefaults.HubAuthorizationHeader,
            HubAccountIdentityDefaults.Scheme,
            MapHub);

        return cloud is null || hub is null
            ? null
            : new CloudAccountLinkSessions(cloud.Identity, hub.Identity, cloud.LastSignIn, hub.LastSignIn);
    }

    private Session? MapCloud(AuthenticateResult result)
    {
        var principal = result.Succeeded ? result.Principal : null;
        return CloudAccountTokenSelector.IsValidatedCloudAccount(principal, _cloudAccountOptions)
            ? new Session(
                ControlClaimsIdentityMapper.ToCloudTrustedWorkspaceIdentity(principal!),
                AccountSignInRecency.ReadLastSignIn(result.Properties))
            : null;
    }

    private Session? MapHub(AuthenticateResult result)
    {
        var principal = result.Succeeded ? result.Principal : null;
        return HubAccountTokenSelector.IsValidatedHubAccount(principal, _hubAccountOptions)
            ? new Session(
                ControlClaimsIdentityMapper.ToHubTrustedWorkspaceIdentity(principal!),
                AccountSignInRecency.ReadLastSignIn(result.Properties))
            : null;
    }

    private static async Task<Session?> AuthenticateCompanionAsync(
        HttpContext context,
        string headerName,
        string scheme,
        Func<AuthenticateResult, Session?> map)
    {
        if (!context.Request.Headers.TryGetValue(headerName, out var values))
            return null;

        var authorization = values.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
            return null;

        var original = context.Request.Headers.Authorization;
        context.Request.Headers.Authorization = authorization;
        try
        {
            return map(await context.AuthenticateAsync(scheme));
        }
        finally
        {
            context.Request.Headers.Authorization = original;
        }
    }

    private sealed record Session(TrustedWorkspaceIdentity Identity, DateTimeOffset? LastSignIn);
}
