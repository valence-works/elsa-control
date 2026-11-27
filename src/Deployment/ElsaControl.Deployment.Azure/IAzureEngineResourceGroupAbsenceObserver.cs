using System.Net;
using System.Text.Json;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Read-only ARM presence check of one engine resource group. Implementations must
/// not create, delete, or otherwise mutate Azure resources. Absent is only a 404
/// whose error code is ResourceGroupNotFound.
/// </summary>
public interface IAzureEngineResourceGroupAbsenceObserver
{
    Task<AzureProviderFreshResourceGroupState> ObserveAsync(
        string subscriptionId,
        string resourceGroupName,
        CancellationToken cancellationToken = default);
}

public static class AzureArmResourceGroupPresence
{
    public static AzureProviderFreshResourceGroupState MapResponse(HttpStatusCode statusCode, string? body)
    {
        if ((int)statusCode is >= 200 and < 300)
            return AzureProviderFreshResourceGroupState.Present;
        if (statusCode == HttpStatusCode.NotFound && HasResourceGroupNotFound(body))
            return AzureProviderFreshResourceGroupState.Absent;
        return AzureProviderFreshResourceGroupState.Unknown;
    }

    private static bool HasResourceGroupNotFound(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                !error.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.String)
                return false;

            return string.Equals(code.GetString(), "ResourceGroupNotFound", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
