namespace ElsaControl.Deployment.Core.Instances;

/// <summary>Lists durable lifecycle operations waiting for provider observation.</summary>
public interface IElsaInstanceProviderPendingOperationStore
{
    Task<IReadOnlyList<ElsaInstanceProviderPendingOperation>> ListPendingProviderOperationsAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A durable lifecycle reservation that still needs the provider hand-off or
/// provider observation. The optional submission is reconstructed from the
/// persisted, already-admitted plan; it is intentionally absent when the store
/// cannot prove that reconstruction is safe.
/// Recovery carries a durably accepted explicit recovery envelope, not an inferred
/// retry based on an attempt number. It is absent for ordinary pending work or when
/// the store cannot prove the accepted recovery binding. Recovery hand-off requires
/// both a valid submission and its matching envelope; absence never authorizes replay.
/// </summary>
public sealed record ElsaInstanceProviderPendingOperation(
    Guid WorkspaceId,
    Guid OperationId,
    ElsaInstanceProviderSubmission? Submission = null,
    ElsaInstanceProviderRecoveryEnvelope? Recovery = null)
{
    /// <summary>
    /// Required hand-off metadata could not be restored safely. Neither submission
    /// nor ordinary provider observation is permitted until the metadata is repaired.
    /// This differs from a valid observation-only item with no submission.
    /// </summary>
    public bool HandoffInvalid { get; init; }

    /// <summary>
    /// Distinct value-free reason for <see cref="HandoffInvalid"/>. Absent when the
    /// item is valid or the store could not classify the restore failure.
    /// </summary>
    public string? HandoffInvalidReason { get; init; }
}
