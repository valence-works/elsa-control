using Pulumi;

namespace ElsaControl.Provisioning.Providers.Azure.Pulumi;

/// <summary>
/// Minimal <c>elsa-engine</c> program. One stack per engine. Engine config is
/// per-stack. Shared platform outputs are read through <see cref="StackReference"/>.
/// Full Bicep parity is #1014.
/// </summary>
public sealed class ElsaEngineStack : Stack
{
    public ElsaEngineStack()
    {
        var config = new Config();
        var engineName = config.Require("engineName");
        var platformStack = config.Require("platformStack");
        var platform = new StackReference(platformStack);
        EngineName = Output.Create(engineName);
        PlatformEnvironmentKind = platform.RequireOutput("containerEnvironmentKind").Apply(static value => value.ToString() ?? "shared-container-environment");
        EndpointKind = Output.Create("https-ingress");
        DatabaseKind = Output.Create("managed-sql");
        SecretsKind = Output.Create("managed-secrets");
        RuntimeKind = Output.Create("container-runtime");
    }

    [Output] public Output<string> EngineName { get; set; }

    [Output] public Output<string> PlatformEnvironmentKind { get; set; }

    [Output] public Output<string> EndpointKind { get; set; }

    [Output] public Output<string> DatabaseKind { get; set; }

    [Output] public Output<string> SecretsKind { get; set; }

    [Output] public Output<string> RuntimeKind { get; set; }
}
