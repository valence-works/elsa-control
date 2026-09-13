using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.RuntimeBuilder.Abstractions.Plans;
using System.Text.Json;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Safe target identities needed to reserve an existing deployment environment.
/// It contains no provider resource IDs, credentials, or command payload.
/// </summary>
public sealed record ElsaInstanceLifecycleDeploymentTarget(
    Guid ApplicationId,
    Guid EnvironmentId,
    Guid EngineId,
    Guid SourceRevisionId,
    Guid ConfirmationId,
    Guid ActorAccountId)
{
    public void Validate()
    {
        if (ApplicationId == Guid.Empty || EnvironmentId == Guid.Empty || EngineId == Guid.Empty ||
            SourceRevisionId == Guid.Empty || ConfirmationId == Guid.Empty || ActorAccountId == Guid.Empty)
            throw new InvalidOperationException("Lifecycle deployment target identity is invalid.");
    }
}

/// <summary>
/// Safe resolution inputs retained by a worker-facing store. The lifecycle outbox
/// remains ID-only; an adapter reconstructs this typed request from governed data.
/// </summary>
public sealed record ElsaInstanceLifecycleResolutionInput(
    ElsaInstancePlanResolutionRequest PlanRequest,
    ElsaInstanceLifecycleDeploymentTarget DeploymentTarget,
    string? ExpectedPlanDigest = null)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(PlanRequest);
        ArgumentNullException.ThrowIfNull(PlanRequest.InstanceIntent);
        ArgumentNullException.ThrowIfNull(PlanRequest.BuilderIntent);
        ArgumentNullException.ThrowIfNull(PlanRequest.ReleaseManifest);
        DeploymentTarget.Validate();
    }
}

public sealed record ElsaInstanceLifecycleWorkItem(
    ElsaInstanceLifecycleOutboxMessage Outbox,
    ElsaInstanceOperation Operation,
    ElsaInstance Instance,
    ElsaInstanceLifecycleResolutionInput Resolution,
    string? LeaseToken = null,
    int LeaseVersion = 0)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Outbox);
        ArgumentNullException.ThrowIfNull(Operation);
        ArgumentNullException.ThrowIfNull(Instance);
        ArgumentNullException.ThrowIfNull(Resolution);
        if (Outbox.Id == Guid.Empty || Outbox.WorkspaceId == Guid.Empty || Outbox.InstanceId == Guid.Empty ||
            Outbox.OperationId == Guid.Empty || Operation.Id == Guid.Empty || Instance.Id == Guid.Empty ||
            Outbox.OperationId != Operation.Id || Outbox.InstanceId != Operation.InstanceId ||
            Outbox.InstanceId != Instance.Id || Outbox.WorkspaceId != Instance.WorkspaceId ||
            Outbox.Action != Operation.Action ||
            !string.Equals(Outbox.RequestHash, Operation.RequestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Persisted lifecycle work item identity is invalid.");
        if (Operation.State != ElsaInstanceOperationState.Accepted)
            throw new InvalidOperationException("Persisted lifecycle work item is not claimed.");
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
        if (Resolution.PlanRequest.WorkspaceId != Instance.WorkspaceId)
            throw new InvalidOperationException("Lifecycle resolution workspace is invalid.");
        if (!string.Equals(Resolution.PlanRequest.InstanceIntent.ComputeCanonicalHash(), Instance.ComputeCanonicalIntentHash(), StringComparison.Ordinal))
            throw new InvalidOperationException("Lifecycle resolution intent does not match the instance.");
        Resolution.Validate();
    }
}

public sealed record ElsaInstanceLifecycleResolvedPlan(
    ElsaResolvedPlanReference Reference,
    string SerializedPlan)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Reference);
        if (string.IsNullOrWhiteSpace(SerializedPlan))
            throw new InvalidOperationException("Resolved lifecycle plan is empty.");
        try
        {
            var typed = ResolvedElsaApplicationPlanSerialization.Deserialize(SerializedPlan);
            var canonical = ResolvedElsaApplicationPlanSerialization.Serialize(typed);
            if (!string.Equals(canonical, SerializedPlan, StringComparison.Ordinal) ||
                !string.Equals(typed.SchemaVersion, ResolvedElsaApplicationPlanSchema.CurrentVersion, StringComparison.Ordinal) ||
                !int.TryParse(typed.SchemaVersion, out var schemaVersion) ||
                schemaVersion != Reference.SchemaVersion ||
                !string.Equals(
                    ResolvedElsaApplicationPlanSerialization.ComputeContentHash(typed),
                    Reference.ContentHash,
                    StringComparison.Ordinal) ||
                ResolvedElsaApplicationPlanValidator.Validate(typed).Count > 0)
                throw new InvalidOperationException("Resolved lifecycle plan identity is invalid.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new InvalidOperationException("Resolved lifecycle plan identity is invalid.");
        }
    }
}

public sealed record ElsaInstanceLifecycleResolutionCommit(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    Guid OutboxId,
    string RequestHash,
    string WorkerId,
    ElsaInstanceOperation Operation,
    ElsaInstance Instance,
    ElsaInstanceLifecycleResolvedPlan Plan,
    ElsaInstanceLifecycleDeploymentTarget DeploymentTarget,
    DateTimeOffset CommittedAt,
    string? LeaseToken = null,
    int LeaseVersion = 0)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || OutboxId == Guid.Empty)
            throw new InvalidOperationException("Lifecycle resolution commit identity is invalid.");
        if (string.IsNullOrWhiteSpace(WorkerId) || string.IsNullOrWhiteSpace(RequestHash) ||
            RequestHash.Length != 64 || RequestHash.Any(x => !Uri.IsHexDigit(x)))
            throw new InvalidOperationException("Lifecycle resolution commit envelope is invalid.");
        ArgumentNullException.ThrowIfNull(Operation);
        ArgumentNullException.ThrowIfNull(Instance);
        ArgumentNullException.ThrowIfNull(Plan);
        Plan.Validate();
        DeploymentTarget.Validate();
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
        if (Operation.State != ElsaInstanceOperationState.Queued || Operation.Id != OperationId ||
            Operation.InstanceId != InstanceId || Instance.Id != InstanceId || Instance.WorkspaceId != WorkspaceId ||
            !string.Equals(Operation.RequestHash, RequestHash, StringComparison.Ordinal) ||
            !IsExactPlanUri(Plan.Reference.PlanUri, WorkspaceId, InstanceId, Plan.Reference.PlanId) ||
            Instance.ResolvedPlanReference is null ||
            !Equals(Instance.ResolvedPlanReference, Plan.Reference) ||
            Instance.CurrentResolvedRelease is null ||
            !Equals(Instance.CurrentResolvedRelease.PlanReference, Plan.Reference))
            throw new InvalidOperationException("Lifecycle resolution commit state is invalid.");
    }

    private static bool IsExactPlanUri(string value, Guid workspaceId, Guid instanceId, string planId)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 7 &&
               string.Equals(segments[0], "api", StringComparison.Ordinal) &&
               string.Equals(segments[1], "workspaces", StringComparison.Ordinal) &&
               Guid.TryParseExact(segments[2], "D", out var uriWorkspaceId) &&
               uriWorkspaceId == workspaceId &&
               string.Equals(segments[3], "instances", StringComparison.Ordinal) &&
               Guid.TryParseExact(segments[4], "D", out var uriInstanceId) &&
               uriInstanceId == instanceId &&
               string.Equals(segments[5], "resolved-plans", StringComparison.Ordinal) &&
               string.Equals(segments[6], planId, StringComparison.Ordinal) &&
               uri.AbsolutePath.EndsWith('/' + planId, StringComparison.Ordinal) &&
               !uri.AbsolutePath.Contains('%', StringComparison.Ordinal) &&
               !uri.AbsolutePath.Contains('\\', StringComparison.Ordinal) &&
               !uri.AbsolutePath.Contains("//", StringComparison.Ordinal) &&
               !segments.Any(segment => segment is "." or "..");
    }
}

public sealed record ElsaInstanceLifecycleResolutionFailure(
    Guid WorkspaceId,
    Guid InstanceId,
    Guid OperationId,
    Guid OutboxId,
    string RequestHash,
    string WorkerId,
    string Code,
    string Summary,
    DateTimeOffset FailedAt,
    string? LeaseToken = null,
    int LeaseVersion = 0)
{
    public void Validate()
    {
        if (WorkspaceId == Guid.Empty || InstanceId == Guid.Empty || OperationId == Guid.Empty || OutboxId == Guid.Empty ||
            string.IsNullOrWhiteSpace(WorkerId) || string.IsNullOrWhiteSpace(RequestHash) ||
            RequestHash.Length != 64 || RequestHash.Any(x => !Uri.IsHexDigit(x)) ||
            string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(Summary) ||
            Code.Length > 128 || Code.Any(char.IsControl) || Summary.Length > 2000 || Summary.Any(char.IsControl))
            throw new InvalidOperationException("Lifecycle resolution failure envelope is invalid.");
        ElsaInstanceLifecycleLease.Validate(LeaseToken, LeaseVersion);
    }
}

internal static class ElsaInstanceLifecycleLease
{
    public static void Validate(string? token, int version)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 64 ||
            token.Any(x => !char.IsAsciiHexDigit(x)) || version < 1)
            throw new InvalidOperationException("Lifecycle worker lease is invalid.");
    }
}

public enum ElsaInstanceLifecycleWorkerOutcome
{
    Queued = 0,
    Failed = 1,
    AlreadyCompleted = 2,
    WaitingForPriorOperation = 3,
    Conflict = 4,
    Deleted = 5
}

public sealed record ElsaInstanceLifecycleWorkerResult(
    ElsaInstanceLifecycleWorkerOutcome Outcome,
    ElsaInstanceOperation Operation,
    ElsaInstance Instance,
    WorkspaceDeploymentRun? Run = null,
    string? FailureCode = null,
    string? FailureSummary = null);

public sealed record ElsaInstanceLifecycleWorkerBatchResult(
    IReadOnlyList<ElsaInstanceLifecycleWorkerResult> Results,
    int ProviderInvocations);

public sealed record ElsaInstanceLifecycleDeploymentRun(
    WorkspaceDeploymentRun Run,
    ElsaInstanceOperation Operation,
    Guid InstanceId);

public sealed record ElsaInstanceLifecycleRecordedFailure(
    Guid OperationId,
    string Code,
    string Summary,
    DateTimeOffset RecordedAt);
