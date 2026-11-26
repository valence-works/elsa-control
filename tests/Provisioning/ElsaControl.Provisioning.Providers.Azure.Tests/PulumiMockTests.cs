using System.Collections.Immutable;
using System.Text.Json;
using ElsaControl.Provisioning.Providers.Azure.Programs;
using Pulumi;
using Pulumi.Testing;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

[CollectionDefinition(nameof(PulumiMockTests), DisableParallelization = true)]
public sealed class PulumiMockTests
{
}

internal sealed class EmptyMocks : IMocks
{
    public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
    {
        var outputs = ImmutableDictionary.CreateBuilder<string, object>();
        outputs.AddRange(args.Inputs);
        if (args.Type is "azure-native:operationalinsights/workspace:Workspace"
            or "azure-native:operationalinsights:Workspace")
        {
            outputs["customerId"] = "workspace-customer";
        }

        if (args.Type is "azure-native:app/managedEnvironment:ManagedEnvironment"
            or "azure-native:app:ManagedEnvironment")
        {
            outputs["id"] = args.Id ?? $"{args.Name}_id";
            outputs["defaultDomain"] = "example.azurecontainerapps.io";
        }

        args.Id ??= $"{args.Name}_id";
        outputs["id"] = args.Id;
        return Task.FromResult<(string?, object)>((args.Id, outputs));
    }

    public Task<object> CallAsync(MockCallArgs args)
    {
        var token = args.Token ?? string.Empty;
        if (token.Contains("getSharedKeys", StringComparison.OrdinalIgnoreCase)
            || token.Contains("listKeys", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<object>(ImmutableDictionary<string, object>.Empty
                .Add("primarySharedKey", "not-a-real-key"));
        }

        if (token.Contains("getRegistry", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<object>(ImmutableDictionary<string, object>.Empty
                .Add("id", "registry_id")
                .Add("loginServer", "registry.example"));
        }

        return Task.FromResult<object>(ImmutableDictionary<string, object>.Empty);
    }
}

internal sealed class PlatformStackReferenceMocks : IMocks
{
    public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
    {
        var outputs = ImmutableDictionary.CreateBuilder<string, object>();
        outputs.AddRange(args.Inputs);
        if (args.Type == "pulumi:pulumi:StackReference")
        {
            outputs["outputs"] = ImmutableDictionary<string, object>.Empty
                .Add("containerEnvironmentKind", "shared-container-environment")
                .Add("environmentName", "staging")
                .Add("registryKind", "shared-registry")
                .Add("containerEnvironmentName", "aca-elsa-platform-staging")
                .Add("resourceGroupName", "rg-elsa-platform-staging")
                .Add("registryName", "runtimeimages")
                .Add("containerEnvironmentId", "env_id")
                .Add("containerEnvironmentDefaultDomain", "example.azurecontainerapps.io")
                .Add("registryId", "registry_id")
                .Add("registryLoginServer", "registry.example");
            outputs["secretOutputNames"] = ImmutableArray<string>.Empty;
        }

        if (args.Type is "azure-native:keyvault/vault:Vault" or "azure-native:keyvault:Vault")
        {
            outputs["properties"] = ImmutableDictionary<string, object>.Empty
                .Add("vaultUri", "https://vault.example/");
        }

        if (args.Type is "azure-native:operationalinsights/workspace:Workspace"
            or "azure-native:operationalinsights:Workspace")
        {
            outputs["customerId"] = "workspace-customer";
        }

        if (args.Type is "azure-native:app/managedEnvironment:ManagedEnvironment"
            or "azure-native:app:ManagedEnvironment")
        {
            outputs["defaultDomain"] = "example.azurecontainerapps.io";
        }

        if (args.Type is "azure-native:sql/server:Server" or "azure-native:sql:Server")
        {
            outputs["fullyQualifiedDomainName"] = "sql.example";
        }

        args.Id ??= $"{args.Name}_id";
        outputs["id"] = args.Id;
        outputs["principalId"] = "principal";
        outputs["tenantId"] = "tenant";
        outputs["clientId"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        return Task.FromResult<(string?, object)>((args.Id, outputs));
    }

    public Task<object> CallAsync(MockCallArgs args)
    {
        var token = args.Token ?? string.Empty;
        if (token.Contains("getSharedKeys", StringComparison.OrdinalIgnoreCase)
            || token.Contains("listKeys", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<object>(ImmutableDictionary<string, object>.Empty
                .Add("primarySharedKey", "not-a-real-key"));
        }

        return Task.FromResult<object>(ImmutableDictionary<string, object>.Empty);
    }
}

internal sealed class RecordingMocks(List<string> types) : IMocks
{
    private readonly PlatformStackReferenceMocks _inner = new();

    public List<MockResourceArgs> Args { get; } = [];

    public async Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
    {
        types.Add(args.Type ?? args.Name ?? "");
        Args.Add(args);
        return await _inner.NewResourceAsync(args);
    }

    public Task<object> CallAsync(MockCallArgs args) => _inner.CallAsync(args);
}

internal sealed class PulumiTestConfig : IDisposable
{
    private const string Variable = "PULUMI_CONFIG";
    private readonly string? _previous;

    private PulumiTestConfig(string? previous)
    {
        _previous = previous;
    }

    public static PulumiTestConfig Set(string projectName, IReadOnlyDictionary<string, string> values)
    {
        var previous = Environment.GetEnvironmentVariable(Variable);
        var prefixed = values.ToDictionary(
            pair => $"{projectName}:{pair.Key}",
            pair => pair.Value,
            StringComparer.Ordinal);
        Environment.SetEnvironmentVariable(Variable, JsonSerializer.Serialize(prefixed));
        return new PulumiTestConfig(previous);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);
}

internal static class OutputExtensions
{
    public static Task<T> GetValueAsync<T>(this Output<T> output)
    {
        var completion = new TaskCompletionSource<T>();
        output.Apply(value =>
        {
            completion.SetResult(value);
            return value;
        });
        return completion.Task;
    }
}

internal static class EngineTestConfig
{
    public static readonly Guid InstanceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static Dictionary<string, string> Values(string? engineName = null, string? platformStack = null) =>
        new(StringComparer.Ordinal)
        {
            ["instanceId"] = InstanceId.ToString("D"),
            ["location"] = "westeurope",
            ["isolation"] = "Dedicated",
            ["topology"] = "combined",
            ["imageRepository"] = "valenceruntimeimages.azurecr.io/runtime-combined",
            ["imageDigest"] = new string('a', 64),
            ["planFingerprint"] = new string('b', 64),
            ["platformStack"] = platformStack ?? "organization/elsa-platform/staging",
            ["engineName"] = engineName ?? "engine-" + InstanceId.ToString("N"),
            ["cpu"] = "0.5",
            ["memory"] = "1Gi",
            ["minReplicas"] = "0",
            ["maxReplicas"] = "1",
            ["elsaVersion"] = "3.8",
            ["sqlBootstrapObjectId"] = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff",
            ["sqlBootstrapLogin"] = "elsa-control-bootstrap",
            ["sqlConnectionSecretValue"] = "seeded-sql",
            ["signingKeySecretValue"] = "seeded-signing",
            ["adminPasswordSecretValue"] = "seeded-admin"
        };

    public static Dictionary<string, string> Platform(string environmentName = "staging") =>
        new(StringComparer.Ordinal)
        {
            ["environmentName"] = environmentName,
            ["location"] = "westeurope",
            ["registryName"] = "runtimeimages",
            ["registryResourceGroupName"] = "rg-runtime-images"
        };
}
