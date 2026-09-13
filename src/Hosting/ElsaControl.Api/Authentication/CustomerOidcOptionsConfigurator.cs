using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Authentication;

internal static class CustomerOidcOptionsConfigurator
{
    public static void Configure(OpenIdConnectOptions options, ControlIdentityOptions controlIdentity)
    {
        options.SignInScheme = CustomerAuthenticationDefaults.CookieScheme;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.SaveTokens = true;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = controlIdentity.RequireHttpsMetadata;
        options.Authority = string.IsNullOrWhiteSpace(controlIdentity.Authority) ? null : controlIdentity.Authority;
        options.ClientId = string.IsNullOrWhiteSpace(controlIdentity.ClientId) ? null : controlIdentity.ClientId;
        options.ClientSecret = string.IsNullOrWhiteSpace(controlIdentity.ClientSecret) ? null : controlIdentity.ClientSecret;
        options.CallbackPath = PathStringFromUri(controlIdentity.RedirectUri, CustomerAuthenticationDefaults.CallbackPath);
        options.CorrelationCookie.Path = options.CallbackPath;
        options.NonceCookie.Path = options.CallbackPath;
        options.SignedOutCallbackPath = PathStringFromUri(controlIdentity.PostLogoutRedirectUri, "/api/auth/logout-callback");
        options.SignedOutRedirectUri = string.IsNullOrWhiteSpace(controlIdentity.PostLogoutRedirectUri)
            ? CustomerAuthenticationDefaults.DefaultReturnPath
            : controlIdentity.PostLogoutRedirectUri;
        options.Scope.Clear();
        foreach (var scope in controlIdentity.Scopes.Where(scope => !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim()).Distinct(StringComparer.Ordinal))
            options.Scope.Add(scope);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrWhiteSpace(controlIdentity.Issuer) || !string.IsNullOrWhiteSpace(controlIdentity.Authority),
            ValidIssuer = string.IsNullOrWhiteSpace(controlIdentity.Issuer)
                ? (string.IsNullOrWhiteSpace(controlIdentity.Authority) ? null : controlIdentity.Authority)
                : controlIdentity.Issuer,
            NameClaimType = controlIdentity.Claims.DisplayName.FirstOrDefault() ?? "name",
            RoleClaimType = "role",
            ValidateAudience = !string.IsNullOrWhiteSpace(controlIdentity.ClientId),
            ValidAudience = string.IsNullOrWhiteSpace(controlIdentity.ClientId) ? null : controlIdentity.ClientId
        };
        if (controlIdentity.IsEntraMultiTenant)
            EntraMultiTenantIdentity.ConfigureIssuerValidation(options.TokenValidationParameters);
        options.Events.OnTokenValidated = context =>
        {
            if (context.Properties is { } properties)
                properties.StoreTokens(properties.GetTokens().Where(token => token.Name == "id_token"));

            return Task.CompletedTask;
        };
    }

    private static PathString PathStringFromUri(string? uri, string fallback)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return new PathString(fallback);

        if (Uri.TryCreate(uri, UriKind.Absolute, out var absolute))
            return new PathString(absolute.AbsolutePath);

        return uri.StartsWith("/", StringComparison.Ordinal)
            ? new PathString(uri)
            : new PathString(fallback);
    }
}
