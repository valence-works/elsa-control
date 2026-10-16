using System.Text.Json.Serialization;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.Api.Cloud;

public static class CloudAccountLinkEndpoints
{
    public const string SessionsRequiredCode = "cloud.account-link.sessions-required";
    public const string CloudAccountMissingCode = "cloud.account-link.cloud-account-missing";
    public const string AccountAlreadyLinkedCode = "cloud.account-link.account-already-linked";
    public const string HubAccountHasEnginesCode = "cloud.account-link.hub-account-has-engines";
    public const string HubAccountHasStateCode = "cloud.account-link.hub-account-has-state";

    public static IEndpointRouteBuilder MapCloudAccountLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/cloud/account-links", async (
            CloudAccountLinkRequest _,
            HttpContext context,
            CloudAccountLinkSessionReader sessions,
            AccountIdentityLinkService links,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.Pragma = "no-cache";

            var pair = await sessions.ReadAsync(context);
            if (pair is null)
            {
                return Problem(
                    SessionsRequiredCode,
                    "A Cloud account session and a Hub session are required.",
                    StatusCodes.Status401Unauthorized);
            }

            var result = await links.LinkAsync(pair.CloudIdentity, pair.HubIdentity, cancellationToken);
            return result.Outcome switch
            {
                AccountIdentityLinkOutcome.Linked or AccountIdentityLinkOutcome.AlreadyLinked =>
                    Results.Ok(new CloudAccountLinkResponse(result.AccountId!.Value, result.Outcome == AccountIdentityLinkOutcome.AlreadyLinked)),
                AccountIdentityLinkOutcome.CloudAccountMissing =>
                    Problem(
                        CloudAccountMissingCode,
                        "The Cloud session is not linked to an existing Control account.",
                        StatusCodes.Status409Conflict),
                AccountIdentityLinkOutcome.AccountAlreadyLinked =>
                    Problem(
                        AccountAlreadyLinkedCode,
                        "The request conflicts with an existing Hub identity link.",
                        StatusCodes.Status409Conflict),
                AccountIdentityLinkOutcome.HubAccountHasEngines =>
                    Problem(
                        HubAccountHasEnginesCode,
                        "The Hub identity already belongs to a Control account that has engines.",
                        StatusCodes.Status409Conflict),
                AccountIdentityLinkOutcome.HubAccountHasState =>
                    Problem(
                        HubAccountHasStateCode,
                        "The Hub identity already belongs to a Control account that has organization or subscription state.",
                        StatusCodes.Status409Conflict),
                _ => Problem(
                    AccountAlreadyLinkedCode,
                    "The request conflicts with an existing Hub identity link.",
                    StatusCodes.Status409Conflict)
            };
        }).WithTags("Cloud Account Links")
            .AllowCloudBff()
            .AllowHubBff();

        return endpoints;
    }

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(
            title: title,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code
            });
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudAccountLinkRequest;

public sealed record CloudAccountLinkResponse(Guid AccountId, bool AlreadyLinked);
