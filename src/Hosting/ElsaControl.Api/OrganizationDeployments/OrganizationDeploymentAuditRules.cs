using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace ElsaControl.Api.OrganizationDeployments;

public static class OrganizationDeploymentAuditRules
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;
    public const int MaxApprovedScopeLabels = 8;

    public const string ProductionEnvironment = "production";
    public const string StagingEnvironment = "staging";
    public const string PreviewEnvironment = "preview";
    public const string SucceededOutcome = "succeeded";
    public const string FailedOutcome = "failed";

    private static readonly Regex FunctionNamePattern = new(
        @"^[a-z0-9][a-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SourceRevisionPattern = new(
        @"^[a-f0-9]{7,64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ApprovedScopePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9 ._:/-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OpaqueIdPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._:-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> Environments = new(StringComparer.Ordinal)
    {
        ProductionEnvironment,
        StagingEnvironment,
        PreviewEnvironment
    };

    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal)
    {
        SucceededOutcome,
        FailedOutcome
    };

    private static readonly string[] IdentifyingPrefixes =
    [
        "cus_", "sub_", "in_", "cs_", "pi_", "price_", "si_", "seti_", "sess_",
        "sk_", "rk_", "pk_", "user-", "acct_", "email-"
    ];

    public static bool TryNormalizePage(int? page, int? pageSize, out int normalizedPage, out int normalizedPageSize)
    {
        normalizedPage = page ?? DefaultPage;
        normalizedPageSize = pageSize ?? DefaultPageSize;
        return normalizedPage >= 1 && normalizedPageSize >= 1 && normalizedPageSize <= MaxPageSize;
    }

    public static bool TryProject(
        OrganizationDeploymentAuditRecord record,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OrganizationDeploymentAuditItemResponse? item)
    {
        item = null;
        if (!IsSafeOpaqueId(record.Id) ||
            !IsSafeFunctionName(record.FunctionName) ||
            !IsSafeSourceRevision(record.SourceRevision) ||
            !Environments.Contains(record.TargetEnvironment) ||
            !IsUtcTimestamp(record.OccurredAt) ||
            !IsSafeApprovedScope(record.ApprovedScope) ||
            !Outcomes.Contains(record.Outcome))
        {
            return false;
        }

        item = new OrganizationDeploymentAuditItemResponse(
            record.Id,
            record.FunctionName,
            record.SourceRevision,
            record.TargetEnvironment,
            FormatUtc(record.OccurredAt),
            [.. record.ApprovedScope],
            record.Outcome);
        return true;
    }

    public static bool IsSafeFunctionName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        FunctionNamePattern.IsMatch(value) &&
        !ContainsLeak(value);

    public static bool IsSafeSourceRevision(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        SourceRevisionPattern.IsMatch(value);

    public static bool IsSafeApprovedScope(IReadOnlyList<string>? labels)
    {
        if (labels is null || labels.Count > MaxApprovedScopeLabels)
            return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label) ||
                !ApprovedScopePattern.IsMatch(label) ||
                ContainsLeak(label) ||
                !seen.Add(label))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsSafeOpaqueId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        OpaqueIdPattern.IsMatch(value) &&
        !ContainsLeak(value);

    public static bool IsUtcTimestamp(DateTimeOffset value) =>
        value != default && value.Offset == TimeSpan.Zero;

    public static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static bool ContainsLeak(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        if (value.Contains('@', StringComparison.Ordinal) ||
            value.Contains("://", StringComparison.Ordinal) ||
            value.Contains("user:", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("connectionstring", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var lower = value.ToLowerInvariant();
        if (IdentifyingPrefixes.Any(prefix =>
                lower.StartsWith(prefix, StringComparison.Ordinal) ||
                lower.Contains('_' + prefix, StringComparison.Ordinal) ||
                lower.Contains('-' + prefix, StringComparison.Ordinal)))
        {
            return true;
        }

        return LooksLikeIpAddress(value);
    }

    private static bool LooksLikeIpAddress(string value) =>
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
}
