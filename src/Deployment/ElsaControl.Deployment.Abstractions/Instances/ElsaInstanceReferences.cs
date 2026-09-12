using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ElsaControl.Deployment.Abstractions.Instances;

/// <summary>
/// Safe, control-plane identity for the immutable desired-state source revision.
/// It intentionally contains no serialized desired state or provider identifier.
/// </summary>
public readonly record struct ElsaDesiredStateRevisionId
{
    public ElsaDesiredStateRevisionId(string value) => Value = ElsaInstanceReferenceValue.RequireToken(value, nameof(value));

    public ElsaDesiredStateRevisionId(Guid value) : this(value == Guid.Empty
        ? throw new ArgumentException("Revision ID is required.", nameof(value))
        : value.ToString("D"))
    {
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

/// <summary>Safe identity for the last accepted lifecycle operation.</summary>
public readonly record struct ElsaLastOperationId
{
    public ElsaLastOperationId(string value) => Value = ElsaInstanceReferenceValue.RequireToken(value, nameof(value));

    public ElsaLastOperationId(Guid value) : this(value == Guid.Empty
        ? throw new ArgumentException("Operation ID is required.", nameof(value))
        : value.ToString("D"))
    {
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;
}

/// <summary>
/// Immutable identity of a resolved plan. The URI points to a control-plane API
/// resource and the content hash is an immutable SHA-256 identity.
/// </summary>
public sealed record ElsaResolvedPlanReference
{
    public ElsaResolvedPlanReference(string planId, int schemaVersion, string contentHash, string planUri)
    {
        PlanId = ElsaInstanceReferenceValue.RequireToken(planId, nameof(planId));
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Schema version must be positive.");
        SchemaVersion = schemaVersion;
        ContentHash = ElsaInstanceReferenceValue.RequireSha256Digest(contentHash, nameof(contentHash));
        PlanUri = ElsaInstanceReferenceValue.RequireAbsoluteApiUri(planUri, nameof(planUri));
    }

    public string PlanId { get; }

    public int SchemaVersion { get; }

    public string ContentHash { get; }

    public string PlanUri { get; }
}

/// <summary>Immutable component identity in a resolved application plan.</summary>
public sealed record ElsaComponentDigest
{
    public ElsaComponentDigest(string componentId, string digest)
    {
        ComponentId = ElsaInstanceReferenceValue.RequireToken(componentId, nameof(componentId));
        Digest = ElsaInstanceReferenceValue.RequireSha256Digest(digest, nameof(digest));
    }

    public string ComponentId { get; }

    public string Digest { get; }
}

/// <summary>
/// Safe exact release projection backed by one immutable resolved plan reference.
/// It records no image tags, payloads, credentials or provider resource IDs.
/// </summary>
public sealed record ElsaCurrentResolvedRelease
{
    public ElsaCurrentResolvedRelease(
        ElsaResolvedPlanReference planReference,
        string distributionId,
        string releaseLine,
        string version,
        string manifestDigest,
        IEnumerable<ElsaComponentDigest> componentDigests)
    {
        PlanReference = planReference ?? throw new ArgumentNullException(nameof(planReference));
        DistributionId = ElsaInstanceValue.Catalog(distributionId, nameof(distributionId));
        ReleaseLine = ElsaInstanceValue.Catalog(releaseLine, nameof(releaseLine));
        Version = ElsaInstanceValue.Catalog(version, nameof(version));
        if (!ElsaReleaseVersions.BelongsToLine(ReleaseLine, Version))
            throw new ArgumentException("Version must belong to the selected release line.", nameof(version));
        ManifestDigest = ElsaInstanceReferenceValue.RequireSha256Digest(manifestDigest, nameof(manifestDigest));

        ArgumentNullException.ThrowIfNull(componentDigests);
        var components = componentDigests.ToArray();
        ElsaInstanceReferenceValue.EnsureComponentCount(components.Length, nameof(componentDigests));
        if (components.Any(x => x is null))
            throw new ArgumentException("Component digests cannot contain null values.", nameof(componentDigests));
        if (components.GroupBy(x => x.ComponentId, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new ArgumentException("Component IDs must be unique.", nameof(componentDigests));
        ComponentDigests = new ReadOnlyCollection<ElsaComponentDigest>(
            components.OrderBy(x => x.ComponentId, StringComparer.Ordinal).ToArray());
    }

    public ElsaCurrentResolvedRelease(
        string planId,
        int schemaVersion,
        string contentHash,
        string planUri,
        string distributionId,
        string releaseLine,
        string version,
        string manifestDigest,
        IEnumerable<ElsaComponentDigest> componentDigests)
        : this(new ElsaResolvedPlanReference(planId, schemaVersion, contentHash, planUri), distributionId,
            releaseLine, version, manifestDigest, componentDigests)
    {
    }

    public ElsaResolvedPlanReference PlanReference { get; }

    public string PlanId => PlanReference.PlanId;

    public string PlanUri => PlanReference.PlanUri;

    public string DistributionId { get; }

    public string ReleaseLine { get; }

    public string Version { get; }

    public string ManifestDigest { get; }

    public IReadOnlyList<ElsaComponentDigest> ComponentDigests { get; }
}

/// <summary>
/// Canonical customer-facing origin for a managed Elsa deployment. Provider resource
/// identifiers, credentials, paths and request-specific URI components are excluded.
/// </summary>
public readonly record struct ElsaManagedEndpointOrigin
{
    public ElsaManagedEndpointOrigin(string value) =>
        Value = ElsaInstanceReferenceValue.RequireManagedEndpointOrigin(value, nameof(value));

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrEmpty(Value);

    public override string ToString() => Value;

    public static bool TryCreate(string? value, out ElsaManagedEndpointOrigin origin)
    {
        try
        {
            origin = new ElsaManagedEndpointOrigin(value!);
            return true;
        }
        catch (ArgumentException)
        {
            origin = default;
            return false;
        }
    }
}

/// <summary>
/// Provider-neutral deployment observation. Values are control-owned safe references;
/// provider resource IDs, credentials and command details are deliberately excluded.
/// <see cref="ManagedHandoff"/> records that the provider configured this deployment's runtime for the
/// managed Elsa handoff, bound to <see cref="EndpointOrigin"/>. Without it the runtime serves no handoff
/// endpoints, so Control must not offer to open the instance.
/// </summary>
public sealed record ElsaCurrentDeploymentReference
{
    public ElsaCurrentDeploymentReference(
        string deploymentId,
        string? revisionId = null,
        string? endpointUri = null,
        bool managedHandoff = false)
    {
        DeploymentId = ElsaInstanceReferenceValue.RequireToken(deploymentId, nameof(deploymentId));
        RevisionId = ElsaInstanceReferenceValue.OptionalToken(revisionId, nameof(revisionId));
        EndpointOrigin = endpointUri is null ? null : new ElsaManagedEndpointOrigin(endpointUri);
        if (managedHandoff && EndpointOrigin is null)
            throw new ArgumentException("A managed handoff is bound to a verified endpoint origin.", nameof(managedHandoff));
        ManagedHandoff = managedHandoff;
    }

    public string DeploymentId { get; }

    public string? RevisionId { get; }

    [JsonIgnore]
    public ElsaManagedEndpointOrigin? EndpointOrigin { get; }

    public string? EndpointUri => EndpointOrigin?.Value;

    public bool ManagedHandoff { get; }

    public string DeploymentReference => DeploymentId;

    public string? RevisionReference => RevisionId;
}

/// <summary>Control-owned placement assignment identity and no provider placement graph.</summary>
public sealed record ElsaPlacementAssignmentReference
{
    public ElsaPlacementAssignmentReference(string assignmentId) =>
        AssignmentId = ElsaInstanceReferenceValue.RequireToken(assignmentId, nameof(assignmentId));

    public string AssignmentId { get; }

    public string Value => AssignmentId;
}

/// <summary>
/// Runtime application tenant reference. The optional audience is a safe runtime
/// identity value, never a credential or provider resource identifier.
/// </summary>
public sealed record ElsaTenantReference
{
    public ElsaTenantReference(string tenantId, string? audience = null)
    {
        TenantId = ElsaInstanceReferenceValue.RequireToken(tenantId, nameof(tenantId));
        Audience = audience is null ? null : ElsaInstanceReferenceValue.RequireSafeAudience(audience, nameof(audience));
    }

    public string TenantId { get; }

    public string? Audience { get; }
}

internal static partial class ElsaInstanceReferenceValue
{
    private const int MaxTokenLength = 128;
    private const int MaxUriLength = 2048;
    private const int MaxComponentCount = 256;
    private static readonly Regex TokenPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ScopePattern = new("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static string RequireToken(string? value, string parameterName)
    {
        if (value is null || value.Any(char.IsControl))
            throw new ArgumentException("A non-empty safe reference is required.", parameterName);
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("A non-empty safe reference is required.", parameterName);
        if (normalized.Length > MaxTokenLength || !TokenPattern.IsMatch(normalized))
            throw new ArgumentException("Reference contains characters that are not permitted in a control-owned identifier.", parameterName);
        return normalized;
    }

    public static string? OptionalToken(string? value, string parameterName)
    {
        if (value is not null && value.Any(char.IsControl))
            throw new ArgumentException("A safe reference cannot contain control characters.", parameterName);
        return string.IsNullOrWhiteSpace(value) ? null : RequireToken(value, parameterName);
    }

    public static string RequireOperationKey(string? value, string parameterName) =>
        RequireOperationValue(value, parameterName, 128, TokenPattern);

    public static string RequireOperationScope(string? value, string parameterName) =>
        RequireOperationValue(value, parameterName, 256, ScopePattern);

    public static string RequireCanonicalHash(string? value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("A canonical 64-character hexadecimal request hash is required.", parameterName);
        return value.ToLowerInvariant();
    }

    public static string RequireSha256Digest(string? value, string parameterName)
    {
        if (value is null || value.Any(char.IsControl))
            throw new ArgumentException("A SHA-256 digest is required.", parameterName);
        var normalized = value?.Trim();
        if (normalized is null || normalized.Length != 71 ||
            !normalized.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
            normalized[7..].Any(x => !Uri.IsHexDigit(x)))
            throw new ArgumentException("A SHA-256 digest is required.", parameterName);
        return "sha256:" + normalized[7..].ToLowerInvariant();
    }

    public static string RequireAbsoluteApiUri(string? value, string parameterName)
    {
        var raw = value?.Trim();
        if (raw is null || raw.Any(char.IsControl))
            throw new ArgumentException("Plan URI must be an absolute HTTPS API URI.", parameterName);
        var authorityEnd = raw.IndexOf("://", StringComparison.Ordinal);
        var pathStart = authorityEnd < 0 ? -1 : raw.IndexOf('/', authorityEnd + 3);
        if (pathStart >= 0 && HasAmbiguousPath(raw[pathStart..]))
            throw new ArgumentException("Plan URI cannot contain ambiguous or traversal path segments.", parameterName);
        var uri = ParseAbsoluteUri(value, parameterName);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(uri.AbsolutePath) ||
            !uri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            HasAmbiguousPath(uri.AbsolutePath))
            throw new ArgumentException("Plan URI must be an absolute HTTPS API URI.", parameterName);
        return uri.AbsoluteUri;
    }

    public static string RequireManagedEndpointOrigin(string? value, string parameterName)
    {
        const string error = "Managed deployment endpoints must be absolute HTTPS origins.";
        var uri = ParseAbsoluteUri(value, parameterName);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath is not ("" or "/") ||
            uri.Host.Contains('*', StringComparison.Ordinal))
            throw new ArgumentException(error, parameterName);

        string host;
        try
        {
            host = uri.IdnHost.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            throw new ArgumentException(error, parameterName);
        }
        var authority = uri.HostNameType == UriHostNameType.IPv6 ? "[" + host + "]" : host;
        if (!uri.IsDefaultPort)
            authority += ":" + uri.Port;
        return Uri.UriSchemeHttps + "://" + authority;
    }

    public static string RequireSafeAudience(string? value, string parameterName)
    {
        var normalized = ElsaInstanceValue.Require(value!, parameterName);
        if (normalized.Any(char.IsControl) || normalized.Contains('?', StringComparison.Ordinal) ||
            normalized.Contains('#', StringComparison.Ordinal) || normalized.Contains('/', StringComparison.Ordinal) ||
            normalized.Contains('\\', StringComparison.Ordinal) || normalized.Contains('@', StringComparison.Ordinal) ||
            !normalized.StartsWith("urn:elsa:", StringComparison.Ordinal) ||
            normalized.Length > 256 ||
            normalized[9..].Any(x => !(char.IsLetterOrDigit(x) || x is ':' or '.' or '_' or '-')))
            throw new ArgumentException("Audience must be a safe Elsa URN.", parameterName);
        return normalized.ToLowerInvariant();
    }

    private static Uri ParseAbsoluteUri(string? value, string parameterName)
    {
        if (value is null || value.Any(char.IsControl))
            throw new ArgumentException("A safe absolute URI is required.", parameterName);
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > MaxUriLength ||
            !Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("A safe absolute URI is required.", parameterName);
        return uri;
    }

    public static void EnsureComponentCount(int count, string parameterName)
    {
        if (count is < 1 or > MaxComponentCount)
            throw new ArgumentException("At least one and no more than 256 component digests are required.", parameterName);
    }

    private static string RequireOperationValue(string? value, string parameterName, int maxLength, Regex pattern)
    {
        if (value is null || value.Any(char.IsControl))
            throw new ArgumentException("A safe operation reference is required.", parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > maxLength || !pattern.IsMatch(normalized))
            throw new ArgumentException("Operation reference contains characters that are not permitted.", parameterName);
        return normalized;
    }

    private static bool HasAmbiguousPath(string path) =>
        path.Contains('%', StringComparison.Ordinal) ||
        path.Contains('\\', StringComparison.Ordinal) ||
        path.Contains("//", StringComparison.Ordinal) ||
        path.Split('/').Any(segment => segment is "." or "..");
}

/// <summary>Canonical validation for customer-supplied lifecycle idempotency keys.</summary>
public static class ElsaInstanceIdempotencyKey
{
    public static string Normalize(string? value) =>
        ElsaInstanceReferenceValue.RequireOperationKey(value, nameof(value));
}

/// <summary>Canonical validation for lifecycle idempotency operation scopes.</summary>
public static class ElsaInstanceIdempotencyScope
{
    public static string Normalize(string? value) =>
        ElsaInstanceReferenceValue.RequireOperationScope(value, nameof(value));
}
