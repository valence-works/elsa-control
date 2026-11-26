using Pulumi;
using ElsaControl.Provisioning.Providers.Azure;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.Resources;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// <c>elsa-platform</c> program. One stack per environment. The commercial
/// registry is referenced, not created. Dedicated engines own their Container
/// Apps environment and Log Analytics workspace.
/// </summary>
public sealed class ElsaPlatformStack : Stack
{
    public ElsaPlatformStack()
    {
        var config = new Config();
        var values = ReadConfig(config);
        AzurePlatformProgramPolicy.Validate(values);

        var environmentName = values["environmentName"];
        var location = values["location"];
        var registryName = values["registryName"];
        var registryResourceGroup = values["registryResourceGroupName"];
        var tags = DefaultTags(environmentName);

        var resourceGroup = new ResourceGroup("platform-rg", new ResourceGroupArgs
        {
            ResourceGroupName = $"rg-elsa-platform-{environmentName}",
            Location = location,
            Tags = tags
        });

        var registry = GetRegistry.Invoke(new GetRegistryInvokeArgs
        {
            ResourceGroupName = registryResourceGroup,
            RegistryName = registryName
        });

        EnvironmentName = Output.Create(environmentName);
        RegistryKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.SharedRegistry);
        IsolationKind = Output.Create(AzureEngineProgramPolicy.SupportedIsolation);
        LocationKind = Output.Create(location);
        RegistryName = Output.Create(registryName);
        RegistryResourceGroupName = Output.Create(registryResourceGroup);
        ResourceGroupName = resourceGroup.Name;
        RegistryId = registry.Apply(value => value.Id);
        RegistryLoginServer = registry.Apply(value => value.LoginServer);
    }

    [Output("environmentName")] public Output<string> EnvironmentName { get; set; }

    [Output("registryKind")] public Output<string> RegistryKind { get; set; }

    [Output("isolationKind")] public Output<string> IsolationKind { get; set; }

    [Output("locationKind")] public Output<string> LocationKind { get; set; }

    [Output("registryName")] public Output<string> RegistryName { get; set; }

    [Output("registryResourceGroupName")] public Output<string> RegistryResourceGroupName { get; set; }

    [Output("resourceGroupName")] public Output<string> ResourceGroupName { get; set; }

    [Output(PulumiStackOutputs.RegistryId)] public Output<string> RegistryId { get; set; }

    [Output(PulumiStackOutputs.RegistryLoginServer)] public Output<string> RegistryLoginServer { get; set; }

    private static Dictionary<string, string> ReadConfig(Config config) =>
        new(StringComparer.Ordinal)
        {
            ["environmentName"] = config.Require("environmentName"),
            ["location"] = config.Require("location"),
            ["registryName"] = config.Require("registryName"),
            ["registryResourceGroupName"] = config.Require("registryResourceGroupName")
        };

    private static InputMap<string> DefaultTags(string environmentName) =>
        new()
        {
            ["managed-by"] = "elsa-control-pulumi",
            ["stack-kind"] = "platform",
            ["environment"] = environmentName
        };
}
