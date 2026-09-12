using ElsaControl.Deployment.Abstractions.Instances;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Deployment.Core.Instances;

public enum ElsaInstanceProviderObservationKind
{
    Confirmed,
    Unknown,
    Ambiguous
}

public enum ElsaInstanceProviderHealthGate
{
    Passed,
    Failed,
    Unknown,
    NotApplicable
}

/// <summary>
/// Platform-owned, opaque reference to a persisted provider recovery observation.
/// The control plane resolves the record through its scoped store and verifies its
/// digest before it can satisfy the retry-safety gate.
/// </summary>
public static class ElsaInstanceProviderRecoveryObservationReference
{
    private const string Prefix = "urn:elsa-control:provider-recovery-observation:v1:";

    public static string Create(Guid recordId, string digest)
    {
        if (recordId == Guid.Empty)
            throw new ArgumentException("Recovery observation record ID is required.", nameof(recordId));
        if (!IsSha256Digest(digest))
            throw new ArgumentException("Recovery observation digest is invalid.", nameof(digest));

        return $"{Prefix}{recordId:N}:sha256:{digest[7..]}";
    }

    public static bool TryParse(string? reference, out Guid recordId, out string digest)
    {
        recordId = Guid.Empty;
        digest = "";
        if (reference is null || reference.Length != Prefix.Length + 32 + 8 + 64 ||
            !reference.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var remainder = reference[Prefix.Length..];
        if (!remainder.AsSpan(32, 8).SequenceEqual(":sha256:"))
            return false;
        var idText = remainder[..32];
        var digestText = remainder[40..];
        var valid = Guid.TryParseExact(idText, "N", out recordId) &&
                    recordId != Guid.Empty &&
                    idText.SequenceEqual(recordId.ToString("N")) &&
                    digestText.Length == 64 &&
                    digestText.AsSpan().ContainsAnyExcept("0123456789abcdef") is false;
        if (valid)
            digest = "sha256:" + digestText;
        return valid;
    }

    private static bool IsSha256Digest(string? value) =>
        value is not null && value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].AsSpan().ContainsAnyExcept("0123456789abcdef") is false;
}

/// <summary>
/// Opaque, safe evidence that a new provider apply would not duplicate uncertain
/// work. Its presence is advisory to a later retry decision; reconciliation never
/// turns it into an automatic retry.
/// </summary>
public sealed record ElsaInstanceProviderRetryEvidence
{
    public ElsaInstanceProviderRetryEvidence(string reference, string digest)
    {
        var isOpaqueObservation = ElsaInstanceProviderRecoveryObservationReference.TryParse(
            reference, out _, out var referenceDigest);
        Reference = isOpaqueObservation
            ? reference
            : RequireToken(reference, nameof(reference));
        Digest = RequireDigest(digest);
        if (isOpaqueObservation && !string.Equals(referenceDigest, Digest, StringComparison.Ordinal))
            throw new ArgumentException("Retry evidence digest does not match the observation reference.", nameof(digest));
    }

    public string Reference { get; }

    public string Digest { get; }

    private static string RequireToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "oci") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is "" or "/")
            throw new ArgumentException("Retry evidence reference is invalid.", parameterName);
        return value.Trim();
    }

    private static string RequireDigest(string value)
    {
        if (value is null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Retry evidence digest is invalid.", nameof(value));
        return value;
    }
}

/// <summary>
/// Provider-neutral, value-free provider observation. Provider adapters retain raw
/// payloads and expose only a correlated lifecycle fact plus optional retry proof.
/// </summary>
public sealed record ElsaInstanceProviderObservation
{
    public ElsaInstanceProviderObservation(
        ElsaInstanceProviderObservationKind kind,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceProviderHealthGate healthGate,
        string correlationId,
        ElsaInstanceProviderRetryEvidence? retryEvidence = null)
        : this(kind, observedLifecycle, healthGate, Guid.Empty, 0, correlationId, retryEvidence, null, false)
    {
    }

    public ElsaInstanceProviderObservation(
        ElsaInstanceProviderObservationKind kind,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceProviderHealthGate healthGate,
        string correlationId,
        ElsaInstanceProviderRetryEvidence? retryEvidence,
        ElsaCurrentDeploymentReference? currentDeploymentReference)
        : this(kind, observedLifecycle, healthGate, Guid.Empty, 0, correlationId, retryEvidence,
            currentDeploymentReference, true)
    {
    }

    public ElsaInstanceProviderObservation(
        ElsaInstanceProviderObservationKind kind,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceProviderHealthGate healthGate,
        Guid operationId,
        int attemptNumber,
        string correlationId,
        ElsaInstanceProviderRetryEvidence? retryEvidence = null)
        : this(kind, observedLifecycle, healthGate, operationId, attemptNumber, correlationId, retryEvidence, null, false)
    {
    }

    public ElsaInstanceProviderObservation(
        ElsaInstanceProviderObservationKind kind,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceProviderHealthGate healthGate,
        Guid operationId,
        int attemptNumber,
        string correlationId,
        ElsaInstanceProviderRetryEvidence? retryEvidence,
        ElsaCurrentDeploymentReference? currentDeploymentReference)
        : this(kind, observedLifecycle, healthGate, operationId, attemptNumber, correlationId, retryEvidence,
            currentDeploymentReference, true)
    {
    }

    private ElsaInstanceProviderObservation(
        ElsaInstanceProviderObservationKind kind,
        ElsaObservedLifecycle observedLifecycle,
        ElsaInstanceProviderHealthGate healthGate,
        Guid operationId,
        int attemptNumber,
        string correlationId,
        ElsaInstanceProviderRetryEvidence? retryEvidence,
        ElsaCurrentDeploymentReference? currentDeploymentReference,
        bool hasCurrentDeploymentProjection)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(observedLifecycle) || !Enum.IsDefined(healthGate))
            throw new ArgumentOutOfRangeException(nameof(kind), "Provider observation value is invalid.");
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128 ||
            correlationId.Any(char.IsControl) || correlationId.Any(char.IsWhiteSpace))
            throw new ArgumentException("Provider observation correlation is invalid.", nameof(correlationId));
        if (kind != ElsaInstanceProviderObservationKind.Confirmed &&
            (observedLifecycle != ElsaObservedLifecycle.Unknown || healthGate != ElsaInstanceProviderHealthGate.Unknown ||
             hasCurrentDeploymentProjection))
            throw new ArgumentException("Uncertain provider observations must remain unknown.", nameof(observedLifecycle));

        Kind = kind;
        ObservedLifecycle = observedLifecycle;
        HealthGate = healthGate;
        OperationId = operationId;
        AttemptNumber = attemptNumber;
        CorrelationId = correlationId;
        RetryEvidence = retryEvidence;
        CurrentDeploymentReference = currentDeploymentReference;
        HasCurrentDeploymentProjection = hasCurrentDeploymentProjection;
    }

    public ElsaInstanceProviderObservationKind Kind { get; }

    public ElsaObservedLifecycle ObservedLifecycle { get; }

    public ElsaInstanceProviderHealthGate HealthGate { get; }

    public Guid OperationId { get; }

    public int AttemptNumber { get; }

    public string CorrelationId { get; }

    public ElsaInstanceProviderRetryEvidence? RetryEvidence { get; }

    public ElsaCurrentDeploymentReference? CurrentDeploymentReference { get; }

    public bool HasCurrentDeploymentProjection { get; }

    public ElsaInstanceProviderObservation Correlate(ElsaInstanceProviderReconciliationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || request.AttemptNumber < 1)
            throw new ArgumentException("Provider reconciliation request identity is invalid.", nameof(request));
        return HasCurrentDeploymentProjection
            ? new(Kind, ObservedLifecycle, HealthGate, request.OperationId, request.AttemptNumber,
                CorrelationId, RetryEvidence, CurrentDeploymentReference)
            : new(Kind, ObservedLifecycle, HealthGate, request.OperationId, request.AttemptNumber,
                CorrelationId, RetryEvidence);
    }

    internal string ComputeFingerprint()
    {
        var canonical = $"{Kind}\n{ObservedLifecycle}\n{HealthGate}\n{OperationId:D}\n{AttemptNumber}\n{CorrelationId}\n{RetryEvidence?.Reference}\n{RetryEvidence?.Digest}\n{HasCurrentDeploymentProjection}\n{CurrentDeploymentReference?.DeploymentId}\n{CurrentDeploymentReference?.RevisionId}\n{CurrentDeploymentReference?.EndpointUri}\n";
        // Observations recorded before the managed handoff existed never carried it; appending the line only
        // when set keeps their evidence fingerprints stable while distinguishing a configured deployment.
        if (CurrentDeploymentReference?.ManagedHandoff == true)
            canonical += "managed-handoff\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record ElsaInstanceProviderReconciliationRequest(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    ElsaDesiredLifecycle DesiredLifecycle,
    ElsaResolvedPlanReference? ResolvedPlanReference,
    ElsaCurrentDeploymentReference? CurrentDeploymentReference,
    int InstanceVersion = 0);

public sealed record ElsaInstanceProviderReconciliationTarget(
    ElsaInstance Instance,
    ElsaInstanceOperation Operation,
    int ReconciliationVersion)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Instance);
        ArgumentNullException.ThrowIfNull(Operation);
        if (Operation.InstanceId != Instance.Id || Operation.State != ElsaInstanceOperationState.RecoveryRequired ||
            ReconciliationVersion < 0)
            throw new InvalidOperationException("Provider reconciliation target is invalid.");
    }
}

public sealed record ElsaInstanceProviderReconciliationCommit(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int ExpectedInstanceVersion,
    int ExpectedAttemptNumber,
    int ExpectedReconciliationVersion,
    string EvidenceFingerprint,
    ElsaInstance Instance,
    ElsaInstanceOperation Operation,
    string DiagnosticCode,
    bool RetrySafe,
    string? RetryEvidenceReference,
    string? RetryEvidenceDigest,
    DateTimeOffset ReconciledAt)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty ||
            ExpectedInstanceVersion < 1 || ExpectedAttemptNumber < 1 || ExpectedReconciliationVersion < 0 ||
            string.IsNullOrWhiteSpace(EvidenceFingerprint) ||
            EvidenceFingerprint.Length != 64 ||
            EvidenceFingerprint.Any(x => !(char.IsAsciiDigit(x) || x is >= 'a' and <= 'f')) ||
            string.IsNullOrWhiteSpace(DiagnosticCode) || DiagnosticCode.Length > 128 ||
            DiagnosticCode.Any(x => !(char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x is '.' or '-')))
            throw new InvalidOperationException("Provider reconciliation commit envelope is invalid.");
        ArgumentNullException.ThrowIfNull(Instance);
        ArgumentNullException.ThrowIfNull(Operation);
        if (Instance.Id != InstanceId || Instance.WorkspaceId != WorkspaceId ||
            Operation.Id != OperationId || Operation.InstanceId != InstanceId ||
            Operation.AttemptNumber != ExpectedAttemptNumber ||
            RetrySafe != (RetryEvidenceReference is not null && RetryEvidenceDigest is not null) ||
            Operation.State is not (ElsaInstanceOperationState.RecoveryRequired or ElsaInstanceOperationState.Succeeded or ElsaInstanceOperationState.Failed))
            throw new InvalidOperationException("Provider reconciliation commit state is invalid.");
        if (RetrySafe)
            _ = new ElsaInstanceProviderRetryEvidence(RetryEvidenceReference!, RetryEvidenceDigest!);
    }
}

public enum ElsaInstanceProviderReconciliationOutcome
{
    Converged,
    RecoveryRequired,
    HealthGateFailed,
    Failed
}

public sealed record ElsaInstanceProviderReconciliationProjection(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    ElsaObservedLifecycle ObservedLifecycle,
    ElsaInstanceHealth Health,
    int InstanceVersion,
    ElsaInstanceOperationState OperationState);

public sealed record ElsaInstanceProviderReconciliationResult(
    ElsaInstanceProviderReconciliationOutcome Outcome,
    ElsaInstanceProviderReconciliationProjection Projection,
    string DiagnosticCode,
    bool RetrySafe,
    bool Replayed,
    DateTimeOffset ReconciledAt);
