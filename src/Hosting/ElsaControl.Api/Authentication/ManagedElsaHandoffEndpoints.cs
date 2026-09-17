using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class ManagedElsaHandoffEndpoints
{
    public static IEndpointRouteBuilder MapManagedElsaHandoffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/managed-elsa/handoff")
            .WithTags("Managed Elsa Identity Handoff")
            .RequireRateLimiting("managed-elsa-handoff");

        group.MapPost("/issue", async (
            HttpContext context,
            ManagedElsaHandoffIssueRequest request,
            IOptions<ManagedElsaHandoffOptions> options,
            IWorkspaceIdentityReader identityReader,
            ManagedElsaHandoffService handoff,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Enabled)
                return Results.Problem(
                    title: "Managed Elsa identity handoff is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: Correlation(context));

            if (!request.TryCreate(out var handoffRequest))
                return Results.BadRequest(new
                {
                    error = "handoff.request.invalid",
                    correlationId = context.TraceIdentifier
                });

            if (await identityReader.ReadAsync(context) is null)
                return WorkspaceIdentityHttpContextExtensions.UnauthorizedWorkspaceIdentity();

            var result = await handoff.IssueAsync(context, handoffRequest!, cancellationToken);
            if (result is not null)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
            }
            return result is null
                ? Failure(StatusCodes.Status403Forbidden, "handoff.denied", context)
                : Results.Ok(new ManagedElsaHandoffIssueResponse(
                    result.Token,
                    result.TokenType,
                    result.Audience,
                    result.RedirectUri.OriginalString,
                    result.IssuedAt,
                    result.ExpiresAt));
        }).AllowCloudBff();

        group.MapPost("/redeem", async (
            HttpContext context,
            ManagedElsaHandoffRedeemRequest request,
            IOptions<ManagedElsaHandoffOptions> options,
            ManagedElsaHandoffService handoff,
            CancellationToken cancellationToken) =>
        {
            if (!options.Value.Enabled)
                return Results.Problem(
                    title: "Managed Elsa identity handoff is not configured.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: Correlation(context));

            if (!request.TryCreate(out var audience, out var redirectUri))
                return Results.BadRequest(new
                {
                    error = "handoff.request.invalid",
                    correlationId = context.TraceIdentifier
                });

            var result = await handoff.RedeemAsync(
                request.Token, audience!, redirectUri!, request.CodeVerifier, cancellationToken, context.TraceIdentifier);
            if (result.Succeeded)
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
            }
            return result.Failure switch
            {
                ManagedElsaHandoffRedeemFailure.Replay => Results.Conflict(new
                {
                    error = "handoff.replay",
                    correlationId = context.TraceIdentifier
                }),
                ManagedElsaHandoffRedeemFailure.AuthorizationRevoked =>
                    Failure(StatusCodes.Status403Forbidden, "handoff.denied", context),
                ManagedElsaHandoffRedeemFailure.InvalidToken =>
                    Failure(StatusCodes.Status401Unauthorized, "handoff.invalid", context),
                _ => Results.Ok(new ManagedElsaHandoffRedeemResponse(
                    result.Claims!.AccountId,
                    result.Claims.OrganizationId,
                    result.Claims.InstanceId,
                    result.Claims.Scopes.Order(StringComparer.Ordinal).ToArray(),
                    result.Claims.ExpiresAt,
                    result.Claims.SessionExpiresAt,
                    result.Claims.RuntimePermissions.Order(StringComparer.Ordinal).ToArray()))
            };
        });

        return endpoints;
    }

    private static IResult Failure(int statusCode, string code, HttpContext context) =>
        Results.Problem(
            statusCode: statusCode,
            title: "Managed Elsa identity handoff could not be completed.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["correlationId"] = context.TraceIdentifier
            });

    private static Dictionary<string, object?> Correlation(HttpContext context) => new()
    {
        ["correlationId"] = context.TraceIdentifier
    };
}

public sealed record ManagedElsaHandoffIssueRequest(
    Guid OrganizationId,
    Guid InstanceId,
    string Audience,
    string RedirectUri,
    string CodeChallenge,
    string[]? Scopes = null)
{
    public bool TryCreate(out ManagedElsaHandoffRequest? request)
    {
        request = null;
        if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirectUri))
            return false;

        request = new ManagedElsaHandoffRequest(
            OrganizationId,
            InstanceId,
            Audience,
            redirectUri,
            CodeChallenge,
            Scopes?.ToHashSet(StringComparer.Ordinal));
        return !string.IsNullOrWhiteSpace(Audience) &&
               ManagedElsaHandoffIssuer.IsValidCodeChallenge(CodeChallenge) &&
               (Scopes is null || Scopes.All(x => !string.IsNullOrWhiteSpace(x))) &&
               ManagedElsaHandoffIssuer.IsSafeRedirectUri(redirectUri);
    }
}

public sealed record ManagedElsaHandoffIssueResponse(
    string Token,
    string TokenType,
    string Audience,
    string RedirectUri,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public sealed record ManagedElsaHandoffRedeemRequest(
    string Token,
    string Audience,
    string RedirectUri,
    string CodeVerifier)
{
    public bool TryCreate(out string? audience, out Uri? redirectUri)
    {
        audience = string.IsNullOrWhiteSpace(Audience) ? null : Audience;
        redirectUri = Uri.TryCreate(RedirectUri, UriKind.Absolute, out var parsed) ? parsed : null;
        return audience is not null &&
               redirectUri is not null &&
               !string.IsNullOrWhiteSpace(CodeVerifier) &&
               ManagedElsaHandoffIssuer.IsSafeRedirectUri(redirectUri);
    }
}

public sealed record ManagedElsaHandoffRedeemResponse(
    Guid AccountId,
    Guid OrganizationId,
    Guid InstanceId,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    DateTimeOffset SessionExpiresAt,
    IReadOnlyList<string> RuntimePermissions);
