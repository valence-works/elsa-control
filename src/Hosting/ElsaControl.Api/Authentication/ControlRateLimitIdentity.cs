using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Rate-limit partition key for a signed-in principal: validated issuer plus subject.
/// Shared by the cloud-bootstrap limiter and available to later handoff limiting.
/// </summary>
internal static class ControlRateLimitIdentity
{
    public const char Separator = '\u001f';

    public static string? PartitionKey(
        ClaimsPrincipal? user,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount)
    {
        if (!TryReadIssuerAndSubject(user, controlIdentity, cloudAccount, hubAccount, out var issuer, out var subject))
            return null;

        return Format(issuer, subject);
    }

    public static string Format(string issuer, string subject) => string.Concat(issuer, Separator, subject);

    public static bool TryReadIssuerAndSubject(
        ClaimsPrincipal? user,
        ControlIdentityOptions controlIdentity,
        CloudAccountIdentityOptions cloudAccount,
        HubAccountIdentityOptions hubAccount,
        [NotNullWhen(true)] out string? issuer,
        [NotNullWhen(true)] out string? subject)
    {
        issuer = null;
        subject = null;
        if (user?.Identity is not { IsAuthenticated: true })
            return false;

        if (HubAccountTokenSelector.IsValidatedHubAccount(user, hubAccount))
        {
            issuer = user.FindFirst("iss")?.Value;
            subject = user.FindFirst("sub")?.Value;
            return !string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject);
        }

        if (CloudAccountTokenSelector.IsValidatedCloudAccount(user, cloudAccount))
        {
            issuer = user.FindFirst("iss")?.Value;
            subject = user.FindFirst("sub")?.Value;
            return !string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject);
        }

        issuer = ControlClaimsIdentityMapper.Issuer(user, controlIdentity);
        subject = ControlClaimsIdentityMapper.Subject(user, controlIdentity);
        return !string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject);
    }
}
