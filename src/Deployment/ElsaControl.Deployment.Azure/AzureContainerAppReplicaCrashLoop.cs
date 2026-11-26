using System.Text.Json;
using System.Text.RegularExpressions;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Value-free crash-loop evidence from Container Apps replica observations.
/// A true result is evidence for the Health window, not a terminal
/// classification; the runner classifies crash-loop only when that window
/// expires. Replica names, resource IDs, and provider detail strings are
/// inspected in memory and never returned.
/// </summary>
internal static partial class AzureContainerAppReplicaCrashLoop
{
    internal const int RestartThreshold = 2;

    public static bool Detected(ReadOnlyMemory<char> json)
    {
        if (json.IsEmpty)
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            return Detected(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool Detected(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var replica in root.EnumerateArray())
            {
                if (ReplicaIsCrashLoop(replica))
                    return true;
            }

            return false;
        }

        return root.ValueKind == JsonValueKind.Object && ReplicaIsCrashLoop(root);
    }

    private static bool ReplicaIsCrashLoop(JsonElement replica)
    {
        var properties = replica.TryGetProperty("properties", out var nested) &&
                         nested.ValueKind == JsonValueKind.Object
            ? nested
            : replica;

        if (IsFailedRunningState(ReadString(properties, "runningState") ?? ReadString(replica, "runningState")))
            return true;
        if (DetailsIndicateCrashLoop(
                ReadString(properties, "runningStateDetails") ?? ReadString(replica, "runningStateDetails")))
            return true;
        if (ContainersIndicateCrashLoop(properties, replica))
            return true;

        return RestartCountsIndicateCrashLoop(properties) ||
               RestartCountsIndicateCrashLoop(replica);
    }

    private static bool ContainersIndicateCrashLoop(JsonElement properties, JsonElement replica)
    {
        if (TryGetContainers(properties, out var containers) || TryGetContainers(replica, out containers))
        {
            foreach (var container in containers.EnumerateArray())
            {
                if (RestartCount(container) >= RestartThreshold)
                    return true;
                if (IsFailedRunningState(ReadString(container, "runningState")))
                    return true;
                if (DetailsIndicateCrashLoop(ReadString(container, "runningStateDetails")))
                    return true;
            }
        }

        return false;
    }

    private static bool RestartCountsIndicateCrashLoop(JsonElement source)
    {
        if (!source.TryGetProperty("restartCounts", out var counts) ||
            counts.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var count in counts.EnumerateArray())
        {
            if (count.ValueKind == JsonValueKind.Number &&
                count.TryGetInt32(out var value) &&
                value >= RestartThreshold)
                return true;
        }

        return false;
    }

    private static bool TryGetContainers(JsonElement source, out JsonElement containers)
    {
        containers = default;
        return source.TryGetProperty("containers", out containers) &&
               containers.ValueKind == JsonValueKind.Array;
    }

    private static int RestartCount(JsonElement container)
    {
        if (container.TryGetProperty("restartCount", out var count) &&
            count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt32(out var value))
            return value;
        if (container.TryGetProperty("container", out var nested) &&
            nested.ValueKind == JsonValueKind.Object)
            return RestartCount(nested);
        return 0;
    }

    private static bool IsFailedRunningState(string? state) =>
        state is not null &&
        (state.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
         state.Equals("CrashLoopBackOff", StringComparison.OrdinalIgnoreCase));

    private static bool DetailsIndicateCrashLoop(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
            return false;
        if (details.Contains("CrashLoopBackOff", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("OOMKilled", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("Back-off", StringComparison.OrdinalIgnoreCase))
            return true;

        var match = ExitCodePattern().Match(details);
        return match.Success &&
               int.TryParse(match.Groups[1].Value, out var exitCode) &&
               exitCode != 0;
    }

    private static string? ReadString(JsonElement source, string name) =>
        source.ValueKind == JsonValueKind.Object &&
        source.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"\bexit(?:ed|ing)?(?:\s+with)?(?:\s+code)?\s*[:=]?\s*'?(\d+)'?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitCodePattern();
}
