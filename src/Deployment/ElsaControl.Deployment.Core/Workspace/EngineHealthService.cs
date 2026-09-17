using ElsaControl.Deployment.Core.Cockpit;
using System.Net;
using System.Security.Authentication;

namespace ElsaControl.Deployment.Core.Workspace;

public sealed class EngineHealthService(
    IWorkspaceDeploymentStore store,
    IEngineHealthProbe probe,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<EngineHealthResult> VerifyEngineAsync(
        Guid workspaceId,
        EngineHealthVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = await store.GetEngineAsync(workspaceId, request.EngineId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow engine does not exist in the workspace.");

        var now = _timeProvider.GetUtcNow();
        var result = await probe.ProbeAsync(engine, cancellationToken);
        var health = Classify(result.Reachable, result.CertificateStatus, result.CredentialVerificationStatus);
        var credentialLastVerifiedAt = result.CredentialVerificationStatus == CredentialVerificationStatus.Verified
            ? (DateTimeOffset?)now
            : null;

        return await store.UpdateEngineHealthAsync(
            workspaceId,
            new EngineHealthUpdate(
                engine.Id,
                engine.EnvironmentId,
                health,
                string.IsNullOrWhiteSpace(result.Version) ? engine.Version : result.Version,
                result.CertificateStatus,
                result.CredentialVerificationStatus,
                credentialLastVerifiedAt,
                result.Reachable ? now : engine.LastHeartbeatAt,
                now,
                SafeMessage(result.Message)),
            cancellationToken);
    }

    public async Task<EngineHealthResult> ApplyHeartbeatAsync(
        Guid workspaceId,
        EngineHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        var engine = await store.GetEngineAsync(workspaceId, request.EngineId, cancellationToken)
            ?? throw new KeyNotFoundException("Workflow engine does not exist in the workspace.");
        if (engine.EnvironmentId != request.EnvironmentId)
            throw new InvalidOperationException("Heartbeat environment does not match the registered engine.");

        var health = Classify(true, request.CertificateStatus, request.CredentialVerificationStatus);
        var credentialLastVerifiedAt = request.CredentialVerificationStatus == CredentialVerificationStatus.Verified
            ? (DateTimeOffset?)request.HeartbeatAt
            : null;

        return await store.ApplyEngineHeartbeatAsync(
            workspaceId,
            new EngineHealthUpdate(
                engine.Id,
                engine.EnvironmentId,
                health,
                string.IsNullOrWhiteSpace(request.Version) ? engine.Version : request.Version,
                request.CertificateStatus,
                request.CredentialVerificationStatus,
                credentialLastVerifiedAt,
                request.HeartbeatAt,
                engine.LastVerificationAt,
                SafeMessage(request.Message ?? "Heartbeat accepted."),
                request.Capabilities),
            cancellationToken);
    }

    private static DeploymentHealth Classify(
        bool reachable,
        CertificateStatus certificateStatus,
        CredentialVerificationStatus credentialStatus)
    {
        if (!reachable)
            return DeploymentHealth.Unreachable;

        return certificateStatus == CertificateStatus.Trusted && credentialStatus == CredentialVerificationStatus.Verified
            ? DeploymentHealth.Healthy
            : DeploymentHealth.Degraded;
    }

    private static string SafeMessage(string message)
    {
        var safe = message.Trim();
        if (safe.Length == 0)
            return "No diagnostic message was provided.";
        return safe.Length <= 512 ? safe : safe[..512];
    }
}

public sealed class HttpEngineHealthProbe(HttpClient httpClient) : IEngineHealthProbe
{
    public async Task<EngineHealthProbeResult> ProbeAsync(
        WorkspaceWorkflowEngine engine,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(engine.BaseUrl, UriKind.Absolute, out var endpoint))
        {
            return new EngineHealthProbeResult(false, engine.Version, CertificateStatus.Untrusted, CredentialVerificationStatus.Unverified,
                "Endpoint address is invalid.");
        }

        if (!EngineEndpointHttpClientPolicy.TryValidateEndpoint(endpoint, out var endpointMessage))
        {
            return new EngineHealthProbeResult(false, engine.Version, CertificateStatus.Untrusted, CredentialVerificationStatus.Unverified,
                endpointMessage);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var version = GetSafeVersion(response, engine.Version);
            var certificateStatus = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? CertificateStatus.Trusted
                : CertificateStatus.Untrusted;

            return new EngineHealthProbeResult(true, version, certificateStatus, CredentialVerificationStatus.Unverified,
                DescribeResponse(response.StatusCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (IsEndpointAddressRejected(ex))
        {
            return new EngineHealthProbeResult(false, engine.Version, CertificateStatus.Untrusted, CredentialVerificationStatus.Unverified,
                "Endpoint address is not publicly routable.");
        }
        catch (HttpRequestException ex) when (IsTlsFailure(ex))
        {
            return new EngineHealthProbeResult(true, engine.Version, CertificateStatus.Untrusted, CredentialVerificationStatus.Unverified,
                "The endpoint accepted a connection, but TLS certificate validation failed.");
        }
        catch (OperationCanceledException)
        {
            return new EngineHealthProbeResult(false, engine.Version, CertificateStatus.Untrusted, CredentialVerificationStatus.Unverified,
                "Endpoint verification timed out.");
        }
        catch (HttpRequestException)
        {
            return new EngineHealthProbeResult(false, engine.Version, CertificateStatus.Untrusted, CredentialVerificationStatus.Unverified,
                "Endpoint did not respond to verification.");
        }
    }

    private static string GetSafeVersion(HttpResponseMessage response, string? fallback)
    {
        if (!response.Headers.TryGetValues("X-Elsa-Version", out var values))
            return fallback ?? string.Empty;

        var version = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128
            || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '+' or '_')))
            return fallback ?? string.Empty;

        return version;
    }

    private static string DescribeResponse(HttpStatusCode statusCode)
    {
        var status = (int)statusCode;
        var credentials = "credentials were not sent, so credential status is unverified.";
        return statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"Endpoint requires authentication (HTTP {status}); no credentials were sent.",
            HttpStatusCode.NotFound =>
                $"Endpoint path was not found (HTTP {status}); {credentials}",
            _ when status is >= 300 and < 400 =>
                $"Endpoint returned a redirect (HTTP {status}); redirects are disabled and {credentials}",
            _ when status >= 500 =>
                $"Endpoint returned a server error (HTTP {status}); {credentials}",
            _ when status is >= 200 and < 300 =>
                $"Endpoint responded with HTTP {status}; {credentials}",
            _ =>
                $"Endpoint responded with HTTP {status}; {credentials}"
        };
    }

    private static bool IsTlsFailure(HttpRequestException exception) =>
        exception.HttpRequestError == HttpRequestError.SecureConnectionError
        || HasInnerException<AuthenticationException>(exception);

    private static bool IsEndpointAddressRejected(HttpRequestException exception) =>
        HasInnerException<EngineEndpointHttpClientPolicy.EngineEndpointAddressRejectedException>(exception);

    private static bool HasInnerException<TException>(Exception exception)
        where TException : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
                return true;
        }

        return false;
    }
}
