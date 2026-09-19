using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

public enum ElsaInstanceCleanupObservationKind
{
    ConfirmedAbsent,
    Unknown,
    Ambiguous,
    Unavailable,
    UnsupportedCancellation,
    InProgress
}

public static class ElsaInstanceDeletionDiagnosticCodes
{
    public const string PredecessorRecoverySuperseded = "deletion.predecessor-recovery-superseded";
}

public sealed record ElsaInstanceCleanupEvidence
{
    public ElsaInstanceCleanupEvidence(string reference, string digest)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 2048 || reference.Any(char.IsControl) ||
            !Uri.TryCreate(reference, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "oci") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is "" or "/")
            throw new ArgumentException("Cleanup evidence reference is invalid.", nameof(reference));
        if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Cleanup evidence digest is invalid.", nameof(digest));

        Reference = reference.Trim();
        Digest = digest;
    }

    public string Reference { get; }
    public string Digest { get; }
}

/// <summary>Control-owned cleanup coordinates; never provider resource identifiers.</summary>
public sealed record ElsaInstanceCleanupRequest(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    ElsaCurrentDeploymentReference? CurrentDeployment,
    ElsaPlacementAssignmentReference? PlacementAssignment,
    ElsaTenantReference? Tenant)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || AttemptNumber < 1)
            throw new InvalidOperationException("Cleanup request identity is invalid.");
    }
}

/// <summary>Value-free cleanup fact returned across the provider trust boundary.</summary>
public sealed record ElsaInstanceCleanupObservation(
    ElsaInstanceCleanupObservationKind Kind,
    Guid OperationId,
    int AttemptNumber,
    string DiagnosticCode,
    ElsaInstanceCleanupEvidence? Evidence = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || OperationId == Guid.Empty || AttemptNumber < 1 ||
            string.IsNullOrWhiteSpace(DiagnosticCode) || DiagnosticCode.Length > 128 ||
            DiagnosticCode.Any(x => !(char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x is '.' or '-')))
            throw new InvalidOperationException("Cleanup observation is invalid.");
    }

    public string ComputeFingerprint()
    {
        Validate();
        var canonical = $"{Kind}\n{OperationId:D}\n{AttemptNumber}\n{DiagnosticCode}\n{Evidence?.Reference}\n{Evidence?.Digest}\n";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public interface IElsaInstanceProviderCleanupPort
{
    Task<ElsaInstanceCleanupObservation> CleanupAsync(
        ElsaInstanceCleanupRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional provider capability for an explicitly accepted Azure Delete recovery. Its absence
/// must not turn an accepted provider recovery into an ordinary cleanup submission.
/// </summary>
public interface IElsaInstanceProviderDeleteRecoveryPort
{
    Task<ElsaInstanceCleanupObservation> RecoverDeleteAsync(
        ElsaInstanceDeleteRecoveryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ElsaInstanceDeletionWorkItem(
    ElsaInstanceLifecycleOutboxMessage Outbox,
    ElsaInstanceOperation Operation,
    ElsaInstance Instance,
    bool CanFinalizeLocally,
    Guid? CorrelatedRunId,
    string LeaseToken,
    int LeaseVersion)
{
    /// <summary>
    /// Identifies the immutable lifecycle recovery row when this Delete was explicitly
    /// recovered. Ordinary and local deletions leave it null; providers must not infer a
    /// recovery claim from the mutable operation alone.
    /// </summary>
    public Guid? RecoveryRequestId { get; init; }

    public void Validate()
    {
        if (Outbox.Action != ElsaInstanceOperationAction.Delete || Operation.Action != ElsaInstanceOperationAction.Delete ||
            Operation.State is not (ElsaInstanceOperationState.Accepted or ElsaInstanceOperationState.Running) ||
            Operation.Id != Outbox.OperationId ||
            Instance.Id != Outbox.InstanceId || Instance.Id != Operation.InstanceId ||
            Instance.WorkspaceId != Outbox.WorkspaceId || Instance.Intent.DesiredLifecycle != ElsaDesiredLifecycle.Deleting ||
            CanFinalizeLocally != (Instance.ObservedLifecycle != ElsaObservedLifecycle.Unknown &&
                Instance.CurrentDeploymentReference is null && Instance.PlacementAssignmentReference is null &&
                Instance.ElsaTenantReference is null))
            throw new InvalidOperationException("Deletion work item is invalid.");
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
        if (RecoveryRequestId == Guid.Empty)
            throw new InvalidOperationException("Deletion recovery identity is invalid.");
        if (CanFinalizeLocally && RecoveryRequestId is not null)
            throw new InvalidOperationException("Provider-backed deletion cannot finalize locally.");
    }
}

/// <summary>
/// Control-owned lease and immutable recovery identity passed only to a provider's dedicated
/// Delete recovery port. The ordinary cleanup request deliberately remains provider-neutral.
/// </summary>
public sealed record ElsaInstanceDeleteRecoveryRequest(
    ElsaInstanceCleanupRequest Cleanup,
    Guid RecoveryRequestId,
    int InstanceVersion,
    string WorkerId,
    string LeaseToken,
    int LeaseVersion)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Cleanup);
        Cleanup.Validate();
        if (RecoveryRequestId == Guid.Empty || InstanceVersion < 1 || string.IsNullOrWhiteSpace(WorkerId))
            throw new InvalidOperationException("Delete recovery identity is invalid.");
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
    }
}

public enum ElsaInstanceDeletionProofKind
{
    LocalNoOwnedResources,
    ProviderConfirmedAbsent
}

public sealed record ElsaInstanceDeletionCommit(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    Guid OutboxId,
    int ExpectedInstanceVersion,
    int ExpectedAttemptNumber,
    Guid? ExpectedRunId,
    string WorkerId,
    string LeaseToken,
    int LeaseVersion,
    string EvidenceFingerprint,
    ElsaInstanceDeletionProofKind ProofKind,
    string DiagnosticCode,
    string? EvidenceReference,
    string? EvidenceDigest,
    ElsaInstance Instance,
    ElsaInstanceOperation Operation,
    DateTimeOffset DeletedAt)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || OutboxId == Guid.Empty ||
            ExpectedInstanceVersion < 1 || ExpectedAttemptNumber < 1 || string.IsNullOrWhiteSpace(WorkerId) ||
            string.IsNullOrWhiteSpace(EvidenceFingerprint) || EvidenceFingerprint.Length != 64 ||
            EvidenceFingerprint.Any(x => !(char.IsAsciiDigit(x) || x is >= 'a' and <= 'f')) ||
            !Enum.IsDefined(ProofKind) || string.IsNullOrWhiteSpace(DiagnosticCode) || DiagnosticCode.Length > 128 ||
            DiagnosticCode.Any(x => !(char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x is '.' or '-')) ||
            (EvidenceReference is null) != (EvidenceDigest is null) || DeletedAt == default)
            throw new InvalidOperationException("Deletion commit envelope is invalid.");
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
        if (EvidenceReference is not null)
            _ = new ElsaInstanceCleanupEvidence(EvidenceReference, EvidenceDigest!);
        if (Instance.Id != InstanceId || Instance.WorkspaceId != WorkspaceId ||
            Instance.ObservedLifecycle != ElsaObservedLifecycle.Deleted || Instance.DeletedAt is null ||
            Operation.Id != OperationId || Operation.InstanceId != InstanceId ||
            Operation.Action != ElsaInstanceOperationAction.Delete || Operation.State != ElsaInstanceOperationState.Succeeded)
            throw new InvalidOperationException("Deletion commit state is invalid.");
    }
}

public sealed record ElsaInstanceDeletionFailure(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    Guid OutboxId,
    int ExpectedInstanceVersion,
    int ExpectedAttemptNumber,
    Guid? ExpectedRunId,
    string WorkerId,
    string LeaseToken,
    int LeaseVersion,
    string EvidenceFingerprint,
    string DiagnosticCode,
    DateTimeOffset FailedAt)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || OutboxId == Guid.Empty ||
            ExpectedInstanceVersion < 1 || ExpectedAttemptNumber < 1 || string.IsNullOrWhiteSpace(WorkerId) ||
            string.IsNullOrWhiteSpace(EvidenceFingerprint) || EvidenceFingerprint.Length != 64 ||
            EvidenceFingerprint.Any(x => !(char.IsAsciiDigit(x) || x is >= 'a' and <= 'f')) ||
            string.IsNullOrWhiteSpace(DiagnosticCode) || DiagnosticCode.Length > 128 ||
            DiagnosticCode.Any(x => !(char.IsAsciiLetterLower(x) || char.IsAsciiDigit(x) || x is '.' or '-')) || FailedAt == default)
            throw new InvalidOperationException("Deletion failure envelope is invalid.");
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
    }
}

public enum ElsaInstanceDeletionOutcome { Deleted, RecoveryRequired, AlreadyCompleted, Conflict }

public sealed record ElsaInstanceDeletionResult(
    ElsaInstanceDeletionOutcome Outcome,
    ElsaInstanceOperation Operation,
    ElsaInstance Instance,
    string DiagnosticCode,
    string EvidenceFingerprint,
    bool Replayed);

public interface IElsaInstanceDeletionStore
{
    Task<ElsaInstanceDeletionWorkItem?> TryClaimNextDeletionAsync(string workerId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> RenewDeletionLeaseAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now, CancellationToken cancellationToken = default);
    /// <summary>
    /// Defers a correlated provider cleanup that is still running. Implementations
    /// retain the operation reservation and current worker ownership, but bound the
    /// next claim attempt so a completed provider operation can be observed later.
    /// A false result means the lease or correlation was lost before the deferral.
    /// </summary>
    Task<bool> DeferDeletionAsync(ElsaInstanceDeletionWorkItem item, string workerId, DateTimeOffset now,
        string diagnosticCode, CancellationToken cancellationToken = default);
    Task<ElsaInstanceDeletionResult> CommitDeletionAsync(ElsaInstanceDeletionCommit commit, CancellationToken cancellationToken = default);
    Task<ElsaInstanceDeletionResult> RequireDeletionRecoveryAsync(ElsaInstanceDeletionFailure failure, CancellationToken cancellationToken = default);
}
