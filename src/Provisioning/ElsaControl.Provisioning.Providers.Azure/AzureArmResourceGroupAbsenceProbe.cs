using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElsaControl.Provisioning.Providers.Azure;

/// <summary>
/// Fresh ARM read after Destroy. Tests inject a handler; production uses the
/// provisioner managed identity (the same identity Pulumi deploys with) and the
/// process subscription environment. Absent only on ResourceGroupNotFound in
/// that subscription. Anything else is Unknown and logged with its ARM status.
/// </summary>
public sealed class AzureArmResourceGroupAbsenceProbe : IResourceGroupAbsenceProbe, IDisposable
{
    private const string ArmScope = "https://management.azure.com/.default";
    private const string ArmApiVersion = "2021-04-01";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<string?> _subscription;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly ILogger _logger;

    public AzureArmResourceGroupAbsenceProbe()
        : this(credentialLocator: null)
    {
    }

    /// <summary>
    /// Production probe. <paramref name="credentialLocator"/> is the provisioner
    /// user-assigned identity client id. A bare DefaultAzureCredential resolves to
    /// the host's AZURE_CLIENT_ID (the API identity), which has no read on the
    /// workloads subscription, so every post-Destroy read would be Unknown.
    /// </summary>
    public AzureArmResourceGroupAbsenceProbe(string? credentialLocator, ILogger? logger = null)
        : this(
            new HttpClient(),
            ownsHttp: true,
            () => Environment.GetEnvironmentVariable("ARM_SUBSCRIPTION_ID")
                  ?? Environment.GetEnvironmentVariable("AZURE_SUBSCRIPTION_ID"),
            CreateTokenSource(CreateCredential(credentialLocator)),
            logger)
    {
    }

    public AzureArmResourceGroupAbsenceProbe(
        HttpMessageHandler handler,
        Func<string?> subscription,
        Func<CancellationToken, Task<string>> token,
        ILogger? logger = null)
        : this(new HttpClient(handler), ownsHttp: true, subscription, token, logger)
    {
    }

    private AzureArmResourceGroupAbsenceProbe(
        HttpClient http,
        bool ownsHttp,
        Func<string?> subscription,
        Func<CancellationToken, Task<string>> token,
        ILogger? logger)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _subscription = subscription;
        _token = token;
        _logger = logger ?? NullLogger.Instance;
    }

    internal static TokenCredential CreateCredential(string? credentialLocator) =>
        string.IsNullOrWhiteSpace(credentialLocator)
            ? new DefaultAzureCredential()
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(credentialLocator.Trim()));

    private static Func<CancellationToken, Task<string>> CreateTokenSource(TokenCredential credential) =>
        async cancellationToken =>
        {
            var token = await credential
                .GetTokenAsync(new TokenRequestContext([ArmScope]), cancellationToken)
                .ConfigureAwait(false);
            return token.Token;
        };

    public async Task<ResourceGroupPresence> ObserveAsync(
        string resourceGroupName,
        CancellationToken cancellationToken = default,
        string? subscriptionId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);
        if (!IsResourceGroupName(resourceGroupName))
            throw new ArgumentException("Resource group name is invalid.", nameof(resourceGroupName));

        var subscription = string.IsNullOrWhiteSpace(subscriptionId) ? _subscription() : subscriptionId;
        if (string.IsNullOrWhiteSpace(subscription) || !Guid.TryParse(subscription, out _))
        {
            _logger.LogWarning(
                "Resource group presence unknown. ResourceGroup={ResourceGroup} Reason={Reason}",
                resourceGroupName,
                "subscription-missing");
            return ResourceGroupPresence.Unknown;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://management.azure.com/subscriptions/{subscription}/resourcegroups/{resourceGroupName}?api-version={ArmApiVersion}");
            var token = await _token(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning(
                    "Resource group presence unknown. ResourceGroup={ResourceGroup} Reason={Reason}",
                    resourceGroupName,
                    "token-empty");
                return ResourceGroupPresence.Unknown;
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var presence = MapResponse(response.StatusCode, body);
            if (presence == ResourceGroupPresence.Unknown)
            {
                _logger.LogWarning(
                    "Resource group presence unknown. ResourceGroup={ResourceGroup} Reason={Reason} StatusCode={StatusCode} ArmErrorCode={ArmErrorCode}",
                    resourceGroupName,
                    "arm-response",
                    (int)response.StatusCode,
                    ReadErrorCode(body) ?? "(none)");
            }
            return presence;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Resource group presence unknown. ResourceGroup={ResourceGroup} Reason={Reason} ExceptionType={ExceptionType}",
                resourceGroupName,
                "probe-exception",
                exception.GetType().FullName);
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

    private static bool HasResourceGroupNotFound(string? body) =>
        string.Equals(ReadErrorCode(body), "ResourceGroupNotFound", StringComparison.Ordinal);

    /// <summary>ARM error code only (bounded, value-free); never the message.</summary>
    internal static string? ReadErrorCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("code", out var code)
                || code.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = code.GetString();
            return value is { Length: > 0 and <= 128 } && value.All(char.IsAsciiLetterOrDigit) ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsResourceGroupName(string value) =>
        value.Length is >= 1 and <= 90
        && value.All(character => char.IsAsciiLetterOrDigit(character)
                                  || character is '.' or '_' or '(' or ')' or '-');
}
