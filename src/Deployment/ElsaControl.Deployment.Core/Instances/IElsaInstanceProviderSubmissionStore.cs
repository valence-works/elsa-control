namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Durable hand-off marker for provider submission. Implementations move the
/// reserved lifecycle run to recovery-required after a provider operation has a
/// stable correlation, so reconciliation can resume after a process restart.
/// </summary>
public interface IElsaInstanceProviderSubmissionStore
{
    Task CommitProviderSubmissionAsync(
        ElsaInstanceProviderSubmissionCommit commit,
        CancellationToken cancellationToken = default);

    Task FailRejectedProviderSubmissionAsync(
        ElsaInstanceProviderSubmissionRejection rejection,
        CancellationToken cancellationToken = default);
}

public sealed record ElsaInstanceProviderSubmissionCommit(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    string CorrelationId,
    DateTimeOffset SubmittedAt,
    string? PlacementAssignmentId = null)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || AttemptNumber < 1 ||
            string.IsNullOrWhiteSpace(CorrelationId) || CorrelationId.Length > 128 ||
            CorrelationId.Any(char.IsControl) || CorrelationId.Any(char.IsWhiteSpace) ||
            PlacementAssignmentId is not null && !Guid.TryParseExact(PlacementAssignmentId, "D", out _))
            throw new InvalidOperationException("Provider submission commit identity is invalid.");
    }
}

/// <summary>
/// Durable terminal failure for a deterministic provider rejection. The reserved
/// lifecycle run never crossed a remote hand-off, so replay must stop.
/// </summary>
public sealed record ElsaInstanceProviderSubmissionRejection(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    int AttemptNumber,
    DateTimeOffset RejectedAt,
    IReadOnlyList<string>? FindingCodes = null)
{
    public IReadOnlyList<string> SafeFindingCodes { get; } =
        ManagedElsaReasonCodeCatalog.SanitizeTranslatorFindings(FindingCodes);

    public string? FirstFindingCode => SafeFindingCodes.Count == 0 ? null : SafeFindingCodes[0];

    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || AttemptNumber < 1)
            throw new InvalidOperationException("Provider submission rejection identity is invalid.");
    }
}
