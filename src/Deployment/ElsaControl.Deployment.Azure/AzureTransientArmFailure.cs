using System.Text.Json;
using System.Text.RegularExpressions;
using ElsaControl.Deployment.Core.Instances;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Explicit allow-list of transient ARM/provider failures that may auto-retry
/// a failed named deployment. Unknown codes stay operator-recoverable.
/// Nested ARM error trees are walked by <c>details[]</c>. Failed deployment
/// operations contribute structured <c>statusMessage.error</c> / operation
/// <c>error</c> codes only. Message text is never parsed.
/// </summary>
public static class AzureTransientArmFailure
{
    public const string ManagedEnvironmentProvisioningError = "ManagedEnvironmentProvisioningError";
    public const string TooManyRequests = "TooManyRequests";
    public const string AllocationFailed = "AllocationFailed";
    public const string ServerTimeout = "ServerTimeout";
    public const string InternalServerError = "InternalServerError";
    public const string QuotaExceeded = "QuotaExceeded";
    public const string RequestDisallowedByPolicy = "RequestDisallowedByPolicy";
    public const string DeploymentFailed = "DeploymentFailed";
    public const string ResourceDeploymentFailure = "ResourceDeploymentFailure";

    public const string ManagedEnvironmentProvisioningErrorCode = "azure.arm.managed-environment-provisioning-error";
    public const string TooManyRequestsCode = "azure.arm.too-many-requests";
    public const string AllocationFailedCode = "azure.arm.allocation-failed";
    public const string ServerTimeoutCode = "azure.arm.server-timeout";
    public const string InternalServerErrorCode = "azure.arm.internal-server-error";

    internal const int MaximumErrorWalkDepth = 8;
    internal const int MaximumErrorWalkNodes = 64;
    internal const int MaximumErrorWalkFanout = 16;
    internal const int MaximumErrorWalkPages = 4;
    internal const string NestedModuleDeploymentType = "Microsoft.Resources/deployments";

    private static readonly HashSet<string> TransientArmCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ManagedEnvironmentProvisioningError,
        TooManyRequests,
        "429",
        AllocationFailed,
        ServerTimeout,
        InternalServerError
    };

    private static readonly HashSet<string> WrapperArmCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        DeploymentFailed,
        ResourceDeploymentFailure
    };

    private static readonly string[][] OperationErrorPaths =
    [
        ["properties", "statusMessage", "error"],
        ["statusMessage", "error"],
        ["properties", "error"],
        ["error"]
    ];

    private static readonly string[][] OperationStatusPaths =
    [
        ["properties", "statusMessage"],
        ["statusMessage"]
    ];

    private static readonly HashSet<string> TerminalArmCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        QuotaExceeded,
        RequestDisallowedByPolicy,
        "InvalidTemplate",
        "ResourceGroupNotFound"
    };

    private static readonly Regex PascalArmCode = new("^[A-Za-z][A-Za-z0-9]{1,127}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsTransient(string? armErrorCode) =>
        !string.IsNullOrWhiteSpace(armErrorCode) && TransientArmCodes.Contains(armErrorCode.Trim());

    public static bool IsTerminal(string? armErrorCode)
    {
        var normalized = Normalize(armErrorCode);
        return normalized is not null && TerminalArmCodes.Contains(normalized);
    }

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

    /// <summary>
    /// Classifies a complete nested ARM error tree. Unknown or terminal codes,
    /// malformed errors, and truncated walks block retry. Only known wrapper and
    /// allow-listed transient codes may contribute to a transient result.
    /// </summary>
    public static string? Classify(JsonElement error) => InspectError(error).TransientDiagnostic;

    public static string? Classify(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return Classify(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Classify(IEnumerable<string?> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        return Inspect(codes).TransientDiagnostic;
    }

    /// <summary>
    /// Walks a deployment-operations list. Codes come from structured
    /// <c>statusMessage.error</c>, <c>properties.error</c>, and sibling
    /// <c>error</c> objects. Free-text <c>message</c> fields are ignored.
    /// </summary>
    public static string? ClassifyOperations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return ClassifyOperations(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? ClassifyOperations(JsonElement operations)
    {
        var codes = new List<string>();
        CollectOperationCodes(operations, codes, depth: 0, nodes: 0, out var incomplete);
        return incomplete ? null : Classify(codes);
    }

    internal static AzureArmErrorInspection InspectError(JsonElement error)
    {
        var codes = new List<string>();
        var incomplete = false;
        CollectCodes(error, codes, depth: 0, nodes: 0, ref incomplete);
        return incomplete || codes.Count == 0 ? new AzureArmErrorInspection(null, NeedsOperator: true) : Inspect(codes);
    }

    internal static AzureArmErrorInspection Inspect(IEnumerable<string?> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        string? firstTransient = null;
        foreach (var code in codes)
        {
            var normalized = Normalize(code);
            if (normalized is null || (!WrapperArmCodes.Contains(normalized) && !IsTransient(normalized)))
                return new AzureArmErrorInspection(null, NeedsOperator: true);
            firstTransient ??= IsTransient(normalized) ? ToSafeDiagnostic(normalized) : null;
        }

        return new AzureArmErrorInspection(firstTransient, NeedsOperator: false);
    }

    internal static bool IsSafeNestedDeploymentName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length is >= 1 and <= 64 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') &&
        name.IndexOfAny(['/', '\\']) < 0;

    internal static IReadOnlyList<string> FailedNestedDeploymentNames(JsonElement operations, out bool incomplete)
    {
        incomplete = operations.ValueKind != JsonValueKind.Array;
        if (incomplete)
            return [];

        var names = new List<string>();
        var fanout = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            if (fanout++ >= MaximumErrorWalkFanout)
            {
                incomplete = true;
                break;
            }
            if (!TryGetTarget(operation, out var type, out var name))
                continue;
            if (!string.Equals(type, NestedModuleDeploymentType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsSafeNestedDeploymentName(name))
            {
                incomplete = true;
                continue;
            }
            names.Add(name!);
        }

        return names;
    }

    internal static int CollectOperationCodes(
        JsonElement operations, List<string> codes, int depth, int nodes, out bool incomplete)
    {
        incomplete = false;
        if (operations.ValueKind != JsonValueKind.Array)
            return CollectOneOperation(operations, codes, depth, nodes, ref incomplete);

        var fanout = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            if (fanout++ >= MaximumErrorWalkFanout)
            {
                incomplete = true;
                break;
            }
            nodes = CollectOneOperation(operation, codes, depth + 1, nodes, ref incomplete);
            if (incomplete)
                break;
        }

        return nodes;
    }

    public static bool IsRetryingOrNeedsOperator(string? code) =>
        code is ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying
            or ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator
            or ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted
            or ManagedElsaReasonCodeCatalog.AzureDeploymentFailed
            or ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled;

    private static int CollectCodes(JsonElement element, List<string> codes, int depth, int nodes, ref bool incomplete)
    {
        if (nodes >= MaximumErrorWalkNodes || depth > MaximumErrorWalkDepth)
        {
            incomplete = true;
            return nodes;
        }
        nodes++;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String)
                    codes.Add(codeElement.GetString() ?? string.Empty);
                else
                    incomplete = true;

                if (element.TryGetProperty("details", out var details) && details.ValueKind != JsonValueKind.Null)
                {
                    if (details.ValueKind == JsonValueKind.Array)
                        nodes = CollectCodes(details, codes, depth + 1, nodes, ref incomplete);
                    else
                        incomplete = true;
                }
                break;
            case JsonValueKind.Array:
                var fanout = 0;
                foreach (var child in element.EnumerateArray())
                {
                    if (fanout++ >= MaximumErrorWalkFanout)
                    {
                        incomplete = true;
                        break;
                    }
                    nodes = CollectCodes(child, codes, depth + 1, nodes, ref incomplete);
                    if (incomplete)
                        break;
                }
                break;
            default:
                incomplete = true;
                break;
        }

        return nodes;
    }

    private static int CollectOneOperation(JsonElement operation, List<string> codes, int depth, int nodes, ref bool incomplete)
    {
        if (nodes >= MaximumErrorWalkNodes || depth > MaximumErrorWalkDepth)
        {
            incomplete = true;
            return nodes;
        }
        nodes++;
        var found = false;
        // Inspect every supported structured error path. A transient in the first
        // path must not hide an unknown or terminal code in a sibling path.
        foreach (var path in OperationErrorPaths)
        {
            if (!TryGetPath(operation, out var error, path))
                continue;
            found = true;
            nodes = CollectCodes(error, codes, depth + 1, nodes, ref incomplete);
        }
        foreach (var path in OperationStatusPaths)
        {
            if (!TryGetObjectPath(operation, out var status, path) || !status.TryGetProperty("code", out _))
                continue;
            found = true;
            nodes = CollectCodes(status, codes, depth + 1, nodes, ref incomplete);
        }
        if (!found)
            incomplete = true;
        return nodes;
    }

    private static bool TryGetTarget(JsonElement operation, out string? type, out string? name)
    {
        if (TryGetString(operation, "targetType", out type) &&
            TryGetString(operation, "targetName", out name))
            return true;

        if (TryGetObjectPath(operation, out var target, "properties", "targetResource") ||
            TryGetObjectPath(operation, out target, "targetResource"))
        {
            TryGetString(target, "resourceType", out type);
            TryGetString(target, "resourceName", out name);
            return type is not null || name is not null;
        }

        type = null;
        name = null;
        return false;
    }

    private static bool TryGetObjectPath(JsonElement element, out JsonElement value, params string[] path) =>
        TryGetPath(element, out value, path) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetPath(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }
}

internal readonly record struct AzureArmErrorInspection(string? TransientDiagnostic, bool NeedsOperator);
