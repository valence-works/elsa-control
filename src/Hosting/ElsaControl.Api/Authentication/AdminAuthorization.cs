using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Authentication;

public static class AdminAuthorization
{
    public const string Policy = "AdminApi";
    public const string ControlAdminRole = "control_admin";

    public static IServiceCollection AddCatalogAuthorization(this IServiceCollection services)
    {
        services.AddOptions<AdminAuthorizationOptions>()
            .BindConfiguration(AdminAuthorizationOptions.ConfigurationSection);
        services.AddSingleton<IAuthorizationHandler, AdminApiAuthorizationHandler>();
        services.AddAuthorization(options =>
            options.AddPolicy(Policy, policy =>
            {
                policy.AuthenticationSchemes.Add(ApiKeyAuthenticationDefaults.Scheme);
                policy.AuthenticationSchemes.Add(CustomerAuthenticationDefaults.CookieScheme);
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new AdminApiRequirement());
            }));
        return services;
    }

    public static bool HasControlAdminRole(ClaimsPrincipal principal) =>
        principal.Claims.Any(claim =>
            IsRoleClaim(claim.Type) &&
            string.Equals(claim.Value, ControlAdminRole, StringComparison.OrdinalIgnoreCase));

    private static bool HasApiKeyIdentity(ClaimsPrincipal principal) =>
        principal.Identities.Any(identity =>
            identity.IsAuthenticated &&
            string.Equals(identity.AuthenticationType, ApiKeyAuthenticationDefaults.Scheme, StringComparison.Ordinal));

    private static bool HasAuthenticatedIdentity(ClaimsPrincipal principal) =>
        principal.Identities.Any(identity => identity.IsAuthenticated);

    private static bool IsRoleClaim(string type) =>
        string.Equals(type, ClaimTypes.Role, StringComparison.Ordinal) ||
        string.Equals(type, "role", StringComparison.Ordinal) ||
        string.Equals(type, "roles", StringComparison.Ordinal);

    private sealed class AdminApiRequirement : IAuthorizationRequirement;

    private sealed class AdminApiAuthorizationHandler(
        IOptions<AdminAuthorizationOptions> options) : AuthorizationHandler<AdminApiRequirement>
    {
        protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminApiRequirement requirement)
        {
            if (HasApiKeyIdentity(context.User) || HasControlAdminRole(context.User))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            if (options.Value.AllowAuthenticatedCustomerSession &&
                HasAuthenticatedIdentity(context.User))
                context.Succeed(requirement);

            return Task.CompletedTask;
        }
    }
}

public sealed class AdminAuthorizationOptions
{
    public const string ConfigurationSection = "Authentication:Admin";

    /// <summary>
    /// Allows any authenticated customer session to use the global admin API. Keep this off in
    /// published deployments; production authorizes operators through the <c>control_admin</c> role
    /// or the admin API key. Local Keycloak may enable it for first-run convenience.
    /// </summary>
    public bool AllowAuthenticatedCustomerSession { get; init; }
}
