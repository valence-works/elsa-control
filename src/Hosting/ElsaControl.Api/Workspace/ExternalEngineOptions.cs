using ElsaControl.Api.Authentication;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

public static class ExternalEngineDefaults
{
    public const string ConfigurationSection = "ElsaControl:ExternalEngines";
    public const string PairingUnavailableCode = "external_engine_pairing_unavailable";
}

public sealed class ExternalEngineOptions
{
    public const string ConfigurationSection = ExternalEngineDefaults.ConfigurationSection;

    /// <summary>
    /// Organization ids that may create or repair an external-engine pairing. An empty
    /// list (the default) refuses every organization. Production ships empty. Staging
    /// lists only rehearsal organizations; the Hosted smoke synthetic owner organization
    /// must stay off this list so QA can prove the refusal.
    /// </summary>
    public string[] PairingAllowedOrganizationIds { get; init; } = [];

    public bool AllowsPairing(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
            return false;

        foreach (var value in PairingAllowedOrganizationIds)
        {
            if (TryParseOrganizationId(value, out var allowed) && allowed == organizationId)
                return true;
        }

        return false;
    }

    internal IEnumerable<string> Validate()
    {
        var values = PairingAllowedOrganizationIds ?? [];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryParseOrganizationId(values[index], out _))
            {
                yield return
                    $"{ConfigurationSection}:PairingAllowedOrganizationIds[{index}] must be a GUID organization id.";
            }
        }
    }

    internal static bool TryParseOrganizationId(string? value, out Guid organizationId)
    {
        organizationId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
            return false;

        return Guid.TryParse(value, out organizationId) && organizationId != Guid.Empty;
    }
}

public interface IExternalEnginePairingAvailability
{
    bool IsPairingAllowed(Guid organizationId);
}

public sealed class ConfiguredExternalEnginePairingAvailability(
    IOptions<ExternalEngineOptions> options) : IExternalEnginePairingAvailability
{
    public bool IsPairingAllowed(Guid organizationId) => options.Value.AllowsPairing(organizationId);
}

public sealed class ExternalEngineConfigurationValidator(
    IOptions<ExternalEngineOptions> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = options.Value.Validate().ToArray();
        if (errors.Length > 0)
            throw new InvalidOperationException($"External engine configuration is invalid: {string.Join(" ", errors)}");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class ExternalEnginePairingEndpointFilters
{
    public static RouteHandlerBuilder RequireExternalEnginePairingAllowed(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var access = httpContext.GetWorkspaceAccess();
            var availability = httpContext.RequestServices.GetRequiredService<IExternalEnginePairingAvailability>();
            if (availability.IsPairingAllowed(access.OrganizationId))
                return await next(context);

            httpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("ElsaControl.Api.Workspace.ExternalEnginePairing")
                .LogWarning(
                    "External-engine pairing refused for organization {OrganizationId} on {Route}.",
                    access.OrganizationId,
                    httpContext.Request.Path.Value);

            return Results.Problem(
                title: "External engine pairing is not available.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = ExternalEngineDefaults.PairingUnavailableCode
                });
        });
}
