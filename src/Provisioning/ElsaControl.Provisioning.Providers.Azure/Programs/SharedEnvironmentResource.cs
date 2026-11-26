using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

internal sealed class SharedEnvironmentArgs
{
    public SharedEnvironmentArgs(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; }
}

internal sealed class SharedEnvironmentResourceArgs : ResourceArgs
{
    [Input("environmentName", required: true)]
    public Input<string> EnvironmentName { get; set; } = null!;
}

/// <summary>
/// Skeleton platform resource. Full Container Apps / registry / network parity
/// is a later leaf.
/// </summary>
internal sealed class SharedEnvironmentResource : CustomResource
{
    [Output("environmentName")]
    public Output<string> EnvironmentName { get; set; } = null!;

    public SharedEnvironmentResource(
        string name,
        SharedEnvironmentArgs args,
        CustomResourceOptions? options = null)
        : base(
            "elsa:azure:platform:SharedEnvironment",
            name,
            new SharedEnvironmentResourceArgs { EnvironmentName = args.EnvironmentName },
            options)
    {
    }
}
