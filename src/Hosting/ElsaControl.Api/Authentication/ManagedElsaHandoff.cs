using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ElsaControl.Api.Authentication;

public static class ManagedElsaHandoffDefaults
{
    public const string ConfigurationSection = "ManagedElsa:Handoff";
    public const string RuntimeSessionScope = "runtime:session";
    public const string TokenType = "elsa-handoff+jwt";
    // This is deliberately distinct from the JWT `exp`, which expires the
    // one-time handoff code. It is the upper bound the runtime must apply to
    // its own local session.
    public const string SessionExpiryClaim = "session_exp";
    public const string TestingEnvironmentName = "Testing";
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultRuntimeSessionMaximumLifetime = TimeSpan.FromHours(8);
    public static readonly TimeSpan MaximumRuntimeSessionLifetime = TimeSpan.FromHours(8);

    /// <summary>
    /// Console page a managed runtime returns the browser to with <c>instanceId</c>, <c>state</c> and
    /// <c>codeChallenge</c> (or <c>handoff_status</c>); it issues the code and posts it to the runtime callback.
    /// </summary>
    public const string ConsoleContinuationPath = "/admin/runtimes";

    /// <summary>
    /// Runtime permissions a Control operator holds in a handed-off managed runtime session. The product defines
    /// no operator-to-runtime role mapping yet, so this is the only documented grant: the runtime image's handoff
    /// configuration example, equal to its bootstrap administrator role. It is administrator-equivalent inside the
    /// instance's dedicated runtime; narrow it here once a mapping is defined.
    /// </summary>
    public static readonly IReadOnlyList<string> RuntimeOperatorPermissions = ["*"];
}

public sealed class ManagedElsaHandoffOptions
{
    public bool Enabled { get; init; }
    public string Issuer { get; init; } = "https://cloud.elsaworkflows.io";
    public TimeSpan TokenLifetime { get; init; } = ManagedElsaHandoffDefaults.DefaultLifetime;
    public TimeSpan RuntimeSessionMaximumLifetime { get; init; } =
        ManagedElsaHandoffDefaults.DefaultRuntimeSessionMaximumLifetime;
    public string? ActiveKeyId { get; init; }
    public string? ActivePrivateKeyPem { get; init; }
    public Dictionary<string, string> PreviousPublicKeys { get; init; } = new(StringComparer.Ordinal);
}

public sealed class ManagedElsaHandoffConfigurationValidator(
    IHostEnvironment environment,
    IOptions<ManagedElsaHandoffOptions> options,
    IServiceProvider services) : IHostedService
{
    public ManagedElsaHandoffConfigurationValidator(
        IHostEnvironment environment,
        IOptions<ManagedElsaHandoffOptions> options)
        : this(environment, options, EmptyServiceProvider.Instance)
    {
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var validated = ManagedElsaHandoffIssuer.ValidateOptions(options.Value);
        if (validated.Enabled)
        {
            var localEnvironment = environment.IsDevelopment() || environment.IsEnvironment(ManagedElsaHandoffDefaults.TestingEnvironmentName);
            var hasAnyConfiguredKey = !string.IsNullOrWhiteSpace(validated.ActiveKeyId) ||
                                      !string.IsNullOrWhiteSpace(validated.ActivePrivateKeyPem);
            var hasConfiguredActiveKey = !string.IsNullOrWhiteSpace(validated.ActiveKeyId) &&
                                         !string.IsNullOrWhiteSpace(validated.ActivePrivateKeyPem);
            if (!localEnvironment && !hasConfiguredActiveKey)
                throw new InvalidOperationException(
                    "Managed Elsa handoff requires a configured active signing key outside local environments.");
            if (hasAnyConfiguredKey || !localEnvironment)
                _ = services.GetRequiredService<ManagedElsaHandoffKeyRing>().ActiveKeyId;
            if (!localEnvironment)
            {
                await using var scope = services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                if (!await database.Database.CanConnectAsync(cancellationToken))
                    throw new InvalidOperationException(
                        "Managed Elsa handoff requires reachable durable persistence outside local environments.");
                _ = await database.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM ManagedElsaHandoffReplayConsumptions WHERE 1 = 0", cancellationToken);
                _ = await database.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM ManagedElsaHandoffAuditEvents WHERE 1 = 0", cancellationToken);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>
/// The authorization result is deliberately supplied by the Elsa Instance boundary.
/// It must be derived from current Control identity and current organization/instance
/// membership; it must not be populated from browser-provided account or role claims.
/// </summary>
public sealed record ManagedElsaHandoffAuthorization(
    Guid AccountId,
    Guid OrganizationId,
    Guid InstanceId,
    string Audience,
    Uri RedirectUri,
    string CodeChallenge,
    IReadOnlySet<string> Scopes,
    int BindingVersion)
{
    public ManagedElsaHandoffAuthorization(
        Guid accountId,
        Guid organizationId,
        Guid instanceId,
        string audience,
        Uri redirectUri,
        string codeChallenge,
        IReadOnlySet<string> scopes)
        : this(accountId, organizationId, instanceId, audience, redirectUri, codeChallenge, scopes, 1)
    {
    }
}

public sealed record ManagedElsaHandoffRequest(
    Guid OrganizationId,
    Guid InstanceId,
    string Audience,
    Uri RedirectUri,
    string CodeChallenge,
    IReadOnlySet<string>? Scopes = null)
{
    public IReadOnlySet<string> RequestedScopes => Scopes is { Count: > 0 }
        ? Scopes
        : new HashSet<string>([ManagedElsaHandoffDefaults.RuntimeSessionScope], StringComparer.Ordinal);
}

public interface IManagedElsaHandoffAuthorizer
{
    ValueTask<ManagedElsaHandoffAuthorization?> AuthorizeAsync(
        TrustedWorkspaceIdentity identity,
        ManagedElsaHandoffRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<bool> IsStillAuthorizedAsync(
        ManagedElsaHandoffClaims claims,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The production authorizer is intentionally not guessed before the Elsa Instance
/// aggregate exists. Register an instance-aware implementation before enabling the
/// endpoint; the default keeps the endpoint fail-closed.
/// </summary>
public sealed class UnconfiguredManagedElsaHandoffAuthorizer : IManagedElsaHandoffAuthorizer
{
    public ValueTask<ManagedElsaHandoffAuthorization?> AuthorizeAsync(
        TrustedWorkspaceIdentity identity,
        ManagedElsaHandoffRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<ManagedElsaHandoffAuthorization?>(null);

    public ValueTask<bool> IsStillAuthorizedAsync(
        ManagedElsaHandoffClaims claims,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}

public sealed class ManagedElsaHandoffKeyRing : IDisposable
{
    private readonly IReadOnlyDictionary<string, RsaSecurityKey> _keys;

    public ManagedElsaHandoffKeyRing(string activeKeyId, RSA activeKey, IEnumerable<(string KeyId, RSA Key)>? validationKeys = null)
    {
        if (string.IsNullOrWhiteSpace(activeKeyId))
            throw new ArgumentException("An active key id is required.", nameof(activeKeyId));

        ArgumentNullException.ThrowIfNull(activeKey);
        var keys = (validationKeys ?? []).ToList();
        if (keys.Any(x => string.Equals(x.KeyId, activeKeyId, StringComparison.Ordinal)))
            throw new ArgumentException("Validation keys must not duplicate the active key id.", nameof(validationKeys));

        keys.Add((activeKeyId, activeKey));

        _keys = keys
            .Select(x => new RsaSecurityKey(x.Key) { KeyId = x.KeyId })
            .ToDictionary(x => x.KeyId!, StringComparer.Ordinal);
        ActiveKeyId = activeKeyId;
    }

    public string ActiveKeyId { get; }

    public SigningCredentials ActiveSigningCredentials =>
        new(_keys[ActiveKeyId], SecurityAlgorithms.RsaSha256);

    public IReadOnlyCollection<SecurityKey> ValidationKeys => _keys.Values.Cast<SecurityKey>().ToArray();

    public bool ContainsKey(string keyId) => _keys.ContainsKey(keyId);

    public static ManagedElsaHandoffKeyRing CreateEphemeral() =>
        new("prototype", RSA.Create(2048));

    public static ManagedElsaHandoffKeyRing CreateConfigured(ManagedElsaHandoffOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ActiveKeyId) || string.IsNullOrWhiteSpace(options.ActivePrivateKeyPem))
            throw new InvalidOperationException("Managed Elsa handoff active signing-key configuration is incomplete.");

        var active = RSA.Create();
        var previous = new List<(string KeyId, RSA Key)>();
        try
        {
            active.ImportFromPem(options.ActivePrivateKeyPem);
            _ = active.ExportParameters(includePrivateParameters: true);
            if (active.KeySize < 2048)
                throw new InvalidOperationException("Managed Elsa handoff signing keys must be at least 2048 bits.");

            foreach (var configured in options.PreviousPublicKeys)
            {
                var key = RSA.Create();
                try
                {
                    key.ImportFromPem(configured.Value);
                    if (key.KeySize < 2048)
                        throw new InvalidOperationException("Managed Elsa handoff validation keys must be at least 2048 bits.");
                    previous.Add((configured.Key, key));
                }
                catch
                {
                    key.Dispose();
                    throw;
                }
            }

            return new ManagedElsaHandoffKeyRing(options.ActiveKeyId, active, previous);
        }
        catch
        {
            active.Dispose();
            foreach (var (_, key) in previous)
                key.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values)
            key.Rsa?.Dispose();
    }
}

public sealed record ManagedElsaHandoffIssueResult(
    string Token,
    string TokenType,
    string KeyId,
    string Audience,
    Uri RedirectUri,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Jti,
    DateTimeOffset SessionExpiresAt);

public sealed record ManagedElsaHandoffClaims(
    string Jti,
    Guid AccountId,
    string ControlIssuer,
    string ControlSubject,
    Guid OrganizationId,
    Guid InstanceId,
    string Audience,
    Uri RedirectUri,
    string CodeChallenge,
    IReadOnlySet<string> Scopes,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    int BindingVersion,
    DateTimeOffset SessionExpiresAt)
{
    public TrustedWorkspaceIdentity ToTrustedWorkspaceIdentity() =>
        new(ControlIssuer, ControlSubject, null, null);
}

public enum ManagedElsaHandoffRedeemFailure
{
    InvalidToken,
    Replay,
    AuthorizationRevoked
}

public sealed record ManagedElsaHandoffRedeemResult(
    ManagedElsaHandoffClaims? Claims,
    ManagedElsaHandoffRedeemFailure? Failure)
{
    public bool Succeeded => Claims is not null && Failure is null;

    public static ManagedElsaHandoffRedeemResult Success(ManagedElsaHandoffClaims claims) => new(claims, null);

    public static ManagedElsaHandoffRedeemResult Denied(ManagedElsaHandoffRedeemFailure failure) => new(null, failure);
}

public interface IManagedElsaHandoffReplayStore
{
    ValueTask<bool> TryConsumeAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
}

public sealed class InMemoryManagedElsaHandoffReplayStore(TimeProvider timeProvider) : IManagedElsaHandoffReplayStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);

    public ValueTask<bool> TryConsumeAsync(
        string jti,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _consumed)
        {
            if (item.Value <= now)
                _consumed.TryRemove(item.Key, out _);
        }

        return ValueTask.FromResult(_consumed.TryAdd(jti, expiresAt));
    }
}

public sealed record ManagedElsaHandoffAuditEvent(
    string Action,
    string Jti,
    Guid? AccountId,
    Guid? OrganizationId,
    Guid? InstanceId,
    string? Audience,
    DateTimeOffset OccurredAt,
    int? BindingVersion = null,
    string? CorrelationId = null)
{
    public ManagedElsaHandoffAuditEvent(
        string action,
        string jti,
        Guid? accountId,
        Guid? organizationId,
        Guid? instanceId,
        string? audience,
        DateTimeOffset occurredAt)
        : this(action, jti, accountId, organizationId, instanceId, audience, occurredAt, null, null)
    {
    }
}

public interface IManagedElsaHandoffAuditSink
{
    ValueTask RecordAsync(ManagedElsaHandoffAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

public sealed class NullManagedElsaHandoffAuditSink : IManagedElsaHandoffAuditSink
{
    public ValueTask RecordAsync(ManagedElsaHandoffAuditEvent auditEvent, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

public sealed class ManagedElsaHandoffIssuer(
    IOptions<ManagedElsaHandoffOptions> options,
    ManagedElsaHandoffKeyRing keyRing,
    TimeProvider timeProvider)
{
    private readonly ManagedElsaHandoffOptions _options = ValidateOptions(options.Value);

    public ManagedElsaHandoffIssueResult Issue(
        TrustedWorkspaceIdentity identity,
        ManagedElsaHandoffRequest request,
        ManagedElsaHandoffAuthorization authorization,
        DateTimeOffset sessionExpiresAt)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(authorization);

        if (!string.Equals(request.Audience, authorization.Audience, StringComparison.Ordinal) ||
            request.OrganizationId != authorization.OrganizationId ||
            request.InstanceId != authorization.InstanceId ||
            !HasExactRedirectBinding(request.RedirectUri, authorization.RedirectUri) ||
            !string.Equals(request.CodeChallenge, authorization.CodeChallenge, StringComparison.Ordinal) ||
            !IsSafeRedirectUri(request.RedirectUri) ||
            !IsValidCodeChallenge(request.CodeChallenge) ||
            authorization.BindingVersion < 1 ||
            string.IsNullOrWhiteSpace(identity.Issuer) ||
            string.IsNullOrWhiteSpace(identity.Subject) ||
            !request.RequestedScopes.SetEquals([ManagedElsaHandoffDefaults.RuntimeSessionScope]) ||
            !request.RequestedScopes.All(authorization.Scopes.Contains))
            throw new InvalidOperationException("The handoff authorization does not match the requested target.");

        var now = timeProvider.GetUtcNow();
        sessionExpiresAt = NormalizeUnixTime(sessionExpiresAt);
        var minimumSessionExpiresAt = NormalizeUnixTime(now.Add(_options.TokenLifetime));
        var maximumSessionExpiresAt = NormalizeUnixTime(now.Add(_options.RuntimeSessionMaximumLifetime));
        if (sessionExpiresAt < minimumSessionExpiresAt ||
            sessionExpiresAt > maximumSessionExpiresAt)
            throw new InvalidOperationException("The Control session does not provide a safe runtime-session bound.");

        var expiresAt = now.Add(_options.TokenLifetime);
        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, authorization.AccountId.ToString("D")),
            new("control_iss", identity.Issuer),
            new("control_sub", identity.Subject),
            new("org_id", authorization.OrganizationId.ToString("D")),
            new("instance_id", authorization.InstanceId.ToString("D")),
            new("binding_version", authorization.BindingVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("redirect_uri", authorization.RedirectUri.OriginalString),
            new("code_challenge", authorization.CodeChallenge),
            new("scope", string.Join(' ', request.RequestedScopes.Order(StringComparer.Ordinal))),
            new(ManagedElsaHandoffDefaults.SessionExpiryClaim,
                sessionExpiresAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, jti)
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = authorization.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            TokenType = ManagedElsaHandoffDefaults.TokenType,
            SigningCredentials = keyRing.ActiveSigningCredentials
        };
        var handler = new JwtSecurityTokenHandler();
        var token = handler.WriteToken(handler.CreateToken(descriptor));

        return new ManagedElsaHandoffIssueResult(
            token,
            ManagedElsaHandoffDefaults.TokenType,
            keyRing.ActiveKeyId,
            authorization.Audience,
            authorization.RedirectUri,
            now,
            expiresAt,
            jti,
            sessionExpiresAt);
    }

    public bool TryCreateSessionBound(
        DateTimeOffset sourceExpiresAt,
        out DateTimeOffset sessionExpiresAt)
    {
        var now = timeProvider.GetUtcNow();
        var maximum = NormalizeUnixTime(now.Add(_options.RuntimeSessionMaximumLifetime));
        sessionExpiresAt = NormalizeUnixTime(sourceExpiresAt) <= maximum
            ? NormalizeUnixTime(sourceExpiresAt)
            : maximum;
        return sessionExpiresAt >= NormalizeUnixTime(now.Add(_options.TokenLifetime));
    }

    internal static ManagedElsaHandoffOptions ValidateOptions(ManagedElsaHandoffOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Issuer) || !Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("ManagedElsa:Handoff:Issuer must be an absolute HTTPS URI.");
        if (options.TokenLifetime <= TimeSpan.Zero || options.TokenLifetime > ManagedElsaHandoffDefaults.MaximumLifetime)
            throw new InvalidOperationException($"ManagedElsa:Handoff:TokenLifetime must be between 1 second and {ManagedElsaHandoffDefaults.MaximumLifetime}.");
        if (options.RuntimeSessionMaximumLifetime <= TimeSpan.Zero ||
            options.RuntimeSessionMaximumLifetime > ManagedElsaHandoffDefaults.MaximumRuntimeSessionLifetime)
            throw new InvalidOperationException(
                $"ManagedElsa:Handoff:RuntimeSessionMaximumLifetime must be between 1 second and {ManagedElsaHandoffDefaults.MaximumRuntimeSessionLifetime}.");
        if (options.RuntimeSessionMaximumLifetime <= options.TokenLifetime)
            throw new InvalidOperationException(
                "ManagedElsa:Handoff:RuntimeSessionMaximumLifetime must exceed TokenLifetime.");

        return options;
    }

    internal static DateTimeOffset NormalizeUnixTime(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());

    internal static bool IsSafeRedirectUri(Uri redirectUri) =>
        redirectUri.IsAbsoluteUri &&
        (redirectUri.Scheme == Uri.UriSchemeHttps ||
         (redirectUri.Scheme == Uri.UriSchemeHttp &&
          (redirectUri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || redirectUri.Host.Equals("127.0.0.1", StringComparison.Ordinal)))) &&
        !redirectUri.Fragment.Any() &&
        !redirectUri.UserInfo.Any();

    internal static bool HasExactRedirectBinding(Uri left, Uri right) =>
        string.Equals(left.OriginalString, right.OriginalString, StringComparison.Ordinal);

    internal static bool IsValidCodeChallenge(string? codeChallenge) =>
        codeChallenge is { Length: 43 } &&
        codeChallenge.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    internal static bool IsValidCodeVerifier(string? codeVerifier) =>
        codeVerifier is { Length: >= 43 and <= 128 } &&
        codeVerifier.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.' or '_' or '~');

    internal static string CreateCodeChallenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

public sealed class ManagedElsaHandoffRedeemer(
    IOptions<ManagedElsaHandoffOptions> options,
    ManagedElsaHandoffKeyRing keyRing,
    IManagedElsaHandoffReplayStore replayStore,
    IManagedElsaHandoffAuthorizer authorizer,
    TimeProvider timeProvider,
    IManagedElsaHandoffAuditSink auditSink)
{
    private readonly ManagedElsaHandoffOptions _options = ManagedElsaHandoffIssuer.ValidateOptions(options.Value);

    public Task<ManagedElsaHandoffRedeemResult> RedeemAsync(
        string token,
        string expectedAudience,
        Uri expectedRedirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default) =>
        RedeemAsync(token, expectedAudience, expectedRedirectUri, codeVerifier, cancellationToken, null);

    public async Task<ManagedElsaHandoffRedeemResult> RedeemAsync(
        string token,
        string expectedAudience,
        Uri expectedRedirectUri,
        string codeVerifier,
        CancellationToken cancellationToken,
        string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(expectedAudience) ||
            !ManagedElsaHandoffIssuer.IsSafeRedirectUri(expectedRedirectUri) ||
            !ManagedElsaHandoffIssuer.IsValidCodeVerifier(codeVerifier))
            return await InvalidAsync(cancellationToken, correlationId);

        ManagedElsaHandoffClaims claims;
        try
        {
            claims = ValidateToken(token, expectedAudience, expectedRedirectUri);
        }
        catch (SecurityTokenException)
        {
            return await InvalidAsync(cancellationToken, correlationId);
        }
        catch (ArgumentException)
        {
            return await InvalidAsync(cancellationToken, correlationId);
        }

        var computedChallenge = ManagedElsaHandoffIssuer.CreateCodeChallenge(codeVerifier);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(computedChallenge),
                Encoding.ASCII.GetBytes(claims.CodeChallenge)))
            return await InvalidAsync(cancellationToken, correlationId);

        if (!await replayStore.TryConsumeAsync(claims.Jti, claims.ExpiresAt, cancellationToken))
        {
            await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
                "redeem.replay_rejected",
                claims.Jti,
                claims.AccountId,
                claims.OrganizationId,
                claims.InstanceId,
                claims.Audience,
                timeProvider.GetUtcNow(),
                claims.BindingVersion,
                correlationId), cancellationToken);
            return ManagedElsaHandoffRedeemResult.Denied(ManagedElsaHandoffRedeemFailure.Replay);
        }

        if (!await authorizer.IsStillAuthorizedAsync(claims, cancellationToken))
        {
            await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
                "redeem.authorization_revoked",
                claims.Jti,
                claims.AccountId,
                claims.OrganizationId,
                claims.InstanceId,
                claims.Audience,
                timeProvider.GetUtcNow(),
                claims.BindingVersion,
                correlationId), cancellationToken);
            return ManagedElsaHandoffRedeemResult.Denied(ManagedElsaHandoffRedeemFailure.AuthorizationRevoked);
        }

        await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
            "redeem.succeeded",
            claims.Jti,
            claims.AccountId,
            claims.OrganizationId,
            claims.InstanceId,
            claims.Audience,
            timeProvider.GetUtcNow(),
            claims.BindingVersion,
            correlationId), cancellationToken);
        return ManagedElsaHandoffRedeemResult.Success(claims);
    }

    private ManagedElsaHandoffClaims ValidateToken(string token, string expectedAudience, Uri expectedRedirectUri)
    {
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keyRing.ValidationKeys,
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = expectedAudience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        };
        var principal = handler.ValidateToken(token, validationParameters, out var validatedToken);
        if (validatedToken is not JwtSecurityToken jwt ||
            !string.Equals(jwt.Header.Alg, SecurityAlgorithms.RsaSha256, StringComparison.Ordinal) ||
            !string.Equals(jwt.Header.Typ, ManagedElsaHandoffDefaults.TokenType, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(jwt.Header.Kid) ||
            !keyRing.ContainsKey(jwt.Header.Kid))
            throw new SecurityTokenException("Only RS256 handoff tokens are accepted.");

        var jti = RequiredClaim(principal, JwtRegisteredClaimNames.Jti);
        var subject = RequiredGuid(principal, JwtRegisteredClaimNames.Sub);
        var controlIssuer = RequiredClaim(principal, "control_iss");
        var controlSubject = RequiredClaim(principal, "control_sub");
        var organizationId = RequiredGuid(principal, "org_id");
        var instanceId = RequiredGuid(principal, "instance_id");
        var bindingVersion = OptionalPositiveInt(principal, "binding_version", 1);
        var codeChallenge = RequiredClaim(principal, "code_challenge");
        var audience = RequiredClaim(principal, JwtRegisteredClaimNames.Aud);
        var redirectUri = new Uri(RequiredClaim(principal, "redirect_uri"), UriKind.Absolute);
        var scopes = RequiredClaim(principal, "scope")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (scopes.Count != 1 || !scopes.Contains(ManagedElsaHandoffDefaults.RuntimeSessionScope) ||
            !ManagedElsaHandoffIssuer.HasExactRedirectBinding(redirectUri, expectedRedirectUri) ||
            !ManagedElsaHandoffIssuer.IsSafeRedirectUri(redirectUri))
            throw new SecurityTokenException("The handoff token is not bound to the requested target.");

        var issuedAt = NumericDateClaim(principal, JwtRegisteredClaimNames.Iat);
        var expiresAt = NumericDateClaim(principal, JwtRegisteredClaimNames.Exp);
        var sessionExpiresAt = NumericDateClaim(principal, ManagedElsaHandoffDefaults.SessionExpiryClaim);
        var now = ManagedElsaHandoffIssuer.NormalizeUnixTime(timeProvider.GetUtcNow());
        var maximumSessionExpiresAt = ManagedElsaHandoffIssuer.NormalizeUnixTime(
            issuedAt.Add(_options.RuntimeSessionMaximumLifetime));
        if (sessionExpiresAt <= now ||
            sessionExpiresAt <= issuedAt ||
            sessionExpiresAt > maximumSessionExpiresAt)
            throw new SecurityTokenException("The handoff token has no safe Control session bound.");

        return new ManagedElsaHandoffClaims(
            jti,
            subject,
            controlIssuer,
            controlSubject,
            organizationId,
            instanceId,
            audience,
            redirectUri,
            codeChallenge,
            scopes,
            issuedAt,
            expiresAt,
            bindingVersion,
            sessionExpiresAt);
    }

    private async Task<ManagedElsaHandoffRedeemResult> InvalidAsync(
        CancellationToken cancellationToken,
        string? correlationId)
    {
        await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
            "redeem.invalid",
            "",
            null,
            null,
            null,
            null,
            timeProvider.GetUtcNow(),
            CorrelationId: correlationId), cancellationToken);
        return ManagedElsaHandoffRedeemResult.Denied(ManagedElsaHandoffRedeemFailure.InvalidToken);
    }

    private static string RequiredClaim(ClaimsPrincipal principal, string claimType) =>
        principal.FindFirst(claimType)?.Value is { Length: > 0 } value
            ? value
            : throw new SecurityTokenException($"Required handoff claim '{claimType}' is missing.");

    private static Guid RequiredGuid(ClaimsPrincipal principal, string claimType) =>
        Guid.TryParse(RequiredClaim(principal, claimType), out var value)
            ? value
            : throw new SecurityTokenException($"Handoff claim '{claimType}' is not a GUID.");

    private static DateTimeOffset NumericDateClaim(ClaimsPrincipal principal, string claimType) =>
        long.TryParse(RequiredClaim(principal, claimType), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? FromUnixTimeSeconds(seconds, claimType)
            : throw new SecurityTokenException($"Handoff claim '{claimType}' is not a numeric date.");

    private static DateTimeOffset FromUnixTimeSeconds(long seconds, string claimType)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new SecurityTokenException($"Handoff claim '{claimType}' is not a valid numeric date.");
        }
    }

    private static int RequiredPositiveInt(ClaimsPrincipal principal, string claimType) =>
        int.TryParse(RequiredClaim(principal, claimType), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new SecurityTokenException($"Handoff claim '{claimType}' is not a positive integer.");

    private static int OptionalPositiveInt(ClaimsPrincipal principal, string claimType, int legacyDefault) =>
        principal.FindFirst(claimType) is null
            ? legacyDefault
            : RequiredPositiveInt(principal, claimType);
}

public sealed class ManagedElsaHandoffService(
    IAuthenticatedControlSessionReader sessionReader,
    IManagedElsaHandoffAuthorizer authorizer,
    ManagedElsaHandoffIssuer issuer,
    ManagedElsaHandoffRedeemer redeemer,
    IManagedElsaHandoffAuditSink auditSink,
    TimeProvider timeProvider)
{
    public async Task<ManagedElsaHandoffIssueResult?> IssueAsync(
        HttpContext context,
        ManagedElsaHandoffRequest request,
        CancellationToken cancellationToken = default)
    {
        var session = await sessionReader.ReadAsync(context, cancellationToken);
        if (session is null || !issuer.TryCreateSessionBound(session.ExpiresAt, out var sessionExpiresAt))
        {
            await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
                "issue.session_expiry_rejected",
                "",
                null,
                null,
                null,
                null,
                timeProvider.GetUtcNow(),
                CorrelationId: context.TraceIdentifier), cancellationToken);
            return null;
        }

        var authorization = await authorizer.AuthorizeAsync(session.Identity, request, cancellationToken);
        if (authorization is null)
        {
            await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
                "issue.authorization_denied",
                "",
                null,
                null,
                null,
                null,
                timeProvider.GetUtcNow(),
                CorrelationId: context.TraceIdentifier), cancellationToken);
            return null;
        }

        var result = issuer.Issue(session.Identity, request, authorization, sessionExpiresAt);
        await auditSink.RecordAsync(new ManagedElsaHandoffAuditEvent(
            "issue.succeeded",
            result.Jti,
            authorization.AccountId,
            authorization.OrganizationId,
            authorization.InstanceId,
            authorization.Audience,
            result.IssuedAt,
            authorization.BindingVersion,
            context.TraceIdentifier), cancellationToken);
        return result;
    }

    public Task<ManagedElsaHandoffRedeemResult> RedeemAsync(
        string token,
        string expectedAudience,
        Uri expectedRedirectUri,
        string codeVerifier,
        CancellationToken cancellationToken = default) =>
        redeemer.RedeemAsync(token, expectedAudience, expectedRedirectUri, codeVerifier, cancellationToken);

    public Task<ManagedElsaHandoffRedeemResult> RedeemAsync(
        string token,
        string expectedAudience,
        Uri expectedRedirectUri,
        string codeVerifier,
        CancellationToken cancellationToken,
        string? correlationId) =>
        redeemer.RedeemAsync(token, expectedAudience, expectedRedirectUri, codeVerifier, cancellationToken, correlationId);
}
