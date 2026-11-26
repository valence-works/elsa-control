using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Pulumi;

/// <summary>
/// Minimal <c>elsa-platform</c> program. One stack per environment. Full Bicep
/// parity is #1014; this skeleton exports capability kinds only.
/// </summary>
public sealed class ElsaPlatformStack : Stack
{
    public ElsaPlatformStack()
    {
        var config = new Config();
        var environmentName = config.Get("environmentName") ?? "unnamed";
        EnvironmentName = Output.Create(environmentName);
        ContainerEnvironmentKind = Output.Create("shared-container-environment");
        RegistryKind = Output.Create("shared-registry");
    }

    [Output] public Output<string> EnvironmentName { get; set; }

    [Output] public Output<string> ContainerEnvironmentKind { get; set; }

    [Output] public Output<string> RegistryKind { get; set; }
}
