using System.Text.RegularExpressions;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Explicit allow-list of transient ARM/provider failures that may auto-retry
/// a failed named deployment. Unknown codes stay operator-recoverable.
/// </summary>
public static class AzureTransientArmFailure
{
    public const string ManagedEnvironmentProvisioningError = "ManagedEnvironmentProvisioningError";
    public const string TooManyRequests = "TooManyRequests";
    public const string AllocationFailed = "AllocationFailed";
    public const string ServerTimeout = "ServerTimeout";
    public const string InternalServerError = "InternalServerError";

    public const string ManagedEnvironmentProvisioningErrorCode = "azure.arm.managed-environment-provisioning-error";
    public const string TooManyRequestsCode = "azure.arm.too-many-requests";
    public const string AllocationFailedCode = "azure.arm.allocation-failed";
    public const string ServerTimeoutCode = "azure.arm.server-timeout";
    public const string InternalServerErrorCode = "azure.arm.internal-server-error";

    private static readonly HashSet<string> TransientArmCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ManagedEnvironmentProvisioningError,
        TooManyRequests,
        "429",
        AllocationFailed,
        ServerTimeout,
        InternalServerError
    };

    private static readonly Regex PascalArmCode = new("^[A-Za-z][A-Za-z0-9]{1,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsTransient(string? armErrorCode) =>
        !string.IsNullOrWhiteSpace(armErrorCode) && TransientArmCodes.Contains(armErrorCode.Trim());

    public static string? Normalize(string? armErrorCode)
    {
        if (string.IsNullOrWhiteSpace(armErrorCode))
            return null;
        var trimmed = armErrorCode.Trim();
        if (string.Equals(trimmed, "429", StringComparison.Ordinal))
            return TooManyRequests;
        return PascalArmCode.IsMatch(trimmed) ? trimmed : null;
    }

    public static string? ToSafeDiagnostic(string? armErrorCode) =>
        Normalize(armErrorCode) switch
        {
            ManagedEnvironmentProvisioningError => ManagedEnvironmentProvisioningErrorCode,
            TooManyRequests => TooManyRequestsCode,
            AllocationFailed => AllocationFailedCode,
            ServerTimeout => ServerTimeoutCode,
            InternalServerError => InternalServerErrorCode,
            _ => null
        };

    public static bool IsRetryingOrNeedsOperator(string? code) =>
        code is ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying
            or ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator
            or ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted
            or ManagedElsaReasonCodeCatalog.AzureDeploymentFailed
            or ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled;
}
