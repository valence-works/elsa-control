using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Authentication;

public static class HubAccountIdentityDefaults
{
    public const string Scheme = "HubAccountJwt";
    public const string ConfigurationSection = "Authentication:HubAccount";
}

public sealed class HubAccountIdentityOptions
{
    public bool Enabled { get; init; }
    public string? Issuer { get; init; }
    public string Audience { get; init; } = "authenticated";
    // Local test signing only. Production must validate the issuer's published JWKS.
    public string? TestSigningKey { get; init; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Issuer) &&
                                !string.IsNullOrWhiteSpace(Audience);
}

public sealed class HubAccountIdentityConfigurationValidator(
    IOptions<HubAccountIdentityOptions> options,
    IOptions<CloudAccountIdentityOptions> cloudAccountOptions,
    IHostEnvironment environment) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        if (!value.Enabled)
            return Task.CompletedTask;

        if (!Uri.TryCreate(value.Issuer, UriKind.Absolute, out var issuer) ||
            (issuer.Scheme != Uri.UriSchemeHttps && !environment.IsEnvironment("Testing")) ||
            !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment) ||
            string.IsNullOrWhiteSpace(value.Audience) ||
            (!environment.IsEnvironment("Testing") && !string.IsNullOrEmpty(value.TestSigningKey)))
            throw new InvalidOperationException("Hub account JWT validation requires an HTTPS issuer, an audience, and published signing keys.");

        var cloud = cloudAccountOptions.Value;
        if (cloud.Enabled && IssuersEqual(value.Issuer, cloud.Issuer))
            throw new InvalidOperationException("Hub account and Cloud account JWT issuers must be different when both paths are enabled.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool IssuersEqual(string? left, string? right)
    {
        var normalizedLeft = NormalizeIssuer(left);
        var normalizedRight = NormalizeIssuer(right);
        return normalizedLeft is not null &&
               string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeIssuer(string? issuer)
    {
        if (string.IsNullOrWhiteSpace(issuer))
            return null;

        return issuer.Trim().TrimEnd('/');
    }
}

public static class HubAccountJwtBearer
{
    public static void Configure(JwtBearerOptions options, HubAccountIdentityOptions hub)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        if (!string.IsNullOrWhiteSpace(hub.Issuer) && string.IsNullOrWhiteSpace(hub.TestSigningKey))
            options.MetadataAddress = $"{hub.Issuer.TrimEnd('/')}/.well-known/openid-configuration";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = string.IsNullOrWhiteSpace(hub.Issuer) ? null : hub.Issuer,
            ValidateAudience = true,
            ValidAudience = string.IsNullOrWhiteSpace(hub.Audience) ? null : hub.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = string.IsNullOrWhiteSpace(hub.TestSigningKey)
                ? null
                : new SymmetricSecurityKey(Encoding.UTF8.GetBytes(hub.TestSigningKey)),
            ValidAlgorithms = string.IsNullOrWhiteSpace(hub.TestSigningKey)
                ? [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256]
                : [SecurityAlgorithms.HmacSha256],
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = context =>
            {
                if (!HubAccountTokenSelector.IsValidatedHubAccount(context.Principal, hub, context.SecurityToken))
                    context.Fail("A signed Elsa Hub user session is required.");
                return Task.CompletedTask;
            }
        };
    }
}

public static class HubAccountTokenSelector
{
    // The issuer is read only to select a validator. This does not authenticate the token.
    public static bool Matches(HttpContext context, HubAccountIdentityOptions options)
    {
        if (!options.Enabled)
            return false;

        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!options.IsConfigured)
            return true;

        try
        {
            var token = new JwtSecurityTokenHandler().ReadJwtToken(authorization[7..].Trim());
            return string.Equals(token.Issuer, options.Issuer, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsValidatedHubAccount(
        ClaimsPrincipal? principal,
        HubAccountIdentityOptions options,
        SecurityToken? token = null) =>
        options.IsConfigured && principal?.Identity?.IsAuthenticated == true &&
        string.Equals(principal.FindFirst("iss")?.Value, options.Issuer, StringComparison.Ordinal) &&
        Guid.TryParse(principal.FindFirst("sub")?.Value, out _) &&
        string.Equals(principal.FindFirst("role")?.Value, "authenticated", StringComparison.Ordinal) &&
        IsNonAnonymous(principal, token);

    private static bool IsNonAnonymous(ClaimsPrincipal principal, SecurityToken? token)
    {
        if (token is JsonWebToken jwt)
            return !jwt.TryGetPayloadValue("is_anonymous", out object? value) || value is false;

        if (token is JwtSecurityToken jwtSecurityToken)
            return !jwtSecurityToken.Payload.TryGetValue("is_anonymous", out var value) || value is false;

        if (token is not null)
            return false;

        var claims = principal.FindAll("is_anonymous").ToArray();
        return claims.Length == 0 ||
               (claims.Length == 1 &&
                claims[0].ValueType == ClaimValueTypes.Boolean &&
                string.Equals(claims[0].Value, "false", StringComparison.Ordinal));
    }
}
