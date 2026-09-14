using ElsaControl.Deployment.Abstractions.Azure;

namespace ElsaControl.PackageCatalog.Core.Accounts;

public enum OrganizationAzureSubscriptionBindState
{
    PendingConsent,
    Verifying,
    Active,
    Degraded,
    Unbound
}

public static class OrganizationAzureSubscriptionBindLifecycle
{
    public static bool CanTransition(
        OrganizationAzureSubscriptionBindState current,
        OrganizationAzureSubscriptionBindState next) =>
        current == next || (current, next) switch
        {
            (OrganizationAzureSubscriptionBindState.PendingConsent, OrganizationAzureSubscriptionBindState.Verifying) => true,
            (OrganizationAzureSubscriptionBindState.PendingConsent, OrganizationAzureSubscriptionBindState.Unbound) => true,
            (OrganizationAzureSubscriptionBindState.Verifying, OrganizationAzureSubscriptionBindState.Active) => true,
            (OrganizationAzureSubscriptionBindState.Verifying, OrganizationAzureSubscriptionBindState.Degraded) => true,
            (OrganizationAzureSubscriptionBindState.Verifying, OrganizationAzureSubscriptionBindState.Unbound) => true,
            (OrganizationAzureSubscriptionBindState.Active, OrganizationAzureSubscriptionBindState.Verifying) => true,
            (OrganizationAzureSubscriptionBindState.Active, OrganizationAzureSubscriptionBindState.Degraded) => true,
            (OrganizationAzureSubscriptionBindState.Active, OrganizationAzureSubscriptionBindState.Unbound) => true,
            (OrganizationAzureSubscriptionBindState.Degraded, OrganizationAzureSubscriptionBindState.Verifying) => true,
            (OrganizationAzureSubscriptionBindState.Degraded, OrganizationAzureSubscriptionBindState.Unbound) => true,
            _ => false
        };
}

public sealed class OrganizationAzureSubscriptionBind
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public string CustomerTenantId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string ManagingTenantId { get; set; } = "";
    public string ManagingPrincipalObjectId { get; set; } = "";
    public string ManagingPrincipalClientId { get; set; } = "";
    public string RegistrationDefinitionId { get; set; } = "";
    public string? RegistrationDefinitionFingerprint { get; set; }
    public OrganizationAzureSubscriptionBindState State { get; set; } = OrganizationAzureSubscriptionBindState.PendingConsent;
    public DateTimeOffset? VerifiedAt { get; set; }
    public string? LastPreflightCode { get; set; }
    public Guid? CreatedByAccountId { get; set; }
    public string? UnbindReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record OrganizationAzureSubscriptionBindRequest(
    string CustomerTenantId,
    string SubscriptionId,
    string ManagingTenantId,
    string ManagingPrincipalObjectId,
    string ManagingPrincipalClientId,
    string RegistrationDefinitionId,
    string? RegistrationDefinitionFingerprint = null);

public sealed record OrganizationAzureSubscriptionBindUnbindRequest(string? Reason = null);

public enum OrganizationAzureSubscriptionBindFailure
{
    OrganizationNotFound,
    AlreadyBound,
    BindInFlight,
    DegradedBindRequiresUnbind,
    SubscriptionMustChange,
    BindNotFound,
    InvalidState,
    AuthorityUnavailable,
    AuthorityRejected,
    InvalidRequest
}

public sealed record OrganizationAzureSubscriptionBindResult(
    OrganizationAzureSubscriptionBind? Bind,
    OrganizationAzureSubscriptionBindFailure? Failure = null)
{
    public bool Succeeded => Bind is not null && Failure is null;

    public static OrganizationAzureSubscriptionBindResult Success(OrganizationAzureSubscriptionBind bind) => new(bind);
    public static OrganizationAzureSubscriptionBindResult Denied(OrganizationAzureSubscriptionBindFailure failure) => new(null, failure);
}

public sealed record OrganizationAzureSubscriptionBindTransition(
    Guid OrganizationId,
    Guid BindId,
    OrganizationAzureSubscriptionBindState ExpectedState,
    OrganizationAzureSubscriptionBindState NewState,
    DateTimeOffset ChangedAt,
    DateTimeOffset? ExpectedUpdatedAt = null,
    DateTimeOffset? VerifiedAt = null,
    string? LastPreflightCode = null,
    string? UnbindReason = null,
    string? RegistrationDefinitionFingerprint = null);

public interface IOrganizationAzureSubscriptionBindStore
{
    Task<OrganizationAzureSubscriptionBind?> GetAsync(Guid organizationId, Guid bindId, CancellationToken cancellationToken = default);
    Task<OrganizationAzureSubscriptionBind?> GetLatestAsync(Guid organizationId, CancellationToken cancellationToken = default);
    Task<OrganizationAzureSubscriptionBindResult> CreatePendingAsync(OrganizationAzureSubscriptionBind bind, CancellationToken cancellationToken = default);
    Task<OrganizationAzureSubscriptionBindResult> TransitionAsync(OrganizationAzureSubscriptionBindTransition transition, CancellationToken cancellationToken = default);
}

/// <summary>Fail-closed default used when the Azure provider authority is not configured.</summary>
public sealed class UnconfiguredAzureLighthouseAuthorityObserver : IAzureLighthouseAuthorityObserver
{
    public Task<AzureLighthouseAuthorityObservationResult> ObserveAsync(
        AzureLighthouseAuthorityObservationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AzureLighthouseAuthorityObservationResult(
            false,
            "azure.lighthouse.authority-unconfigured",
            "The delegated Azure authority observer is not configured."));
}

public sealed class OrganizationAzureSubscriptionBindService(
    IOrganizationAzureSubscriptionBindStore store,
    IAzureLighthouseAuthorityObserver authorityObserver,
    TimeProvider? timeProvider = null)
{
    // Lighthouse verification runs four sequential Azure CLI commands. The governed
    // command timeout is at most one hour, so the lease covers that worst case plus
    // enough hand-off margin before another caller may reclaim it.
    private static readonly TimeSpan VerificationLeaseDuration = TimeSpan.FromHours(4) + TimeSpan.FromMinutes(15);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<OrganizationAzureSubscriptionBind?> GetAsync(Guid organizationId, Guid bindId, CancellationToken cancellationToken = default) =>
        store.GetAsync(organizationId, bindId, cancellationToken);

    public Task<OrganizationAzureSubscriptionBind?> GetLatestAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
        store.GetLatestAsync(organizationId, cancellationToken);

    public async Task<OrganizationAzureSubscriptionBindResult> CreateAsync(
        Guid organizationId,
        OrganizationAzureSubscriptionBindRequest request,
        Guid? createdByAccountId,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeRequest(request, out var normalized))
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidRequest);

        var current = await store.GetLatestAsync(organizationId, cancellationToken);
        if (current is not null)
        {
            if (current.State == OrganizationAzureSubscriptionBindState.Unbound &&
                string.Equals(current.SubscriptionId, normalized.SubscriptionId, StringComparison.Ordinal))
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange);
            if (current.State == OrganizationAzureSubscriptionBindState.Active)
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.AlreadyBound);
            if (current.State is OrganizationAzureSubscriptionBindState.PendingConsent or OrganizationAzureSubscriptionBindState.Verifying)
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindInFlight);
            if (current.State == OrganizationAzureSubscriptionBindState.Degraded)
                return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.DegradedBindRequiresUnbind);
        }

        var bind = new OrganizationAzureSubscriptionBind
        {
            OrganizationId = organizationId,
            CustomerTenantId = normalized.CustomerTenantId,
            SubscriptionId = normalized.SubscriptionId,
            ManagingTenantId = normalized.ManagingTenantId,
            ManagingPrincipalObjectId = normalized.ManagingPrincipalObjectId,
            ManagingPrincipalClientId = normalized.ManagingPrincipalClientId,
            RegistrationDefinitionId = normalized.RegistrationDefinitionId,
            RegistrationDefinitionFingerprint = normalized.RegistrationDefinitionFingerprint,
            State = OrganizationAzureSubscriptionBindState.PendingConsent,
            CreatedByAccountId = createdByAccountId,
            CreatedAt = _timeProvider.GetUtcNow(),
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        return await store.CreatePendingAsync(bind, cancellationToken);
    }

    public async Task<OrganizationAzureSubscriptionBindResult> RelinkAsync(
        Guid organizationId,
        OrganizationAzureSubscriptionBindRequest request,
        Guid? createdByAccountId,
        CancellationToken cancellationToken = default)
    {
        var current = await store.GetLatestAsync(organizationId, cancellationToken);
        if (current is null)
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindNotFound);
        if (current.State != OrganizationAzureSubscriptionBindState.Unbound)
            return current.State == OrganizationAzureSubscriptionBindState.Degraded
                ? OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.DegradedBindRequiresUnbind)
                : OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.AlreadyBound);
        if (!TryNormalizeRequest(request, out var normalized))
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidRequest);
        if (string.Equals(current.SubscriptionId, normalized.SubscriptionId, StringComparison.Ordinal))
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange);
        return await CreateAsync(organizationId, request, createdByAccountId, cancellationToken);
    }

    public async Task<OrganizationAzureSubscriptionBindResult> VerifyAsync(
        Guid organizationId,
        Guid bindId,
        CancellationToken cancellationToken = default)
    {
        var bind = await store.GetAsync(organizationId, bindId, cancellationToken);
        if (bind is null)
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindNotFound);
        if (bind.State is OrganizationAzureSubscriptionBindState.Unbound)
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidState);

        var now = _timeProvider.GetUtcNow();
        if (bind.State == OrganizationAzureSubscriptionBindState.Verifying &&
            now - bind.UpdatedAt < VerificationLeaseDuration)
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindInFlight);

        var started = await store.TransitionAsync(new(
            organizationId, bindId, bind.State,
            OrganizationAzureSubscriptionBindState.Verifying, now,
            ExpectedUpdatedAt: bind.UpdatedAt,
            LastPreflightCode: bind.State == OrganizationAzureSubscriptionBindState.Verifying
                ? "azure.lighthouse.verification-restarted"
                : null), cancellationToken);
        if (!started.Succeeded)
            return started;
        var leaseUpdatedAt = started.Bind!.UpdatedAt;

        AzureLighthouseAuthorityObservationResult observation;
        try
        {
            observation = await authorityObserver.ObserveAsync(
                new AzureLighthouseAuthorityObservationRequest(
                    bind.CustomerTenantId,
                    bind.SubscriptionId,
                    bind.ManagingTenantId,
                    bind.ManagingPrincipalObjectId,
                    bind.ManagingPrincipalClientId,
                    bind.RegistrationDefinitionId,
                    bind.RegistrationDefinitionFingerprint), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await store.TransitionAsync(new(
                    organizationId,
                    bindId,
                    OrganizationAzureSubscriptionBindState.Verifying,
                    OrganizationAzureSubscriptionBindState.Degraded,
                    _timeProvider.GetUtcNow(),
                    ExpectedUpdatedAt: leaseUpdatedAt,
                    LastPreflightCode: "azure.lighthouse.verification-cancelled"), CancellationToken.None);
            }
            catch (Exception)
            {
                // Cancellation remains the caller's result; cleanup is best effort because
                // another operator may have already closed this bind.
            }
            throw;
        }
        catch (Exception)
        {
            observation = new(false, "azure.lighthouse.observation-failed", "The delegated Azure authority could not be observed.");
        }

        var completed = await store.TransitionAsync(new(
            organizationId,
            bindId,
            OrganizationAzureSubscriptionBindState.Verifying,
            observation.Succeeded ? OrganizationAzureSubscriptionBindState.Active : OrganizationAzureSubscriptionBindState.Degraded,
            _timeProvider.GetUtcNow(),
            ExpectedUpdatedAt: leaseUpdatedAt,
            VerifiedAt: observation.Succeeded ? _timeProvider.GetUtcNow() : null,
            LastPreflightCode: observation.Code,
            RegistrationDefinitionFingerprint: observation.Succeeded
                ? observation.RegistrationDefinitionFingerprint
                : null), CancellationToken.None);
        return completed.Succeeded
            ? completed
            : OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidState);
    }

    public async Task<OrganizationAzureSubscriptionBindResult> UnbindAsync(
        Guid organizationId,
        Guid bindId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var bind = await store.GetAsync(organizationId, bindId, cancellationToken);
        if (bind is null)
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindNotFound);
        if (bind.State == OrganizationAzureSubscriptionBindState.Unbound)
            return OrganizationAzureSubscriptionBindResult.Success(bind);
        if (reason is { Length: > 512 })
            return OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidRequest);

        var result = await store.TransitionAsync(new(
            organizationId, bindId, bind.State,
            OrganizationAzureSubscriptionBindState.Unbound,
            _timeProvider.GetUtcNow(),
            ExpectedUpdatedAt: bind.UpdatedAt,
            UnbindReason: string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()), cancellationToken);
        return result;
    }

    private static bool TryNormalizeRequest(
        OrganizationAzureSubscriptionBindRequest request,
        out OrganizationAzureSubscriptionBindRequest normalized)
    {
        normalized = request;
        if (request is null ||
            !TryCanonicalGuid(request.CustomerTenantId, out var customerTenantId) ||
            !TryCanonicalGuid(request.SubscriptionId, out var subscriptionId) ||
            !TryCanonicalGuid(request.ManagingTenantId, out var managingTenantId) ||
            !TryCanonicalGuid(request.ManagingPrincipalObjectId, out var principalObjectId) ||
            !TryCanonicalGuid(request.ManagingPrincipalClientId, out var principalClientId) ||
            !TrySafeIdentifier(request.RegistrationDefinitionId, 2048, out var registrationDefinitionId) ||
            !TryOptionalSafeIdentifier(request.RegistrationDefinitionFingerprint, 256, out var fingerprint))
            return false;

        normalized = new(customerTenantId, subscriptionId, managingTenantId, principalObjectId, principalClientId, registrationDefinitionId, fingerprint);
        return true;
    }

    private static bool TryCanonicalGuid(string? value, out string normalized)
    {
        normalized = "";
        if (!Guid.TryParseExact(value, "D", out var guid) || guid == Guid.Empty)
            return false;
        normalized = guid.ToString("D");
        return true;
    }

    private static bool TrySafeIdentifier(string? value, int maxLength, out string normalized)
    {
        normalized = value?.Trim() ?? "";
        return normalized.Length is > 0 && normalized.Length <= maxLength && !normalized.Any(char.IsControl);
    }

    private static bool TryOptionalSafeIdentifier(string? value, int maxLength, out string? normalized)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is null || normalized.Length <= maxLength && !normalized.Any(char.IsControl);
    }
}
