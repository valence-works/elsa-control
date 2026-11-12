using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CShells.Lifecycle;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ElsaControl.SharedEngine.Spike.Tests;

[Collection("ShardHost")]
public sealed class IsolationTests
{
    private readonly ShardHostFixture _fixture;

    public IsolationTests(ShardHostFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Unknown_host_reaches_empty_Default_shell()
    {
        var defaultShell = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.DefaultShell);
        var settings = defaultShell.ServiceProvider.GetRequiredService<CShells.ShellSettings>();
        Assert.DoesNotContain(settings.EnabledFeatures, name => name.Contains("WorkflowsApi", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(settings.EnabledFeatures, name => name.Contains("FastEndpoints", StringComparison.OrdinalIgnoreCase));
        Assert.Null(defaultShell.ServiceProvider.GetService<IWorkflowDefinitionStore>());

        using var client = TenantClient.Create(_fixture.Factory, SpikeConstants.UnknownHost);
        var payload = await TenantClient.GetWhoamiAsync(client);
        Assert.Equal(SpikeConstants.DefaultShell, payload.GetProperty("shell").GetString());

        using var workflows = await client.GetAsync("/elsa/api/workflow-definitions");
        Assert.NotEqual(HttpStatusCode.OK, workflows.StatusCode);
    }

    [Fact]
    public async Task Tenant_hosts_resolve_to_their_own_shells()
    {
        using var clientA = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantAHost);
        using var clientB = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost);
        var a = await TenantClient.GetWhoamiAsync(clientA);
        var b = await TenantClient.GetWhoamiAsync(clientB);
        Assert.Equal(SpikeConstants.TenantA, a.GetProperty("shell").GetString());
        Assert.Equal(SpikeConstants.TenantB, b.GetProperty("shell").GetString());
    }

    [Fact]
    public async Task Api_list_and_get_do_not_cross_tenants()
    {
        var shellA = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantA);
        var shellB = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantB);
        await TenantClient.PublishNamedAsync(shellA, SpikeConstants.SharedDefinitionId, "from-a");
        await TenantClient.PublishNamedAsync(shellB, SpikeConstants.SharedDefinitionId, "from-b");

        using var clientA = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantAHost, SpikeConstants.TenantAApiKey);
        using var clientB = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost, SpikeConstants.TenantBApiKey);

        var listedA = await ListDefinitionIdsAsync(clientA);
        var listedB = await ListDefinitionIdsAsync(clientB);
        Assert.Contains(SpikeConstants.SharedDefinitionId, listedA);
        Assert.Contains(SpikeConstants.SharedDefinitionId, listedB);

        using var crossGet = await clientB.GetAsync($"/elsa/api/workflow-definitions/{SpikeConstants.SharedDefinitionId}");
        Assert.True(crossGet.IsSuccessStatusCode);
        var body = await crossGet.Content.ReadFromJsonAsync<JsonElement>();
        var name = ReadName(body);
        Assert.Equal("from-b", name);

        using var writeAsBOnA = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantAHost, SpikeConstants.TenantBApiKey);
        using var forbidden = await writeAsBOnA.GetAsync("/elsa/api/workflow-definitions");
        Assert.Equal(HttpStatusCode.Unauthorized, forbidden.StatusCode);

        await using var scopeA = shellA.BeginScope();
        var storeA = scopeA.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
        var fromA = await storeA.FindManyAsync(new WorkflowDefinitionFilter { DefinitionId = SpikeConstants.SharedDefinitionId });
        Assert.All(fromA, definition => Assert.Equal("from-a", definition.Name));
    }

    [Fact]
    public async Task Http_endpoint_is_isolated_by_host()
    {
        var shellA = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantA);
        var shellB = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantB);
        await TenantClient.PublishHttpHelloAsync(shellA, "http-hello", SpikeConstants.HttpHelloPath, "tenant-a");
        await TenantClient.PublishHttpHelloAsync(shellB, "http-hello", SpikeConstants.HttpHelloPath, "tenant-b");
        var triggerNamesA = await ListTriggerDefinitionIdsAsync(shellA);
        Assert.Contains("http-hello", triggerNamesA);

        using var clientA = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantAHost);
        using var clientB = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost);
        using var unknown = TenantClient.Create(_fixture.Factory, SpikeConstants.UnknownHost);

        using var rawA = await clientA.GetAsync($"/workflows/{SpikeConstants.HttpHelloPath}");
        var responseA = await rawA.Content.ReadAsStringAsync();
        Assert.True(rawA.IsSuccessStatusCode, $"tenant-a HTTP {(int)rawA.StatusCode}: {responseA}");
        using var rawB = await clientB.GetAsync($"/workflows/{SpikeConstants.HttpHelloPath}");
        var responseB = await rawB.Content.ReadAsStringAsync();
        Assert.True(rawB.IsSuccessStatusCode, $"tenant-b HTTP {(int)rawB.StatusCode}: {responseB}");
        using var unknownResponse = await unknown.GetAsync($"/workflows/{SpikeConstants.HttpHelloPath}");

        Assert.Contains("tenant-a", responseA);
        Assert.Contains("tenant-b", responseB);
        Assert.DoesNotContain("tenant-a", responseB);
        Assert.DoesNotContain("tenant-b", responseA);
        Assert.NotEqual(HttpStatusCode.OK, unknownResponse.StatusCode);
    }

    [Fact]
    public async Task Bookmark_resume_does_not_cross_tenants()
    {
        var shellA = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantA);
        var shellB = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantB);
        await TenantClient.PublishBookmarkWorkflowAsync(shellA, SpikeConstants.BookmarkDefinitionId);
        await TenantClient.PublishBookmarkWorkflowAsync(shellB, SpikeConstants.BookmarkDefinitionId);

        using var clientA = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantAHost);
        using var clientB = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost);

        using var startA = await clientA.GetAsync($"/workflows/{SpikeConstants.HttpStartPath}");
        var startABody = await startA.Content.ReadAsStringAsync();
        Assert.True(startA.IsSuccessStatusCode, $"start {(int)startA.StatusCode} {startA.ReasonPhrase}: {startABody}");

        using var resumeOnB = await clientB.GetAsync($"/workflows/{SpikeConstants.HttpResumePath}");
        var resumeOnBBody = resumeOnB.IsSuccessStatusCode ? await resumeOnB.Content.ReadAsStringAsync() : string.Empty;
        Assert.DoesNotContain("tenant-a-resumed", resumeOnBBody);

        using var resumeOnA = await clientA.GetAsync($"/workflows/{SpikeConstants.HttpResumePath}");
        var resumeOnABody = await resumeOnA.Content.ReadAsStringAsync();
        Assert.Contains($"{SpikeConstants.TenantA}-resumed", resumeOnABody);

        await using var scopeB = shellB.BeginScope();
        var bookmarksB = await scopeB.ServiceProvider.GetRequiredService<IBookmarkStore>().FindManyAsync(new BookmarkFilter());
        Assert.DoesNotContain(bookmarksB, bookmark => string.Equals(bookmark.Name, "Elsa.HttpEndpoint", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(bookmark.WorkflowInstanceId));
    }

    [Fact]
    public async Task Timers_are_stored_per_shell()
    {
        var shellA = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantA);
        var shellB = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantB);
        await TenantClient.PublishTimerWorkflowAsync(shellA, SpikeConstants.TimerDefinitionId, TimeSpan.FromHours(1));
        await TenantClient.PublishTimerWorkflowAsync(shellB, "timer-b-only", TimeSpan.FromHours(1));

        var triggersA = await ListTriggerDefinitionIdsAsync(shellA);
        var triggersB = await ListTriggerDefinitionIdsAsync(shellB);
        Assert.Contains(SpikeConstants.TimerDefinitionId, triggersA);
        Assert.DoesNotContain(SpikeConstants.TimerDefinitionId, triggersB);
        Assert.Contains("timer-b-only", triggersB);
        Assert.DoesNotContain("timer-b-only", triggersA);
    }

    [Fact]
    public async Task Studio_session_tokens_and_api_keys_do_not_cross_tenants()
    {
        using var loginA = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantAHost);
        using var loginResponse = await loginA.PostAsJsonAsync("/elsa/api/identity/login", new
        {
            username = SpikeConstants.AdminUserName,
            password = SpikeConstants.AdminPassword
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = loginBody.GetProperty("accessToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));

        using var clientBWithAToken = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost);
        clientBWithAToken.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var stolenSession = await clientBWithAToken.GetAsync("/elsa/api/workflow-definitions");
        Assert.Equal(HttpStatusCode.Unauthorized, stolenSession.StatusCode);

        using var clientBWithAKey = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost, SpikeConstants.TenantAApiKey);
        using var stolenKey = await clientBWithAKey.GetAsync("/elsa/api/workflow-definitions");
        Assert.Equal(HttpStatusCode.Unauthorized, stolenKey.StatusCode);

        using var clientB = TenantClient.Create(_fixture.Factory, SpikeConstants.TenantBHost, SpikeConstants.TenantBApiKey);
        using var ownKey = await clientB.GetAsync("/elsa/api/workflow-definitions");
        Assert.Equal(HttpStatusCode.OK, ownKey.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_modules_are_not_enabled_on_shared_engines()
    {
        var shell = await TenantClient.ActivateAsync(_fixture.Factory, SpikeConstants.TenantA);
        var featureNames = shell.ServiceProvider.GetServices<CShells.Features.IShellFeature>()
            .Select(feature => feature.GetType().Name)
            .ToArray();
        Assert.DoesNotContain(featureNames, name => name.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(featureNames, name => name.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(featureNames, name => name.Contains("ConsoleLog", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(featureNames, name => name.Contains("ProtoActor", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<HashSet<string>> ListDefinitionIdsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/elsa/api/workflow-definitions");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var items = json.TryGetProperty("items", out var itemsElement) ? itemsElement : json;
        return items.EnumerateArray()
            .Select(item => item.TryGetProperty("definitionId", out var id) ? id.GetString() : item.GetProperty("DefinitionId").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ReadName(JsonElement body)
    {
        if (body.TryGetProperty("name", out var name))
            return name.GetString();
        if (body.TryGetProperty("model", out var model) && model.TryGetProperty("name", out var modelName))
            return modelName.GetString();
        return body.TryGetProperty("Name", out var pascal) ? pascal.GetString() : null;
    }

    private static async Task<HashSet<string>> ListTriggerDefinitionIdsAsync(IShell shell)
    {
        await using var scope = shell.BeginScope();
        var store = scope.ServiceProvider.GetRequiredService<ITriggerStore>();
        var triggers = await store.FindManyAsync(new TriggerFilter());
        return triggers
            .Select(trigger => trigger.WorkflowDefinitionId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }
}
