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
    /// Classifies a nested ARM error tree. Any terminal nested code blocks retry.
    /// Otherwise the first allow-listed nested code becomes the safe diagnostic.
    /// Wrapper codes such as DeploymentFailed and ResourceDeploymentFailure are neither.
    /// </summary>
    public static string? Classify(JsonElement error)
    {
        var codes = new List<string>();
        CollectCodes(error, codes, depth: 0, nodes: 0);
        return Classify(codes);
    }

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
        CollectOperationCodes(operations, codes, depth: 0, nodes: 0);
        return Classify(codes);
    }

    internal static AzureArmErrorInspection InspectError(JsonElement error)
    {
        var codes = new List<string>();
        CollectCodes(error, codes, depth: 0, nodes: 0);
        return Inspect(codes);
    }

    internal static AzureArmErrorInspection Inspect(IEnumerable<string?> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        string? firstTransient = null;
        foreach (var code in codes)
        {
            var normalized = Normalize(code);
            if (normalized is null)
                continue;
            if (TerminalArmCodes.Contains(normalized))
                return new AzureArmErrorInspection(null, HasTerminal: true);
            firstTransient ??= IsTransient(normalized) ? ToSafeDiagnostic(normalized) : null;
        }

        return new AzureArmErrorInspection(firstTransient, HasTerminal: false);
    }

    internal static bool IsSafeNestedDeploymentName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length is >= 1 and <= 64 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') &&
        name.IndexOfAny(['/', '\\']) < 0;

    internal static IReadOnlyList<string> FailedNestedDeploymentNames(JsonElement operations)
    {
        if (operations.ValueKind != JsonValueKind.Array)
            return [];

        var names = new List<string>();
        var fanout = 0;
        foreach (var operation in operations.EnumerateArray())
        {
            if (fanout++ >= MaximumErrorWalkFanout)
                break;
            if (!TryGetTarget(operation, out var type, out var name) || name is null)
                continue;
            if (!string.Equals(type, NestedModuleDeploymentType, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsSafeNestedDeploymentName(name))
                continue;
            names.Add(name);
        }

        return names;
    }

    internal static int CollectOperationCodes(JsonElement operations, List<string> codes, int depth, int nodes)
    {
        if (operations.ValueKind == JsonValueKind.Array)
        {
            var fanout = 0;
            foreach (var operation in operations.EnumerateArray())
            {
                if (fanout++ >= MaximumErrorWalkFanout || nodes >= MaximumErrorWalkNodes || depth > MaximumErrorWalkDepth)
                    break;
                nodes = CollectOneOperation(operation, codes, depth + 1, nodes);
            }

            return nodes;
        }

        return CollectOneOperation(operations, codes, depth, nodes);
    }

    public static bool IsRetryingOrNeedsOperator(string? code) =>
        code is ManagedElsaReasonCodeCatalog.AzureRecoveryRetrying
            or ManagedElsaReasonCodeCatalog.AzureRecoveryNeedsOperator
            or ManagedElsaReasonCodeCatalog.AzureRecoveryAutoResumeExhausted
            or ManagedElsaReasonCodeCatalog.AzureDeploymentFailed
            or ManagedElsaReasonCodeCatalog.AzureDeploymentCanceled;

    private static int CollectCodes(JsonElement element, List<string> codes, int depth, int nodes)
    {
        if (nodes >= MaximumErrorWalkNodes || depth > MaximumErrorWalkDepth)
            return nodes;
        nodes++;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("code", out var codeElement) &&
                    codeElement.ValueKind == JsonValueKind.String)
                {
                    var code = codeElement.GetString();
                    if (!string.IsNullOrWhiteSpace(code))
                        codes.Add(code);
                }

                if (element.TryGetProperty("details", out var details) &&
                    details.ValueKind == JsonValueKind.Array)
                    nodes = CollectCodes(details, codes, depth + 1, nodes);
                break;
            case JsonValueKind.Array:
                var fanout = 0;
                foreach (var child in element.EnumerateArray())
                {
                    if (fanout++ >= MaximumErrorWalkFanout || nodes >= MaximumErrorWalkNodes)
                        break;
                    nodes = CollectCodes(child, codes, depth + 1, nodes);
                }

                break;
        }

        return nodes;
    }

    private static int CollectOneOperation(JsonElement operation, List<string> codes, int depth, int nodes)
    {
        if (nodes >= MaximumErrorWalkNodes || depth > MaximumErrorWalkDepth)
            return nodes;
        nodes++;
        if (TryGetStructuredOperationError(operation, out var error))
            nodes = CollectCodes(error, codes, depth + 1, nodes);
        return nodes;
    }

    private static bool TryGetStructuredOperationError(JsonElement operation, out JsonElement error)
    {
        if (TryGetObjectPath(operation, out error, "properties", "statusMessage", "error") ||
            TryGetObjectPath(operation, out error, "statusMessage", "error") ||
            TryGetObjectPath(operation, out error, "properties", "error") ||
            TryGetObjectPath(operation, out error, "error"))
            return true;

        if ((TryGetObjectPath(operation, out var statusMessage, "properties", "statusMessage") ||
             TryGetObjectPath(operation, out statusMessage, "statusMessage")) &&
            statusMessage.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.String)
        {
            error = statusMessage;
            return true;
        }

        error = default;
        return false;
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

    private static bool TryGetObjectPath(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;
        foreach (var segment in path)
        {
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty(segment, out value) ||
                value.ValueKind != JsonValueKind.Object)
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

internal readonly record struct AzureArmErrorInspection(string? TransientDiagnostic, bool HasTerminal);
