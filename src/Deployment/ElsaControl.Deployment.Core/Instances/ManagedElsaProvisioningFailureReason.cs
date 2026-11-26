using System.Diagnostics.CodeAnalysis;

namespace ElsaControl.Deployment.Core.Instances;

/// <summary>
/// Maps persisted provider observations onto the closed, value-free customer
/// reason codes for honest provisioning failures (#1011). Internal Azure
/// health and wait-exceeded codes stay on the provider path; only these
/// customer codes cross the Hub contract.
/// </summary>
public static class ManagedElsaProvisioningFailureReason
{
    public const string AzureHealthCrashLoop = "azure.health.crash-loop";
    public const string AzureHealthTimeout = "azure.health.timeout";
    public const string AzureHealthUnhealthy = "azure.health.unhealthy";

    public static bool IsCustomerCode(string? code) =>
        code is ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop
            or ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth
            or ManagedElsaReasonCodeCatalog.ProvisioningTimeoutFoundation
            or ManagedElsaReasonCodeCatalog.ProvisioningTimeoutWorkload;

    public static bool IsProviderPersistedCode(string? code) =>
        IsCustomerCode(code) ||
        code is AzureHealthCrashLoop
            or AzureHealthTimeout
            or AzureHealthUnhealthy
            or ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded;

    public static bool TryMap(
        string? code,
        [NotNullWhen(true)] out string? customerCode) =>
        TryMap(code, currentStage: null, out customerCode);

    public static bool TryMap(
        string? code,
        string? currentStage,
        [NotNullWhen(true)] out string? customerCode)
    {
        customerCode = null;
        if (string.IsNullOrWhiteSpace(code))
            return false;
        if (IsCustomerCode(code))
        {
            customerCode = code;
            return true;
        }

        if (code is AzureHealthCrashLoop)
        {
            customerCode = ManagedElsaReasonCodeCatalog.ProvisioningHealthCrashLoop;
            return true;
        }

        if (code is AzureHealthTimeout or AzureHealthUnhealthy)
        {
            customerCode = ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth;
            return true;
        }

        if (string.Equals(code, ManagedElsaReasonCodeCatalog.AzureDeploymentWaitExceeded, StringComparison.Ordinal))
        {
            customerCode = TimeoutForStage(currentStage);
            return customerCode is not null;
        }

        return false;
    }

    public static string? Resolve(
        string? reconciliationDiagnosticCode,
        string? failureCode,
        string? lastObservationReasonCode,
        IEnumerable<string>? diagnosticCodes,
        string? currentStage)
    {
        foreach (var code in Candidates(
                     reconciliationDiagnosticCode,
                     lastObservationReasonCode,
                     diagnosticCodes,
                     failureCode))
        {
            if (TryMap(code, currentStage, out var mapped))
                return mapped;
        }

        return ManagedElsaReasonCodeCatalog.IsSafeTranslatorFinding(reconciliationDiagnosticCode)
            ? reconciliationDiagnosticCode
            : null;
    }

    public static string? TimeoutForStage(string? currentStage) => currentStage switch
    {
        ManagedElsaProvisioningProgressStages.HostingFoundation =>
            ManagedElsaReasonCodeCatalog.ProvisioningTimeoutFoundation,
        ManagedElsaProvisioningProgressStages.RuntimeDeployment =>
            ManagedElsaReasonCodeCatalog.ProvisioningTimeoutWorkload,
        ManagedElsaProvisioningProgressStages.HealthVerification =>
            ManagedElsaReasonCodeCatalog.ProvisioningTimeoutHealth,
        _ => null
    };

    private static IEnumerable<string> Candidates(
        string? reconciliationDiagnosticCode,
        string? lastObservationReasonCode,
        IEnumerable<string>? diagnosticCodes,
        string? failureCode)
    {
        if (!string.IsNullOrWhiteSpace(reconciliationDiagnosticCode))
            yield return reconciliationDiagnosticCode;
        if (!string.IsNullOrWhiteSpace(lastObservationReasonCode))
            yield return lastObservationReasonCode;
        if (diagnosticCodes is not null)
        {
            foreach (var code in diagnosticCodes)
            {
                if (!string.IsNullOrWhiteSpace(code))
                    yield return code;
            }
        }

        if (!string.IsNullOrWhiteSpace(failureCode))
            yield return failureCode;
    }
}
