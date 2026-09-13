using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;
using ElsaControl.RuntimeBuilder.Core.ReleaseManifests;

namespace ElsaControl.RuntimeBuilder.Core.ReleaseCatalog;

public sealed class GovernedReleaseCatalogIngestionService(
    ReleaseManifestAdmissionService admissionService,
    IGovernedReleaseCatalogStore store,
    TimeProvider timeProvider) : IReleaseCatalogIngestionService
{
    public async Task<GovernedReleaseCatalogAdmissionResult> AdmitAsync(
        ReleaseManifestArtifact artifact,
        GovernedReleaseCatalogAdmissionOptions catalogOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(admissionService);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var catalogLifecycle = catalogOptions.CatalogLifecycle?.Trim();
        if (string.IsNullOrWhiteSpace(catalogLifecycle))
            return Rejected("catalog.lifecycle.required", "A server-owned catalog lifecycle is required.", "catalogLifecycle");
        if (catalogLifecycle.Length > 64 || catalogLifecycle.Any(char.IsControl))
            return Rejected("catalog.lifecycle.invalid", "The server-owned catalog lifecycle is invalid.", "catalogLifecycle");
        catalogLifecycle = catalogLifecycle.ToLowerInvariant();

        var options = catalogOptions.ManifestAdmission
            ?? throw new ArgumentException("Manifest admission options are required.", nameof(catalogOptions));

        if (!string.IsNullOrWhiteSpace(options.TopologyId))
            return Rejected("catalog.topology.unspecified.required", "Catalog admission projects every topology from the signed manifest.", "options.topologyId");

        var admission = await admissionService.AdmitAsync(artifact, options, cancellationToken);
        if (!admission.Accepted)
            return new(false, null, [], admission.Findings
                .Select(x => new GovernedReleaseCatalogFinding(x.Code, x.Message, x.Scope))
                .ToArray());

        if (admission.Manifest is null
            || admission.Reference is null
            || admission.Digest is null
            || admission.PayloadDigest is null
            || admission.SignatureEvidence is null)
            return Rejected("catalog.admission.incomplete", "Accepted admission facts are incomplete.", "admission");

        // The admission service can intentionally read a historical shape for an
        // explicit migration, but the durable governed catalog only accepts the
        // current producer contract. A legacy projection must never downgrade a
        // previously admitted release.
        if (!string.Equals(admission.Manifest.SchemaVersion, ReleaseManifestSchema.CurrentVersion, StringComparison.Ordinal))
            return Rejected("catalog.schema.downgrade", "Only the current producer manifest schema can enter the governed catalog.", "manifest.schemaVersion");

        IReadOnlyList<GovernedReleaseCatalogEntry> entries;
        try
        {
            entries = Project(admission, catalogLifecycle, timeProvider.GetUtcNow());
            foreach (var entry in entries)
                GovernedReleaseCatalogStorageContract.ValidateLengths(entry);
        }
        catch (Exception exception) when (exception is ReleaseManifestProjectionValidationException
            or GovernedReleaseCatalogStorageValidationException)
        {
            return Rejected("catalog.projection.invalid", "Admitted release facts cannot be projected safely.", "admission");
        }

        var write = await store.StoreAsync(entries, cancellationToken);
        return write.Status == GovernedReleaseCatalogWriteStatus.Conflict
            ? new(false, write.Status, write.Entries,
                [new("catalog.identity.conflict", "A different immutable release already owns this catalog identity.", "catalog")],
                IncomingEntries: entries)
            : new(true, write.Status, write.Entries, []);
    }

    private static IReadOnlyList<GovernedReleaseCatalogEntry> Project(
        ReleaseManifestAdmissionResult admission,
        string catalogLifecycle,
        DateTimeOffset admittedAt)
    {
        var manifest = admission.Manifest!;
        ReleaseManifestPlanProjector.ValidateProjectionShape(manifest);
        var distribution = manifest.Distribution;
        var registryClass = admission.RegistryClass.Trim().ToLowerInvariant();
        return manifest.Topologies.Select(topology =>
        {
            var components = ReleaseManifestPlanProjector.SelectComponents(topology, registryClass);
            // Manifest and signature identities are catalog-level facts retained in
            // the dedicated fields below. Topology evidence contains only the
            // topology-selected supply-chain evidence.
            var evidence = ReleaseManifestPlanProjector.ProjectEvidence(admission, topology, [])
                .Where(x => x.Kind is not ReleaseManifestEvidenceKinds.Manifest
                    and not ReleaseManifestEvidenceKinds.Signature);
            return new GovernedReleaseCatalogEntry(
                manifest.SchemaVersion,
                admission.Reference!,
                admission.Digest!,
                admission.PayloadDigest!,
                admission.SignatureEvidence!.Reference,
                admission.SignatureEvidence.Digest,
                registryClass,
                new(
                    distribution.Id,
                    distribution.Generation,
                    distribution.ReleaseLine,
                    distribution.ReleaseVersion,
                    distribution.Channel,
                    distribution.Lifecycle,
                    distribution.Edition,
                    distribution.Source.Repository,
                    distribution.Source.Commit,
                    distribution.Source.RunId),
                new(
                    topology.Id,
                    topology.Compatibility.PackageManifestSchema,
                    CanonicalStrings(topology.RuntimeKinds),
                    CanonicalStrings(topology.Compatibility.RuntimeCapabilities),
                    topology.Components
                        .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new GovernedReleaseComponentVersion(x.Key, x.Value))
                        .ToArray(),
                    components.Select(component => new GovernedReleaseComponent(
                            component.Id,
                            component.Image.Reference,
                            component.Image.Digest,
                            component.Image.PlatformDigests is null
                                ? new Dictionary<string, string>()
                                : new SortedDictionary<string, string>(
                                    component.Image.PlatformDigests.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                                    StringComparer.OrdinalIgnoreCase),
                            CanonicalStrings(component.Roles),
                            CanonicalStrings(component.Capabilities),
                            component.Endpoints.Select(endpoint => new GovernedReleaseEndpoint(
                                endpoint.Name,
                                endpoint.Protocol,
                                endpoint.Port,
                                endpoint.Visibility,
                                endpoint.RequiresTls,
                                endpoint.Path))
                                .OrderBy(endpoint => endpoint.Name, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(endpoint => endpoint.Protocol, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(endpoint => endpoint.Port)
                                .ThenBy(endpoint => endpoint.Visibility, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(endpoint => endpoint.RequiresTls)
                                .ThenBy(endpoint => endpoint.Path, StringComparer.Ordinal)
                                .ToArray(),
                            component.CompanionComponentId))
                        .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    evidence.Select(x => new GovernedReleaseEvidence(
                        x.Kind,
                        x.Reference,
                        x.Digest ?? throw new ReleaseManifestProjectionValidationException("Admitted evidence must retain a digest.")))
                        .OrderBy(x => x.Kind, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Reference, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(x => x.Digest, StringComparer.OrdinalIgnoreCase)
                        .ToArray()),
                catalogLifecycle,
                admittedAt,
                manifest.ComponentDeclarations is null
                    ? null
                    : new(
                        manifest.ComponentDeclarations.Format,
                        manifest.ComponentDeclarations.Digest,
                        manifest.ComponentDeclarations.Packages
                            .Select(package => new GovernedReleasePackageDeclaration(package.Id, package.Version))
                            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                            .ToArray()));
        }).OrderBy(x => x.Topology.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> CanonicalStrings(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static GovernedReleaseCatalogAdmissionResult Rejected(string code, string message, string scope) =>
        new(false, null, [], [new(code, message, scope)]);
}
