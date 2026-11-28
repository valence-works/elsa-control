using System.Reflection;
using System.Text.RegularExpressions;

namespace ElsaControl.Provisioning.Core;

/// <summary>
/// Operator-facing exception text for App Insights. Customer events stay
/// value-free; this redacts secrets in place so the Pulumi stderr tail is
/// still searchable.
/// </summary>
public static partial class ProvisioningExceptionLog
{
    public const int StderrTailLength = 2048;

    public static string Message(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Redact(exception.Message);
    }

    public static string? StderrTail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var stderr = TryReadStandardError(current);
            if (string.IsNullOrWhiteSpace(stderr)
                && current.GetType().Name.Contains("CommandException", StringComparison.Ordinal))
            {
                stderr = current.Message;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
                return Redact(Tail(stderr));
        }

        return null;
    }

    public static string Redact(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;
        var redacted = UrlCredential().Replace(value, "://***@");
        redacted = SignatureQuery().Replace(redacted, "$1sig=redacted");
        redacted = SecretAssignment().Replace(redacted, "$1=redacted");
        return redacted;
    }

    private static string Tail(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= StderrTailLength
            ? trimmed
            : trimmed[^StderrTailLength..];
    }

    private static string? TryReadStandardError(Exception exception)
    {
        var property = exception.GetType().GetProperty("StandardError", BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(exception) as string;
    }

    [GeneratedRegex(@"://[^/\s?#]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredential();

    [GeneratedRegex(@"([?&])sig=[^&\s]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SignatureQuery();

    [GeneratedRegex(@"(password|accountkey|sharedaccesssignature|connectionstring)\s*=\s*\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAssignment();
}
