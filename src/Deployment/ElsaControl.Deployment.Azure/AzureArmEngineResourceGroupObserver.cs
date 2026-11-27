using System.Net.Http.Headers;
using Azure.Core;

namespace ElsaControl.Deployment.Azure;

/// <summary>
/// Fresh ARM GET of one engine resource group. Tests inject a handler; production
/// uses the worker's managed identity. The observer never logs tokens or bodies.
/// </summary>
public sealed class AzureArmEngineResourceGroupObserver : IAzureEngineResourceGroupAbsenceObserver, IDisposable
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmApiVersion = "2021-04-01";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly TokenCredential _credential;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public AzureArmEngineResourceGroupObserver(TokenCredential credential)
        : this(credential, CreateHttpClient(), ownsHttp: true)
    {
    }

    public AzureArmEngineResourceGroupObserver(TokenCredential credential, HttpMessageHandler handler)
        : this(credential, CreateHttpClient(handler), ownsHttp: true)
    {
    }

    private AzureArmEngineResourceGroupObserver(TokenCredential credential, HttpClient http, bool ownsHttp)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _http = http;
        _ownsHttp = ownsHttp;
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = RequestTimeout;
        return http;
    }

    public async Task<AzureProviderFreshResourceGroupState> ObserveAsync(
        string subscriptionId,
        string resourceGroupName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(subscriptionId, "D", out _) ||
            string.IsNullOrWhiteSpace(resourceGroupName) ||
            !IsResourceGroupName(resourceGroupName))
            return AzureProviderFreshResourceGroupState.Unknown;

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://management.azure.com/subscriptions/{subscriptionId}/resourcegroups/{Uri.EscapeDataString(resourceGroupName)}?api-version={ArmApiVersion}");
            var token = await _credential
                .GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.Token))
                return AzureProviderFreshResourceGroupState.Unknown;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return AzureArmResourceGroupPresence.MapResponse(response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AzureProviderFreshResourceGroupState.Unknown;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private static bool IsResourceGroupName(string value) =>
        value.Length is >= 1 and <= 90
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '.' or '_' or '(' or ')' or '-');
}
