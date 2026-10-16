using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Allowed handoff signing/verification key identifiers. The format is a
/// configuration-path-safe JWT <c>kid</c>: it binds as an Azure App Setting,
/// Container Apps environment variable, JSON key, and numbered-list value.
/// </summary>
public static class ManagedElsaHandoffKeyIds
{
    public const int MaximumLength = 64;

    public static bool IsAllowed(string? keyId)
    {
        if (keyId is not { Length: >= 1 and <= MaximumLength })
            return false;
        if (keyId.Contains("__", StringComparison.Ordinal))
            return false;
        if (!IsAlphaNumeric(keyId[0]))
            return false;
        if (keyId.Length > 1 && !IsAlphaNumeric(keyId[^1]))
            return false;
        for (var index = 1; index < keyId.Length - 1; index++)
        {
            if (!IsKeyIdBodyChar(keyId[index]))
                return false;
        }

        return true;
    }

    public static IReadOnlyList<string> RetiredKeyIds(
        IReadOnlyCollection<string> previouslyTrusted,
        IReadOnlyCollection<string> currentlyTrusted)
    {
        ArgumentNullException.ThrowIfNull(previouslyTrusted);
        ArgumentNullException.ThrowIfNull(currentlyTrusted);
        var current = currentlyTrusted.ToHashSet(StringComparer.Ordinal);
        return previouslyTrusted
            .Where(keyId => IsAllowed(keyId) && !current.Contains(keyId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsAlphaNumeric(char value) =>
        value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9');

    private static bool IsKeyIdBodyChar(char value) =>
        IsAlphaNumeric(value) || value is '.' or '_' or '-';
}

/// <summary>
/// Append-only key-ring audit helpers. Actions name key ids only and never
/// include PEM, tokens, or other key material.
/// </summary>
public static class ManagedElsaHandoffKeyRingAudit
{
    public static async Task RecordTransitionAsync(
        IServiceProvider services,
        ManagedElsaHandoffKeyRing keyRing,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyRing);
        if (services.GetService<IServiceScopeFactory>() is not { } scopes)
            return;

        await using var scope = scopes.CreateAsyncScope();
        var audit = scope.ServiceProvider.GetService<IManagedElsaHandoffAuditSink>();
        var timeProvider = scope.ServiceProvider.GetService<TimeProvider>() ?? TimeProvider.System;
        if (audit is null)
            return;

        await RecordTransitionAsync(audit, keyRing, timeProvider, cancellationToken);
    }

    public static async Task RecordTransitionAsync(
        IManagedElsaHandoffAuditSink audit,
        ManagedElsaHandoffKeyRing keyRing,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(timeProvider);

        await audit.RecordKeyRingTransitionAsync(
            keyRing.ActiveKeyId,
            keyRing.PreviousVerificationKeyIds,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public static Task RecordLoadedAsync(
        IManagedElsaHandoffAuditSink audit,
        ManagedElsaHandoffKeyRing keyRing,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return RecordLoadedAsync(audit, keyRing, timeProvider.GetUtcNow(), cancellationToken);
    }

    public static async Task RecordLoadedAsync(
        IManagedElsaHandoffAuditSink audit,
        ManagedElsaHandoffKeyRing keyRing,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(keyRing);

        await RecordAsync(
            audit,
            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix + keyRing.ActiveKeyId,
            occurredAt,
            cancellationToken);
        foreach (var keyId in keyRing.PreviousVerificationKeyIds)
        {
            await RecordAsync(
                audit,
                ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix + keyId,
                occurredAt,
                cancellationToken);
        }
    }

    public static async Task RecordRetiredAsync(
        IManagedElsaHandoffAuditSink audit,
        IReadOnlyCollection<string> previouslyTrusted,
        IReadOnlyCollection<string> currentlyTrusted,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        await RecordRetiredAsync(
            audit,
            previouslyTrusted,
            currentlyTrusted,
            previousActiveKeyId: null,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    public static async Task RecordRetiredAsync(
        IManagedElsaHandoffAuditSink audit,
        IReadOnlyCollection<string> previouslyTrusted,
        IReadOnlyCollection<string> currentlyTrusted,
        string? previousActiveKeyId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        foreach (var keyId in ManagedElsaHandoffKeyIds.RetiredKeyIds(previouslyTrusted, currentlyTrusted))
        {
            var signerRevoked = string.Equals(keyId, previousActiveKeyId, StringComparison.Ordinal);
            await RecordAsync(audit, RetiredAction(keyId, signerRevoked), occurredAt, cancellationToken);
        }
    }

    public static ManagedElsaHandoffTrustedKeySnapshot ParseTrustedSnapshot(IEnumerable<string> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        string? activeKeyId = null;
        var trusted = new List<string>();
        foreach (var action in actions)
        {
            if (TryReadKeyId(action, ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix, out var active) &&
                active is not null)
            {
                activeKeyId = active;
                if (!trusted.Contains(active, StringComparer.Ordinal))
                    trusted.Add(active);
            }
            else if (TryReadKeyId(action, ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix, out var previous) &&
                     previous is not null &&
                     !trusted.Contains(previous, StringComparer.Ordinal))
            {
                trusted.Add(previous);
            }
        }

        return activeKeyId is null && trusted.Count == 0
            ? ManagedElsaHandoffTrustedKeySnapshot.Empty
            : new ManagedElsaHandoffTrustedKeySnapshot(activeKeyId, trusted);
    }

    public static string RetiredAction(string keyId, bool signerRevoked) =>
        signerRevoked
            ? ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix + keyId +
              ManagedElsaHandoffDefaults.KeyRingRetiredRevokedSuffix
            : ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix + keyId;

    private static bool TryReadKeyId(string action, string prefix, out string? keyId)
    {
        keyId = null;
        if (!action.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var candidate = action[prefix.Length..];
        if (!ManagedElsaHandoffKeyIds.IsAllowed(candidate))
            return false;
        keyId = candidate;
        return true;
    }

    private static Task RecordAsync(
        IManagedElsaHandoffAuditSink audit,
        string action,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new ManagedElsaHandoffAuditEvent(
                action,
                "",
                null,
                null,
                null,
                null,
                occurredAt),
            cancellationToken).AsTask();
}
