using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public sealed class ControlIdentityReader(IOptions<ControlIdentityOptions> options) :
    IWorkspaceIdentityReader,
    IAuthenticatedControlSessionReader
{
    private readonly ControlIdentityOptions _options = options.Value;

    public static bool HasBearerToken(HttpContext context)
    {
        foreach (var header in context.Request.Headers.Authorization)
        {
            if (header is null)
                continue;

            foreach (var value in header.Split(',', StringSplitOptions.TrimEntries))
            {
                var scheme = value.TrimStart();
                var hasBearerScheme = scheme.Length == "Bearer".Length ||
                                      (scheme.Length > "Bearer".Length && char.IsWhiteSpace(scheme["Bearer".Length]));
                if (hasBearerScheme && scheme.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    public async ValueTask<TrustedWorkspaceIdentity?> ReadAsync(HttpContext context)
    {
        var result = await context.AuthenticateAsync(ControlIdentityDefaults.Scheme);
        var user = result.Succeeded
            ? result.Principal
            : HasBearerToken(context) ? null : context.User;
        return ControlClaimsIdentityMapper.ToTrustedWorkspaceIdentity(user, _options);
    }

    public async ValueTask<AuthenticatedControlSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var result = await context.AuthenticateAsync(ControlIdentityDefaults.Scheme);
        if (!result.Succeeded || result.Principal is null)
            return null;

        var identity = ControlClaimsIdentityMapper.ToTrustedWorkspaceIdentity(result.Principal, _options);
        return identity is not null && TryReadBearerExpiry(result.Principal, out var expiresAt)
            ? new AuthenticatedControlSession(identity, expiresAt)
            : null;
    }

    internal static bool TryReadBearerExpiry(ClaimsPrincipal principal, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        var value = principal.FindFirst(JwtRegisteredClaimNames.Exp)?.Value
                    ?? principal.FindFirst("exp")?.Value
                    ?? principal.FindFirst(ClaimTypes.Expiration)?.Value;
        if (!long.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return false;

        try
        {
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

internal static class ControlClaimsIdentityMapper
{
    public static TrustedWorkspaceIdentity? ToTrustedWorkspaceIdentity(ClaimsPrincipal? user, ControlIdentityOptions options)
    {
        if (user?.Identity is not { IsAuthenticated: true })
            return null;

        var subject = ClaimValue(user, options.Claims.Subject)
            ?? ClaimValue(user, ClaimTypes.NameIdentifier);
        var displayName = FirstClaimValue(user, options.Claims.DisplayName)
            ?? ClaimValue(user, JwtRegisteredClaimNames.Name)
            ?? ClaimValue(user, ClaimTypes.Name);
        var email = FirstClaimValue(user, options.Claims.Email)
            ?? ClaimValue(user, JwtRegisteredClaimNames.Email)
            ?? ClaimValue(user, ClaimTypes.Email);
        if (options.IsEntraMultiTenant)
            return EntraMultiTenantIdentity.ToTrustedWorkspaceIdentity(user, options.Entra, subject, displayName, email);

        var issuer = ClaimValue(user, JwtRegisteredClaimNames.Iss)
            ?? options.Issuer
            ?? options.Authority;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            return null;

        return new TrustedWorkspaceIdentity(issuer, subject, displayName, email);
    }

    private static string? FirstClaimValue(ClaimsPrincipal principal, IEnumerable<string> types)
    {
        foreach (var type in types)
        {
            var value = ClaimValue(principal, type);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string? ClaimValue(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value;
}

public sealed class CompositeWorkspaceIdentityReader(IEnumerable<IWorkspaceIdentityReader> readers) :
    IWorkspaceIdentityReader,
    IAuthenticatedControlSessionReader
{
    private readonly IReadOnlyList<IWorkspaceIdentityReader> _readers = readers.ToArray();

    public async ValueTask<TrustedWorkspaceIdentity?> ReadAsync(HttpContext context)
    {
        foreach (var reader in _readers)
        {
            var identity = await reader.ReadAsync(context);
            if (identity is not null)
                return identity;
            if (reader is ControlIdentityReader && ControlIdentityReader.HasBearerToken(context))
                return null;
        }

        return null;
    }

    public async ValueTask<AuthenticatedControlSession?> ReadAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        foreach (var reader in _readers)
        {
            if (reader is not IAuthenticatedControlSessionReader sessionReader)
                continue;

            var session = await sessionReader.ReadAsync(context, cancellationToken);
            if (session is not null)
                return session;

            // A bearer header is an explicit source selection. Never let an
            // invalid or lifetime-less bearer fall through to a cookie/header.
            if (reader is ControlIdentityReader && ControlIdentityReader.HasBearerToken(context))
                return null;
        }

        return null;
    }
}
