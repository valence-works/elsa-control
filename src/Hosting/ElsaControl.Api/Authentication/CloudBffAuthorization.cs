using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class CloudBffDefaults
{
    public const string ConfigurationSection = "Authentication:CloudBff";
    public const string DefaultScope = "ElsaCloud.Dashboard";
}

public sealed class CloudBffOptions
{
    public const string ConfigurationSection = CloudBffDefaults.ConfigurationSection;

    public bool Enabled { get; init; }
    public string? ClientId { get; init; }
    public string Scope { get; init; } = CloudBffDefaults.DefaultScope;

    internal bool IsConfigured => Enabled &&
                                  !string.IsNullOrWhiteSpace(ClientId) &&
                                  !string.IsNullOrWhiteSpace(Scope);

    internal IEnumerable<string> Validate()
    {
        if (!Enabled)
            yield break;

        if (string.IsNullOrWhiteSpace(ClientId))
            yield return $"{ConfigurationSection}:ClientId is required when BFF authentication is enabled.";
        else if (!string.Equals(ClientId, ClientId.Trim(), StringComparison.Ordinal) ||
                 ClientId.Any(char.IsWhiteSpace))
            yield return $"{ConfigurationSection}:ClientId must be one non-whitespace client identifier.";

        if (string.IsNullOrWhiteSpace(Scope))
            yield return $"{ConfigurationSection}:Scope is required when BFF authentication is enabled.";
        else if (!string.Equals(Scope, Scope.Trim(), StringComparison.Ordinal) ||
                 Scope.Any(char.IsWhiteSpace))
            yield return $"{ConfigurationSection}:Scope must be one non-whitespace scope value.";
    }
}

public sealed class CloudBffConfigurationValidator(
    IOptions<CloudBffOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"Cloud BFF authentication configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public enum CloudBffTokenDecision
{
    NotBff,
    Valid,
    Invalid
}

public static class CloudBffAuthorization
{
    private const string AuthorizedPartyClaim = "azp";
    private const string ApplicationIdClaim = "appid";
    private const string ScopeClaim = "scp";

    public static CloudBffTokenDecision Classify(ClaimsPrincipal? principal, CloudBffOptions options)
    {
        if (principal?.Identities.Any(identity => identity.IsAuthenticated) != true)
            return CloudBffTokenDecision.NotBff;

        var configuredScope = string.IsNullOrWhiteSpace(options.Scope)
            ? CloudBffDefaults.DefaultScope
            : options.Scope;

        var authorizedParties = principal.Claims
            .Where(claim => string.Equals(claim.Type, AuthorizedPartyClaim, StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .ToArray();
        var applicationIds = principal.Claims
            .Where(claim => string.Equals(claim.Type, ApplicationIdClaim, StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .ToArray();
        var clientValues = authorizedParties.Concat(applicationIds).ToArray();
        var hasConfiguredClient = !string.IsNullOrWhiteSpace(options.ClientId) &&
                                  clientValues.Any(value =>
                                      string.Equals(value, options.ClientId, StringComparison.Ordinal));
        var hasMalformedClientClaims = authorizedParties.Length > 1 ||
                                       applicationIds.Length > 1 ||
                                       clientValues.Any(value => string.IsNullOrWhiteSpace(value) ||
                                                                 !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
                                                                 value.Any(char.IsWhiteSpace));
        var hasForeignClient = !string.IsNullOrWhiteSpace(options.ClientId) &&
                               clientValues.Any(value =>
                                   !string.Equals(value, options.ClientId, StringComparison.Ordinal));
        var scopes = principal.Claims
            .Where(claim => string.Equals(claim.Type, ScopeClaim, StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .ToArray();
        var hasMalformedScopeClaims = scopes.Length > 1 ||
                                      scopes.Any(value =>
                                          value.Contains(',') ||
                                          value.Any(character =>
                                              char.IsWhiteSpace(character) && character != ' '));
        var hasDedicatedScope = scopes.Length == 1 &&
                                scopes[0]
                                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    .Any(scope => string.Equals(scope, configuredScope, StringComparison.Ordinal));

        // A token with neither BFF marker is an ordinary Control bearer token. Once
        // either marker appears, an incomplete or conflicting pair is never allowed
        // to fall back to ordinary customer authorization.
        if (!hasConfiguredClient && !hasDedicatedScope &&
            !hasMalformedClientClaims && !hasMalformedScopeClaims)
            return CloudBffTokenDecision.NotBff;

        return options.IsConfigured && hasConfiguredClient && hasDedicatedScope && !hasForeignClient &&
               !hasMalformedClientClaims && !hasMalformedScopeClaims
            ? CloudBffTokenDecision.Valid
            : CloudBffTokenDecision.Invalid;
    }
}

public sealed class CloudBffAllowedEndpointMetadata
{
    public static CloudBffAllowedEndpointMetadata Instance { get; } = new();

    private CloudBffAllowedEndpointMetadata()
    {
    }
}

public static class CloudBffEndpointConventionExtensions
{
    public static RouteHandlerBuilder AllowCloudBff(this RouteHandlerBuilder builder)
    {
        builder.WithMetadata(CloudBffAllowedEndpointMetadata.Instance);
        return builder;
    }
}

public sealed class CloudBffAuthorizationMiddleware(
    RequestDelegate next,
    IOptions<CloudBffOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var decision = CloudBffAuthorization.Classify(context.User, options.Value);
        if (decision == CloudBffTokenDecision.NotBff)
        {
            await next(context);
            return;
        }

        if (decision == CloudBffTokenDecision.Valid &&
            context.GetEndpoint()?.Metadata.GetMetadata<CloudBffAllowedEndpointMetadata>() is not null)
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await Results.Problem(
                title: "The Cloud BFF credential is not authorized for this endpoint.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "cloud-bff.denied",
                    ["correlationId"] = context.TraceIdentifier
                })
            .ExecuteAsync(context);
    }
}

public static class CloudBffAuthorizationMiddlewareExtensions
{
    public static IApplicationBuilder UseCloudBffAuthorization(this IApplicationBuilder app) =>
        app.UseMiddleware<CloudBffAuthorizationMiddleware>();
}
