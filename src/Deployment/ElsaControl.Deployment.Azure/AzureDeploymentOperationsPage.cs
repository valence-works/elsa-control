using System.Text.Json;

namespace ElsaControl.Deployment.Azure;

/// <summary>One explicit ARM page; no SDK/CLI list iterator may hide continuation requests.</summary>
internal sealed class AzureDeploymentOperationsPage(JsonElement failedOperations, string? nextLink) : AzureCommandSafeOutput
{
    internal JsonElement FailedOperations { get; } = failedOperations;
    internal string? NextLink { get; } = nextLink;

    internal const int MaximumOperationsPerPage = 64;
    private const string ApiVersion = "2025-04-01";
    private const int MaximumContinuationLength = 4096;

    internal static string FirstUrl(string subscription, string group, string deployment) =>
        $"https://management.azure.com/subscriptions/{Uri.EscapeDataString(subscription)}/resourcegroups/{Uri.EscapeDataString(group)}/deployments/{Uri.EscapeDataString(deployment)}/operations?api-version={ApiVersion}&$top={MaximumOperationsPerPage}";

    internal static AzureDeploymentOperationsPage Parse(ReadOnlyMemory<char> output)
    {
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("value", out var operations) || operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() > MaximumOperationsPerPage)
            throw new FormatException();

        var failed = new List<JsonElement>();
        foreach (var operation in operations.EnumerateArray())
        {
            if (operation.ValueKind != JsonValueKind.Object ||
                !operation.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
                !properties.TryGetProperty("provisioningState", out var state) || state.ValueKind != JsonValueKind.String)
                throw new FormatException();
            if (string.Equals(state.GetString(), "Failed", StringComparison.OrdinalIgnoreCase))
                failed.Add(operation);
            else if (!string.Equals(state.GetString(), "Succeeded", StringComparison.OrdinalIgnoreCase))
                throw new FormatException(); // An unfinished or unknown operation is not complete retry evidence.
        }

        string? next = null;
        if (root.TryGetProperty("nextLink", out var continuation) && continuation.ValueKind != JsonValueKind.Null)
        {
            if (continuation.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(continuation.GetString()))
                throw new FormatException();
            next = continuation.GetString();
        }
        return new(JsonSerializer.SerializeToElement(failed), next);
    }

    internal static bool IsSafeContinuation(string next, string firstUrl)
    {
        // Compare the original escaped path too: URI normalization must not admit
        // dot segments, encoded separators, userinfo, alternate ports, or another scope.
        var expected = new Uri(firstUrl);
        if (next.Length > MaximumContinuationLength || next.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !next.StartsWith(expected.GetLeftPart(UriPartial.Path) + "?", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(next, UriKind.Absolute, out var uri) || uri.Fragment.Length != 0)
            return false;

        for (var index = 0; index < next.Length; index++)
            if (next[index] == '%' && (index + 2 >= next.Length || !Uri.IsHexDigit(next[index + 1]) || !Uri.IsHexDigit(next[index + 2])))
                return false;

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = parameter.Split('=', 2);
            if (pair.Length != 2)
                return false;
            var key = Uri.UnescapeDataString(pair[0]);
            var value = Uri.UnescapeDataString(pair[1]);
            if (!keys.Add(key) || value.Length == 0)
                return false;
            if (key.Equals("api-version", StringComparison.OrdinalIgnoreCase))
            {
                if (value != ApiVersion)
                    return false;
            }
            else if (key.Equals("$top", StringComparison.OrdinalIgnoreCase))
            {
                if (value != MaximumOperationsPerPage.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    return false;
            }
            else if (!key.Equals("$skiptoken", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return keys.Contains("api-version") && keys.Contains("$skiptoken");
    }
}
