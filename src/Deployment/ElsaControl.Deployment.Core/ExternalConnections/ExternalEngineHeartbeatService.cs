using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ElsaControl.Deployment.Core.ExternalConnections;

public sealed class ExternalEngineHeartbeatService(
    IExternalEngineConnectionStore connections,
    ExternalEngineEnrollmentService enrollment,
    TimeProvider timeProvider)
{
    public const string HeartbeatOperation = "external-engine.heartbeat.submit";
    public const string CurrentProtocol = "1";
    public const string StatusCapability = "connection.status";
    public const string StudioCapability = "studio.open";
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(90);
    /// <summary>Rate-limit floor. The connector MUST NOT heartbeat faster than this.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);
    /// <summary>Normative connector cadence. The connector heartbeats every 15 seconds by default.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);
    /// <summary>Normative ceiling. The connector MUST NOT heartbeat less often than this.</summary>
    public static readonly TimeSpan MaxHeartbeatInterval = TimeSpan.FromSeconds(15);
    /// <summary>
    /// Server-constant runner lease TTL: 3× <see cref="MaxHeartbeatInterval"/>.
    /// Worst-case takeover is TTL plus one max-interval beat, which must stay
    /// inside <see cref="FreshnessWindow"/> so a clean failover never projects stale.
    /// </summary>
    public static readonly TimeSpan RunnerLeaseTtl = TimeSpan.FromSeconds(45);
    public const string RunnerChangedAuditAction = "external-engine.runner-changed";
    public const string LabelChangedAuditAction = "external-engine.label-changed";
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumObservationAge = ExternalEngineEnrollmentDefaults.MaximumProofAge;
    private static readonly HashSet<string> AllowedCapabilities =
        new([StatusCapability, StudioCapability], StringComparer.Ordinal);

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

        if (HasLiveRunnerLease(connection, now)
            && !string.Equals(connection.ActiveRunnerId, report.RunnerId, StringComparison.Ordinal))
        {
            return new(
                ExternalEngineHeartbeatStatus.RunnerConflict,
                connection,
                RetryAfter: RemainingLease(connection, now));
        }

        if (!string.Equals(report.ConnectorProtocol, CurrentProtocol, StringComparison.Ordinal))
        {
            var diagnostic = await connections.TryRecordUnsupportedProtocolAsync(
                connection,
                report.Sequence,
                report.ObservedAt,
                proof.IdentityId,
                now,
                MinimumInterval,
                cancellationToken);
            return diagnostic.Status switch
            {
                ExternalEngineHeartbeatStoreStatus.Applied => new(
                    ExternalEngineHeartbeatStatus.UnsupportedProtocol,
                    ExternalEngineConnectionFreshness.Project(diagnostic.Connection!, now)),
                ExternalEngineHeartbeatStoreStatus.OutOfOrder => new(ExternalEngineHeartbeatStatus.OutOfOrder, diagnostic.Connection),
                ExternalEngineHeartbeatStoreStatus.RateLimited => new(
                    ExternalEngineHeartbeatStatus.RateLimited,
                    diagnostic.Connection,
                    RetryAfter: diagnostic.RetryAfter),
                ExternalEngineHeartbeatStoreStatus.Revoked => new(ExternalEngineHeartbeatStatus.Revoked, diagnostic.Connection),
                ExternalEngineHeartbeatStoreStatus.ScopeMismatch => new(
                    ExternalEngineHeartbeatStatus.ProofDenied,
                    diagnostic.Connection,
                    ExternalEngineConnectorProofFailure.ScopeMismatch),
                _ => new(ExternalEngineHeartbeatStatus.Conflict, diagnostic.Connection)
            };
        }

        if (report.Capabilities.Any(capability => !AllowedCapabilities.Contains(capability)))
            return new(ExternalEngineHeartbeatStatus.InvalidReport, connection);

        var acceptedCapabilities = report.Capabilities.Order(StringComparer.Ordinal).ToArray();
        var evidence = ClassifyReleaseEvidence(report);
        var status = report.RuntimeHealth != ExternalEngineRuntimeHealth.Unhealthy
            ? ExternalEngineConnectionStatus.Connected
            : ExternalEngineConnectionStatus.Degraded;
        var takeover = connection.ActiveRunnerId is not null
            && !string.Equals(connection.ActiveRunnerId, report.RunnerId, StringComparison.Ordinal);
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
            StudioDestinationCandidate: acceptedCapabilities.Contains(StudioCapability, StringComparer.Ordinal)
                ? report.StudioDestination
                : null,
            acceptedCapabilities,
            report.DisplayName,
            report.RunnerId,
            ResetSequenceBaseline: takeover);

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
                case ExternalEngineHeartbeatStoreStatus.RunnerConflict:
                    return new(
                        ExternalEngineHeartbeatStatus.RunnerConflict,
                        stored.Connection,
                        RetryAfter: stored.RetryAfter ?? RemainingLease(stored.Connection ?? connection, now));
                case ExternalEngineHeartbeatStoreStatus.ScopeMismatch:
                    return new(ExternalEngineHeartbeatStatus.ProofDenied, stored.Connection, ExternalEngineConnectorProofFailure.ScopeMismatch);
                case ExternalEngineHeartbeatStoreStatus.Concurrent:
                    connection = await connections.FindAsync(
                        proof.OrganizationId, proof.WorkspaceId, proof.ConnectionId, cancellationToken);
                    if (connection is null)
                        return null;
                    if (HasLiveRunnerLease(connection, now)
                        && !string.Equals(connection.ActiveRunnerId, report.RunnerId, StringComparison.Ordinal))
                    {
                        return new(
                            ExternalEngineHeartbeatStatus.RunnerConflict,
                            connection,
                            RetryAfter: RemainingLease(connection, now));
                    }

                    takeover = connection.ActiveRunnerId is not null
                        && !string.Equals(connection.ActiveRunnerId, report.RunnerId, StringComparison.Ordinal);
                    projection = projection with { ResetSequenceBaseline = takeover };
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
    /// Strings are written with <see cref="Utf8JsonWriter"/> and
    /// <see cref="JavaScriptEncoder.Default"/>: ASCII letters, digits, space, and a small
    /// punctuation set stay literal; HTML-sensitive characters including <c>&amp;</c> become
    /// <c>\u00XX</c> (so <c>&amp;</c> is <c>\u0026</c>); every non-ASCII code point becomes
    /// <c>\uXXXX</c> (for example <c>é</c> is <c>\u00E9</c> and <c>π</c> is <c>\u03C0</c>).
    /// </summary>
    public static byte[] CreateCanonicalPayload(ExternalEngineHeartbeatReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var normalized = Normalize(report);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", normalized.Sequence);
            writer.WriteString("observedAt", normalized.ObservedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));
            writer.WriteString("connectorProtocol", normalized.ConnectorProtocol);
            writer.WriteString("connectorVersion", normalized.ConnectorVersion);
            writer.WriteString("runnerId", normalized.RunnerId);
            if (normalized.DisplayName is not null)
                writer.WriteString("displayName", normalized.DisplayName);
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

    /// <summary>
    /// Connector heartbeats are self-attested. Claimed distribution, version,
    /// runtime kind, and component digests stay on the connection as metadata
    /// labelled <see cref="ExternalEngineReleaseEvidenceLevel.SelfReported"/>.
    /// Only Valence-operated managed or Hosted observation may mint
    /// <see cref="ExternalEngineReleaseEvidenceLevel.VerifiedManifest"/> or
    /// higher.
    /// </summary>
    private static ReleaseEvidence ClassifyReleaseEvidence(ExternalEngineHeartbeatReport report)
    {
        if (report.ObservedDistribution is null || report.ObservedVersion is null)
            return new(ExternalEngineReleaseEvidenceLevel.None, null);

        return new(ExternalEngineReleaseEvidenceLevel.SelfReported, null);
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
        var displayName = NormalizeHostDisplayName(report.DisplayName);
        var runnerId = ExternalEngineEnrollmentProtocol.RequiredRunnerId(report.RunnerId);
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
            Components = components,
            RunnerId = runnerId,
            DisplayName = displayName
        };
    }

    /// <summary>
    /// Host labels are optional. When present they must already be trimmed, are
    /// NFC-normalized, then measured in Unicode code points (UTF-32 / Rune
    /// count). Control, Unicode format (Cf), line/paragraph separator, private-use,
    /// surrogate, and unassigned runes are rejected. The NFC form is what is
    /// stored and hashed.
    /// </summary>
    public static string? NormalizeHostDisplayName(string? value)
    {
        if (value is null)
            return null;
        if (value.Length == 0)
            throw new ArgumentException("DisplayName must be omitted when empty.", nameof(value));
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("DisplayName must not include leading or trailing whitespace.", nameof(value));

        var normalized = value.Normalize(NormalizationForm.FormC);
        var codePoints = 0;
        foreach (var rune in normalized.EnumerateRunes())
        {
            codePoints++;
            if (IsForbiddenDisplayNameRune(rune))
                throw new ArgumentException("DisplayName must contain 1 to 80 Unicode code points of safe plain text.", nameof(value));
        }

        if (codePoints is < 1 or > 80)
            throw new ArgumentException("DisplayName must contain 1 to 80 Unicode code points of safe plain text.", nameof(value));
        return normalized;
    }

    private static bool IsForbiddenDisplayNameRune(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse
            or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned;

    public static bool HasLiveRunnerLease(ExternalEngineConnection connection, DateTimeOffset now) =>
        connection.ActiveRunnerId is not null
        && connection.RunnerLeaseExpiresAt is { } expires
        && expires > now;

    public static TimeSpan RemainingLease(ExternalEngineConnection connection, DateTimeOffset now)
    {
        var remaining = (connection.RunnerLeaseExpiresAt ?? now) - now;
        if (remaining > RunnerLeaseTtl)
            remaining = RunnerLeaseTtl;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1);
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
