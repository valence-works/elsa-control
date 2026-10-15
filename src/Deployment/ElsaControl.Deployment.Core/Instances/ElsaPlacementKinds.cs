namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Catalog placement kinds from ADR-0017. These are catalog strings, not a
/// public C# enum, so a later kind needs no schema change.
/// </summary>
public static class ElsaPlacementKinds
{
    public const string DedicatedValence = "dedicated-valence";
    public const string ByoSubscription = "byo-subscription";
    public const string SharedShard = "shared-shard";
}

public static class ElsaShardClasses
{
    public const string Standard = "standard";
    public const string Lab = "lab";
}

public static class ElsaPlacementAssignmentStates
{
    public const string Reserved = "Reserved";
    public const string Active = "Active";
    public const string Retiring = "Retiring";
    public const string Retired = "Retired";
}

/// <summary>
/// Server-side kind selection used for first placement and move targets.
/// Counting then uses the written assignment, never isolation or TargetMode
/// alone (ADR-0017 Amendment 1).
/// </summary>
public static class ElsaPlacementKindResolver
{
    public const string TrainingLabOrigin = "training-lab";

    public static ElsaPlacementKindSelection FromIsolation(string? isolationProfile, string? origin = null)
    {
        if (string.Equals(origin, TrainingLabOrigin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(isolationProfile, ElsaShardClasses.Lab, StringComparison.OrdinalIgnoreCase))
            return new ElsaPlacementKindSelection(ElsaPlacementKinds.SharedShard, ElsaShardClasses.Lab);

        if (string.Equals(isolationProfile, "shared", StringComparison.OrdinalIgnoreCase))
            return new ElsaPlacementKindSelection(ElsaPlacementKinds.SharedShard, ElsaShardClasses.Standard);

        return new ElsaPlacementKindSelection(ElsaPlacementKinds.DedicatedValence, ShardClass: null);
    }

    public static string IsolationFor(ElsaPlacementKindSelection selection)
    {
        if (selection.ConsumesSharedAllowance)
            return "shared";
        if (selection.IsLab)
            return ElsaShardClasses.Lab;
        return "dedicated";
    }
}

public readonly record struct ElsaPlacementKindSelection(string Kind, string? ShardClass)
{
    public bool ConsumesSharedAllowance =>
        string.Equals(Kind, ElsaPlacementKinds.SharedShard, StringComparison.Ordinal) &&
        string.Equals(ShardClass, ElsaShardClasses.Standard, StringComparison.Ordinal);

    public bool ConsumesDedicatedAllowance =>
        string.Equals(Kind, ElsaPlacementKinds.DedicatedValence, StringComparison.Ordinal) ||
        string.Equals(Kind, ElsaPlacementKinds.ByoSubscription, StringComparison.Ordinal);

    public bool IsLab =>
        string.Equals(Kind, ElsaPlacementKinds.SharedShard, StringComparison.Ordinal) &&
        string.Equals(ShardClass, ElsaShardClasses.Lab, StringComparison.Ordinal);

    public bool IsSameKindAs(string? otherKind, string? otherShardClass) =>
        string.Equals(Kind, otherKind, StringComparison.Ordinal) &&
        string.Equals(ShardClass ?? "", otherShardClass ?? "", StringComparison.Ordinal);
}

/// <summary>
/// Placement context for <see cref="IElsaInstanceCommercialGate"/>. Counts are
/// taken from counting assignments (Reserved or Active; never Retiring). A
/// cross-kind or same-kind move that creates a target tenant counts on both
/// sides until the flip commits.
/// </summary>
public sealed record ElsaInstanceCommercialPlacementRequest(
    string Kind,
    string? ShardClass,
    bool IsMove = false,
    string? SourceKind = null,
    string? SourceShardClass = null,
    int? ActiveSharedCount = null,
    int? ActiveDedicatedCount = null)
{
    public ElsaPlacementKindSelection Selection => new(Kind, ShardClass);

    /// <summary>
    /// A move that writes a new target assignment. Every package-set change
    /// is a move to a fresh tenant (ADR-0017 I1).
    /// </summary>
    public bool CreatesTargetTenant => IsMove;
}

public sealed record ElsaInstancePlacementMoveRequest(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    string TargetKind,
    string? TargetShardClass = null,
    string? PackageSetDigest = null,
    Guid? TargetShardId = null);

public sealed record ElsaInstancePlacementMoveResult(
    Guid InstanceId,
    Guid TargetAssignmentId,
    string TargetKind,
    string? TargetShardClass,
    string TargetState,
    Guid? SourceAssignmentId,
    string? SourceState);

public sealed record ElsaInstancePlacementMoveCompletionRequest(
    Guid OrganizationId,
    Guid WorkspaceId,
    Guid InstanceId,
    Guid TargetAssignmentId);

public interface IElsaInstancePlacementAdmissionStore
{
    Task<ElsaInstancePlacementMoveResult> MovePlacementAsync(
        ElsaInstancePlacementMoveRequest request,
        CancellationToken cancellationToken = default);

    Task<ElsaInstancePlacementMoveResult> CommitPlacementMoveAsync(
        ElsaInstancePlacementMoveCompletionRequest request,
        CancellationToken cancellationToken = default);

    Task<ElsaInstancePlacementMoveResult> AbortPlacementMoveAsync(
        ElsaInstancePlacementMoveCompletionRequest request,
        CancellationToken cancellationToken = default);
}
