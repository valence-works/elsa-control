namespace ElsaControl.Api.Authentication;

public static class ControlIdentityDefaults
{
    public const string Scheme = "ControlOidcJwt";
    public const string ConfigurationSection = "Authentication:ControlIdentity";
}

public sealed class ControlIdentityOptions
{
    public ControlIdentityProviderKind Provider { get; init; } = ControlIdentityProviderKind.GenericOidc;
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string? Issuer { get; init; }
    public string? SymmetricSigningKey { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string[] Scopes { get; init; } = ["openid", "profile", "email"];
    public string? RedirectUri { get; init; }
    public string? PostLogoutRedirectUri { get; init; }
    public bool RequireHttpsMetadata { get; init; } = true;
    public ControlIdentityClaimOptions Claims { get; init; } = new();
    public ControlEntraIdentityOptions Entra { get; init; } = new();

    public bool IsCustomerLoginConfigured =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId);

    public bool IsEntraMultiTenant => Provider == ControlIdentityProviderKind.MicrosoftEntra && Entra.MultiTenant;
}

public sealed class ControlEntraIdentityOptions
{
    /// <summary>
    /// Accepts work or school sign-ins from any Microsoft Entra tenant, validating each token's
    /// issuer against its own <c>tid</c>. A customer tenant's first sign-in mints its organization.
    /// </summary>
    public bool MultiTenant { get; init; }

    /// <summary>
    /// Valence-operated tenants. Their users keep the dogfood identity keying and never take the
    /// customer organization mint.
    /// </summary>
    public string[] DogfoodTenantIds { get; init; } = [];
}

public sealed class ControlIdentityClaimOptions
{
    public string Subject { get; init; } = "sub";
    public string[] DisplayName { get; init; } = ["name"];
    public string[] Email { get; init; } = ["email"];
}

public enum ControlIdentityProviderKind
{
    GenericOidc,
    MicrosoftEntra,
    Auth0,
    Keycloak,
    Custom
}
