using System.Text.Json.Serialization;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Cloud;

public static class CloudAccountLinkEndpoints
{
    public const string SessionsRequiredCode = "cloud.account-link.sessions-required";
    public const string CloudAccountMissingCode = "cloud.account-link.cloud-account-missing";
    public const string AccountAlreadyLinkedCode = "cloud.account-link.account-already-linked";
    public const string HubAccountHasEnginesCode = "cloud.account-link.hub-account-has-engines";
    public const string HubAccountHasStateCode = "cloud.account-link.hub-account-has-state";
    public const string ReauthenticationRequiredCode = "cloud.account-link.reauthentication-required";

    public static IEndpointRouteBuilder MapCloudAccountLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/cloud/account-links", async (
            CloudAccountLinkRequest _,
            HttpContext context,
            CloudAccountLinkSessionReader sessions,
            AccountIdentityLinkService links,
            IOptions<HubAccountIdentityOptions> hubAccountOptions,
            TimeProvider timeProvider,
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

            // Linking needs a recent interactive sign-in on both sessions, checked before any lookup or write.
            var window = hubAccountOptions.Value.LinkReauthenticationWindow;
            var now = timeProvider.GetUtcNow();
            var hubStale = !AccountSignInRecency.IsRecent(pair.HubLastSignIn, now, window);
            var cloudStale = !AccountSignInRecency.IsRecent(pair.CloudLastSignIn, now, window);
            if (hubStale || cloudStale)
                return ReauthenticationRequired(context, window, (hubStale, cloudStale) switch
                {
                    (true, true) => "both",
                    (true, false) => "hub",
                    _ => "cloud"
                });

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
                        "The Hub identity already belongs to a Control account that has organization, workspace, or subscription state.",
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

    // RFC 9470 step-up challenge; staleSession tells the client which sign-in to repeat.
    private static IResult ReauthenticationRequired(HttpContext context, TimeSpan window, string staleSession)
    {
        context.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_user_authentication\", max_age={(long)window.TotalSeconds}";
        return Results.Problem(
            title: "A recent sign-in to both the Hub and the Cloud account is required to link them.",
            statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ReauthenticationRequiredCode,
                ["staleSession"] = staleSession
            });
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
