using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;

namespace ElsaControl.Api.Authentication;

public static class PartnerApiKeyDefaults
{
    public const string Scheme = "PartnerApiKey";
    public const string Policy = "PartnerApi";
    public const string Role = PartnerProvisionerPermissions.RoleName;
    public const string ConfigurationSection = "PartnerApi";
    public const string UnauthorizedCode = "partner.unauthenticated";
    public const string UnauthorizedTitle = "Authentication failed.";
    public const string InsufficientScopeCode = "partner.insufficient-scope";
    public const string RouteForbiddenCode = "partner.route-forbidden";
    public const string OrganizationIdClaim = "partner_organization_id";
    public const string WorkspaceIdClaim = "partner_workspace_id";
    public const string AccountIdClaim = "partner_account_id";
    public const string KeyIdClaim = "partner_key_id";
    public const string ScopeClaim = "partner_scope";
    public const string OpenApiDocumentName = "partner-v1";
}

public sealed class PartnerApiOptions
{
    public const string ConfigurationSection = PartnerApiKeyDefaults.ConfigurationSection;
    public const string LiveEnvironment = "live";
    public const string TestEnvironment = "test";

    public string Environment { get; set; } = "";

    /// <summary>
    /// When true, Production may start with a process-local <see cref="IDistributedCache"/>.
    /// Isolated tests and gated live proofs may set this. Production hosts must not.
    /// </summary>
    public bool AllowProcessLocalCache { get; set; }

    public void ApplyEnvironmentDefault(IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(Environment))
            Environment = environment.IsProduction() ? LiveEnvironment : TestEnvironment;
    }
}

public sealed class PartnerApiConfigurationValidator(
    IHostEnvironment environment,
    IOptions<PartnerApiOptions> options,
    IDistributedCache cache) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"Partner API configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal IEnumerable<string> Validate()
    {
        if (environment.IsProduction() &&
            !string.Equals(options.Value.Environment, PartnerApiOptions.LiveEnvironment, StringComparison.Ordinal))
            yield return "PartnerApi:Environment must be live in Production.";

        if (environment.IsProduction() &&
            !options.Value.AllowProcessLocalCache &&
            cache is MemoryDistributedCache)
            yield return "Partner API requires a shared IDistributedCache in Production so revocation invalidates every instance. MemoryDistributedCache is process-local.";
    }
}

public static class PartnerApiKeySelector
{
    public static bool Matches(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var token = authorization["Bearer ".Length..].Trim();
        return token.StartsWith(PartnerApiKeyFormat.Prefix + "_", StringComparison.Ordinal);
    }
}

public sealed class DistributedPartnerApiKeyCache(IDistributedCache cache) : IPartnerApiKeyCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PartnerApiKeyCacheEntry?> GetAsync(string keyId, CancellationToken cancellationToken = default)
    {
        var payload = await cache.GetStringAsync(CacheKey(keyId), cancellationToken);
        return string.IsNullOrEmpty(payload)
            ? null
            : JsonSerializer.Deserialize<PartnerApiKeyCacheEntry>(payload, Json);
    }

    public Task SetAsync(PartnerApiKeyCacheEntry entry, CancellationToken cancellationToken = default) =>
        cache.SetStringAsync(
            CacheKey(entry.KeyId),
            JsonSerializer.Serialize(entry, Json),
            new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = IPartnerApiKeyCache.ValidEntryLifetime
            },
            cancellationToken);

    public Task InvalidateAsync(string keyId, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(CacheKey(keyId), cancellationToken);

    private static string CacheKey(string keyId) => "partner-api-key:" + keyId;
}

public sealed class PartnerApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    PartnerApiKeyService keys,
    IOptions<PartnerApiOptions> partnerApi,
    TimeProvider time)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!PartnerApiKeySelector.Matches(Context))
            return AuthenticateResult.NoResult();

        var authorization = Request.Headers.Authorization.FirstOrDefault() ?? "";
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : "";
        var parsed = PartnerApiKeyFormat.TryParse(token, out var credential);
        var authenticated = await keys.AuthenticateAsync(
            token,
            partnerApi.Value.Environment,
            time.GetUtcNow(),
            Context.RequestAborted);
        if (authenticated is null)
        {
            Logger.LogInformation(
                "Partner API key authentication failed for key {KeyId}.",
                parsed ? credential.KeyId : "unparsed");
            return AuthenticateResult.Fail("Invalid partner API key.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, authenticated.Record.ProvisionerAccountId.ToString("D")),
            new(ClaimTypes.Role, PartnerApiKeyDefaults.Role),
            new(PartnerApiKeyDefaults.OrganizationIdClaim, authenticated.Record.OrganizationId.ToString("D")),
            new(PartnerApiKeyDefaults.WorkspaceIdClaim, authenticated.Record.WorkspaceId.ToString("D")),
            new(PartnerApiKeyDefaults.AccountIdClaim, authenticated.Record.ProvisionerAccountId.ToString("D")),
            new(PartnerApiKeyDefaults.KeyIdClaim, authenticated.Record.KeyId)
        };
        foreach (var scope in authenticated.Scopes)
            claims.Add(new Claim(PartnerApiKeyDefaults.ScopeClaim, scope));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status401Unauthorized, PartnerApiKeyDefaults.UnauthorizedTitle, PartnerApiKeyDefaults.UnauthorizedCode);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(
            StatusCodes.Status403Forbidden,
            "The partner API key is missing a required scope.",
            PartnerApiKeyDefaults.InsufficientScopeCode);

    private Task WriteProblemAsync(int statusCode, string title, string code)
    {
        Response.Headers.CacheControl = "no-store";
        return Results.Problem(
                title: title,
                statusCode: statusCode,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["correlationId"] = Context.TraceIdentifier
                })
            .ExecuteAsync(Context);
    }
}

public static class PartnerApiAuthorization
{
    public static IServiceCollection AddPartnerApiAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, PartnerScopeAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            options.AddPolicy(PartnerApiKeyDefaults.Policy, policy =>
            {
                policy.AuthenticationSchemes.Add(PartnerApiKeyDefaults.Scheme);
                policy.RequireAuthenticatedUser();
                policy.RequireRole(PartnerApiKeyDefaults.Role);
            });

            foreach (var scope in PartnerApiKeyScopes.All)
            {
                options.AddPolicy(PolicyName(scope), policy =>
                {
                    policy.AuthenticationSchemes.Add(PartnerApiKeyDefaults.Scheme);
                    policy.RequireAuthenticatedUser();
                    policy.RequireRole(PartnerApiKeyDefaults.Role);
                    policy.AddRequirements(new PartnerScopeRequirement(scope));
                });
            }
        });
        return services;
    }

    public static string PolicyName(string scope) => PartnerApiKeyDefaults.Policy + ":" + scope;

    public static bool IsPartnerApiKey(ClaimsPrincipal principal) =>
        principal.Identities.Any(identity =>
            identity.IsAuthenticated &&
            string.Equals(identity.AuthenticationType, PartnerApiKeyDefaults.Scheme, StringComparison.Ordinal));
}

public sealed class PartnerScopeRequirement(string scope) : IAuthorizationRequirement
{
    public string Scope { get; } = scope;
}

public sealed class PartnerScopeAuthorizationHandler : AuthorizationHandler<PartnerScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PartnerScopeRequirement requirement)
    {
        if (context.User.Claims.Any(claim =>
                string.Equals(claim.Type, PartnerApiKeyDefaults.ScopeClaim, StringComparison.Ordinal) &&
                string.Equals(claim.Value, requirement.Scope, StringComparison.Ordinal)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public sealed class PartnerApiAllowedEndpointMetadata
{
    public static PartnerApiAllowedEndpointMetadata Instance { get; } = new();

    private PartnerApiAllowedEndpointMetadata()
    {
    }
}

public static class PartnerApiEndpointConventionExtensions
{
    public static RouteHandlerBuilder AllowPartnerApi(this RouteHandlerBuilder builder)
    {
        builder.WithMetadata(PartnerApiAllowedEndpointMetadata.Instance);
        return builder;
    }

    public static RouteGroupBuilder AllowPartnerApi(this RouteGroupBuilder builder)
    {
        builder.WithMetadata(PartnerApiAllowedEndpointMetadata.Instance);
        return builder;
    }
}

public sealed class PartnerApiIsolationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!PartnerApiAuthorization.IsPartnerApiKey(context.User) || context.GetEndpoint() is null)
        {
            await next(context);
            return;
        }

        if (context.GetEndpoint()?.Metadata.GetMetadata<PartnerApiAllowedEndpointMetadata>() is not null)
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        await Results.Problem(
                title: "A partner API key cannot call this route.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = PartnerApiKeyDefaults.RouteForbiddenCode,
                    ["correlationId"] = context.TraceIdentifier
                })
            .ExecuteAsync(context);
    }
}

public static class PartnerApiIsolationMiddlewareExtensions
{
    public static IApplicationBuilder UsePartnerApiIsolation(this IApplicationBuilder app) =>
        app.UseMiddleware<PartnerApiIsolationMiddleware>();
}

public static class PartnerApiRateLimitDefaults
{
    public const string PolicyName = "partner-api";
}

public sealed class PartnerApiRateLimiterPolicy : IRateLimiterPolicy<string>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        if (PartnerApiAuthorization.IsPartnerApiKey(httpContext.User))
            return RateLimitPartition.GetNoLimiter("partner-authenticated");

        return RateLimitPartition.GetFixedWindowLimiter(
            ControlIdentityAndIpRateLimiter.UnresolvedIdentityKey(httpContext),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = ControlIdentityAndIpRateLimiter.UnresolvedIdentityPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    }
}

public static class PartnerApiPrincipal
{
    public static bool TryRead(ClaimsPrincipal principal, out Guid organizationId, out Guid workspaceId, out Guid accountId, out string keyId)
    {
        organizationId = default;
        workspaceId = default;
        accountId = default;
        keyId = "";
        if (!PartnerApiAuthorization.IsPartnerApiKey(principal))
            return false;

        keyId = principal.FindFirstValue(PartnerApiKeyDefaults.KeyIdClaim) ?? "";
        return Guid.TryParse(principal.FindFirstValue(PartnerApiKeyDefaults.OrganizationIdClaim), out organizationId) &&
               Guid.TryParse(principal.FindFirstValue(PartnerApiKeyDefaults.WorkspaceIdClaim), out workspaceId) &&
               Guid.TryParse(principal.FindFirstValue(PartnerApiKeyDefaults.AccountIdClaim), out accountId) &&
               PartnerApiKeyFormat.IsKeyId(keyId);
    }
}
