using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ElsaControl.PackageCatalog.Core.Accounts;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Multi-tenant Microsoft Entra sign-in: per-token issuer validation, and the mapping of a
/// validated principal to the Control identity that keys its account and organization.
/// </summary>
internal static class EntraMultiTenantIdentity
{
    public const string TenantIdClaim = "tid";
    public const string ObjectIdClaim = "oid";

    // Every personal Microsoft account signs in through this tenant. Only work or school
    // accounts are admitted.
    private const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

    public static string IssuerFor(string tenantId) => $"https://login.microsoftonline.com/{tenantId}/v2.0";

    /// <summary>
    /// Replaces fixed-issuer validation: the issuer must be the Entra v2.0 issuer of the tenant
    /// named by the token's own <c>tid</c>, so no tenant can present another tenant's issuer.
    /// </summary>
    public static void ConfigureIssuerValidation(TokenValidationParameters parameters)
    {
        parameters.ValidateIssuer = true;
        parameters.ValidIssuer = null;
        parameters.IssuerValidator = ValidateIssuer;
    }

    public static string ValidateIssuer(string issuer, SecurityToken token, TokenValidationParameters parameters)
    {
        var tenantId = token switch
        {
            JsonWebToken jwt => jwt.TryGetPayloadValue<string>(TenantIdClaim, out var value) ? value : null,
            JwtSecurityToken jwt => jwt.Payload.TryGetValue(TenantIdClaim, out var value) ? value as string : null,
            _ => null
        };

        if (!TryNormalizeTenantId(tenantId, out _) || !string.Equals(issuer, IssuerFor(tenantId), StringComparison.Ordinal))
            throw new SecurityTokenInvalidIssuerException(
                "The token issuer is not the Microsoft Entra issuer of its work or school tenant.")
            {
                InvalidIssuer = issuer
            };

        return issuer;
    }

    /// <summary>
    /// Customer accounts are keyed by the tenant issuer and the immutable object id
    /// (<c>oid</c>), never by display claims. Dogfood tenants keep the configured subject claim
    /// so their existing accounts resolve unchanged. Without a valid <c>tid</c> there is no identity.
    /// </summary>
    public static TrustedWorkspaceIdentity? ToTrustedWorkspaceIdentity(
        ClaimsPrincipal user,
        ControlEntraIdentityOptions options,
        string? configuredSubject,
        string? displayName,
        string? email)
    {
        if (!TryReadTenantId(user, out var tenantId))
            return null;

        var issuer = IssuerFor(tenantId);
        if (IsDogfoodTenant(options, tenantId))
            return string.IsNullOrWhiteSpace(configuredSubject)
                ? null
                : new TrustedWorkspaceIdentity(issuer, configuredSubject, displayName, email);

        var objectId = user.FindFirst(ObjectIdClaim)?.Value;
        return string.IsNullOrWhiteSpace(objectId)
            ? null
            : new TrustedWorkspaceIdentity(issuer, objectId, displayName, email) { CustomerEntraTenantId = tenantId };
    }

    /// <summary>
    /// A customer tenant administrator can assign this multi-tenant app's roles to their own
    /// users, so operator roles are honored only from Valence dogfood tenants.
    /// </summary>
    public static bool MayHoldOperatorRoles(ClaimsPrincipal user, ControlIdentityOptions options) =>
        !options.IsEntraMultiTenant || (TryReadTenantId(user, out var tenantId) && IsDogfoodTenant(options.Entra, tenantId));

    public static bool TryNormalizeTenantId([NotNullWhen(true)] string? value, [NotNullWhen(true)] out string? tenantId)
    {
        tenantId = Guid.TryParseExact(value, "D", out var parsed) ? parsed.ToString("D") : null;
        return tenantId is not null && tenantId != ConsumerTenantId;
    }

    private static bool TryReadTenantId(ClaimsPrincipal user, [NotNullWhen(true)] out string? tenantId) =>
        TryNormalizeTenantId(user.FindFirst(TenantIdClaim)?.Value, out tenantId);

    private static bool IsDogfoodTenant(ControlEntraIdentityOptions options, string tenantId) =>
        options.DogfoodTenantIds.Any(dogfood => TryNormalizeTenantId(dogfood, out var normalized) && normalized == tenantId);
}
