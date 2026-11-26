using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fresh ARM read after Destroy. Tests inject a handler; production uses
/// workload identity and the process subscription environment. Absent only
/// on ResourceGroupNotFound in that subscription.
/// </summary>
public sealed class AzureArmResourceGroupAbsenceProbe : IResourceGroupAbsenceProbe, IDisposable
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmApiVersion = "2021-04-01";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string?> _subscription;
    private readonly Func<CancellationToken, Task<string>> _token;

    public AzureArmResourceGroupAbsenceProbe()
        : this(
            new HttpClient(),
            ownsHttp: true,
            () => Environment.GetEnvironmentVariable("ARM_SUBSCRIPTION_ID")
                  ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID"),
            static async cancellationToken =>
            {
                var credential = new DefaultAzureCredential();
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken)
                    .ConfigureAwait(false);
                return token.Token;
            })
    {
    }

    public AzureArmResourceGroupAbsenceProbe(
        HttpMessageHandler handler,
        Func<string?> subscription,
        Func<CancellationToken, Task<string>> token)
        : this(new HttpClient(handler), ownsHttp: true, subscription, token)
    {
    }

    private AzureArmResourceGroupAbsenceProbe(
        HttpClient http,
        bool ownsHttp,
        Func<string?> subscription,
        Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _subscription = subscription;
        _token = token;
    }

    public async Task<ResourceGroupPresence> ObserveAsync(
        string resourceGroupName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);
        if (!IsResourceGroupName(resourceGroupName))
            throw new ArgumentException("Resource group name is invalid.", nameof(resourceGroupName));

        var subscription = _subscription();
        if (string.IsNullOrWhiteSpace(subscription) || !Guid.TryParse(subscription, out _))
            return ResourceGroupPresence.Unknown;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://management.azure.com/subscriptions/{subscription}/resourcegroups/{resourceGroupName}?api-version={ArmApiVersion}");
            var token = await _token(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return ResourceGroupPresence.Unknown;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return MapResponse(response.StatusCode, body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ResourceGroupPresence.Unknown;
        }
    }

    public static ResourceGroupPresence MapResponse(HttpStatusCode statusCode, string? body)
    {
        if ((int)statusCode is >= 200 and < 300)
            return ResourceGroupPresence.Present;
        if (statusCode == HttpStatusCode.NotFound && HasResourceGroupNotFound(body))
            return ResourceGroupPresence.Absent;
        return ResourceGroupPresence.Unknown;
    }

    public static ResourceGroupPresence MapStatus(HttpStatusCode statusCode) =>
        MapResponse(statusCode, statusCode == HttpStatusCode.NotFound
            ? """{"error":{"code":"ResourceGroupNotFound"}}"""
            : null);

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static bool HasResourceGroupNotFound(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error)
                || !error.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return string.Equals(code.GetString(), "ResourceGroupNotFound", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsResourceGroupName(string value) =>
        value.Length is >= 1 and <= 90
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '.' or '_' or '(' or ')' or '-');
}
