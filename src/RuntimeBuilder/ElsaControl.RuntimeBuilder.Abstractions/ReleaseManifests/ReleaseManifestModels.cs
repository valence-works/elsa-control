namespace ElsaControl.RuntimeBuilder.Abstractions.ReleaseManifests;

/// <summary>
/// The producer-owned release-manifest schema is independent from Elsa package versions.
/// </summary>
public static class ReleaseManifestSchema
{
    /// <summary>
    /// The producer's semver schema identity. This is intentionally independent of
    /// the Elsa release line carried by a manifest.
    /// </summary>
    public const string CurrentVersion = "2.0.0";

    /// <summary>
    /// The pre-producer Control shape remains readable so existing catalog records and
    /// fixtures can be migrated deliberately. It is not the current producer contract.
    /// </summary>
    public const string LegacyVersion = "1";

    public const string PreviousProducerVersion = "1.0.0";

    public const string DefaultOidcIssuer = "https://token.actions.githubusercontent.com";
}

/// <summary>
/// Stable capability identities declared by producer release images. The descriptor
/// that accompanies a capability is validated at admission time and is intentionally
/// not retained in the provider-neutral manifest or resolved plan.
/// </summary>
public static class ReleaseManifestRuntimeIntegrationCapabilities
{
    public const string ManagedElsaHandoffV2 = "managed-elsa-handoff-v2";

    /// <summary>
    /// The image accepts Control's exact owner/admin Studio grants during managed handoff.
    /// Older handoff-v2 images support only structured-log read and must not receive these grants.
    /// </summary>
    public const string ManagedElsaStudioGrantsV1 = "managed-elsa-studio-grants-v1";
}

public sealed record CommercialReleaseManifest(
    string SchemaVersion,
    ReleaseManifestDistribution Distribution,
    IReadOnlyList<ReleaseManifestTopology> Topologies,
    /// <summary>
    /// Safe, typed projection of the producer's signed image-build package declarations.
    /// These declarations describe packages baked into the release image; they are not
    /// customer-selected package-catalog inputs.
    /// </summary>
    ReleaseManifestComponentDeclarations? ComponentDeclarations = null);

public sealed record ReleaseManifestComponentDeclarations(
    string Format,
    string Digest,
    IReadOnlyList<ReleaseManifestPackageDeclaration> Packages);

public sealed record ReleaseManifestPackageDeclaration(
    string Id,
    string Version);

public sealed record ReleaseManifestDistribution(
    string Id,
    string Generation,
    string ReleaseLine,
    string ReleaseVersion,
    string Channel,
    string Lifecycle,
    ReleaseManifestSource Source,
    /// <summary>
    /// The current producer schema requires the governed <c>commercial</c> edition.
    /// This remains nullable only for historical Control-only manifests that omit it
    /// when explicitly admitted through the legacy-schema path. Image registry classes
    /// remain the provider-neutral selection identity when a release contains multiple
    /// editions.
    /// </summary>
    string? Edition = null);

public sealed record ReleaseManifestSource(
    string Repository,
    string Commit,
    string Workflow,
    string RunId);

public sealed record ReleaseManifestTopology(
    string Id,
    IReadOnlyList<string> RuntimeKinds,
    IReadOnlyList<ReleaseManifestImage> Images,
    IReadOnlyDictionary<string, string> Components,
    IReadOnlyDictionary<string, string> Endpoints,
    ReleaseManifestCompatibility Compatibility,
    ReleaseManifestSupplyChain SupplyChain);

/// <summary>
/// An image can optionally identify a component. The v1 producer shape may use one
/// image per topology and omit that identity; the projector then uses the topology id.
/// </summary>
public sealed record ReleaseManifestImage(
    string RegistryClass,
    string Reference,
    string IndexDigest,
    IReadOnlyDictionary<string, string>? PlatformDigests = null,
    string? ComponentId = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Capabilities = null,
    IReadOnlyList<ReleaseManifestEndpoint>? Endpoints = null,
    string? CompanionComponentId = null);

public sealed record ReleaseManifestEndpoint(
    string Name,
    string Protocol,
    int Port,
    string Visibility,
    bool RequiresTls,
    string? Path = null);

public sealed record ReleaseManifestCompatibility(
    string PackageManifestSchema,
    IReadOnlyList<string> RuntimeCapabilities);

public sealed record ReleaseManifestSupplyChain(
    ReleaseManifestAttestation? Sbom,
    ReleaseManifestAttestation? Provenance,
    IReadOnlyList<ReleaseManifestSignatureEvidence> Signatures,
    ReleaseManifestVulnerabilityScan? VulnerabilityScan);

public sealed record ReleaseManifestAttestation(
    string Uri,
    string Digest,
    string? PayloadDigest = null);

public sealed record ReleaseManifestSignatureEvidence(
    string RegistryClass,
    string Identity,
    string Uri,
    string? Digest = null);

public sealed record ReleaseManifestVulnerabilityScan(
    string Tool,
    string Policy,
    string Report,
    string? Digest = null,
    string? PayloadDigest = null);

/// <summary>
/// An immutable artifact envelope. Payload is used only at the ingestion boundary and
/// is never copied into a catalog record or resolved plan.
/// </summary>
public sealed record ReleaseManifestArtifact(
    string Reference,
    string Digest,
    string Payload,
    /// <summary>
    /// Exact UTF-8 payload identity. This is distinct from <see cref="Digest"/>,
    /// which identifies the signed OCI subject containing the payload.
    /// </summary>
    string? PayloadDigest = null)
{
    public ReleaseManifestSubjectIdentity Subject => new(Reference, Digest);
}

/// <summary>
/// Identity of the immutable OCI subject signed by the producer.
/// </summary>
public sealed record ReleaseManifestSubjectIdentity(
    string Reference,
    string Digest);

/// <summary>
/// Identity of the exact manifest payload blob contained by the OCI subject.
/// </summary>
public sealed record ReleaseManifestPayloadIdentity(
    string Digest);

/// <summary>
/// Cryptographic verification is deliberately a seam: registry/cosign implementations
/// can be supplied by the host without coupling this contract to a credential provider.
/// </summary>
public sealed record ReleaseManifestSignatureVerification(
    bool IsValid,
    string Subject,
    string SubjectDigest,
    string EvidenceReference,
    string EvidenceDigest,
    string? OidcIssuer = null,
    /// <summary>
    /// Digest of the exact payload proven to be bound to the signed OCI subject.
    /// The admission service compares this with its own UTF-8 hash.
    /// </summary>
    string? BoundPayloadDigest = null);

public interface IReleaseManifestSignatureVerifier
{
    ValueTask<ReleaseManifestSignatureVerification> VerifyAsync(
        ReleaseManifestArtifact artifact,
        CancellationToken cancellationToken = default);
}

public sealed record ReleaseManifestAdmissionOptions(
    string ExpectedSignatureSubject,
    string RegistryClass = "paid",
    string? TopologyId = null,
    string? ExpectedOidcIssuer = null,
    /// <summary>
    /// Explicit migration switch for the historical Control-only shape. Normal
    /// admission never enables it; producer releases use <see cref="ReleaseManifestSchema.CurrentVersion"/>.
    /// </summary>
    bool AllowLegacySchema = false);

public sealed record ReleaseManifestAdmissionFinding(
    string Code,
    string Message,
    string Scope);

/// <summary>
/// Safe retained evidence from a verified signature. Signer identity is intentionally
/// kept inside the verifier boundary and is not projected into a resolved plan.
/// </summary>
public sealed record ReleaseManifestAdmissionEvidence(
    string Reference,
    string Digest);

public sealed record ReleaseManifestAdmissionResult(
    bool Accepted,
    string? Reference,
    string? Digest,
    CommercialReleaseManifest? Manifest,
    ReleaseManifestAdmissionEvidence? SignatureEvidence,
    string RegistryClass,
    string? TopologyId,
    IReadOnlyList<ReleaseManifestAdmissionFinding> Findings,
    /// <summary>
    /// Exact UTF-8 payload identity, retained separately from the signed OCI subject
    /// digest in <see cref="Digest"/>.
    /// </summary>
    string? PayloadDigest = null)
{
    public ReleaseManifestSubjectIdentity? Subject => Reference is not null && Digest is not null
        ? new(Reference, Digest)
        : null;

    public ReleaseManifestPayloadIdentity? Payload => PayloadDigest is not null
        ? new(PayloadDigest)
        : null;
}

public static class ReleaseManifestEvidenceKinds
{
    public const string Manifest = "release-manifest";
    public const string Signature = "release-manifest-signature";
    public const string Sbom = "release-manifest-sbom";
    public const string Provenance = "release-manifest-provenance";
    public const string VulnerabilityScan = "release-manifest-vulnerability-scan";
}

/// <summary>
/// Shared trust-boundary contract for evidence that may leave the immutable
/// resolved-plan store through customer-facing projections.
/// </summary>
public static class ReleaseManifestEvidenceContract
{
    public const string GenericDescription = "Retained immutable evidence.";

    private static readonly IReadOnlyDictionary<string, string> FixedDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ReleaseManifestEvidenceKinds.Manifest] = "Verified producer release manifest.",
            [ReleaseManifestEvidenceKinds.Signature] = "Verified release-manifest signature evidence.",
            [ReleaseManifestEvidenceKinds.Sbom] = "Verified release SBOM evidence.",
            [ReleaseManifestEvidenceKinds.Provenance] = "Verified release provenance evidence.",
            [ReleaseManifestEvidenceKinds.VulnerabilityScan] = "Producer-retained release vulnerability-scan evidence."
        };

    public static string DescriptionFor(string kind) =>
        FixedDescriptions.TryGetValue(kind, out var description) ? description : GenericDescription;

    public static bool IsSafe(string? kind, string? reference, string? digest, string? description)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.Any(char.IsControl) || kind.Length > 128 ||
            !IsDigest(digest) || string.IsNullOrWhiteSpace(reference) || !IsSafeReference(reference, digest!) ||
            string.IsNullOrWhiteSpace(description) || description.Any(char.IsControl))
            return false;

        return FixedDescriptions.TryGetValue(kind, out var expected)
            ? string.Equals(description, expected, StringComparison.Ordinal)
            : string.Equals(description, GenericDescription, StringComparison.Ordinal);
    }

    public static bool IsDigest(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == "sha256:".Length + 64 &&
        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
        value["sha256:".Length..].All(Uri.IsHexDigit);

    public static bool IsSafeReference(string reference, string digest)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Any(char.IsWhiteSpace) ||
            reference.Any(char.IsControl) || reference.Contains('%', StringComparison.Ordinal) ||
            reference.Contains('\\', StringComparison.Ordinal) || reference.Contains('?', StringComparison.Ordinal) ||
            reference.Contains('#', StringComparison.Ordinal) ||
            !Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) || string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Scheme.Equals("oci", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            return false;

        var segments = uri.AbsolutePath[1..].Split('/');
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
            return false;

        var marker = reference.IndexOf("@sha256:", StringComparison.OrdinalIgnoreCase);
        if (reference.Contains('@', StringComparison.Ordinal))
        {
            if (marker < 0 || reference.IndexOf('@', marker + 1) >= 0)
                return false;
            var embedded = reference[(marker + 1)..];
            if (!IsDigest(embedded) || !string.Equals(embedded, digest, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
