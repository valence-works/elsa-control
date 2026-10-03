using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Core.Telemetry;

/// <summary>
/// Writes the single operator-alert event after
/// <c>RequiresHumanAt</c> is set. Callers invoke this only from the
/// post-commit outbox flush; reads, refreshes, and auto-resume
/// outcome writes must not call it.
/// </summary>
public static class ManagedLifecycleRecoveryRequiredAlert
{
    public const string EventName = ManagedLifecycleTelemetry.RecoveryRequiredEnteredActivityName;
    public const string ReasonCode = ManagedLifecycleOperationalHealthDiagnosticCodes.RecoveryRequired;

    public static void RecordEntered(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        int attemptNumber,
        Guid? runId = null,
        string? persistedDedupeIdentity = null)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty || operationId == Guid.Empty || attemptNumber < 1)
            return;

        var dedupe = string.IsNullOrWhiteSpace(persistedDedupeIdentity)
            ? ComputeDedupeIdentity(workspaceId, instanceId, operationId, attemptNumber, runId)
            : persistedDedupeIdentity;
        ManagedLifecycleTelemetry.RecordRecoveryRequiredEntered(
            workspaceId,
            instanceId,
            operationId,
            ReasonCode,
            dedupe);
    }

    public static string ComputeDedupeIdentity(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        int attemptNumber,
        Guid? runId = null) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            workspaceId.ToString("D"),
            instanceId.ToString("D"),
            operationId.ToString("D"),
            attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            runId?.ToString("D") ?? string.Empty,
            ReasonCode))));
}
