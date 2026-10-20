using System.Net;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Operator check for <see cref="ControlRateLimitBffEgressOptions.ConfigurationKey"/>: for a
/// validated Cloud/Hub session, logs the resolved <c>Connection.RemoteIpAddress</c>, whether it
/// matched the BFF egress networks, and the policy name. Off at the default log level; enable it
/// briefly with <c>Logging__LogLevel__ElsaControl.Api.RateLimiting=Debug</c>. It never logs a
/// token, cookie, identity value or header.
/// </summary>
internal sealed class ControlRateLimitBffSourceDiagnostic(ILogger logger, string policyName)
{
    public const string LoggerCategory = "ElsaControl.Api.RateLimiting";

    public void Record(IPAddress? remoteIpAddress, bool matched)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        logger.LogDebug(
            "Rate limit policy {PolicyName} saw a validated Cloud/Hub session from RemoteIpAddress {RemoteIpAddress}; BffEgressNetworks matched={Matched}.",
            policyName,
            remoteIpAddress?.ToString() ?? "unknown",
            matched);
    }
}
