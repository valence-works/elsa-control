using System.Net;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Operator check for <see cref="ControlRateLimitBffEgressOptions.ConfigurationKey"/> and
/// <see cref="ControlRateLimitBffCredentialOptions.ConfigurationSection"/>: for a validated
/// Cloud/Hub session, logs the resolved <c>Connection.RemoteIpAddress</c>, whether it matched the
/// BFF egress networks, the BFF credential source the header matched (<c>cloud</c>, <c>hub</c> or
/// <c>none</c>), and the policy name. Off at the default log level; enable it briefly with
/// <c>Logging__LogLevel__ElsaControl.Api.RateLimiting=Debug</c>. It never logs a token, cookie,
/// identity value, header or credential value.
/// </summary>
internal sealed class ControlRateLimitBffSourceDiagnostic(ILogger logger, string policyName)
{
    public const string LoggerCategory = "ElsaControl.Api.RateLimiting";

    public const string NoCredentialSource = "none";

    public void Record(IPAddress? remoteIpAddress, bool matched, string? credentialSource)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        logger.LogDebug(
            "Rate limit policy {PolicyName} saw a validated Cloud/Hub session from RemoteIpAddress {RemoteIpAddress}; BffEgressNetworks matched={Matched}; BffCredential source={BffCredentialSource}.",
            policyName,
            remoteIpAddress?.ToString() ?? "unknown",
            matched,
            credentialSource ?? NoCredentialSource);
    }
}
