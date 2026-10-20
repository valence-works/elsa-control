using System.Net;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Operator check for <see cref="ControlRateLimitBffEgressOptions.ConfigurationKey"/>: for a
/// validated Cloud/Hub session, logs the resolved <c>Connection.RemoteIpAddress</c>, whether it
/// matched the BFF egress networks, and the policy name. Off at the default log level; enable it
/// briefly with the app setting <c>RateLimiting__SourceDiagnosticLogLevel=Debug</c> (see
/// <see cref="LogLevelConfigurationKey"/>). It never logs a token, cookie, identity value or header.
/// </summary>
internal sealed class ControlRateLimitBffSourceDiagnostic(ILogger logger, string policyName)
{
    public const string LoggerCategory = "ElsaControl.Api.RateLimiting";

    /// <summary>
    /// Dot-free switch for the <see cref="LoggerCategory"/> level. App Service on Linux turns periods
    /// in app setting names into underscores, so <c>Logging__LogLevel__ElsaControl.Api.RateLimiting</c>
    /// never reaches the container; this key does. Unset leaves the category at the configured level.
    /// </summary>
    public const string LogLevelConfigurationKey = "RateLimiting:SourceDiagnosticLogLevel";

    /// <summary>
    /// Reads <see cref="LogLevelConfigurationKey"/>: <c>null</c> when unset or blank, otherwise a
    /// <see cref="LogLevel"/> name. Any other value refuses startup and names the key.
    /// </summary>
    public static LogLevel? ReadLogLevel(IConfiguration configuration)
    {
        var value = configuration[LogLevelConfigurationKey];
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (!char.IsDigit(trimmed[0]) && !trimmed.StartsWith('-') &&
            Enum.TryParse<LogLevel>(trimmed, ignoreCase: true, out var level) && Enum.IsDefined(level))
            return level;

        throw new InvalidOperationException(
            $"{LogLevelConfigurationKey} must be one of {string.Join(", ", Enum.GetNames<LogLevel>())}; got '{trimmed}'.");
    }

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
