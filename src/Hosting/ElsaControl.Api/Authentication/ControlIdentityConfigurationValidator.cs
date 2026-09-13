using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public sealed class ControlIdentityConfigurationValidator(
    IHostEnvironment environment,
    IConfiguration configuration,
    IOptions<ControlIdentityOptions> options) : IHostedService
{
    private readonly ControlIdentityOptions _options = options.Value;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"Control identity configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IEnumerable<string> Validate()
    {
        var isProduction = environment.IsProduction();
        if (isProduction && !_options.RequireHttpsMetadata)
            yield return "Authentication:ControlIdentity:RequireHttpsMetadata must be true in Production.";

        if (isProduction && configuration.GetValue<bool>(TrustedHeaderWorkspaceIdentityReader.EnabledConfigurationKey))
            yield return "Authentication:WorkspaceTrustedHeaders:Enabled must be false in Production.";

        if (!string.IsNullOrWhiteSpace(_options.ClientId) && string.IsNullOrWhiteSpace(_options.Authority))
            yield return "Authentication:ControlIdentity:Authority is required when ClientId is configured.";

        if (!string.IsNullOrWhiteSpace(_options.ClientSecret) && string.IsNullOrWhiteSpace(_options.ClientId))
            yield return "Authentication:ControlIdentity:ClientId is required when ClientSecret is configured.";

        if (_options.Entra.MultiTenant)
        {
            if (_options.Provider != ControlIdentityProviderKind.MicrosoftEntra)
                yield return "Authentication:ControlIdentity:Entra:MultiTenant requires Provider MicrosoftEntra.";
            if (!string.IsNullOrWhiteSpace(_options.Issuer))
                yield return "Authentication:ControlIdentity:Issuer must be empty when Entra:MultiTenant is enabled; each token is validated against its own tenant issuer.";
            if (_options.Entra.DogfoodTenantIds.Any(id => !EntraMultiTenantIdentity.TryNormalizeTenantId(id, out _)))
                yield return "Authentication:ControlIdentity:Entra:DogfoodTenantIds must contain work or school tenant ids (GUIDs).";
            if (configuration.GetValue<bool>($"{AdminAuthorizationOptions.ConfigurationSection}:{nameof(AdminAuthorizationOptions.AllowAuthenticatedCustomerSession)}"))
                yield return "Authentication:Admin:AllowAuthenticatedCustomerSession must be false when Entra:MultiTenant is enabled; any customer tenant could otherwise administer Control.";
        }

        if (_options.Provider == ControlIdentityProviderKind.Keycloak)
        {
            if (string.IsNullOrWhiteSpace(_options.Authority))
                yield return "Authentication:ControlIdentity:Authority is required for Keycloak.";
            if (string.IsNullOrWhiteSpace(_options.ClientId))
                yield return "Authentication:ControlIdentity:ClientId is required for Keycloak.";
            if (isProduction && string.IsNullOrWhiteSpace(_options.ClientSecret))
                yield return "Authentication:ControlIdentity:ClientSecret is required for Keycloak in Production.";
        }
    }
}
