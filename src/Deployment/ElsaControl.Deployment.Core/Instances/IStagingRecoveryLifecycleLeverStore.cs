using ElsaControl.Deployment.Abstractions.Instances;

namespace ElsaControl.Deployment.Core.Instances;

public static class StagingRecoveryLifecycleLeverStoreDefaults
{
    public const string TransitionCode = "staging.lever.recovery-required";
    public const string FiredEventType = "staging.lever.fired";
    public const string ResetEventType = "staging.lever.reset";
    public const string ResetCode = "staging.lever.reset";
    public const string RecoveryRequiredEventType = "lifecycle.recovery-required";
    public const string AcceptedEventType = "lifecycle.accepted";
}

/// <summary>
/// Persistence port for the staging recovery lever. Implementations accept a
/// normal Reconcile and transition it to RecoveryRequired in one unit of work
/// using <see cref="ElsaInstanceOperation.TransitionTo"/>. A matching reset
/// clears only a lever-parked operation through the same transition methods;
/// it never fabricates provider retry evidence or calls production Recover.
/// </summary>
public interface IStagingRecoveryLifecycleLeverStore
{
    Task<StagingRecoveryLifecycleLeverCommit> AcceptReconcileAndRequireRecoveryAsync(
        Guid instanceId,
        string? operatorSubject,
        CancellationToken cancellationToken = default);

    Task<StagingRecoveryLifecycleLeverCommit> ResetLeverParkedReconcileAsync(
        Guid instanceId,
        string? operatorSubject,
        CancellationToken cancellationToken = default);
}

public sealed record StagingRecoveryLifecycleLeverCommit(
    ElsaInstance Instance,
    ElsaInstanceOperation Operation);
