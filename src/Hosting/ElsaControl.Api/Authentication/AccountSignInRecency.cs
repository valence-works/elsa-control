using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Reads when an account session last signed in interactively, from the <c>amr</c> claim of its validated
/// Supabase access token. <c>iat</c> cannot answer this because every token refresh re-issues it.
/// </summary>
public static class AccountSignInRecency
{
    // Same allowance as the account JWT validators' ClockSkew.
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private const string ValidatedTokenParameter = "ElsaControl.ValidatedAccountToken";
    private static readonly HashSet<string> NonSignInMethods = new(StringComparer.Ordinal) { "token_refresh", "anonymous" };

    /// <summary>Keeps the validated token on the authentication result so its payload can be read later.</summary>
    public static void KeepValidatedToken(AuthenticationProperties properties, SecurityToken? token) =>
        properties.SetParameter(ValidatedTokenParameter, token);

    public static DateTimeOffset? ReadLastSignIn(AuthenticationProperties? properties) =>
        LastSignIn(properties?.GetParameter<SecurityToken>(ValidatedTokenParameter));

    /// <summary>
    /// The largest <c>amr</c> timestamp whose method is not <c>token_refresh</c> or <c>anonymous</c>, or null
    /// when <c>amr</c> is missing, is not an array, has a malformed entry, or has no qualifying entry.
    /// </summary>
    public static DateTimeOffset? LastSignIn(SecurityToken? token)
    {
        var encodedPayload = token switch
        {
            JsonWebToken jsonWebToken => jsonWebToken.EncodedPayload,
            JwtSecurityToken jwtSecurityToken => jwtSecurityToken.RawPayload,
            _ => null
        };
        if (string.IsNullOrEmpty(encodedPayload))
            return null;

        try
        {
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(encodedPayload));
            if (payload.RootElement.ValueKind != JsonValueKind.Object ||
                !payload.RootElement.TryGetProperty("amr", out var amr) ||
                amr.ValueKind != JsonValueKind.Array)
                return null;

            DateTimeOffset? lastSignIn = null;
            foreach (var entry in amr.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String ||
                    !entry.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.Number ||
                    !timestamp.TryGetInt64(out var seconds))
                    return null;

                if (NonSignInMethods.Contains(method.GetString()!))
                    continue;

                var signedInAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (lastSignIn is null || signedInAt > lastSignIn)
                    lastSignIn = signedInAt;
            }

            return lastSignIn;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static bool IsRecent(DateTimeOffset? lastSignIn, DateTimeOffset now, TimeSpan window) =>
        lastSignIn is { } signedInAt &&
        signedInAt <= now + ClockSkew &&
        now - signedInAt <= window;
}
