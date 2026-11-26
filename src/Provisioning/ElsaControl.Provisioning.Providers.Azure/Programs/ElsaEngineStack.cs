using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// The <c>elsa-engine</c> program. One stack per engine. Per-stack config is
/// required; the platform environment is read through a <see cref="StackReference"/>.
/// </summary>
public sealed class ElsaEngineStack : Stack
{
    public const string ProjectName = "elsa-engine";

    [Output] public Output<string> EngineName { get; private set; }

    [Output] public Output<string> PlatformEnvironmentName { get; private set; }

    public ElsaEngineStack()
    {
        var defined = Define(ReadInputs());
        EngineName = defined.EngineName;
        PlatformEnvironmentName = defined.PlatformEnvironmentName;
    }

    public static EngineStackInputs ReadInputs()
    {
        var config = new Config();
        return new EngineStackInputs(
            config.Require("engineName"),
            config.Require("runtimeImage"),
            config.Require("platformStack"));
    }

    public static EngineStackOutputs Define(EngineStackInputs inputs)
    {
        var platform = new StackReference(inputs.PlatformStack);
        var platformEnvironmentName = platform.RequireOutput("environmentName").Apply(value => (string)value!);
        var engine = new DedicatedEngineResource(
            "engine",
            new DedicatedEngineArgs(inputs.EngineName, platformEnvironmentName, inputs.RuntimeImage));
        return new EngineStackOutputs(engine.EngineName, engine.PlatformEnvironmentName);
    }
}

public sealed record EngineStackInputs(string EngineName, string RuntimeImage, string PlatformStack);

public sealed record EngineStackOutputs(Output<string> EngineName, Output<string> PlatformEnvironmentName);
