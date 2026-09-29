using System.Security.Cryptography;
using System.Text;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Core.Telemetry;

/// <summary>
/// Writes the single operator-alert event for an actual entry into
/// <c>RecoveryRequired</c>. Callers invoke this only from the state-machine
/// compare-and-set that changed the row; reads, refreshes, and auto-resume
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
        Guid? runId = null)
    {
        if (workspaceId == Guid.Empty || instanceId == Guid.Empty || operationId == Guid.Empty)
            return;

        ManagedLifecycleTelemetry.RecordRecoveryRequiredEntered(
            workspaceId,
            instanceId,
            operationId,
            ReasonCode,
            ComputeDedupeIdentity(workspaceId, instanceId, operationId, runId));
    }

    internal static string ComputeDedupeIdentity(
        Guid workspaceId,
        Guid instanceId,
        Guid operationId,
        Guid? runId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(
            '\n',
            workspaceId.ToString("D"),
            instanceId.ToString("D"),
            operationId.ToString("D"),
            runId?.ToString("D") ?? string.Empty,
            ReasonCode))));
}
