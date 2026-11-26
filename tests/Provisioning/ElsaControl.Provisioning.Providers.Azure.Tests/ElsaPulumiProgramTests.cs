using ElsaControl.Provisioning.Providers.Azure.Programs;
using Pulumi;
using Pulumi.Testing;

namespace ElsaControl.Provisioning.Providers.Azure.Tests;

[Collection("Pulumi")]
public sealed class ElsaPulumiProgramTests
{
    [Fact]
    public async Task Platform_stack_exports_environment_name()
    {
        var mocks = new RecordingMocks();
        await Deployment.TestAsync(
            mocks,
            new TestOptions
            {
                ProjectName = ElsaPlatformStack.ProjectName,
                StackName = "ci-platform",
                IsPreview = false
            },
            CreatePlatform);

        Assert.Contains("elsa:azure:platform:SharedEnvironment", mocks.ResourceTypes);
        Assert.Equal("ci-platform", mocks.Input("elsa:azure:platform:SharedEnvironment", "environmentName"));
        AssertNoCloudIdentifiers(mocks);
    }

    [Fact]
    public async Task Engine_stack_reads_platform_output_through_stack_reference()
    {
        var mocks = new RecordingMocks();
        await Deployment.TestAsync(
            mocks,
            new TestOptions
            {
                ProjectName = ElsaEngineStack.ProjectName,
                StackName = "org-slug",
                IsPreview = false
            },
            CreateEngine);

        Assert.Contains("pulumi:pulumi:StackReference", mocks.ResourceTypes);
        Assert.Contains("elsa:azure:engine:DedicatedEngine", mocks.ResourceTypes);
        Assert.Equal("org-slug", mocks.Input("elsa:azure:engine:DedicatedEngine", "engineName"));
        Assert.Equal("ci-platform", mocks.Input("elsa:azure:engine:DedicatedEngine", "platformEnvironmentName"));
        Assert.Equal("runtime-combined:test", mocks.Input("elsa:azure:engine:DedicatedEngine", "runtimeImage"));
        Assert.Contains("elsa-platform/ci-platform", mocks.StackReferences);
        AssertNoCloudIdentifiers(mocks);
    }

    private static IDictionary<string, object?> CreatePlatform()
    {
        ElsaPlatformStack.Define("ci-platform");
        return new Dictionary<string, object?> { ["environmentName"] = "ci-platform" };
    }

    private static IDictionary<string, object?> CreateEngine()
    {
        ElsaEngineStack.Define(new EngineStackInputs(
            "org-slug",
            "runtime-combined:test",
            "elsa-platform/ci-platform"));
        return new Dictionary<string, object?>
        {
            ["engineName"] = "org-slug",
            ["platformEnvironmentName"] = "ci-platform"
        };
    }

    private static void AssertNoCloudIdentifiers(RecordingMocks mocks)
    {
        Assert.DoesNotContain(mocks.ResourceTypes, type => type.Contains("subscriptions", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mocks.StackReferences, name => name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class RecordingMocks : IMocks
    {
        private readonly Dictionary<string, Dictionary<string, object>> _inputs = new(StringComparer.Ordinal);

        public List<string> ResourceTypes { get; } = [];

        public List<string> StackReferences { get; } = [];

        public string Input(string resourceType, string key)
        {
            Assert.True(_inputs.TryGetValue(resourceType, out var inputs), $"Missing resource '{resourceType}'.");
            Assert.True(inputs.TryGetValue(key, out var value), $"Missing input '{key}' on '{resourceType}'.");
            return Assert.IsType<string>(value);
        }

        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            if (args.Type is { } type)
                ResourceTypes.Add(type);

            var state = new Dictionary<string, object>();
            foreach (var (key, value) in args.Inputs)
            {
                if (value is not null)
                    state[key] = value;
            }

            if (args.Type is { } recordedType)
            {
                _inputs[recordedType] = new Dictionary<string, object>(state, StringComparer.Ordinal);
            }

            if (args.Type == "pulumi:pulumi:StackReference")
            {
                if (args.Inputs.TryGetValue("name", out var name))
                    StackReferences.Add(name?.ToString() ?? "");
                state["outputs"] = new Dictionary<string, object>
                {
                    ["environmentName"] = "ci-platform"
                };
                state["secretOutputNames"] = Array.Empty<string>();
            }

            return Task.FromResult<(string?, object)>((args.Id ?? $"mock-{args.Name}", state));
        }

        public Task<object> CallAsync(MockCallArgs args) => Task.FromResult<object>(args.Args);
    }
}
