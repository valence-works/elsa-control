using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class CloudAccountIdentityDefaults
{
    public const string Scheme = "CloudAccountJwt";
    public const string SelectorScheme = "CustomerBearerSelector";
    public const string ConfigurationSection = "Authentication:CloudAccount";
}

public sealed class CloudAccountIdentityOptions
{
    public bool Enabled { get; init; }
    public string? Issuer { get; init; }
    public string Audience { get; init; } = "authenticated";
    // Local test signing only. Production must validate the issuer's published JWKS.
    public string? TestSigningKey { get; init; }

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(Issuer) &&
                                !string.IsNullOrWhiteSpace(Audience);
}

public sealed class CloudAccountIdentityConfigurationValidator(
    IOptions<CloudAccountIdentityOptions> options,
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
            throw new InvalidOperationException("Cloud account JWT validation requires an HTTPS issuer, an audience, and published signing keys.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class CloudAccountTokenSelector
{
    // The issuer is read only to select a validator. This does not authenticate the token.
    public static bool Matches(HttpContext context, CloudAccountIdentityOptions options)
    {
        if (!options.IsConfigured) return false;
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

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

    public static bool IsValidatedCloudAccount(ClaimsPrincipal? principal, CloudAccountIdentityOptions options) =>
        options.IsConfigured && principal?.Identity?.IsAuthenticated == true &&
        string.Equals(principal.FindFirst("iss")?.Value, options.Issuer, StringComparison.Ordinal) &&
        Guid.TryParse(principal.FindFirst("sub")?.Value, out _) &&
        string.Equals(principal.FindFirst("role")?.Value, "authenticated", StringComparison.Ordinal);
}
