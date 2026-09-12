using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using ElsaControl.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Tests;

internal static class TestWorkspaceIdentity
{
    public static HttpClient CreateControlIdentityClient(
        this ControlApiTestApplication app,
        string subject = "user-123",
        string? issuer = ControlApiTestApplication.TestControlIdentityIssuer,
        string? audience = ControlApiTestApplication.TestControlIdentityAudience,
        DateTimeOffset? expires = null,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(subject, issuer, audience, expires, claims));
        return client;
    }

    /// <summary>Adds a protected customer session cookie, as issued after OIDC sign-in.</summary>
    public static void AddControlSessionCookie(this ControlApiTestApplication app, HttpClient client, params Claim[] additionalClaims)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CustomerAuthenticationDefaults.CookieScheme);
        var claims = new List<Claim>
        {
            new("sub", "admin-user"),
            new("iss", ControlApiTestApplication.TestControlIdentityIssuer),
            new("name", "Admin User")
        };
        claims.AddRange(additionalClaims);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CustomerAuthenticationDefaults.CookieScheme));
        var ticket = new AuthenticationTicket(principal, CustomerAuthenticationDefaults.CookieScheme);
        var cookie = options.TicketDataFormat.Protect(ticket);
        client.DefaultRequestHeaders.Add("Cookie", $"{CustomerAuthenticationDefaults.CookieName}={cookie}");
    }

    public static string CreateToken(
        string subject,
        string? issuer = ControlApiTestApplication.TestControlIdentityIssuer,
        string? audience = ControlApiTestApplication.TestControlIdentityAudience,
        DateTimeOffset? expires = null,
        IReadOnlyDictionary<string, string>? claims = null)
    {
        var now = DateTimeOffset.UtcNow;
        var tokenClaims = new List<Claim>();
        if (!string.IsNullOrWhiteSpace(subject))
            tokenClaims.Add(new Claim(JwtRegisteredClaimNames.Sub, subject));

        foreach (var claim in claims ?? new Dictionary<string, string>
                 {
                     ["name"] = "Ada Lovelace",
                     ["email"] = "ada@example.test"
                 })
        {
            tokenClaims.Add(new Claim(claim.Key, claim.Value));
        }

        var expiresAt = expires ?? now.AddMinutes(15);
        var notBefore = expiresAt <= now ? expiresAt.AddMinutes(-5) : now.AddMinutes(-1);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(tokenClaims),
            NotBefore = notBefore.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ControlApiTestApplication.TestControlIdentitySigningKey)),
                SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}
