using System.Collections.Immutable;
using System.Text.Json;
using ElsaControl.Provisioning.Providers.Azure.Pulumi;
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
        args.Id ??= $"{args.Name}_id";
        return Task.FromResult<(string?, object)>((args.Id, outputs));
    }

    public Task<object> CallAsync(MockCallArgs args) =>
        Task.FromResult<object>(ImmutableDictionary<string, object>.Empty);
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
                .Add("registryKind", "shared-registry");
            outputs["secretOutputNames"] = ImmutableArray<string>.Empty;
        }

        args.Id ??= $"{args.Name}_id";
        return Task.FromResult<(string?, object)>((args.Id, outputs));
    }

    public Task<object> CallAsync(MockCallArgs args) =>
        Task.FromResult<object>(ImmutableDictionary<string, object>.Empty);
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
