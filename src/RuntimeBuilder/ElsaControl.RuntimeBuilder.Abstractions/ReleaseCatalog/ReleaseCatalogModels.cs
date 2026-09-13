using ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

namespace ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;

/// <summary>
/// Safe, durable projection of one admitted release-manifest topology for one registry class.
/// Raw manifest payloads and signer identities never enter this contract.
/// </summary>
public sealed record GovernedReleaseCatalogEntry(
    string SchemaVersion,
    string ManifestReference,
    string ManifestDigest,
    string PayloadDigest,
    string SignatureEvidenceReference,
    string SignatureEvidenceDigest,
    string RegistryClass,
    GovernedReleaseDistribution Distribution,
    GovernedReleaseTopology Topology,
    string CatalogLifecycle,
    DateTimeOffset AdmittedAt,
    GovernedReleaseComponentDeclarations? ComponentDeclarations = null);

public sealed record GovernedReleaseDistribution(
    string Id,
    string Generation,
    string ReleaseLine,
    string ReleaseVersion,
    string Channel,
    string ProducerLifecycle,
    string? Edition,
    string SourceRepository,
    string SourceCommit,
    string SourceRunId);

public sealed record GovernedReleaseTopology(
    string Id,
    string PackageManifestSchema,
    IReadOnlyList<string> RuntimeKinds,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<GovernedReleaseComponentVersion> ComponentVersions,
    IReadOnlyList<GovernedReleaseComponent> Components,
    IReadOnlyList<GovernedReleaseEvidence> Evidence);

public sealed record GovernedReleaseComponentVersion(
    string Id,
    string Version);

public sealed record GovernedReleaseComponentDeclarations(
    string Format,
    string Digest,
    IReadOnlyList<GovernedReleasePackageDeclaration> Packages);

public sealed record GovernedReleasePackageDeclaration(
    string Id,
    string Version);

public sealed record GovernedReleaseComponent(
    string Id,
    string ImageReference,
    string ImageDigest,
    IReadOnlyDictionary<string, string> PlatformDigests,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<GovernedReleaseEndpoint> Endpoints,
    string? CompanionComponentId);

public sealed record GovernedReleaseEndpoint(
    string Name,
    string Protocol,
    int Port,
    string Visibility,
    bool RequiresTls,
    string? Path);

public sealed record GovernedReleaseEvidence(
    string Kind,
    string Reference,
    string Digest);

public sealed record GovernedReleaseCatalogQuery(
    string? DistributionId = null,
    string? ReleaseLine = null,
    string? ReleaseVersion = null,
    string? Channel = null,
    string? ProducerLifecycle = null,
    string? CatalogLifecycle = null,
    string? RegistryClass = null,
    string? TopologyId = null,
    string? RuntimeKind = null,
    string? Capability = null);

/// <summary>
/// Server-owned catalog policy supplied alongside the verifier options. The producer's
/// lifecycle is retained as evidence, but it is never promoted to catalog eligibility.
/// </summary>
public sealed record GovernedReleaseCatalogAdmissionOptions(
    ReleaseManifestAdmissionOptions ManifestAdmission,
    string CatalogLifecycle);

public enum GovernedReleaseCatalogWriteStatus
{
    Stored,
    Unchanged,
    Conflict
}

public sealed record GovernedReleaseCatalogWriteResult(
    GovernedReleaseCatalogWriteStatus Status,
    IReadOnlyList<GovernedReleaseCatalogEntry> Entries,
    string? Code = null);

public interface IGovernedReleaseCatalogStore
{
    Task<GovernedReleaseCatalogWriteResult> StoreAsync(
        IReadOnlyList<GovernedReleaseCatalogEntry> entries,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
        GovernedReleaseCatalogQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Application boundary for admitting a signed producer manifest into the durable
/// Control-owned catalog. Implementations must admit and project the complete manifest
/// before invoking the store, so a rejected or incomplete manifest cannot create rows.
/// </summary>
public interface IReleaseCatalogIngestionService
{
    Task<GovernedReleaseCatalogAdmissionResult> AdmitAsync(
        ReleaseManifestArtifact artifact,
        GovernedReleaseCatalogAdmissionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record GovernedReleaseCatalogFinding(
    string Code,
    string Message,
    string Scope);

public sealed record GovernedReleaseCatalogAdmissionResult(
    bool Accepted,
    GovernedReleaseCatalogWriteStatus? WriteStatus,
    IReadOnlyList<GovernedReleaseCatalogEntry> Entries,
    IReadOnlyList<GovernedReleaseCatalogFinding> Findings,
    IReadOnlyList<GovernedReleaseCatalogEntry>? IncomingEntries = null);
