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
    public static async Task RecordLoadedAsync(
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

        await RecordLoadedAsync(audit, keyRing, timeProvider, cancellationToken);
    }

    public static async Task RecordLoadedAsync(
        IManagedElsaHandoffAuditSink audit,
        ManagedElsaHandoffKeyRing keyRing,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(timeProvider);

        await RecordAsync(
            audit,
            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix + keyRing.ActiveKeyId,
            timeProvider,
            cancellationToken);
        foreach (var keyId in keyRing.PreviousVerificationKeyIds)
        {
            await RecordAsync(
                audit,
                ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix + keyId,
                timeProvider,
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
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        foreach (var keyId in ManagedElsaHandoffKeyIds.RetiredKeyIds(previouslyTrusted, currentlyTrusted))
        {
            await RecordAsync(
                audit,
                ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix + keyId,
                timeProvider,
                cancellationToken);
        }
    }

    private static Task RecordAsync(
        IManagedElsaHandoffAuditSink audit,
        string action,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        audit.RecordAsync(
            new ManagedElsaHandoffAuditEvent(
                action,
                "",
                null,
                null,
                null,
                null,
                timeProvider.GetUtcNow()),
            cancellationToken).AsTask();
}
