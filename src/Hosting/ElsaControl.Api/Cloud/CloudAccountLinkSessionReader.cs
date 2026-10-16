using System.Security.Claims;
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
    TrustedWorkspaceIdentity HubIdentity);

public sealed class CloudAccountLinkSessionReader(
    IOptions<CloudAccountIdentityOptions> cloudAccountOptions,
    IOptions<HubAccountIdentityOptions> hubAccountOptions)
{
    private readonly CloudAccountIdentityOptions _cloudAccountOptions = cloudAccountOptions.Value;
    private readonly HubAccountIdentityOptions _hubAccountOptions = hubAccountOptions.Value;

    public async Task<CloudAccountLinkSessions?> ReadAsync(HttpContext context)
    {
        var primary = await context.AuthenticateAsync(CloudAccountIdentityDefaults.SelectorScheme);
        var principal = primary.Succeeded ? primary.Principal : null;

        var cloud = MapCloud(principal);
        var hub = MapHub(principal);

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
            : new CloudAccountLinkSessions(cloud, hub);
    }

    private TrustedWorkspaceIdentity? MapCloud(ClaimsPrincipal? principal) =>
        CloudAccountTokenSelector.IsValidatedCloudAccount(principal, _cloudAccountOptions)
            ? ControlClaimsIdentityMapper.ToCloudTrustedWorkspaceIdentity(principal!)
            : null;

    private TrustedWorkspaceIdentity? MapHub(ClaimsPrincipal? principal) =>
        HubAccountTokenSelector.IsValidatedHubAccount(principal, _hubAccountOptions)
            ? ControlClaimsIdentityMapper.ToHubTrustedWorkspaceIdentity(principal!)
            : null;

    private static async Task<TrustedWorkspaceIdentity?> AuthenticateCompanionAsync(
        HttpContext context,
        string headerName,
        string scheme,
        Func<ClaimsPrincipal?, TrustedWorkspaceIdentity?> map)
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
            var result = await context.AuthenticateAsync(scheme);
            return result.Succeeded ? map(result.Principal) : null;
        }
        finally
        {
            context.Request.Headers.Authorization = original;
        }
    }
}
