using ElsaControl.PackageCatalog.Core.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.Api.Authentication;

/// <summary>
/// Allowed handoff signing/verification key identifiers. The format is a
/// configuration-path-safe JWT <c>kid</c>: it binds as an Azure App Setting,
/// Container Apps environment variable, JSON key, and numbered-list value.
/// </summary>
public static class ManagedElsaHandoffKeyIds
{
    public const int MaximumLength = ManagedElsaHandoffKeyRingAuditSnapshotParser.MaximumKeyIdLength;

    public static bool IsAllowed(string? keyId) =>
        ManagedElsaHandoffKeyRingAuditSnapshotParser.IsAllowedKeyId(keyId);

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

    public static ManagedElsaHandoffTrustedKeySnapshot ParseTrustedSnapshot(IEnumerable<string> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var parsed = ManagedElsaHandoffKeyRingAuditSnapshotParser.Parse(
            actions,
            ManagedElsaHandoffDefaults.KeyRingActiveAuditActionPrefix,
            ManagedElsaHandoffDefaults.KeyRingPreviousAuditActionPrefix);
        return parsed.ActiveKeyId is null && parsed.TrustedKeyIds.Count == 0
            ? ManagedElsaHandoffTrustedKeySnapshot.Empty
            : new ManagedElsaHandoffTrustedKeySnapshot(parsed.ActiveKeyId, parsed.TrustedKeyIds);
    }

    public static string RetiredAction(string keyId, bool signerRevoked) =>
        signerRevoked
            ? ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix + keyId +
              ManagedElsaHandoffDefaults.KeyRingRetiredRevokedSuffix
            : ManagedElsaHandoffDefaults.KeyRingRetiredAuditActionPrefix + keyId;
}
