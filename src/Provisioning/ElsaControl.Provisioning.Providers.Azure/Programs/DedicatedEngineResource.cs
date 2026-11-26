using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

internal sealed class DedicatedEngineArgs
{
    public DedicatedEngineArgs(Input<string> engineName, Input<string> platformEnvironmentName, Input<string> runtimeImage)
    {
        ArgumentNullException.ThrowIfNull(engineName);
        ArgumentNullException.ThrowIfNull(platformEnvironmentName);
        ArgumentNullException.ThrowIfNull(runtimeImage);
        EngineName = engineName;
        PlatformEnvironmentName = platformEnvironmentName;
        RuntimeImage = runtimeImage;
    }

    public Input<string> EngineName { get; }

    public Input<string> PlatformEnvironmentName { get; }

    public Input<string> RuntimeImage { get; }
}

internal sealed class DedicatedEngineResourceArgs : ResourceArgs
{
    [Input("engineName", required: true)]
    public Input<string> EngineName { get; set; } = null!;

    [Input("platformEnvironmentName", required: true)]
    public Input<string> PlatformEnvironmentName { get; set; } = null!;

    [Input("runtimeImage", required: true)]
    public Input<string> RuntimeImage { get; set; } = null!;
}

/// <summary>
/// Skeleton engine resource. Full SQL / Key Vault / Container App parity is a
/// later leaf.
/// </summary>
internal sealed class DedicatedEngineResource : CustomResource
{
    [Output("engineName")]
    public Output<string> EngineName { get; set; } = null!;

    [Output("platformEnvironmentName")]
    public Output<string> PlatformEnvironmentName { get; set; } = null!;

    public DedicatedEngineResource(
        string name,
        DedicatedEngineArgs args,
        CustomResourceOptions? options = null)
        : base(
            "elsa:azure:engine:DedicatedEngine",
            name,
            new DedicatedEngineResourceArgs
            {
                EngineName = args.EngineName,
                PlatformEnvironmentName = args.PlatformEnvironmentName,
                RuntimeImage = args.RuntimeImage
            },
            options)
    {
    }
}
