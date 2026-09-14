using ElsaControl.Deployment.Abstractions.Azure;
using ElsaControl.PackageCatalog.Core.Accounts;

namespace ElsaControl.PackageCatalog.Core.Tests;

public sealed class OrganizationAzureSubscriptionBindServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrganizationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid AccountId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Create_normalizes_identifiers_and_persists_the_pending_consent_record()
    {
        var store = new FakeBindStore();
        var service = CreateService(store);
        var request = new OrganizationAzureSubscriptionBindRequest(
            "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA",
            "CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC",
            "DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD",
            "EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE",
            "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
            "  registration/v1  ",
            "  fingerprint  ");

        var result = await service.CreateAsync(OrganizationId, request, AccountId);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Bind);
        var bind = result.Bind!;
        Assert.Equal(OrganizationId, bind.OrganizationId);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", bind.CustomerTenantId);
        Assert.Equal("cccccccc-cccc-cccc-cccc-cccccccccccc", bind.SubscriptionId);
        Assert.Equal("dddddddd-dddd-dddd-dddd-dddddddddddd", bind.ManagingTenantId);
        Assert.Equal("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", bind.ManagingPrincipalObjectId);
        Assert.Equal("ffffffff-ffff-ffff-ffff-ffffffffffff", bind.ManagingPrincipalClientId);
        Assert.Equal("registration/v1", bind.RegistrationDefinitionId);
        Assert.Equal("fingerprint", bind.RegistrationDefinitionFingerprint);
        Assert.Equal(OrganizationAzureSubscriptionBindState.PendingConsent, bind.State);
        Assert.Equal(AccountId, bind.CreatedByAccountId);
        Assert.Equal(Now, bind.CreatedAt);
        Assert.Equal(Now, bind.UpdatedAt);
    }

    [Theory]
    [InlineData("not-a-guid", "azure-subscription-bind.invalid-request")]
    [InlineData("00000000-0000-0000-0000-000000000000", "azure-subscription-bind.invalid-request")]
    public async Task Create_rejects_noncanonical_or_empty_guid_identifiers(string subscriptionId, string _)
    {
        var store = new FakeBindStore();
        var result = await CreateService(store).CreateAsync(
            OrganizationId,
            Request(subscriptionId: subscriptionId),
            AccountId);

        Assert.False(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindFailure.InvalidRequest, result.Failure);
        Assert.Empty(store.Binds);
    }

    [Theory]
    [InlineData(OrganizationAzureSubscriptionBindState.Active, OrganizationAzureSubscriptionBindFailure.AlreadyBound)]
    [InlineData(OrganizationAzureSubscriptionBindState.PendingConsent, OrganizationAzureSubscriptionBindFailure.BindInFlight)]
    [InlineData(OrganizationAzureSubscriptionBindState.Verifying, OrganizationAzureSubscriptionBindFailure.BindInFlight)]
    [InlineData(OrganizationAzureSubscriptionBindState.Degraded, OrganizationAzureSubscriptionBindFailure.DegradedBindRequiresUnbind)]
    public async Task Create_rejects_existing_active_or_inflight_lifecycle_states(
        OrganizationAzureSubscriptionBindState state,
        OrganizationAzureSubscriptionBindFailure expectedFailure)
    {
        var store = new FakeBindStore { Binds = [Bind(state)] };

        var result = await CreateService(store).CreateAsync(OrganizationId, Request(), AccountId);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedFailure, result.Failure);
        Assert.Single(store.Binds);
    }

    [Fact]
    public async Task Create_rejects_reusing_the_subscription_after_unbind()
    {
        var store = new FakeBindStore { Binds = [Bind(OrganizationAzureSubscriptionBindState.Unbound)] };

        var result = await CreateService(store).CreateAsync(OrganizationId, Request(), AccountId);

        Assert.False(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange, result.Failure);
    }

    [Fact]
    public async Task Relink_requires_an_unbound_record_and_a_different_subscription()
    {
        var old = Bind(OrganizationAzureSubscriptionBindState.Unbound);
        var store = new FakeBindStore { Binds = [old] };

        var result = await CreateService(store).RelinkAsync(
            OrganizationId,
            Request(subscriptionId: "99999999-9999-9999-9999-999999999999"),
            AccountId);

        Assert.True(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.PendingConsent, result.Bind!.State);
        Assert.Equal("99999999-9999-9999-9999-999999999999", result.Bind.SubscriptionId);
        Assert.Equal(2, store.Binds.Count);
        Assert.Equal(old.Id, store.Binds[0].Id);
    }

    [Fact]
    public async Task Relink_rejects_the_same_subscription_and_non_unbound_records()
    {
        var sameSubscriptionStore = new FakeBindStore { Binds = [Bind(OrganizationAzureSubscriptionBindState.Unbound)] };
        var same = await CreateService(sameSubscriptionStore).RelinkAsync(OrganizationId, Request(), AccountId);
        Assert.Equal(OrganizationAzureSubscriptionBindFailure.SubscriptionMustChange, same.Failure);

        var activeStore = new FakeBindStore { Binds = [Bind(OrganizationAzureSubscriptionBindState.Active)] };
        var active = await CreateService(activeStore).RelinkAsync(OrganizationId, Request(subscriptionId: "cccccccc-cccc-cccc-cccc-cccccccccccc"), AccountId);
        Assert.Equal(OrganizationAzureSubscriptionBindFailure.AlreadyBound, active.Failure);
    }

    [Fact]
    public async Task Relink_reports_an_invalid_request_before_subscription_change_policy()
    {
        var store = new FakeBindStore { Binds = [Bind(OrganizationAzureSubscriptionBindState.Unbound)] };

        var result = await CreateService(store).RelinkAsync(
            OrganizationId,
            Request(subscriptionId: "not-a-guid"),
            AccountId);

        Assert.Equal(OrganizationAzureSubscriptionBindFailure.InvalidRequest, result.Failure);
    }

    [Fact]
    public async Task Verify_promotes_to_active_and_persists_observation_code_and_fingerprint()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.PendingConsent);
        var store = new FakeBindStore { Binds = [bind] };
        var observer = new FakeAuthorityObserver
        {
            Result = new(true, "azure.lighthouse.observation-succeeded", "ok", "observed-fingerprint")
        };

        var result = await CreateService(store, observer).VerifyAsync(OrganizationId, bind.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Active, bind.State);
        Assert.Equal("azure.lighthouse.observation-succeeded", bind.LastPreflightCode);
        Assert.Equal("observed-fingerprint", bind.RegistrationDefinitionFingerprint);
        Assert.Equal(Now, bind.VerifiedAt);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Verifying, store.Transitions[0].NewState);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Active, store.Transitions[1].NewState);
        Assert.Equal("recorded-fingerprint", observer.LastRequest!.RegistrationDefinitionFingerprint);
    }

    [Fact]
    public async Task Verify_demotes_to_degraded_when_observation_is_rejected()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.PendingConsent);
        var store = new FakeBindStore { Binds = [bind] };
        var observer = new FakeAuthorityObserver
        {
            Result = new(false, "azure.lighthouse.rbac-insufficient", "rejected")
        };

        var result = await CreateService(store, observer).VerifyAsync(OrganizationId, bind.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Degraded, bind.State);
        Assert.Equal("azure.lighthouse.rbac-insufficient", bind.LastPreflightCode);
        Assert.Null(bind.VerifiedAt);
        Assert.Equal("recorded-fingerprint", bind.RegistrationDefinitionFingerprint);
    }

    [Fact]
    public async Task Verify_rejects_a_current_inflight_lease_without_running_an_observer()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.Verifying);
        var store = new FakeBindStore { Binds = [bind] };
        var observer = new FakeAuthorityObserver();

        var result = await CreateService(store, observer).VerifyAsync(OrganizationId, bind.Id);

        Assert.Equal(OrganizationAzureSubscriptionBindFailure.BindInFlight, result.Failure);
        Assert.Equal(0, observer.CallCount);
        Assert.Empty(store.Transitions);
    }

    [Fact]
    public async Task Verify_reclaims_a_stale_inflight_lease_with_compare_and_set()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.Verifying);
        bind.UpdatedAt = Now.AddMinutes(-16);
        var store = new FakeBindStore { Binds = [bind] };

        var result = await CreateService(store).VerifyAsync(OrganizationId, bind.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Active, bind.State);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Verifying, store.Transitions[0].ExpectedState);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Verifying, store.Transitions[0].NewState);
        Assert.Equal(Now.AddMinutes(-16), store.Transitions[0].ExpectedUpdatedAt);
        Assert.Equal(Now, store.Transitions[1].ExpectedUpdatedAt);
    }

    [Fact]
    public async Task Verify_cancellation_marks_the_bind_degraded_before_rethrowing()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.PendingConsent);
        var store = new FakeBindStore { Binds = [bind] };
        var observer = new FakeAuthorityObserver { ThrowCancellation = true };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateService(store, observer).VerifyAsync(OrganizationId, bind.Id, cancellation.Token));

        Assert.Equal(OrganizationAzureSubscriptionBindState.Degraded, bind.State);
        Assert.Equal("azure.lighthouse.verification-cancelled", bind.LastPreflightCode);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Degraded, store.Transitions[^1].NewState);
    }

    [Fact]
    public async Task Unbind_trims_the_reason_and_is_idempotent()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.Active);
        var store = new FakeBindStore { Binds = [bind] };
        var service = CreateService(store);

        var first = await service.UnbindAsync(OrganizationId, bind.Id, "  customer requested removal  ");
        var second = await service.UnbindAsync(OrganizationId, bind.Id, "ignored");

        Assert.True(first.Succeeded);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Unbound, bind.State);
        Assert.Equal("customer requested removal", bind.UnbindReason);
        Assert.True(second.Succeeded);
        Assert.Equal(1, store.Transitions.Count(transition => transition.NewState == OrganizationAzureSubscriptionBindState.Unbound));
    }

    [Fact]
    public async Task Unbind_rejects_an_oversized_reason()
    {
        var bind = Bind(OrganizationAzureSubscriptionBindState.Active);
        var store = new FakeBindStore { Binds = [bind] };

        var result = await CreateService(store).UnbindAsync(OrganizationId, bind.Id, new string('x', 513));

        Assert.Equal(OrganizationAzureSubscriptionBindFailure.InvalidRequest, result.Failure);
        Assert.Equal(OrganizationAzureSubscriptionBindState.Active, bind.State);
    }

    private static OrganizationAzureSubscriptionBindService CreateService(
        FakeBindStore store,
        FakeAuthorityObserver? observer = null) =>
        new(store, observer ?? new FakeAuthorityObserver(), new FixedTimeProvider(Now));

    private static OrganizationAzureSubscriptionBindRequest Request(
        string subscriptionId = "cccccccc-cccc-cccc-cccc-cccccccccccc") =>
        new(
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            subscriptionId,
            "dddddddd-dddd-dddd-dddd-dddddddddddd",
            "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
            "ffffffff-ffff-ffff-ffff-ffffffffffff",
            "registration/v1",
            "recorded-fingerprint");

    private static OrganizationAzureSubscriptionBind Bind(OrganizationAzureSubscriptionBindState state) =>
        new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrganizationId,
            CustomerTenantId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            SubscriptionId = "cccccccc-cccc-cccc-cccc-cccccccccccc",
            ManagingTenantId = "dddddddd-dddd-dddd-dddd-dddddddddddd",
            ManagingPrincipalObjectId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
            ManagingPrincipalClientId = "ffffffff-ffff-ffff-ffff-ffffffffffff",
            RegistrationDefinitionId = "/subscriptions/cccccccc-cccc-cccc-cccc-cccccccccccc/providers/Microsoft.ManagedServices/registrationDefinitions/9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9",
            RegistrationDefinitionFingerprint = "recorded-fingerprint",
            State = state,
            CreatedAt = Now.AddMinutes(-1),
            UpdatedAt = Now.AddMinutes(-1)
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeAuthorityObserver : IAzureLighthouseAuthorityObserver
    {
        public AzureLighthouseAuthorityObservationResult Result { get; init; } = new(true, "ok", "ok", "observed-fingerprint");
        public bool ThrowCancellation { get; init; }
        public int CallCount { get; private set; }
        public AzureLighthouseAuthorityObservationRequest? LastRequest { get; private set; }

        public Task<AzureLighthouseAuthorityObservationResult> ObserveAsync(
            AzureLighthouseAuthorityObservationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            if (ThrowCancellation)
                throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeBindStore : IOrganizationAzureSubscriptionBindStore
    {
        public List<OrganizationAzureSubscriptionBind> Binds { get; init; } = [];
        public List<OrganizationAzureSubscriptionBindTransition> Transitions { get; } = [];

        public Task<OrganizationAzureSubscriptionBind?> GetAsync(Guid organizationId, Guid bindId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Binds.SingleOrDefault(bind => bind.OrganizationId == organizationId && bind.Id == bindId));

        public Task<OrganizationAzureSubscriptionBind?> GetLatestAsync(Guid organizationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Binds.Where(bind => bind.OrganizationId == organizationId).OrderByDescending(bind => bind.CreatedAt).ThenByDescending(bind => bind.Id).FirstOrDefault());

        public Task<OrganizationAzureSubscriptionBindResult> CreatePendingAsync(OrganizationAzureSubscriptionBind bind, CancellationToken cancellationToken = default)
        {
            bind.Id = Guid.NewGuid();
            Binds.Add(bind);
            return Task.FromResult(OrganizationAzureSubscriptionBindResult.Success(bind));
        }

        public Task<OrganizationAzureSubscriptionBindResult> TransitionAsync(OrganizationAzureSubscriptionBindTransition transition, CancellationToken cancellationToken = default)
        {
            var bind = Binds.SingleOrDefault(candidate => candidate.OrganizationId == transition.OrganizationId && candidate.Id == transition.BindId);
            if (bind is null)
                return Task.FromResult(OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.BindNotFound));
            if (bind.State != transition.ExpectedState ||
                (transition.ExpectedUpdatedAt.HasValue && bind.UpdatedAt != transition.ExpectedUpdatedAt.Value) ||
                !OrganizationAzureSubscriptionBindLifecycle.CanTransition(bind.State, transition.NewState))
                return Task.FromResult(OrganizationAzureSubscriptionBindResult.Denied(OrganizationAzureSubscriptionBindFailure.InvalidState));
            bind.State = transition.NewState;
            bind.UpdatedAt = transition.ChangedAt;
            bind.VerifiedAt = transition.VerifiedAt ?? bind.VerifiedAt;
            bind.LastPreflightCode = transition.LastPreflightCode ?? bind.LastPreflightCode;
            bind.RegistrationDefinitionFingerprint = transition.RegistrationDefinitionFingerprint ?? bind.RegistrationDefinitionFingerprint;
            bind.UnbindReason = transition.UnbindReason ?? bind.UnbindReason;
            Transitions.Add(transition);
            return Task.FromResult(OrganizationAzureSubscriptionBindResult.Success(bind));
        }
    }
}
