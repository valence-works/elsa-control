using System.Text.Json;
using ElsaControl.Deployment.Abstractions.Azure;
using ElsaControl.Deployment.Azure;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class AzureLighthouseAuthorityObserverTests
{
    private const string CustomerTenantId = "11111111-1111-1111-1111-111111111111";
    private const string SubscriptionId = "22222222-2222-2222-2222-222222222222";
    private const string ManagingTenantId = "33333333-3333-3333-3333-333333333333";
    private const string PrincipalObjectId = "44444444-4444-4444-4444-444444444444";
    private const string ClientId = "55555555-5555-5555-5555-555555555555";

    [Fact]
    public void Versioned_offer_link_is_pinned_to_an_immutable_commit()
    {
        Assert.DoesNotContain("/tree/main/", AzureLighthouseOfferIdentity.ArtifactUrl, StringComparison.Ordinal);
        Assert.Matches(
            @"^https://github\.com/valence-works/elsa-control/tree/[0-9a-f]{40}/infra/azure-lighthouse/v1$",
            AzureLighthouseOfferIdentity.ArtifactUrl);
    }

    [Fact]
    public async Task Observation_uses_the_exact_subscription_tenant_client_and_fixed_registration_identity()
    {
        var process = new FakeCommandProcess();
        var registration = AzureLighthouseOfferIdentity.RegistrationDefinitionId(SubscriptionId);

        var result = await Observer(process).ObserveAsync(Request() with { RegistrationDefinitionId = registration });

        Assert.True(result.Succeeded, result.Code + ": " + result.Message);
        Assert.Equal("azure.lighthouse.observation-succeeded", result.Code);
        Assert.NotNull(result.RegistrationDefinitionFingerprint);
        Assert.Contains(process.Calls, call => call is ["account", "show", "--subscription", SubscriptionId, ..]);
        Assert.Contains(process.Calls, call => call is ["rest", "--method", "get", "--url", ..] &&
                                               call.Contains("https://management.azure.com" + registration + "?api-version=2019-06-01"));
        var login = Assert.Single(process.Calls, call => call[0] == "login");
        Assert.Contains("--identity", login);
        Assert.Equal(ClientId, login[Array.IndexOf(login, "--client-id") + 1]);
        Assert.Equal(SubscriptionId, process.AccountSubscriptionRequested);
    }

    [Fact]
    public async Task Observation_rejects_a_registration_definition_that_is_not_the_fixed_v1_identity()
    {
        var process = new FakeCommandProcess();
        var wrongRegistration = $"/subscriptions/{SubscriptionId}/providers/Microsoft.ManagedServices/registrationDefinitions/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        var result = await Observer(process).ObserveAsync(Request() with { RegistrationDefinitionId = wrongRegistration });

        Assert.False(result.Succeeded);
        Assert.Equal("azure.lighthouse.registration-mismatch", result.Code);
        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Observation_rejects_the_expected_definition_under_a_different_assignment_name()
    {
        var process = new FakeCommandProcess
        {
            RegistrationAssignmentId = $"/subscriptions/{SubscriptionId}/providers/Microsoft.ManagedServices/registrationAssignments/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        };

        var result = await Observer(process).ObserveAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("azure.lighthouse.registration-not-found", result.Code);
    }

    [Theory]
    [InlineData("subscription")]
    [InlineData("tenant")]
    [InlineData("client")]
    public async Task Observation_rejects_an_account_observation_with_a_mismatched_identity(string mismatch)
    {
        var process = new FakeCommandProcess
        {
            AccountSubscriptionId = mismatch == "subscription" ? "66666666-6666-6666-6666-666666666666" : SubscriptionId,
            AccountTenantId = mismatch == "tenant" ? "77777777-7777-7777-7777-777777777777" : CustomerTenantId,
            AccountClientId = mismatch == "client" ? "88888888-8888-8888-8888-888888888888" : ClientId
        };

        var result = await Observer(process).ObserveAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("azure.lighthouse.subscription-mismatch", result.Code);
        Assert.DoesNotContain(process.Calls, call => call[0] == "rest");
    }

    [Fact]
    public async Task Observation_accepts_only_contributor_and_limited_user_access_administrator_with_key_vault_secrets_user()
    {
        var result = await Observer(new FakeCommandProcess()).ObserveAsync(Request());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("extra-authorization")]
    [InlineData("missing-contributor")]
    [InlineData("extra-delegated-role")]
    [InlineData("wrong-delegated-role")]
    public async Task Observation_rejects_extra_or_insufficient_delegated_authorization(string variant)
    {
        var process = new FakeCommandProcess
        {
            Authorizations = variant switch
            {
                "extra-authorization" => [Contributor(), UserAccessAdministrator(), ExtraAuthorization()],
                "missing-contributor" => [UserAccessAdministrator()],
                "extra-delegated-role" => [Contributor(), UserAccessAdministrator(AzureProviderAuthorityRoleDefinitionIds.KeyVaultSecretsUser, AzureProviderAuthorityRoleDefinitionIds.Contributor)],
                "wrong-delegated-role" => [Contributor(), UserAccessAdministrator("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")],
                _ => throw new ArgumentOutOfRangeException(nameof(variant))
            }
        };

        var result = await Observer(process).ObserveAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("azure.lighthouse.rbac-insufficient", result.Code);
    }

    [Fact]
    public async Task Observation_returns_and_validates_the_definition_fingerprint()
    {
        var process = new FakeCommandProcess();
        var observer = Observer(process);
        var first = await observer.ObserveAsync(Request());
        var fingerprint = first.RegistrationDefinitionFingerprint;
        Assert.True(first.Succeeded);

        var matching = await observer.ObserveAsync(Request(fingerprint));
        var mismatch = await observer.ObserveAsync(Request(new string('a', 64)));

        Assert.True(matching.Succeeded);
        Assert.False(mismatch.Succeeded);
        Assert.Equal("azure.lighthouse.registration-fingerprint-mismatch", mismatch.Code);
    }

    [Fact]
    public async Task Observation_fails_closed_when_the_configured_authority_identity_does_not_match_the_request()
    {
        var process = new FakeCommandProcess();
        var request = Request() with { ManagingPrincipalObjectId = "99999999-9999-9999-9999-999999999999" };

        var result = await Observer(process).ObserveAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal("azure.lighthouse.principal-mismatch", result.Code);
        Assert.Empty(process.Calls);
    }

    private static AzureLighthouseAuthorityObserver Observer(FakeCommandProcess process) =>
        new(Options(), process);

    private static AzureProviderRunnerOptions Options() => new()
    {
        AzureCliPath = "/usr/bin/az",
        AzureCliClientId = ClientId,
        LighthouseManagingTenantId = ManagingTenantId,
        SqlBootstrapObjectId = PrincipalObjectId
    };

    private static AzureLighthouseAuthorityObservationRequest Request(string? fingerprint = null) =>
        new(
            CustomerTenantId,
            SubscriptionId,
            ManagingTenantId,
            PrincipalObjectId,
            ClientId,
            AzureLighthouseOfferIdentity.RegistrationDefinitionId(SubscriptionId),
            fingerprint);

    private static AuthorizationSpec Contributor() =>
        new(PrincipalObjectId, AzureProviderAuthorityRoleDefinitionIds.Contributor, []);

    private static AuthorizationSpec UserAccessAdministrator(params string[] delegated) =>
        new(PrincipalObjectId, AzureProviderAuthorityRoleDefinitionIds.UserAccessAdministrator, delegated);

    private static AuthorizationSpec ExtraAuthorization() =>
        new(PrincipalObjectId, AzureProviderAuthorityRoleDefinitionIds.Owner, []);

    private sealed class FakeCommandProcess : IAzureCommandProcess
    {
        public List<string[]> Calls { get; } = [];
        public string AccountSubscriptionId { get; init; } = SubscriptionId;
        public string AccountTenantId { get; init; } = CustomerTenantId;
        public string AccountClientId { get; init; } = ClientId;
        public string RegistrationAssignmentId { get; init; } = AzureLighthouseOfferIdentity.RegistrationAssignmentId(SubscriptionId);
        public string? AccountSubscriptionRequested { get; private set; }
        public IReadOnlyList<AuthorizationSpec> Authorizations { get; init; } = [Contributor(), UserAccessAdministrator(AzureProviderAuthorityRoleDefinitionIds.KeyVaultSecretsUser)];

        public Task<AzureCommandProcessResult<T>> ExecuteAsync<T>(
            AzureCommandProcessRequest request,
            AzureCommandOutputProjector<T> outputProjector,
            CancellationToken cancellationToken = default)
            where T : AzureCommandSafeOutput
        {
            var arguments = request.Arguments.Select(argument => argument.Value).ToArray();
            Calls.Add(arguments);
            var output = arguments switch
            {
                ["account", "show", ..] => AccountJson(),
                ["rest", "--method", "get", "--url", var url, ..] when url.Contains("registrationAssignments", StringComparison.Ordinal) => AssignmentsJson(),
                ["rest", "--method", "get", "--url", ..] => DefinitionJson(),
                _ => "{}"
            };
            if (arguments is ["account", "show", "--subscription", var subscription, ..])
                AccountSubscriptionRequested = subscription;
            var value = outputProjector(output.AsMemory());
            return Task.FromResult(new AzureCommandProcessResult<T>(
                AzureCommandProcessStatus.Succeeded,
                AzureCommandProcessFailureKind.None,
                0,
                value,
                "azure.command.succeeded",
                "The Azure command completed successfully."));
        }

        private string AccountJson() => JsonSerializer.Serialize(new
        {
            id = AccountSubscriptionId,
            tenantId = AccountTenantId,
            name = "userAssignedIdentity",
            type = "servicePrincipal",
            identity = "MSIClient-" + AccountClientId
        });

        private string AssignmentsJson() => JsonSerializer.Serialize(new
        {
            value = new[]
            {
                new
                {
                    id = RegistrationAssignmentId,
                    properties = new { registrationDefinitionId = AzureLighthouseOfferIdentity.RegistrationDefinitionId(SubscriptionId) }
                }
            }
        });

        private string DefinitionJson() => JsonSerializer.Serialize(new
        {
            properties = new
            {
                managedByTenantId = ManagingTenantId,
                authorizations = Authorizations.Select(authorization => new
                {
                    principalId = authorization.PrincipalId,
                    roleDefinitionId = "/providers/Microsoft.Authorization/roleDefinitions/" + authorization.RoleDefinitionId,
                    delegatedRoleDefinitionIds = authorization.DelegatedRoleDefinitionIds
                        .Select(role => "/providers/Microsoft.Authorization/roleDefinitions/" + role)
                        .ToArray()
                }).ToArray()
            }
        });
    }

    private sealed record AuthorizationSpec(
        string PrincipalId,
        string RoleDefinitionId,
        IReadOnlyList<string> DelegatedRoleDefinitionIds);
}
