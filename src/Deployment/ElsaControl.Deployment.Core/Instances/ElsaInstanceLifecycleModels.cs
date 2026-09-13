using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Immutable, provider-neutral snapshot of the validated runtime-builder input
/// selected for a managed instance. The exact validated JSON bytes are retained so
/// a lifecycle worker can resume from durable state without consulting the
/// original request or a mutable saved configuration.
/// </summary>
public sealed record ElsaInstanceProvisioningContext(
    Guid ApplicationId,
    Guid EnvironmentId,
    string BuilderIntentJson,
    string ConfigurationDigest,
    Guid? RuntimeConfigurationId = null,
    string? ConfigurationName = null,
    string? PreviewDigest = null,
    string? RequestDigest = null,
    string? ResolvedPlanDigest = null)
{
    public const int MaxBuilderIntentJsonLength = 131_072;
    public const int MaxConfigurationNameLength = 200;

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        MaxDepth = 32,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };

    public ElsaInstanceProvisioningContext Normalize()
    {
        ValidateIdentity();
        var json = ValidateJson(BuilderIntentJson);
        var digest = RequireDigest(ConfigurationDigest);
        var expectedDigest = "sha256:" + Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        if (!string.Equals(expectedDigest, digest, StringComparison.Ordinal))
            throw new ArgumentException("Configuration digest does not match the builder intent JSON.", nameof(ConfigurationDigest));
        var configurationName = NormalizeName(ConfigurationName);
        var previewDigest = NormalizeOptionalDigest(PreviewDigest, nameof(PreviewDigest));
        var requestDigest = NormalizeOptionalDigest(RequestDigest, nameof(RequestDigest));
        var resolvedPlanDigest = NormalizeOptionalDigest(ResolvedPlanDigest, nameof(ResolvedPlanDigest));
        if ((previewDigest is null) != (requestDigest is null))
            throw new ArgumentException("Preview and request digests must be supplied together.", nameof(PreviewDigest));
        if (previewDigest is not null && resolvedPlanDigest is null)
            throw new ArgumentException("A resolved plan digest is required for a reviewed provisioning context.", nameof(ResolvedPlanDigest));
        return this with
        {
            BuilderIntentJson = json,
            ConfigurationDigest = digest,
            ConfigurationName = configurationName,
            PreviewDigest = previewDigest,
            RequestDigest = requestDigest,
            ResolvedPlanDigest = resolvedPlanDigest
        };
    }

    public void Validate() => _ = Normalize();

    public string ComputeCanonicalHash()
    {
        var normalized = Normalize();
        var canonical = new StringBuilder()
            .Append(normalized.ApplicationId.ToString("D")).Append('\n')
            .Append(normalized.EnvironmentId.ToString("D")).Append('\n')
            .Append(normalized.BuilderIntentJson.Length).Append(':').Append(normalized.BuilderIntentJson).Append('\n')
            .Append(normalized.ConfigurationDigest).Append('\n')
            .Append(normalized.RuntimeConfigurationId?.ToString("D") ?? "null").Append('\n')
            .Append(normalized.ConfigurationName?.Length.ToString() ?? "null").Append(':')
            .Append(normalized.ConfigurationName ?? "").Append('\n')
            .Append(normalized.PreviewDigest ?? "null").Append('\n')
            .Append(normalized.RequestDigest ?? "null").Append('\n')
            .Append(normalized.ResolvedPlanDigest ?? "null").Append('\n')
            .ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private void ValidateIdentity()
    {
        if (ApplicationId == Guid.Empty || EnvironmentId == Guid.Empty)
            throw new ArgumentException("Provisioning application and environment IDs are required.", nameof(ApplicationId));
        if (RuntimeConfigurationId == Guid.Empty)
            throw new ArgumentException("Runtime configuration ID cannot be empty.", nameof(RuntimeConfigurationId));
    }

    private static string ValidateJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxBuilderIntentJsonLength)
            throw new ArgumentException("Builder intent JSON is required and bounded.", nameof(BuilderIntentJson));

        try
        {
            using var document = JsonDocument.Parse(value, JsonOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Builder intent JSON must be an object.", nameof(BuilderIntentJson));

            return value;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Builder intent JSON is invalid.", nameof(BuilderIntentJson), exception);
        }
    }

    private static string RequireDigest(string value, string parameterName = nameof(ConfigurationDigest))
    {
        var normalized = value?.Trim().ToLowerInvariant();
        if (normalized is null || normalized.Length != 71 ||
            !normalized.StartsWith("sha256:", StringComparison.Ordinal) ||
            normalized[7..].Any(x => !char.IsAsciiHexDigit(x)))
            throw new ArgumentException("A SHA-256 digest is required.", parameterName);
        return normalized;
    }

    private static string? NormalizeOptionalDigest(string? value, string parameterName)
    {
        if (value is null)
            return null;
        return RequireDigest(value, parameterName);
    }

    private static string? NormalizeName(string? value)
    {
        if (value is null)
            return null;
        var normalized = value.Trim();
        if (normalized.Length == 0)
            return null;
        if (normalized.Length > MaxConfigurationNameLength || normalized.Any(char.IsControl))
            throw new ArgumentException("Configuration name is invalid.", nameof(ConfigurationName));
        return normalized;
    }
}

/// <summary>
/// Input for creating a managed Elsa instance. The optional ID lets callers choose
/// an opaque identity before the first request; when omitted the service allocates
/// one and reuses it when the idempotency key is replayed.
/// </summary>
public sealed record ElsaInstanceCreateRequest(
    Guid OrganizationId,
    Guid WorkspaceId,
    string Name,
    string Slug,
    ElsaInstanceIntent Intent,
    string IdempotencyKey,
    Guid? InstanceId = null,
    Guid? ActorAccountId = null,
    ElsaInstanceProvisioningContext? ProvisioningContext = null);

/// <summary>
/// Input shared by lifecycle actions. HTTP adapters should map a strong If-Match
/// ETag to <see cref="ExpectedVersion"/> before calling this provider-neutral port.
/// </summary>
public sealed record ElsaInstanceLifecycleRequest(
    Guid WorkspaceId,
    Guid InstanceId,
    int ExpectedVersion,
    string IdempotencyKey,
    string? Reason = null,
    Guid? DeleteConfirmationId = null,
    Guid? ActorAccountId = null)
{
    public int IfMatchVersion => ExpectedVersion;
}

/// <summary>Input for an immutable intent revision and optional instance metadata update.</summary>
public sealed record ElsaInstanceIntentUpdateRequest(
    Guid WorkspaceId,
    Guid InstanceId,
    ElsaInstanceIntent? Intent,
    int ExpectedVersion,
    string IdempotencyKey,
    string? Name = null,
    string? Reason = null,
    Guid? ActorAccountId = null)
{
    public int IfMatchVersion => ExpectedVersion;
}

/// <summary>
/// Safe durable work notification. The worker reloads the aggregate and resolves it
/// after the acceptance transaction; intent and provider payloads are not copied to
/// the outbox.
/// </summary>
public sealed record ElsaInstanceLifecycleOutboxMessage(
    Guid Id,
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    ElsaInstanceOperationAction Action,
    string RequestHash,
    DateTimeOffset CreatedAt);

/// <summary>Result returned after intent, operation and outbox are accepted.</summary>
public sealed record ElsaInstanceLifecycleAcceptance(
    ElsaInstance Instance,
    ElsaInstanceOperation Operation,
    ElsaInstanceLifecycleOutboxMessage Outbox,
    bool Replayed);

public sealed record ElsaInstanceDeleteConfirmationRequirement(Guid ConfirmationId, Guid AccountId);

public sealed record ElsaInstanceAcceptanceContext(
    Guid? ActorAccountId,
    string? Reason,
    ElsaInstanceDeleteConfirmationRequirement? DeleteConfirmation = null,
    ElsaInstanceProvisioningContext? ProvisioningContext = null);

public sealed class ElsaInstanceDeleteConfirmationException : InvalidOperationException
{
    public ElsaInstanceDeleteConfirmationException() : base("Delete confirmation is invalid or unavailable.") { }
}

/// <summary>
/// Stable classification for an <see cref="ElsaInstanceLifecycleConflictException"/>,
/// used by API adapters to select an HTTP status code and ProblemDetails error code
/// without inspecting the (human-oriented) exception message.
/// </summary>
public enum ElsaInstanceLifecycleConflictReason
{
    /// <summary>An optimistic concurrency (If-Match/expected-version) conflict.</summary>
    VersionConflict,

    /// <summary>An idempotency key was reused for a request that does not match the original.</summary>
    IdempotencyConflict,

    /// <summary>A blocking operation is already active on the instance.</summary>
    OperationActive,

    /// <summary>The requested slug is already reserved by another instance in the workspace.</summary>
    SlugConflict,

    /// <summary>The organization commercial projection does not permit this mutation.</summary>
    CommercialDenied,

    /// <summary>Any other invariant violation that does not fit the categories above.</summary>
    InvalidState,
}

/// <summary>
/// Indicates that a request cannot be accepted because an idempotency or optimistic
/// concurrency invariant was violated. The message is deliberately stable and safe
/// for an API boundary.
/// </summary>
public sealed class ElsaInstanceLifecycleConflictException : InvalidOperationException
{
    public ElsaInstanceLifecycleConflictException(
        string message,
        ElsaInstanceLifecycleConflictReason reason = ElsaInstanceLifecycleConflictReason.InvalidState,
        string? commercialCode = null)
        : base(message)
    {
        Reason = reason;
        CommercialCode = commercialCode;
    }

    public ElsaInstanceLifecycleConflictReason Reason { get; }
    public string? CommercialCode { get; }
}
