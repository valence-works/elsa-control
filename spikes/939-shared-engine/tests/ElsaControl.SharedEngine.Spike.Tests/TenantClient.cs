using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CShells.Lifecycle;
using Microsoft.AspNetCore.Mvc.Testing;
using Elsa.Http;
using Elsa.Scheduling.Activities;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaControl.SharedEngine.Spike.Tests;

internal static class TenantClient
{
    public static HttpClient Create(SpikeWebApplicationFactory factory, string host, string? apiKey = null)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{host}/"),
            AllowAutoRedirect = false
        });
        client.DefaultRequestHeaders.Host = host;
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
        return client;
    }

    public static async Task<IShell> ActivateAsync(SpikeWebApplicationFactory factory, string shellName)
    {
        var registry = factory.Services.GetRequiredService<IShellRegistry>();
        return await registry.GetOrActivateAsync(shellName);
    }

    public static async Task<WorkflowDefinition> PublishHttpHelloAsync(IShell shell, string definitionId, string path, string body)
    {
        return await PublishAsync(shell, definitionId, new Sequence
        {
            Activities =
            {
                new HttpEndpoint
                {
                    Path = new(path),
                    Authorize = new(false),
                    CanStartWorkflow = true
                },
                new WriteHttpResponse { Content = new(body) }
            }
        });
    }

    public static async Task<WorkflowDefinition> PublishBookmarkWorkflowAsync(IShell shell, string definitionId)
    {
        return await PublishAsync(shell, definitionId, new Sequence
        {
            Activities =
            {
                new HttpEndpoint
                {
                    Path = new(SpikeConstants.HttpStartPath),
                    Authorize = new(false),
                    CanStartWorkflow = true
                },
                new HttpEndpoint
                {
                    Path = new(SpikeConstants.HttpResumePath),
                    Authorize = new(false)
                },
                new WriteHttpResponse { Content = new($"{shell.Descriptor.Name}-resumed") }
            }
        });
    }

    public static async Task<WorkflowDefinition> PublishTimerWorkflowAsync(IShell shell, string definitionId, TimeSpan interval)
    {
        return await PublishAsync(shell, definitionId, new Sequence
        {
            Activities =
            {
                new Elsa.Scheduling.Activities.Timer(interval) { CanStartWorkflow = true },
                new WriteLine("timer-fired")
            }
        });
    }

    public static async Task<WorkflowDefinition> PublishNamedAsync(IShell shell, string definitionId, string name)
    {
        return await PublishAsync(shell, definitionId, new Sequence
        {
            Activities = { new WriteLine(name) }
        }, name);
    }

    private static async Task<WorkflowDefinition> PublishAsync(IShell shell, string definitionId, Sequence root, string? name = null)
    {
        await using var scope = shell.BeginScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionPublisher>();
        var definition = await publisher.NewAsync(root);
        definition.DefinitionId = definitionId;
        definition.Name = name ?? definitionId;
        definition = await publisher.SaveDraftAsync(definition);
        var published = await publisher.PublishAsync(definition);
        if (!published.Succeeded)
        {
            var errors = string.Join("; ", published.ValidationErrors.Select(error => error.Message));
            throw new InvalidOperationException($"Publish of '{definitionId}' in shell '{shell.Descriptor.Name}' failed: {errors}");
        }

        return definition;
    }

    public static async Task<JsonElement> GetWhoamiAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/spike/whoami");
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET /spike/whoami -> {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    public static async Task<JsonElement?> GetJsonAsync(HttpClient client, string requestUri)
    {
        using var response = await client.GetAsync(requestUri);
        if (!response.IsSuccessStatusCode)
            return null;
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
