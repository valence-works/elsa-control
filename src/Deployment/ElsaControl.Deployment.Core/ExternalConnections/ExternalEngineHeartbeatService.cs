using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;

namespace ElsaControl.Deployment.Core.ExternalConnections;

public sealed class ExternalEngineHeartbeatService(
    IExternalEngineConnectionStore connections,
    ExternalEngineEnrollmentService enrollment,
    IGovernedReleaseCatalogStore releaseCatalog,
    TimeProvider timeProvider)
{
    public const string HeartbeatOperation = "external-engine.heartbeat.submit";
    public const string CurrentProtocol = "1";
    public const string StatusCapability = "connection.status";
    public const string StudioCapability = "studio.open";
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumObservationAge = ExternalEngineEnrollmentDefaults.MaximumProofAge;
    private static readonly HashSet<string> AllowedCapabilities =
        new([StatusCapability, StudioCapability], StringComparer.Ordinal);
    private static readonly HashSet<string> SupportedOssDistributions =
        new(["elsa-oss"], StringComparer.OrdinalIgnoreCase);

    public async Task<ExternalEngineHeartbeatResult?> SubmitAsync(
        ExternalEngineHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = Normalize(request.Report);
        var proof = request.Proof;
        ArgumentNullException.ThrowIfNull(proof);
        var connection = await connections.FindAsync(
            proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId, cancellationToken);
        if (connection is null)
            return null;
        if (connection.Status == ExternalEngineConnectionStatus.Revoked)
            return null;
        if (connection.ActiveIdentityId != proof.IdentityId)
            return new(ExternalEngineHeartbeatStatus.ProofDenied, ProofFailure: ExternalEngineConnectorProofFailure.ScopeMismatch);

        var digest = CreatePayloadDigest(report);
        var proofResult = await enrollment.VerifyConnectorProofAsync(
            proof, HeartbeatOperation, digest, recordSuccessfulNonceAudit: false, cancellationToken);
        if (!proofResult.Succeeded)
            return new(ExternalEngineHeartbeatStatus.ProofDenied, ProofFailure: proofResult.Failure);

        var now = timeProvider.GetUtcNow();
        if (!IsValidObservation(report, now))
            return new(ExternalEngineHeartbeatStatus.InvalidReport);

        if (!string.Equals(report.ConnectorProtocol, CurrentProtocol, StringComparison.Ordinal)
            || report.Capabilities.Any(capability => !AllowedCapabilities.Contains(capability)))
            return new(ExternalEngineHeartbeatStatus.InvalidReport, connection);

        var acceptedCapabilities = report.Capabilities.Order(StringComparer.Ordinal).ToArray();
        var evidence = await ClassifyReleaseEvidenceAsync(report, cancellationToken);
        var status = report.RuntimeHealth != ExternalEngineRuntimeHealth.Unhealthy
            ? ExternalEngineConnectionStatus.Connected
            : ExternalEngineConnectionStatus.Degraded;
        var projection = new ExternalEngineHeartbeatProjection(
            report.Sequence,
            report.ObservedAt,
            status,
            report.RuntimeHealth,
            ExternalEngineConnectorReachability.Reachable,
            report.ConnectorProtocol,
            report.ConnectorVersion,
            report.ObservedDistribution,
            report.ObservedVersion,
            report.RuntimeKind,
            evidence.Level,
            evidence.Reference,
            StudioDestination: null,
            acceptedCapabilities);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var stored = await connections.TryApplyHeartbeatAsync(
                connection, projection, proof.IdentityId, now, MinimumInterval, cancellationToken);
            switch (stored.Status)
            {
                case ExternalEngineHeartbeatStoreStatus.Applied:
                    return new(
                        status == ExternalEngineConnectionStatus.Connected
                            ? ExternalEngineHeartbeatStatus.Accepted
                            : ExternalEngineHeartbeatStatus.Degraded,
                        ExternalEngineConnectionFreshness.Project(stored.Connection!, now));
                case ExternalEngineHeartbeatStoreStatus.OutOfOrder:
                    return new(ExternalEngineHeartbeatStatus.OutOfOrder, stored.Connection);
                case ExternalEngineHeartbeatStoreStatus.RateLimited:
                    return new(ExternalEngineHeartbeatStatus.RateLimited, stored.Connection, RetryAfter: stored.RetryAfter);
                case ExternalEngineHeartbeatStoreStatus.Revoked:
                    return new(ExternalEngineHeartbeatStatus.Revoked, stored.Connection);
                case ExternalEngineHeartbeatStoreStatus.ScopeMismatch:
                    return new(ExternalEngineHeartbeatStatus.ProofDenied, stored.Connection, ExternalEngineConnectorProofFailure.ScopeMismatch);
                case ExternalEngineHeartbeatStoreStatus.Concurrent:
                    connection = await connections.FindAsync(
                        proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId, cancellationToken);
                    if (connection is null)
                        return null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        return new(ExternalEngineHeartbeatStatus.Conflict, connection);
    }

    public static string CreatePayloadDigest(ExternalEngineHeartbeatReport report)
    {
        return ExternalEngineEnrollmentProtocol.Base64UrlEncode(SHA256.HashData(CreateCanonicalPayload(report)));
    }

    /// <summary>
    /// Produces the protocol-v1 heartbeat bytes that the connector signs. The fixed property order,
    /// compact UTF-8 JSON encoding, explicit nulls, normalized arrays, and UTC timestamp format are
    /// part of the wire contract and must remain stable for protocol version 1.
    /// </summary>
    public static byte[] CreateCanonicalPayload(ExternalEngineHeartbeatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var normalized = Normalize(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", normalized.Sequence);
            writer.WriteString("observedAt", normalized.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("connectorProtocol", normalized.ConnectorProtocol);
            writer.WriteString("connectorVersion", normalized.ConnectorVersion);
            writer.WriteString("runtimeHealth", normalized.RuntimeHealth switch
            {
                ExternalEngineRuntimeHealth.Unknown => "unknown",
                ExternalEngineRuntimeHealth.Healthy => "healthy",
                ExternalEngineRuntimeHealth.Unhealthy => "unhealthy",
                _ => throw new ArgumentOutOfRangeException(nameof(report))
            });
            writer.WriteString("runtimeKind", normalized.RuntimeKind);
            WriteNullableString(writer, "observedDistribution", normalized.ObservedDistribution);
            WriteNullableString(writer, "observedVersion", normalized.ObservedVersion);
            WriteNullableString(writer, "studioDestination", normalized.StudioDestination);
            writer.WriteStartArray("capabilities");
            foreach (var capability in normalized.Capabilities)
                writer.WriteStringValue(capability);
            writer.WriteEndArray();
            writer.WriteStartArray("components");
            foreach (var component in normalized.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("id", component.Id);
                writer.WriteString("imageDigest", component.ImageDigest);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
            writer.WriteNull(propertyName);
        else
            writer.WriteString(propertyName, value);
    }

    private async Task<ReleaseEvidence> ClassifyReleaseEvidenceAsync(
        ExternalEngineHeartbeatReport report,
        CancellationToken cancellationToken)
    {
        if (report.ObservedDistribution is null || report.ObservedVersion is null)
            return new(ExternalEngineReleaseEvidenceLevel.None, null);

        var candidates = await releaseCatalog.QueryAsync(
            new GovernedReleaseCatalogQuery(
                DistributionId: report.ObservedDistribution,
                ReleaseVersion: report.ObservedVersion,
                RuntimeKind: report.RuntimeKind),
            cancellationToken);
        if (candidates.Count == 0)
            return new(ExternalEngineReleaseEvidenceLevel.SelfReported, null);

        var matched = candidates.FirstOrDefault(candidate => ComponentsMatch(candidate.Topology.Components, report.Components));
        if (matched is null)
            return new(ExternalEngineReleaseEvidenceLevel.SelfReported, null);
        if (string.Equals(report.ObservedDistribution, "valence-runtime", StringComparison.OrdinalIgnoreCase))
            return new(ExternalEngineReleaseEvidenceLevel.VerifiedManifest, matched.ManifestDigest);

        var supported = SupportedOssDistributions.Contains(report.ObservedDistribution)
            ? candidates.FirstOrDefault(candidate =>
            ComponentsMatch(candidate.Topology.Components, report.Components)
            && string.Equals(candidate.CatalogLifecycle, "supported", StringComparison.OrdinalIgnoreCase))
            : null;
        return supported is null
            ? new(ExternalEngineReleaseEvidenceLevel.SelfReported, null)
            : new(ExternalEngineReleaseEvidenceLevel.SupportedRelease, supported.ManifestDigest);
    }

    private static bool ComponentsMatch(
        IReadOnlyList<GovernedReleaseComponent> catalog,
        IReadOnlyList<ExternalEngineComponentObservation> observed)
    {
        if (catalog.Count == 0 || catalog.Count != observed.Count)
            return false;
        var observedById = observed.ToDictionary(x => x.Id, x => x.ImageDigest, StringComparer.OrdinalIgnoreCase);
        return catalog.All(component =>
            observedById.TryGetValue(component.Id, out var digest)
            && string.Equals(digest, component.ImageDigest, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidObservation(ExternalEngineHeartbeatReport report, DateTimeOffset now) =>
        report.Sequence > 0
        && report.ObservedAt <= now + MaximumFutureSkew
        && report.ObservedAt >= now - MaximumObservationAge;

    private static ExternalEngineHeartbeatReport Normalize(ExternalEngineHeartbeatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Enum.IsDefined(report.RuntimeHealth))
            throw new ArgumentException("Runtime health is invalid.", nameof(report));
        var protocol = Required(report.ConnectorProtocol, 64, nameof(report.ConnectorProtocol));
        var connectorVersion = Required(report.ConnectorVersion, 64, nameof(report.ConnectorVersion));
        var runtimeKind = Required(report.RuntimeKind, 64, nameof(report.RuntimeKind));
        var distribution = Optional(report.ObservedDistribution, 128, nameof(report.ObservedDistribution));
        var version = Optional(report.ObservedVersion, 128, nameof(report.ObservedVersion));
        if ((distribution is null) != (version is null))
            throw new ArgumentException("Observed distribution and version must be supplied together.", nameof(report));

        var capabilities = (report.Capabilities ?? [])
            .Select(value => Required(value, 128, nameof(report.Capabilities)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(33)
            .ToArray();
        if (capabilities.Length > 32)
            throw new ArgumentException("At most 32 capabilities may be reported.", nameof(report));

        var components = (report.Components ?? [])
            .Select(component => component is null
                ? throw new ArgumentException("Component evidence cannot contain null items.", nameof(report))
                : new ExternalEngineComponentObservation(
                    Required(component.Id, 128, nameof(report.Components)),
                    NormalizeDigest(component.ImageDigest)))
            .OrderBy(component => component.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(component => component.ImageDigest, StringComparer.OrdinalIgnoreCase)
            .Take(33)
            .ToArray();
        if (components.Length > 32 || components.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != components.Length)
            throw new ArgumentException("Component evidence must contain at most 32 unique component IDs.", nameof(report));

        return report with
        {
            ObservedAt = report.ObservedAt.ToUniversalTime(),
            ConnectorProtocol = protocol,
            ConnectorVersion = connectorVersion,
            RuntimeKind = runtimeKind,
            ObservedDistribution = distribution,
            ObservedVersion = version,
            StudioDestination = NormalizeStudioDestination(report.StudioDestination),
            Capabilities = capabilities,
            Components = components
        };
    }

    private static string? NormalizeStudioDestination(string? value)
    {
        var normalized = Optional(value, 2048, nameof(ExternalEngineHeartbeatReport.StudioDestination));
        if (normalized is null)
            return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Studio destination must be an absolute HTTPS URL without credentials, query, or fragment.", nameof(value));
        return uri.AbsoluteUri;
    }

    private static string NormalizeDigest(string value)
    {
        var normalized = Required(value, 71, nameof(value)).ToLowerInvariant();
        if (!normalized.StartsWith("sha256:", StringComparison.Ordinal)
            || normalized.Length != 71
            || normalized[7..].Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Component image digests must use canonical sha256:<64 hex> form.", nameof(value));
        return normalized;
    }

    private static string Required(string value, int maximumLength, string parameterName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > maximumLength
            || normalized.Any(char.IsControl))
            throw new ArgumentException($"{parameterName} is required and must contain safe bounded text.", parameterName);
        return normalized;
    }

    private static string? Optional(string? value, int maximumLength, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, maximumLength, parameterName);

    private sealed record ReleaseEvidence(ExternalEngineReleaseEvidenceLevel Level, string? Reference);
}

public static class ExternalEngineConnectionFreshness
{
    public static ExternalEngineConnection Project(ExternalEngineConnection connection, DateTimeOffset now)
    {
        if (connection.Status == ExternalEngineConnectionStatus.Revoked || connection.LastAuthenticatedAt is null)
            return connection;
        if (now - connection.LastAuthenticatedAt <= ExternalEngineHeartbeatService.FreshnessWindow)
            return connection;
        return connection with
        {
            Status = ExternalEngineConnectionStatus.Degraded,
            ConnectorReachability = ExternalEngineConnectorReachability.Unreachable
        };
    }


    public static ExternalEngineHeartbeatFreshness Classify(ExternalEngineConnection connection, DateTimeOffset now) =>
        connection.Status == ExternalEngineConnectionStatus.Revoked
            ? ExternalEngineHeartbeatFreshness.Revoked
            : connection.LastAuthenticatedAt is null
                ? ExternalEngineHeartbeatFreshness.Waiting
                : now - connection.LastAuthenticatedAt > ExternalEngineHeartbeatService.FreshnessWindow
                    ? ExternalEngineHeartbeatFreshness.Stale
                    : ExternalEngineHeartbeatFreshness.Fresh;
}
