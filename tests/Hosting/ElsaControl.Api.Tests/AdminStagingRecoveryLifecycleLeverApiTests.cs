using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElsaControl.Api.Admin.Organizations;
using ElsaControl.Api.Admin.Staging;
using ElsaControl.Api.Authentication;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Instances;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkspaceEntity = ElsaControl.PackageCatalog.Core.Accounts.Workspace;

namespace ElsaControl.Api.Tests;

public sealed class AdminStagingRecoveryLifecycleLeverApiTests
{
    private static readonly Guid AllowlistedInstanceId = Guid.Parse("30000000-0000-0000-0000-000000000033");
    private static readonly Guid OtherInstanceId = Guid.Parse("30000000-0000-0000-0000-000000000044");
    private static readonly Guid SmokeOwnerInstanceId = Guid.Parse("30000000-0000-0000-0000-000000000099");
    private const string BffClientId = "elsa-cloud-bff";
    private const string BffScope = CloudBffDefaults.DefaultScope;
    private const string TestSecretKey = "sk_test_harness";
    private const string LiveSecretKey = "sk_live_harness";

    [Fact]
    public async Task Flag_off_refuses_even_for_an_allowlisted_instance_and_writes_nothing()
    {
        await using var app = CreateApp(recoveryEnabled: false, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var response = await PostAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);
    }

    [Fact]
    public async Task Billing_lever_flag_only_does_not_arm_recovery()
    {
        await using var app = CreateApp(
            recoveryEnabled: false,
            billingEnabled: true,
            allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var recovery = await PostAsync(Operator(app), AllowlistedInstanceId);
        var billing = await PostBillingAsync(Operator(app), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, recovery.StatusCode);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.DisabledCode, await ProblemCodeAsync(recovery));
        Assert.Equal(HttpStatusCode.NotFound, billing.StatusCode);
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);
    }

    [Fact]
    public async Task Recovery_lever_flag_only_does_not_arm_billing()
    {
        await using var app = CreateApp(
            recoveryEnabled: true,
            billingEnabled: false,
            allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var billing = await PostBillingAsync(Operator(app), Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Forbidden, billing.StatusCode);
        Assert.Equal(StagingBillingLifecycleLeverDefaults.DisabledCode, await ProblemCodeAsync(billing));
    }

    [Fact]
    public async Task Allowlisted_instance_with_a_stripe_test_key_accepts_reconcile_and_parks_recovery_required()
    {
        await using var app = CreateApp(recoveryEnabled: true, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var response = await PostAsync(Operator(app), AllowlistedInstanceId);
        if (response.StatusCode != HttpStatusCode.OK)
            Assert.Fail($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadControlJsonAsync<StagingRecoveryLifecycleLeverResponse>();
        Assert.NotNull(body);
        Assert.Equal(AllowlistedInstanceId, body.InstanceId);
        Assert.Equal(ElsaInstanceOperationAction.Reconcile, body.Action);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, body.State);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.TransitionCode, body.Code);
        Assert.False(ElsaInstanceOperation.CanTransition(
            ElsaInstanceOperationState.Accepted,
            ElsaInstanceOperationState.RecoveryRequired));
        Assert.True(ElsaInstanceOperation.CanTransition(
            ElsaInstanceOperationState.Queued,
            ElsaInstanceOperationState.RecoveryRequired));
        await AssertFiredOnceAsync(app, AllowlistedInstanceId, body.OperationId);
    }

    [Fact]
    public async Task Flag_on_with_a_live_stripe_key_is_disabled()
    {
        await using var app = CreateApp(
            recoveryEnabled: true,
            allowlisted: AllowlistedInstanceId,
            stripeSecretKey: LiveSecretKey);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var response = await PostAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);
        Assert.DoesNotContain(LiveSecretKey, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("rk_test_x")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("sk_live_x")]
    public async Task Flag_on_without_a_stripe_test_secret_key_is_disabled(string secretKey)
    {
        await using var app = CreateApp(
            recoveryEnabled: true,
            allowlisted: AllowlistedInstanceId,
            stripeSecretKey: secretKey);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var response = await PostAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.DisabledCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);
        if (!string.IsNullOrWhiteSpace(secretKey))
            Assert.DoesNotContain(secretKey, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_allowlisted_instance_is_refused_without_writes()
    {
        await using var app = CreateApp(recoveryEnabled: true, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, OtherInstanceId);

        var response = await PostAsync(Operator(app), OtherInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.InstanceNotAllowedCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, OtherInstanceId);
    }

    [Fact]
    public async Task Smoke_owner_instance_is_refused_even_when_allowlisted()
    {
        await using var app = CreateApp(
            recoveryEnabled: true,
            allowlisted: SmokeOwnerInstanceId,
            smokeOwnerInstanceId: SmokeOwnerInstanceId);
        await SeedReadyInstanceAsync(app, SmokeOwnerInstanceId);

        var response = await PostAsync(Operator(app), SmokeOwnerInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(StagingRecoveryLifecycleLeverDefaults.InstanceNotAllowedCode, await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, SmokeOwnerInstanceId);
    }

    [Fact]
    public async Task Active_operation_returns_409_and_writes_no_second_fire()
    {
        await using var app = CreateApp(recoveryEnabled: true, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var first = await PostAsync(Operator(app), AllowlistedInstanceId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await PostAsync(Operator(app), AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("instance.operation-active", await ProblemCodeAsync(second));
        await AssertFiredOnceAsync(app, AllowlistedInstanceId);
    }

    [Fact]
    public async Task Deleted_instance_returns_409_when_the_state_machine_refuses()
    {
        await using var app = CreateApp(recoveryEnabled: true, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId, deleted: true);

        var response = await PostAsync(Operator(app), AllowlistedInstanceId);
        if (response.StatusCode != HttpStatusCode.Conflict)
            Assert.Fail($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        Assert.Equal("instance.invalid-state", await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);
    }

    [Fact]
    public async Task Recover_then_fire_again_writes_a_second_recovery_required_and_one_fired_event_each()
    {
        await using var app = CreateApp(recoveryEnabled: true, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var first = await PostAsync(Operator(app), AllowlistedInstanceId);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadControlJsonAsync<StagingRecoveryLifecycleLeverResponse>();
        Assert.NotNull(firstBody);

        await RecoverAndCompleteAsync(app, firstBody.WorkspaceId, AllowlistedInstanceId, firstBody.InstanceVersion);

        var second = await PostAsync(Operator(app), AllowlistedInstanceId);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadControlJsonAsync<StagingRecoveryLifecycleLeverResponse>();
        Assert.NotNull(secondBody);
        Assert.NotEqual(firstBody.OperationId, secondBody.OperationId);
        Assert.Equal(ElsaInstanceOperationState.RecoveryRequired, secondBody.State);

        Assert.Equal(2, await CountFiredAsync(app, AllowlistedInstanceId));
        Assert.Equal(1, await CountFiredAsync(app, AllowlistedInstanceId, firstBody.OperationId));
        Assert.Equal(1, await CountFiredAsync(app, AllowlistedInstanceId, secondBody.OperationId));
    }

    [Fact]
    public async Task Cloud_bff_caller_is_denied_and_the_route_is_not_allowlisted()
    {
        await using var app = CreateApp(
            recoveryEnabled: true,
            allowlisted: AllowlistedInstanceId,
            additionalConfiguration: new Dictionary<string, string?>
            {
                [$"{CloudBffOptions.ConfigurationSection}:Enabled"] = "true",
                [$"{CloudBffOptions.ConfigurationSection}:ClientId"] = BffClientId,
                [$"{CloudBffOptions.ConfigurationSection}:Scope"] = BffScope
            });
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);
        var bff = app.CreateControlIdentityClient(
            subject: "bff-user",
            claims: new Dictionary<string, string> { ["azp"] = BffClientId, ["scp"] = BffScope });

        var response = await PostAsync(bff, AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("cloud-bff.denied", await ProblemCodeAsync(response));
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);

        var allowed = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<CloudBffAllowedEndpointMetadata>() is not null)
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}") ?? []);
        Assert.DoesNotContain(
            "POST /api/staging/lifecycle-lever/instances/{instanceId:guid}/recovery-required",
            allowed,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task Anonymous_and_customer_callers_are_denied_without_writes()
    {
        await using var app = CreateApp(recoveryEnabled: true, allowlisted: AllowlistedInstanceId);
        await SeedReadyInstanceAsync(app, AllowlistedInstanceId);

        var anonymous = await PostAsync(app.CreateClient(), AllowlistedInstanceId);
        var customer = app.CreateClient(new() { AllowAutoRedirect = false });
        app.AddControlSessionCookie(customer, subject: "customer-user", expiresUtc: DateTimeOffset.UtcNow.AddHours(2));
        customer.DefaultRequestHeaders.Add("Origin", "http://localhost");
        var customerResponse = await PostAsync(customer, AllowlistedInstanceId);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, customerResponse.StatusCode);
        await AssertLeverDidNotFireAsync(app, AllowlistedInstanceId);
    }

    private static ControlApiTestApplication CreateApp(
        bool recoveryEnabled,
        Guid? allowlisted = null,
        Guid? smokeOwnerInstanceId = null,
        bool billingEnabled = false,
        bool stripeEnabled = true,
        string? stripeSecretKey = TestSecretKey,
        IReadOnlyDictionary<string, string?>? additionalConfiguration = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            [$"{StagingRecoveryLifecycleLeverOptions.ConfigurationSection}:Enabled"] = recoveryEnabled ? "true" : "false",
            [$"{StagingBillingLifecycleLeverOptions.ConfigurationSection}:Enabled"] = billingEnabled ? "true" : "false",
            ["Billing:Stripe:Enabled"] = stripeEnabled ? "true" : "false",
            ["Billing:Stripe:SecretKey"] = stripeSecretKey
        };
        if (allowlisted is { } instanceId)
            configuration[$"{StagingRecoveryLifecycleLeverOptions.ConfigurationSection}:AllowedInstanceIds:0"] = instanceId.ToString("D");
        if (smokeOwnerInstanceId is { } smoke)
            configuration[$"{StagingRecoveryLifecycleLeverOptions.ConfigurationSection}:SmokeOwnerInstanceId"] = smoke.ToString("D");
        if (additionalConfiguration is not null)
        {
            foreach (var (key, value) in additionalConfiguration)
                configuration[key] = value;
        }

        return new ControlApiTestApplication(configuration);
    }

    private static HttpClient Operator(ControlApiTestApplication app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationDefaults.HeaderName, "local-dev-key");
        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid instanceId) =>
        client.PostAsync($"/api/staging/lifecycle-lever/instances/{instanceId:D}/recovery-required", null);

    private static Task<HttpResponseMessage> PostBillingAsync(HttpClient client, Guid organizationId) =>
        client.PostAsJsonAsync(
            $"/api/admin/organizations/{organizationId:D}/billing/lifecycle-deadline/advance",
            new AdminBillingLifecycleDeadlineAdvanceRequest(OrganizationBillingLifecycleDeadline.GraceEndsAt),
            ControlApiTestApplication.JsonOptions);

    private static async Task SeedReadyInstanceAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        bool deleted = false)
    {
        Guid operationId = Guid.Empty;
        await app.SeedAsync(async db =>
        {
            var organization = new Organization { Name = "Recovery lever org" };
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();
            var workspace = new WorkspaceEntity { Name = "Recovery lever workspace", OrganizationId = organization.Id };
            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organization.Id,
                ManagedHostingEnabled = true,
                SubscriptionState = OrganizationSubscriptionState.Active,
                MaxInstances = int.MaxValue,
                SyncedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var created = await new ElsaInstanceLifecycleService(
                    new EfCoreElsaInstanceLifecycleStore(db, new EmptyLifecycleResolutionInputSource()))
                .CreateAsync(new ElsaInstanceCreateRequest(
                    organization.Id,
                    workspace.Id,
                    "Managed Elsa",
                    $"recovery-lever-{instanceId:N}"[..32],
                    new ElsaInstanceIntent(
                        new ElsaReleaseIntent("server-studio", "3.10", "3.10.4"),
                        new ElsaApplicationIntent("combined"),
                        new ElsaPlacementIntent(
                            "managed", "westeurope", "dedicated", "standard-small", "public", "managed")),
                    $"create-recovery-lever-{instanceId:N}",
                    instanceId));
            operationId = created.Operation.Id;
        });

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await ExecuteAsync(
            db,
            "UPDATE ElsaInstanceOperations SET State = @state WHERE Id = @id",
            ("@state", ElsaInstanceOperationState.Succeeded.ToString()),
            ("@id", operationId)));
        var observed = deleted ? ElsaObservedLifecycle.Deleted : ElsaObservedLifecycle.Ready;
        var desired = deleted ? ElsaDesiredLifecycle.Deleting : ElsaDesiredLifecycle.Running;
        Assert.Equal(1, await ExecuteAsync(
            db,
            "UPDATE ElsaInstances SET ObservedLifecycle = @observed, DesiredLifecycle = @desired, Health = @health WHERE Id = @id",
            ("@observed", observed.ToString()),
            ("@desired", desired.ToString()),
            ("@health", (deleted ? ElsaInstanceHealth.Unknown : ElsaInstanceHealth.Healthy).ToString()),
            ("@id", instanceId)));
        db.ChangeTracker.Clear();
    }

    private static async Task RecoverAndCompleteAsync(
        ControlApiTestApplication app,
        Guid workspaceId,
        Guid instanceId,
        int expectedVersion)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await ExecuteAsync(
            db,
            "UPDATE ElsaInstanceOperations SET FailureCode = @code, ReconciliationRetryEvidenceReference = @reference, ReconciliationRetryEvidenceDigest = @digest WHERE InstanceId = @instanceId AND State = @state",
            ("@code", ElsaInstanceProviderReconciliationService.RetrySafeCode),
            ("@reference", "https://evidence.example/retry/recovery-lever"),
            ("@digest", "sha256:" + new string('a', 64)),
            ("@instanceId", instanceId),
            ("@state", ElsaInstanceOperationState.RecoveryRequired.ToString())));
        db.ChangeTracker.Clear();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ElsaInstanceLifecycleService>();
        var recovered = await lifecycle.RecoverAsync(new ElsaInstanceLifecycleRequest(
            workspaceId,
            instanceId,
            expectedVersion,
            $"recover-lever-{instanceId:N}"));
        Assert.Equal(ElsaInstanceOperationState.Queued, recovered.Operation.State);
        Assert.Equal(1, await ExecuteAsync(
            db,
            "UPDATE ElsaInstanceOperations SET State = @state WHERE Id = @id",
            ("@state", ElsaInstanceOperationState.Succeeded.ToString()),
            ("@id", recovered.Operation.Id)));
        db.ChangeTracker.Clear();
    }

    private static async Task AssertFiredOnceAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Guid? operationId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(1, await ScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM ElsaInstanceOperations WHERE InstanceId = @instanceId AND Action = @action AND State = @state",
            ("@instanceId", instanceId),
            ("@action", ElsaInstanceOperationAction.Reconcile.ToString()),
            ("@state", ElsaInstanceOperationState.RecoveryRequired.ToString())));
        var persistedOperationId = await ScalarGuidAsync(
            db,
            "SELECT Id FROM ElsaInstanceOperations WHERE InstanceId = @instanceId AND Action = @action AND State = @state",
            ("@instanceId", instanceId),
            ("@action", ElsaInstanceOperationAction.Reconcile.ToString()),
            ("@state", ElsaInstanceOperationState.RecoveryRequired.ToString()));
        if (operationId is { } expected)
            Assert.Equal(expected, persistedOperationId);
        Assert.Equal(
            StagingRecoveryLifecycleLeverDefaults.TransitionCode,
            await ScalarStringAsync(
                db,
                "SELECT FailureCode FROM ElsaInstanceOperations WHERE Id = @id",
                ("@id", persistedOperationId)));
        Assert.Equal(1, await CountFiredAsync(app, instanceId, persistedOperationId));
        Assert.Equal(1, await ScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM ElsaInstanceAuditEvents WHERE OperationId = @id AND EventType = @eventType",
            ("@id", persistedOperationId),
            ("@eventType", StagingRecoveryLifecycleLeverDefaults.RecoveryRequiredEventType)));
        Assert.Equal(0, await ScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM ElsaInstanceAuditEvents WHERE InstanceId = @instanceId AND EventType LIKE '%alert%'",
            ("@instanceId", instanceId)));
    }

    private static async Task AssertLeverDidNotFireAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        Assert.Equal(0, await ScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM ElsaInstanceOperations WHERE InstanceId = @instanceId AND Action = @action",
            ("@instanceId", instanceId),
            ("@action", ElsaInstanceOperationAction.Reconcile.ToString())));
        Assert.Equal(0, await CountFiredAsync(app, instanceId));
    }

    private static async Task<int> CountFiredAsync(
        ControlApiTestApplication app,
        Guid instanceId,
        Guid? operationId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        return operationId is { } id
            ? await ScalarIntAsync(
                db,
                "SELECT COUNT(*) FROM ElsaInstanceAuditEvents WHERE InstanceId = @instanceId AND OperationId = @operationId AND EventType = @eventType",
                ("@instanceId", instanceId),
                ("@operationId", id),
                ("@eventType", StagingRecoveryLifecycleLeverDefaults.FiredEventType))
            : await ScalarIntAsync(
                db,
                "SELECT COUNT(*) FROM ElsaInstanceAuditEvents WHERE InstanceId = @instanceId AND EventType = @eventType",
                ("@instanceId", instanceId),
                ("@eventType", StagingRecoveryLifecycleLeverDefaults.FiredEventType));
    }

    private static async Task<int> ExecuteAsync(
        CatalogDbContext db,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarIntAsync(
        CatalogDbContext db,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<Guid> ScalarGuidAsync(
        CatalogDbContext db,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        var raw = await command.ExecuteScalarAsync();
        return raw is Guid guid ? guid : Guid.Parse(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture)!);
    }

    private static async Task<string?> ScalarStringAsync(
        CatalogDbContext db,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString();

    private sealed class EmptyLifecycleResolutionInputSource : IElsaInstanceLifecycleResolutionInputSource
    {
        public Task<ElsaInstanceLifecycleResolutionInput?> GetAsync(
            ElsaInstance instance,
            ElsaInstanceOperation operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ElsaInstanceLifecycleResolutionInput?>(null);
    }
}
