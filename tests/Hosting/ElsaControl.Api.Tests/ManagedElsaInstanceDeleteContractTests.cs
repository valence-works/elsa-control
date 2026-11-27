using System.Net;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ElsaControl.Api.OrganizationBilling;
using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Abstractions.Instances;
using ElsaControl.Deployment.Core.Provisioning;
using ElsaControl.Deployment.Core.Workspace;
using ElsaControl.PackageCatalog.Core.Accounts;
using ElsaControl.PackageCatalog.Persistence.EntityFrameworkCore;
using ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Pins Hub-facing managed-instance delete JSON so an accidental contract
/// change fails CI. <c>elsa-hub</c> tests for #219, #217 and #230 must load
/// the same files.
/// </summary>
public sealed class ManagedElsaInstanceDeleteContractTests : IClassFixture<ManagedElsaInstanceDeleteContractTests.Fixture>
{
    public const string RelativeDirectory = "contracts/managed-instance-delete";
    public const string CatalogFileName = "catalog.json";
    public const string RegenerateEnvironmentVariable = "ELSA_CONTROL_REGENERATE_MANAGED_INSTANCE_DELETE_CONTRACTS";

    private const string CanonicalGuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string CanonicalTimestamp = "2026-01-15T12:00:00+00:00";
    private const string CanonicalTrace = "trace";

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TimestampPattern = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly string[] SecretMarkers =
    [
        "Exception",
        "stack trace",
        "sk_test",
        "sk_live",
        "whsec_",
        "provider.private",
        "password",
        "SubscriptionId",
        "client_secret"
    ];

    private readonly Fixture _fixture;

    public ManagedElsaInstanceDeleteContractTests(Fixture fixture) => _fixture = fixture;

    public static TheoryData<string, int, string?> CatalogScenarios()
    {
        var data = new TheoryData<string, int, string?>();
        foreach (var entry in CatalogEntries)
            data.Add(entry.File, entry.HttpStatus, entry.Code);
        return data;
    }

    private static readonly CatalogEntry[] CatalogEntries =
    [
        new("confirmation-success.json", 200, null,
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete-confirmations"),
        new("delete-accepted.json", 202, null,
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete"),
        new("delete-permission-required.json", 403, "instance.delete-permission-required",
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete-confirmations"),
        new("delete-confirmation-required.json", 400, "instance.delete-confirmation-required",
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete"),
        new("delete-confirmation-invalid.json", 409, "instance.delete-confirmation-invalid",
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete"),
        new("operation-active.json", 409, "instance.operation-active",
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete"),
        new("invalid-state.json", 409, "instance.invalid-state",
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/operations"),
        new("changed-since-read.json", 412, ManagedElsaInstanceEndpoints.ChangedSinceReadCode,
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete"),
        new("rate-limit-exceeded.json", 429, ManagedElsaInstanceLifecycleRateLimitDefaults.ExceededCode,
            "POST /api/workspaces/{workspaceId}/instances/{instanceId}/restart"),
        new("overview-deleting.json", 200, ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode,
            "GET /api/workspaces/{workspaceId}/instances/{instanceId}/overview")
    ];

    [Fact]
    public void Catalog_lists_every_golden_hub_must_load()
    {
        AssertCommitted(CatalogFileName, CatalogJson());
    }

    [Fact]
    public async Task Confirmation_success_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-confirmation");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-confirmation");

        using var response = await owner.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("confirmationId", out var confirmationId));
        Assert.False(parsed.RootElement.TryGetProperty("id", out _));
        Assert.True(Guid.TryParse(confirmationId.GetString(), out _));
        Assert.Equal(HostedBillingCopy.EngineDeletion.Code, parsed.RootElement.GetProperty("billingNotice").GetProperty("code").GetString());
        AssertCommitted("confirmation-success.json", json);
    }

    [Fact]
    public async Task Accepted_delete_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-accepted");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-accepted");
        var confirmationId = await ConfirmAsync(owner, workspaceId, created.Instance.InstanceId);

        using var response = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-accepted", confirmationId);
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("operationId", out var operationId));
        Assert.True(Guid.TryParse(operationId.GetString(), out var parsedOperationId) && parsedOperationId != Guid.Empty);
        AssertCommitted("delete-accepted.json", json);
    }

    [Fact]
    public async Task Delete_permission_required_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-permission-owner");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-permission");
        // SourceAdmin can mutate the workspace but does not get instances.delete
        // unless granted. A Reader is refused by RequireWorkspaceAccess before
        // the handler's instance.delete-permission-required problem.
        await app.AddWorkspaceMemberAsync(workspaceId, "delete-contract-permission-source-admin", WorkspaceRole.SourceAdmin);
        using var sourceAdmin = app.CreateTrustedWorkspaceClient("delete-contract-permission-source-admin");

        using var confirmation = await sourceAdmin.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/delete-confirmations",
            content: null);
        using var deletion = await SendDeleteAsync(
            sourceAdmin, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-permission", Guid.NewGuid());
        var json = await ReadBodyAsync(confirmation);
        var deleteJson = await ReadBodyAsync(deletion);

        Assert.Equal(HttpStatusCode.Forbidden, confirmation.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
        AssertProblem(json, 403, "instance.delete-permission-required");
        Assert.Equal(NormalizeVolatile(json), NormalizeVolatile(deleteJson));
        AssertCommitted("delete-permission-required.json", json);
    }

    [Fact]
    public async Task Delete_confirmation_required_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-required");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-required");

        using var response = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-required", Guid.Empty);
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertProblem(json, 400, "instance.delete-confirmation-required");
        AssertCommitted("delete-confirmation-required.json", json);
    }

    [Fact]
    public async Task Delete_confirmation_invalid_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-invalid");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-invalid");

        using var response = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-invalid", Guid.NewGuid());
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertProblem(json, 409, "instance.delete-confirmation-invalid");
        AssertCommitted("delete-confirmation-invalid.json", json);
    }

    [Fact]
    public async Task Operation_active_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-active");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-active");
        var firstConfirmation = await ConfirmAsync(owner, workspaceId, created.Instance.InstanceId);
        using var first = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-active-first", firstConfirmation);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var acceptedEtag = first.Headers.ETag?.ToString()
            ?? throw new InvalidOperationException("Delete acceptance ETag is missing.");
        var secondConfirmation = await ConfirmAsync(owner, workspaceId, created.Instance.InstanceId);

        using var response = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, acceptedEtag,
            "delete-contract-active-second", secondConfirmation);
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertProblem(json, 409, "instance.operation-active");
        AssertCommitted("operation-active.json", json);
    }

    [Fact]
    public async Task Invalid_state_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-invalid-state");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-invalid-state");

        using var response = await owner.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/operations",
            created.Instance.ETag,
            "delete-contract-invalid-state",
            new ManagedElsaInstanceOperationRequest(ElsaInstanceOperationAction.Start)));
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertProblem(json, 409, "instance.invalid-state");
        AssertCommitted("invalid-state.json", json);
    }

    [Fact]
    public async Task Changed_since_read_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-changed");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-changed");
        using var rename = await owner.SendAsync(Mutation(
            HttpMethod.Patch,
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}",
            created.Instance.ETag,
            "delete-contract-changed-rename",
            new ManagedElsaInstancePatchRequest(Name: "Renamed before delete")));
        Assert.Equal(HttpStatusCode.Accepted, rename.StatusCode);
        var confirmationId = await ConfirmAsync(owner, workspaceId, created.Instance.InstanceId);

        using var response = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-changed", confirmationId);
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        AssertProblem(json, 412, ManagedElsaInstanceEndpoints.ChangedSinceReadCode);
        Assert.Contains(ManagedElsaInstanceEndpoints.ChangedSinceReadDetail, json, StringComparison.Ordinal);
        AssertCommitted("changed-since-read.json", json);
    }

    [Fact]
    public async Task Rate_limit_exceeded_matches_the_committed_golden_file()
    {
        await using var app = new ControlApiTestApplication(new Dictionary<string, string?>
        {
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIp:PermitLimit"] = "20",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIp:Window"] = "00:01:00",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:BffPerIp:PermitLimit"] = "20",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:BffPerIp:Window"] = "00:01:00",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIdentity:PermitLimit"] = "1",
            [$"{ManagedElsaInstanceLifecycleRateLimitOptions.ConfigurationSection}:PerIdentity:Window"] = "00:01:00"
        });
        await app.SeedAsync(_ => Task.CompletedTask);
        using var client = app.CreateControlIdentityClient(subject: "delete-contract-rate-limit");
        client.DefaultRequestHeaders.TryAddWithoutValidation(ControlApiTestApplication.TestRemoteIpHeader, "203.0.113.80");
        var workspaceId = Guid.Parse(CanonicalGuid);
        var instanceId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        using var allowed = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/restart", new { });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, allowed.StatusCode);

        using var response = await client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/restart", new { });
        var json = await ReadBodyAsync(response);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        AssertProblem(json, 429, ManagedElsaInstanceLifecycleRateLimitDefaults.ExceededCode);
        AssertCommitted("rate-limit-exceeded.json", json);
    }

    [Fact]
    public async Task Overview_deleting_matches_the_committed_golden_file()
    {
        var app = await PrepareApplicationAsync();
        var owner = app.CreateTrustedWorkspaceClient("delete-contract-deleting");
        var (workspaceId, created) = await CreateReadyInstanceAsync(app, owner, "delete-contract-deleting");
        var confirmationId = await ConfirmAsync(owner, workspaceId, created.Instance.InstanceId);
        using var deletion = await SendDeleteAsync(
            owner, workspaceId, created.Instance.InstanceId, created.Instance.ETag,
            "delete-contract-deleting", confirmationId);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        var accepted = await deletion.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteAcceptedResponse>();
        await MarkOperationSucceededAsync(app, accepted!.OperationId);
        await SetDeletedAsync(app, created.Instance.InstanceId);

        using var response = await owner.GetAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{created.Instance.InstanceId:D}/overview");
        var json = await ReadBodyAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var parsed = JsonDocument.Parse(json);
        var delete = parsed.RootElement.GetProperty("allowedActions").GetProperty("delete");
        Assert.False(delete.GetProperty("allowed").GetBoolean());
        Assert.Equal(
            ManagedElsaInstanceOverviewEndpoints.InstanceDeletingCode,
            delete.GetProperty("reasonCode").GetString());

        var slice = new JsonObject
        {
            ["allowedActions"] = new JsonObject
            {
                ["delete"] = JsonNode.Parse(delete.GetRawText())
            },
            ["activeOperation"] = parsed.RootElement.TryGetProperty("activeOperation", out var active)
                ? JsonNode.Parse(active.GetRawText())
                : null,
            ["lastOperation"] = parsed.RootElement.TryGetProperty("lastOperation", out var last)
                ? JsonNode.Parse(last.GetRawText())
                : null
        };
        AssertCommitted("overview-deleting.json", slice.ToJsonString(JsonOptions));
    }

    [Fact]
    public void Golden_files_do_not_embed_exception_text_provider_identifiers_or_secrets()
    {
        foreach (var file in Directory.GetFiles(CommittedDirectory(), "*.json"))
        {
            var json = File.ReadAllText(file);
            AssertSafe(json, file);
        }
    }

    [Theory]
    [MemberData(nameof(CatalogScenarios))]
    public void Catalog_entry_has_a_committed_body_file(string fileName, int httpStatus, string? code)
    {
        var file = new FileInfo(Path.Combine(CommittedDirectory(), fileName));
        if (!file.Exists && IsTruthy(Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable)))
            return;

        Assert.True(file.Exists, $"Committed golden file is missing at {RelativeDirectory}/{fileName}.");
        Assert.InRange(httpStatus, 200, 429);
        if (code is not null)
            Assert.Contains($"\"{code}\"", File.ReadAllText(file.FullName), StringComparison.Ordinal);
    }

    private async Task<ControlApiTestApplication> PrepareApplicationAsync()
    {
        _fixture.ReleaseCatalog.SetEntries(
            [ReleaseCatalogEntry("valence-runtime", "3.8", "3.8.4", "stable", "combined", "supported", "paid")]);
        await _fixture.Application.SeedAsync(_ => Task.CompletedTask);
        return _fixture.Application;
    }

    private static async Task<(Guid WorkspaceId, ManagedElsaInstanceAcceptedResponse Created)> CreateReadyInstanceAsync(
        ControlApiTestApplication app,
        HttpClient client,
        string slug)
    {
        var workspaceId = await client.GetDefaultWorkspaceIdAsync();
        await EnableManagedHostingAsync(app, workspaceId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workspaces/{workspaceId:D}/instances")
        {
            Content = JsonContent.Create(
                new ManagedElsaInstanceCreateRequest("Claims runtime", slug, Intent()),
                options: ControlApiTestApplication.JsonOptions)
        };
        request.Headers.Add("Idempotency-Key", $"create-{slug}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = (await response.Content.ReadControlJsonAsync<ManagedElsaInstanceAcceptedResponse>())!;
        await MarkOperationSucceededAsync(app, created.Operation.Id);
        await MarkInstanceReadyAsync(app, created.Instance.InstanceId);
        return (workspaceId, created);
    }

    private static async Task<Guid> ConfirmAsync(HttpClient client, Guid workspaceId, Guid instanceId)
    {
        using var response = await client.PostAsync(
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete-confirmations",
            content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadControlJsonAsync<ManagedElsaInstanceDeleteConfirmationResponse>();
        return body!.ConfirmationId;
    }

    private static Task<HttpResponseMessage> SendDeleteAsync(
        HttpClient client,
        Guid workspaceId,
        Guid instanceId,
        string etag,
        string idempotencyKey,
        Guid confirmationId) =>
        client.SendAsync(Mutation(
            HttpMethod.Post,
            $"/api/workspaces/{workspaceId:D}/instances/{instanceId:D}/delete",
            etag,
            idempotencyKey,
            new ManagedElsaInstanceDeleteRequest(confirmationId)));

    private static HttpRequestMessage Mutation(
        HttpMethod method,
        string path,
        string? etag,
        string? idempotencyKey,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: ControlApiTestApplication.JsonOptions);
        if (etag is not null)
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task EnableManagedHostingAsync(ControlApiTestApplication app, Guid workspaceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var organizationId = await db.Workspaces.Where(x => x.Id == workspaceId)
            .Select(x => x.OrganizationId).SingleAsync();
        if (!await db.OrganizationEntitlementSnapshots.AnyAsync(x => x.OrganizationId == organizationId))
        {
            db.OrganizationEntitlementSnapshots.Add(new OrganizationEntitlementSnapshot
            {
                OrganizationId = organizationId,
                ManagedHostingEnabled = true,
                MaxSources = 5,
                MaxWorkspaces = 5,
                MaxInstances = int.MaxValue,
                SubscriptionState = OrganizationSubscriptionState.Active
            });
            await db.SaveChangesAsync();
        }
    }

    private static async Task MarkOperationSucceededAsync(ControlApiTestApplication app, Guid operationId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var completedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE ElsaInstanceOperations SET State = {ElsaInstanceOperationState.Succeeded.ToString()}, CompletedAt = {completedAtTicks} WHERE Id = {operationId}");
    }

    private static async Task MarkInstanceReadyAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET DesiredLifecycle = {ElsaDesiredLifecycle.Running.ToString()},
                ObservedLifecycle = {ElsaObservedLifecycle.Ready.ToString()},
                Health = {ElsaInstanceHealth.Healthy.ToString()}
            WHERE Id = {instanceId}
            """);
    }

    private static async Task SetDeletedAsync(ControlApiTestApplication app, Guid instanceId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var deletedAt = DateTimeOffset.UtcNow.UtcTicks;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ElsaInstances
            SET DesiredLifecycle = {ElsaDesiredLifecycle.Deleting.ToString()},
                ObservedLifecycle = {ElsaObservedLifecycle.Deleted.ToString()},
                DeletedAt = {deletedAt}
            WHERE Id = {instanceId}
            """);
    }

    private static ElsaInstanceIntent Intent() => new(
        new ElsaReleaseIntent("valence-runtime", "3.8", channel: "stable"),
        new ElsaApplicationIntent("combined", "starter",
            new Dictionary<string, ElsaFeatureOverride> { ["replicas"] = ElsaFeatureOverride.FromNumber(3) },
            "approved"),
        new ElsaPlacementIntent("managed", "westeurope", "dedicated", "standard-small", "public", "managed"));

    private static GovernedReleaseCatalogEntry ReleaseCatalogEntry(
        string distributionId,
        string releaseLine,
        string version,
        string channel,
        string topologyId,
        string catalogLifecycle,
        string registryClass,
        char digestMarker = 'a') => new(
        "1.0",
        $"oci://registry.example.test/releases/manifest@sha256:{new string(digestMarker, 64)}",
        $"sha256:{new string(digestMarker, 64)}",
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "https://evidence.example.test/signatures/manifest",
        "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
        registryClass,
        new GovernedReleaseDistribution(
            distributionId, "3", releaseLine, version, channel, "supported", null,
            "https://github.com/valence-works/elsa", "0123456789abcdef", "run-1"),
        new GovernedReleaseTopology(topologyId, "1.0", ["server"], [], [], [], []),
        catalogLifecycle,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response) =>
        await response.Content.ReadAsStringAsync();

    private static void AssertProblem(string json, int status, string code)
    {
        using var body = JsonDocument.Parse(json);
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
    }

    private static void AssertSafe(string json, string file)
    {
        foreach (var marker in SecretMarkers)
            Assert.False(
                json.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"{Path.GetFileName(file)} must not contain {marker}.");
    }

    private static string CatalogJson()
    {
        var catalog = new
        {
            hubMustLoad = true,
            goldens = CatalogEntries.Select(entry => new
            {
                file = entry.File,
                httpStatus = entry.HttpStatus,
                code = entry.Code,
                endpoint = entry.Endpoint
            })
        };
        return CanonicalJson(JsonSerializer.Serialize(catalog, JsonOptions));
    }

    private static void AssertCommitted(string fileName, string json)
    {
        AssertSafe(json, fileName);
        var actual = CanonicalJson(NormalizeVolatile(json));
        var file = new FileInfo(Path.Combine(CommittedDirectory(), fileName));
        var regenerate = IsTruthy(Environment.GetEnvironmentVariable(RegenerateEnvironmentVariable));
        if (regenerate && (IsTruthy(Environment.GetEnvironmentVariable("CI")) || IsTruthy(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"))))
        {
            Assert.Fail(
                "Managed-instance delete contract regeneration is disabled when CI or GITHUB_ACTIONS is set. " +
                $"Unset {RegenerateEnvironmentVariable}.");
        }

        if (regenerate)
        {
            Directory.CreateDirectory(file.DirectoryName!);
            File.WriteAllText(file.FullName, actual + Environment.NewLine);
        }

        Assert.True(file.Exists, $"Committed golden file is missing at {RelativeDirectory}/{fileName}.");
        Assert.Equal(CanonicalJson(File.ReadAllText(file.FullName)), actual);
    }

    private static string NormalizeVolatile(string json)
    {
        json = GuidPattern.Replace(json, CanonicalGuid);
        json = TimestampPattern.Replace(json, CanonicalTimestamp);
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON was empty.");
        ReplaceNamed(node, "traceId", CanonicalTrace);
        ReplaceNamed(node, "correlationId", CanonicalTrace);
        return node.ToJsonString(JsonOptions);
    }

    private static void ReplaceNamed(JsonNode node, string name, string value)
    {
        if (node is JsonObject obj)
        {
            if (obj.ContainsKey(name))
                obj[name] = value;
            foreach (var property in obj)
            {
                if (property.Value is not null)
                    ReplaceNamed(property.Value, name, value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                    ReplaceNamed(item, name, value);
            }
        }
    }

    private static string CanonicalJson(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON was empty.");
        return node.ToJsonString(JsonOptions);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private static string CommittedDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ElsaControl.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new InvalidOperationException("Could not locate repository root.");
        return Path.Combine(directory.FullName, RelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    public sealed class Fixture : IAsyncLifetime
    {
        public Fixture()
        {
            ReleaseCatalog = new MutableReleaseCatalogStore();
            Application = new ControlApiTestApplication(configureServices: services =>
            {
                services.AddSingleton<IEngineProvisioningModule, TestProvisioningModule>();
                services.RemoveAll<IGovernedReleaseCatalogStore>();
                services.AddSingleton<IGovernedReleaseCatalogStore>(ReleaseCatalog);
            });
        }

        internal ControlApiTestApplication Application { get; }

        internal MutableReleaseCatalogStore ReleaseCatalog { get; }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync() => await ((IAsyncDisposable)Application).DisposeAsync();
    }

    internal sealed class MutableReleaseCatalogStore : IGovernedReleaseCatalogStore
    {
        private IReadOnlyList<GovernedReleaseCatalogEntry> _entries = [];

        public void SetEntries(IReadOnlyList<GovernedReleaseCatalogEntry> entries) => _entries = entries;

        public Task<GovernedReleaseCatalogWriteResult> StoreAsync(
            IReadOnlyList<GovernedReleaseCatalogEntry> entries,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GovernedReleaseCatalogEntry>> QueryAsync(
            GovernedReleaseCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GovernedReleaseCatalogEntry>>(_entries
                .Where(entry => query.DistributionId is null ||
                                string.Equals(entry.Distribution.Id, query.DistributionId, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.ReleaseLine is null ||
                                string.Equals(entry.Distribution.ReleaseLine, query.ReleaseLine, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.ReleaseVersion is null ||
                                string.Equals(entry.Distribution.ReleaseVersion, query.ReleaseVersion, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.Channel is null ||
                                string.Equals(entry.Distribution.Channel, query.Channel, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.CatalogLifecycle is null ||
                                string.Equals(entry.CatalogLifecycle, query.CatalogLifecycle, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.RegistryClass is null ||
                                string.Equals(entry.RegistryClass, query.RegistryClass, StringComparison.OrdinalIgnoreCase))
                .Where(entry => query.TopologyId is null ||
                                string.Equals(entry.Topology.Id, query.TopologyId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    private sealed class TestProvisioningModule : IEngineProvisioningModule
    {
        public string Id => "test";
        public string DisplayName => "Test provider";
    }

    private sealed record CatalogEntry(string File, int HttpStatus, string? Code, string Endpoint);
}
