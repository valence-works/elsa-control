using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Cockpit;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Core.Plans;
using ElsaControl.RuntimeBuilder.Core.RuntimeConfigurations;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.Extensions.Options;

namespace ElsaControl.Api.Workspace;

/// <summary>Uses the same admission and plan resolver as the durable lifecycle, without creating work.</summary>
public sealed class EngineProvisioningPreviewService(
    RuntimeConfigurationService configurations,
    IGovernedReleaseCatalogStore releaseCatalog,
    IElsaInstancePlanResolver resolver,
    DeploymentCockpitService cockpit,
    IEngineProvisioningTargetStore targets,
    IOptions<ElsaInstancePlanAuthorityOptions> authority,
    EngineProvisioningPlanContext planContext,
    IEnumerable<IEngineProvisioningModule> provisioningModules)
{
    public async Task<EngineProvisioningPreviewResponse> PreviewAsync(
        Guid workspaceId,
        EngineProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        var findings = new List<ElsaInstancePlanResolutionFinding>();
        var configurationName = "Recommended runtime";
        EngineProvisioningPreviewResponse Invalid(string code, string message, string scope) =>
            new(false, [.. findings, ElsaInstancePlanResolutionFinding.Error(code, message, scope)],
                configurationName, null, null, null);

        if (request.Intent is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 ||
            request.Name.Any(char.IsControl) || string.IsNullOrWhiteSpace(request.Slug) ||
            request.ApplicationId == Guid.Empty || request.EnvironmentId == Guid.Empty)
            return Invalid("provisioning.request.invalid", "Provide an engine name, application, environment and release.", "engine");
        if (request.Intent.Release.RequestedVersion is null)
            return Invalid("provisioning.release.version-required", "Choose an exact governed release version.", "release");
        if (request.Intent.DesiredLifecycle != ElsaDesiredLifecycle.Running)
            return Invalid("provisioning.lifecycle.invalid", "A new engine must be provisioned in the running state.", "engine");

        string slug;
        try { slug = ElsaInstanceSlug.Normalize(request.Slug); }
        catch (ArgumentException) { return Invalid("provisioning.slug.invalid", "Use a valid engine address.", "slug"); }

        var deployment = await cockpit.GetCockpitAsync(workspaceId, cancellationToken);
        var application = deployment.Applications.SingleOrDefault(x => x.Id == request.ApplicationId.ToString());
        var environment = application?.Environments.SingleOrDefault(x => x.Id == request.EnvironmentId.ToString());
        if (environment is null)
            return Invalid("provisioning.target.unavailable", "Select an application and environment in this workspace.", "environment");
        var availableTargets = await targets.GetAvailableTargetsAsync(workspaceId, cancellationToken);
        if (!availableTargets.Any(x => x.ApplicationId == request.ApplicationId && x.EnvironmentId == request.EnvironmentId))
            return Invalid("provisioning.target.occupied", "This environment already has an engine or deployment state. Select an empty environment.", "environment");

        RuntimeBuilderIntent? builderIntent = request.BuilderIntent;
        string? sourceRevision = null;
        if (request.RuntimeConfigurationId is { } configurationId)
        {
            var configuration = await configurations.GetAsync(workspaceId, configurationId, cancellationToken);
            if (configuration is null)
                return Invalid("provisioning.configuration.unavailable", "The saved configuration is unavailable in this workspace.", "runtimeConfiguration");
            configurationName = configuration.Name;
            sourceRevision = Hash(configuration.IntentJson);
            try { builderIntent ??= RuntimeConfigurationService.DeserializeIntent(configuration.IntentJson); }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            { return Invalid("provisioning.configuration.invalid", "The saved configuration cannot be read.", "runtimeConfiguration"); }
        }
        else if (builderIntent is not null)
            configurationName = "Custom runtime";

        builderIntent ??= new RuntimeBuilderIntent(new RuntimeImageSelection("elsa-instance", null, null, null), [], [], [], null);
        var projection = ManagedRuntimeConfigurationAdapter.Project(builderIntent);
        findings.AddRange(projection.Findings);
        if (!projection.CanProvision || projection.BuilderIntent is null)
            return new(false, findings, configurationName, null, null, null);

        var snapshotJson = RuntimeConfigurationService.SerializeIntent(projection.BuilderIntent);
        var configurationDigest = Hash(snapshotJson);
        try
        {
            new ElsaInstanceProvisioningContext(request.ApplicationId, request.EnvironmentId,
                snapshotJson, configurationDigest, request.RuntimeConfigurationId, configurationName).Validate();
        }
        catch (ArgumentException)
        {
            return Invalid("provisioning.configuration.invalid", "The configuration exceeds the supported size or nesting limits.", "runtimeConfiguration");
        }

        if (!ManagedElsaInstanceEndpoints.MatchesInitialLaunchProfile(request.Intent.Placement))
            return Invalid("provisioning.hosting.unavailable", "The selected hosting options are unavailable.", "hosting");
        var release = await ManagedElsaInstanceEndpoints.FindEligibleCatalogEntryAsync(releaseCatalog, request.Intent, cancellationToken);
        if (release is null || !CatalogElsaInstanceLifecycleResolutionInputSource.TryBuildManifest(release, out var admission))
            return Invalid("provisioning.release.unavailable", "The selected release cannot be provisioned. Choose an available governed release.", "release");
        if (!authority.Value.TryGetOrigin(out var origin))
            return Invalid("provisioning.host.not-configured", "The provisioning host is not configured. Contact the host administrator.", "hosting");

        var resolution = await resolver.ResolveAsync(new ElsaInstancePlanResolutionRequest(
            request.Intent, projection.BuilderIntent, admission, "provisioning-preview",
            $"{origin}/api/workspaces/{workspaceId:D}/instances/{Guid.Empty:D}/resolved-plans/provisioning-preview", workspaceId,
            GovernedSecretReferences: planContext.GovernedSecretReferences), cancellationToken);
        findings.AddRange(resolution.Findings);
        if (!resolution.Succeeded || resolution.Reference is null || resolution.Plan is null)
            return new(false, findings, configurationName, null, null, null);

        var providers = provisioningModules.ToArray();
        if (providers.Length != 1)
            return Invalid("provisioning.provider.unavailable", "A provisioning provider must be configured for this host.", "provider");
        var providerFindings = providers[0].ValidatePlan(resolution.Plan, request.Intent.Placement.RegionCode);
        findings.AddRange(providerFindings);
        if (providerFindings.Any(x => !IsNonBlockingProviderFinding(x.Severity)))
            return new(false, findings, configurationName, null, null, null);

        // Include the source revision and resolved plan so a changed saved configuration,
        // package catalog or governed release cannot be silently accepted after review.
        var previewDigest = Hash(JsonSerializer.Serialize(new
        {
            workspaceId, request.ApplicationId, request.EnvironmentId, Name = request.Name.Trim(), Slug = slug,
            Intent = request.Intent.ComputeCanonicalHash(), request.RuntimeConfigurationId, sourceRevision,
            configurationDigest, resolution.Reference.ContentHash
        }));
        return new(true, findings, configurationName, configurationDigest, previewDigest, projection.BuilderIntent)
        { ResolvedPlanDigest = resolution.Reference.ContentHash };
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsNonBlockingProviderFinding(string severity) =>
        string.Equals(severity, "info", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase);
}

public sealed record EngineProvisioningPlanContext(IReadOnlyDictionary<string, string>? GovernedSecretReferences);

public sealed record EngineProvisioningRequest(
    string? Name,
    string? Slug,
    Guid ApplicationId,
    Guid EnvironmentId,
    ElsaInstanceIntent? Intent,
    Guid? RuntimeConfigurationId = null,
    RuntimeBuilderIntent? BuilderIntent = null,
    string? PreviewDigest = null);

public sealed record EngineProvisioningPreviewResponse(
    bool CanProvision,
    IReadOnlyList<ElsaInstancePlanResolutionFinding> Findings,
    string ConfigurationName,
    string? ConfigurationDigest,
    string? PreviewDigest,
    RuntimeBuilderIntent? BuilderIntent)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ResolvedPlanDigest { get; init; }
}
