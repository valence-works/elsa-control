using System.Security.Claims;
using ElsaControl.Api.Authentication;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Admin.Organizations;

public static class AdminPartnerApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapAdminPartnerApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/organizations/{organizationId:guid}/partner/keys")
            .RequireAuthorization(AdminAuthorization.Policy)
            .WithTags("Admin Organizations");

        group.MapPost("", async (
            Guid organizationId,
            AdminPartnerApiKeyCreateRequest request,
            HttpContext context,
            IOptions<ControlIdentityOptions> identityOptions,
            IOptions<PartnerApiOptions> partnerApi,
            PartnerApiKeyService keys,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var result = await keys.CreateAsync(
                new CreatePartnerApiKeyRequest(
                    organizationId,
                    partnerApi.Value.Environment,
                    request.Name,
                    request.Scopes ?? [],
                    request.ExpiresAt,
                    ActorAccountId(context.User, identityOptions.Value),
                    OperatorSubject(context.User, identityOptions.Value)),
                time.GetUtcNow(),
                cancellationToken);
            return result.Failure switch
            {
                PartnerApiKeyCreateFailure.OrganizationNotFound => Problem(
                    "organization.not-found",
                    "Organization was not found.",
                    StatusCodes.Status404NotFound),
                PartnerApiKeyCreateFailure.InvalidEnvironment => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["environment"] = ["Environment is invalid."] }),
                PartnerApiKeyCreateFailure.InvalidName => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["name"] = ["Name must be 128 characters or fewer."] }),
                PartnerApiKeyCreateFailure.InvalidScopes => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["scopes"] = ["One or more scopes are not allowed."] }),
                PartnerApiKeyCreateFailure.InvalidExpiry => Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["expiresAt"] = ["Expiry must be in the future."] }),
                PartnerApiKeyCreateFailure.ActiveKeyLimitReached => Problem(
                    "partner.key-limit",
                    "A partner may have at most two active keys.",
                    StatusCodes.Status409Conflict),
                null when result.Succeeded => Created(result.Created!),
                _ => Results.Problem(
                    title: "The partner API key could not be created.",
                    statusCode: StatusCodes.Status500InternalServerError)
            };
        }).AddEndpointFilter(NoStoreCacheFilter.Instance);

        group.MapGet("", async (
            Guid organizationId,
            PartnerApiKeyService keys,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var records = await keys.ListAsync(organizationId, cancellationToken);
            var now = time.GetUtcNow();
            return Results.Json(
                records.Select(record => ToListItem(record, now)).ToList(),
                statusCode: StatusCodes.Status200OK);
        }).AddEndpointFilter(NoStoreCacheFilter.Instance);

        group.MapPost("/{keyId}/revoke", async (
            Guid organizationId,
            string keyId,
            HttpContext context,
            IOptions<ControlIdentityOptions> identityOptions,
            PartnerApiKeyService keys,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            var result = await keys.RevokeAsync(
                organizationId,
                keyId,
                ActorAccountId(context.User, identityOptions.Value),
                OperatorSubject(context.User, identityOptions.Value),
                time.GetUtcNow(),
                cancellationToken);
            return result.Failure switch
            {
                PartnerApiKeyRevokeFailure.OrganizationNotFound => Problem(
                    "organization.not-found",
                    "Organization was not found.",
                    StatusCodes.Status404NotFound),
                PartnerApiKeyRevokeFailure.KeyNotFound => Problem(
                    "partner.key-not-found",
                    "Partner API key was not found.",
                    StatusCodes.Status404NotFound),
                PartnerApiKeyRevokeFailure.AlreadyRevoked => Problem(
                    "partner.key-revoked",
                    "Partner API key was already revoked.",
                    StatusCodes.Status409Conflict),
                null when result.Succeeded => Results.Json(
                    ToListItem(result.Record!, time.GetUtcNow()),
                    statusCode: StatusCodes.Status200OK),
                _ => Results.Problem(
                    title: "The partner API key could not be revoked.",
                    statusCode: StatusCodes.Status500InternalServerError)
            };
        }).AddEndpointFilter(NoStoreCacheFilter.Instance);

        return endpoints;
    }

    private static IResult Created(CreatedPartnerApiKey created) =>
        Results.Json(
            new AdminPartnerApiKeyCreatedResponse(
                created.Record.KeyId,
                created.Record.Name,
                created.Record.Scopes,
                created.Record.CreatedAt,
                created.Record.ExpiresAt,
                created.Token),
            statusCode: StatusCodes.Status201Created);

    private static AdminPartnerApiKeyListItem ToListItem(PartnerApiKeyRecord record, DateTimeOffset now) =>
        new(
            record.KeyId,
            record.Name,
            record.Scopes,
            record.CreatedAt,
            record.ExpiresAt,
            record.LastUsedAt,
            record.StateAt(now).ToString());

    private static Guid? ActorAccountId(ClaimsPrincipal user, ControlIdentityOptions identityOptions)
    {
        var value = user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var accountId) ? accountId : null;
    }

    private static string? OperatorSubject(ClaimsPrincipal user, ControlIdentityOptions identityOptions) =>
        user.FindFirstValue(identityOptions.Claims.Subject) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    private static IResult Problem(string code, string title, int statusCode) =>
        Results.Problem(
            title: title,
            statusCode: statusCode,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}

public sealed record AdminPartnerApiKeyCreateRequest(
    string? Name,
    IReadOnlyList<string>? Scopes,
    DateTimeOffset? ExpiresAt);

public sealed record AdminPartnerApiKeyCreatedResponse(
    string KeyId,
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string Token);

public sealed record AdminPartnerApiKeyListItem(
    string KeyId,
    string? Name,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    string State);

file sealed class NoStoreCacheFilter : IEndpointFilter
{
    public static NoStoreCacheFilter Instance { get; } = new();

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return result;
    }
}
