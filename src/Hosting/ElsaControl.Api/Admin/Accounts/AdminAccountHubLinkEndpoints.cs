using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Accounts;

public static class AdminAccountHubLinkEndpoints
{
    public const string NotFoundCode = "cloud.account-link.not-found";

    public static IEndpointRouteBuilder MapAdminAccountHubLinkEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/accounts/{accountId:guid}/hub-link")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Accounts");

        group.MapGet("", async (
            Guid accountId,
            AccountIdentityLinkService links,
            CancellationToken cancellationToken) =>
        {
            var status = await links.GetStatusAsync(accountId, cancellationToken);
            return Results.Ok(ToResponse(status));
        });

        group.MapGet("/audit", async (
            Guid accountId,
            AccountIdentityLinkService links,
            CancellationToken cancellationToken) =>
        {
            var status = await links.GetStatusAsync(accountId, cancellationToken);
            return Results.Ok(new AdminAccountHubLinkAuditResponse(status.AccountId, ToAudit(status.Audit)));
        });

        group.MapDelete("", async (
            Guid accountId,
            HttpContext context,
            IOptions<ControlIdentityOptions> identityOptions,
            AccountIdentityLinkService links,
            CancellationToken cancellationToken) =>
        {
            var result = await links.UnlinkAsync(
                accountId,
                OperatorSubject(context.User, identityOptions.Value),
                cancellationToken);
            return result.Outcome switch
            {
                AccountIdentityUnlinkOutcome.Unlinked => Results.NoContent(),
                _ => Results.Problem(
                    title: "The Control account has no Hub identity to unlink.",
                    statusCode: StatusCodes.Status404NotFound,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = NotFoundCode
                    })
            };
        });

        return endpoints;
    }

    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static AdminAccountHubLinkResponse ToResponse(AccountIdentityLinkStatus status) =>
        new(status.AccountId, status.Linked, ToAudit(status.Audit));

    private static IReadOnlyList<AccountIdentityLinkAuditResponse> ToAudit(
        IReadOnlyList<AccountIdentityLinkAuditRecord> audit) =>
        audit.Select(record => new AccountIdentityLinkAuditResponse(
            record.Id,
            record.Action.ToString(),
            record.ActorKind.ToString(),
            record.ActorAccountId,
            record.SourceAccountId,
            record.CreatedAt)).ToList();
}

public sealed record AdminAccountHubLinkResponse(
    Guid AccountId,
    bool Linked,
    IReadOnlyList<AccountIdentityLinkAuditResponse> Audit);

public sealed record AdminAccountHubLinkAuditResponse(
    Guid AccountId,
    IReadOnlyList<AccountIdentityLinkAuditResponse> Items);

public sealed record AccountIdentityLinkAuditResponse(
    Guid Id,
    string Action,
    string ActorKind,
    Guid? ActorAccountId,
    Guid? SourceAccountId,
    DateTimeOffset CreatedAt);
