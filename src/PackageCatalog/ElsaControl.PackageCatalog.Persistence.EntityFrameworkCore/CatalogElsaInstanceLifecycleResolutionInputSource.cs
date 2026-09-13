using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.RuntimeBuilder.Abstractions;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using Microsoft.EntityFrameworkCore;

namespace ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;

/// <summary>
/// Server-owned origin used to construct dereferenceable resolved-plan references for lifecycle
/// workers. It is intentionally required at runtime; a placeholder authority would make a
/// persisted plan look valid while pointing at an endpoint that cannot serve it.
/// </summary>
public sealed class ElsaInstancePlanAuthorityOptions
{
    public const string ConfigurationSection = "ControlPlane";

    public string? Origin { get; init; }

    public bool TryGetOrigin(out string origin)
    {
        origin = "";
        var value = Origin?.Trim();
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/"))
            return false;

        origin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}

/// <summary>
/// Reconstructs the resolver input for a claimed managed-instance operation from
/// the durable catalog projection. The request body and producer payload are not
/// available at this boundary; only the exact admitted catalog row is projected.
/// </summary>
public sealed class CatalogElsaInstanceLifecycleResolutionInputSource(
    CatalogDbContext dbContext,
    IGovernedReleaseCatalogStore releaseCatalog,
    ElsaInstancePlanAuthorityOptions? authorityOptions = null,
    IReadOnlyDictionary<string, string>? governedSecretReferences = null) : IElsaInstanceLifecycleResolutionInputSource
{
    private readonly ElsaInstancePlanAuthorityOptions _authorityOptions = authorityOptions ?? new();
    private static readonly JsonSerializerOptions BuilderIntentJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.InstanceId != instance.Id || operation.Action == ElsaInstanceOperationAction.Delete)
            return null;
        if (!_authorityOptions.TryGetOrigin(out var planAuthority))
            return null;

        var previewConsentDigest = instance.ReleaseIntent.PreviewManifestDigest;
        var candidates = await releaseCatalog.QueryAsync(new GovernedReleaseCatalogQuery(
            DistributionId: instance.ReleaseIntent.DistributionId,
            ReleaseLine: instance.ReleaseIntent.ReleaseLine,
            ReleaseVersion: instance.ReleaseIntent.RequestedVersion,
            Channel: instance.ReleaseIntent.Channel,
            CatalogLifecycle: previewConsentDigest is null ? "supported" : null,
            RegistryClass: "paid",
            TopologyId: instance.ApplicationIntent.TopologyId), cancellationToken);

        // A null consent remains Supported-only. Explicit Preview consent widens
        // the query only to the two eligible catalog lifecycles; ambiguity is
        // rejected before comparing the requested digest so one row cannot be
        // selected from an otherwise conflicting admission set.
        var eligibleCandidates = candidates
            .Where(entry => IsEligibleCatalogLifecycle(entry.CatalogLifecycle, previewConsentDigest is not null))
            .Take(2)
            .ToArray();
        if (eligibleCandidates.Length != 1)
            return null;

        var entry = eligibleCandidates[0];
        if (!Matches(instance, entry) ||
            previewConsentDigest is not null && !string.Equals(entry.ManifestDigest, previewConsentDigest, StringComparison.OrdinalIgnoreCase) ||
            !TryBuildManifest(entry, out var admission))
            return null;

        var target = await FindDeploymentTargetAsync(instance, operation, cancellationToken);
        if (target is null)
            return null;

        var planId = PlanId(entry.ManifestDigest);
        var planUri = $"{planAuthority}/api/workspaces/{instance.WorkspaceId:D}/instances/{instance.Id:D}/resolved-plans/{planId}";
        var snapshot = await dbContext.ElsaInstanceProvisioningContexts
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.WorkspaceId == instance.WorkspaceId && x.InstanceId == instance.Id,
                cancellationToken);
        var requiresProvisioningContext = await dbContext.ElsaInstances
            .AsNoTracking()
            .Where(x => x.WorkspaceId == instance.WorkspaceId && x.Id == instance.Id)
            .Select(x => x.RequiresProvisioningContext)
            .SingleOrDefaultAsync(cancellationToken);
        RuntimeBuilderIntent builderIntent;
        if (snapshot is null)
        {
            if (requiresProvisioningContext)
                return null;
            // Instances accepted before managed provisioning carried no builder
            // context. Keep their historical resolver input until they are
            // explicitly recreated through the reviewed path.
            builderIntent = new RuntimeBuilderIntent(
                new RuntimeImageSelection("elsa-instance", null, null, null),
                [], [], [], null);
        }
        else
        {
            ElsaInstanceProvisioningContext provisioningContext;
            try
            {
                provisioningContext = new ElsaInstanceProvisioningContext(
                    snapshot.ApplicationId,
                    snapshot.EnvironmentId,
                    snapshot.BuilderIntentJson,
                    snapshot.ConfigurationDigest,
                    snapshot.RuntimeConfigurationId,
                    snapshot.ConfigurationName,
                    snapshot.PreviewDigest,
                    snapshot.RequestDigest,
                    snapshot.ResolvedPlanDigest).Normalize();
                if (provisioningContext.ApplicationId != target.ApplicationId ||
                    provisioningContext.EnvironmentId != target.EnvironmentId)
                    return null;
                builderIntent = JsonSerializer.Deserialize<RuntimeBuilderIntent>(
                    provisioningContext.BuilderIntentJson, BuilderIntentJsonOptions)
                    ?? throw new JsonException("Builder intent JSON is null.");
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (Exception exception) when (exception is JsonException or NotSupportedException)
            {
                return null;
            }
        }
        var request = new ElsaInstancePlanResolutionRequest(
            instance.Intent,
            builderIntent,
            admission,
            planId,
            planUri,
            instance.WorkspaceId,
            GovernedSecretReferences: governedSecretReferences);
        return new ElsaInstanceLifecycleResolutionInput(
            request,
            target,
            operation.Action == ElsaInstanceOperationAction.Create
                ? snapshot?.ResolvedPlanDigest
                : null);
    }

    private async Task<ElsaInstanceLifecycleDeploymentTarget?> FindDeploymentTargetAsync(
        ElsaInstance instance,
        ElsaInstanceOperation operation,
        CancellationToken cancellationToken)
    {
        var environments = await dbContext.DeploymentEnvironments
            .AsNoTracking()
            .Include(x => x.Engines)
            .Where(x => x.WorkspaceId == instance.WorkspaceId && x.ElsaInstanceId == instance.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (environments.Count != 1)
            return null;

        var environment = environments[0];
        if (environment.Engines.Count != 1)
            return null;
        var engine = environment.Engines[0];

        // More than one acceptance event is an ambiguous persisted identity. Load
        // at most two rows so malformed history cannot cause an unbounded read or
        // let SingleOrDefaultAsync surface a provider exception to the worker.
        var acceptedActors = await dbContext.ElsaInstanceAuditEvents
            .AsNoTracking()
            .Where(x => x.WorkspaceId == instance.WorkspaceId &&
                        x.InstanceId == instance.Id &&
                        x.OperationId == operation.Id &&
                        x.EventType == "lifecycle.accepted")
            .Select(x => x.ActorAccountId)
            .Take(2)
            .ToListAsync(cancellationToken);
        if (acceptedActors.Count != 1 ||
            acceptedActors[0] is not { } actorAccountId ||
            actorAccountId == Guid.Empty)
            return null;

        // The deployment-run contract requires a confirmation identity. The
        // confirmation is a stable operation-scoped placeholder because the
        // managed provider path does not expose a provider confirmation token.
        // Preserve the authenticated actor from the acceptance audit event.
        var confirmationId = DeterministicGuid(operation.Id, "confirmation");
        var sourceRevisionId = environment.DesiredRevisionId ?? DeterministicGuid(instance.Id, "desired-revision");
        return new(
            environment.ApplicationId,
            environment.Id,
            engine.Id,
            sourceRevisionId,
            confirmationId,
            actorAccountId);
    }

    private static bool Matches(ElsaInstance instance, GovernedReleaseCatalogEntry entry) =>
        string.Equals(entry.Distribution.Id, instance.ReleaseIntent.DistributionId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Distribution.ReleaseLine, instance.ReleaseIntent.ReleaseLine, StringComparison.OrdinalIgnoreCase) &&
        (instance.ReleaseIntent.RequestedVersion is null ||
        string.Equals(entry.Distribution.ReleaseVersion, instance.ReleaseIntent.RequestedVersion, StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(entry.Distribution.Channel, instance.ReleaseIntent.Channel, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(entry.Topology.Id, instance.ApplicationIntent.TopologyId, StringComparison.OrdinalIgnoreCase);

    private static bool IsEligibleCatalogLifecycle(string lifecycle, bool allowPreview) =>
        string.Equals(lifecycle, "supported", StringComparison.OrdinalIgnoreCase) ||
        allowPreview && string.Equals(lifecycle, "preview", StringComparison.OrdinalIgnoreCase);

    public static bool TryBuildManifest(
        GovernedReleaseCatalogEntry entry,
        out ReleaseManifestAdmissionResult admission)
    {
        try
        {
            var topology = entry.Topology;
            var images = topology.Components
                .Select(component => new ReleaseManifestImage(
                    entry.RegistryClass,
                    component.ImageReference,
                    component.ImageDigest,
                    component.PlatformDigests,
                    component.Id,
                    component.Roles,
                    component.Capabilities,
                    component.Endpoints.Select(endpoint => new ReleaseManifestEndpoint(
                        endpoint.Name,
                        endpoint.Protocol,
                        endpoint.Port,
                        endpoint.Visibility,
                        endpoint.RequiresTls,
                        endpoint.Path)).ToArray(),
                    component.CompanionComponentId))
                .ToArray();
            if (images.Length == 0)
            {
                admission = null!;
                return false;
            }

            var manifest = new CommercialReleaseManifest(
                entry.SchemaVersion,
                new(
                    entry.Distribution.Id,
                    entry.Distribution.Generation,
                    entry.Distribution.ReleaseLine,
                    entry.Distribution.ReleaseVersion,
                    entry.Distribution.Channel,
                    entry.Distribution.ProducerLifecycle,
                    new(
                        entry.Distribution.SourceRepository,
                        entry.Distribution.SourceCommit,
                        "release-manifest",
                        entry.Distribution.SourceRunId),
                    entry.Distribution.Edition),
                [new ReleaseManifestTopology(
                    topology.Id,
                    topology.RuntimeKinds,
                    images,
                    topology.ComponentVersions.ToDictionary(x => x.Id, x => x.Version, StringComparer.OrdinalIgnoreCase),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    new(topology.PackageManifestSchema, topology.Capabilities),
                    new(
                        topology.Evidence.FirstOrDefault(x => x.Kind == ReleaseManifestEvidenceKinds.Sbom) is { } sbom
                            ? new ReleaseManifestAttestation(sbom.Reference, sbom.Digest)
                            : null,
                        topology.Evidence.FirstOrDefault(x => x.Kind == ReleaseManifestEvidenceKinds.Provenance) is { } provenance
                            ? new ReleaseManifestAttestation(provenance.Reference, provenance.Digest)
                            : null,
                        [],
                        topology.Evidence.FirstOrDefault(x => x.Kind == ReleaseManifestEvidenceKinds.VulnerabilityScan) is { } scan
                            ? new ReleaseManifestVulnerabilityScan("catalog", "governed-policy", scan.Reference, scan.Digest)
                            : null))],
                entry.ComponentDeclarations is null
                    ? null
                    : new(
                        entry.ComponentDeclarations.Format,
                        entry.ComponentDeclarations.Digest,
                        entry.ComponentDeclarations.Packages
                            .Select(package => new ReleaseManifestPackageDeclaration(package.Id, package.Version))
                            .ToArray()));

            admission = new ReleaseManifestAdmissionResult(
                true,
                entry.ManifestReference,
                entry.ManifestDigest,
                manifest,
                new ReleaseManifestAdmissionEvidence(entry.SignatureEvidenceReference, entry.SignatureEvidenceDigest),
                entry.RegistryClass,
                topology.Id,
                [],
                entry.PayloadDigest);
            return true;
        }
        catch (ArgumentException)
        {
            admission = null!;
            return false;
        }
    }

    private static string PlanId(string manifestDigest)
    {
        var digest = manifestDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? manifestDigest["sha256:".Length..]
            : manifestDigest;
        return $"release-{digest.ToLowerInvariant()}";
    }

    private static Guid DeterministicGuid(Guid seed, string purpose)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"elsa-control:{purpose}:{seed:D}"));
        return new Guid(bytes[..16]);
    }
}
