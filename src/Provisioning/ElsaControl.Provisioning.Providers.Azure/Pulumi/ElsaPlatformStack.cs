using Pulumi;
using Pulumi.AzureNative.App;
using ElsaControl.Provisioning.Providers.Azure;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.ContainerRegistry;
using Pulumi.AzureNative.Monitor;
using Pulumi.AzureNative.Monitor.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.Resources;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// <c>elsa-platform</c> program. One stack per environment: Log Analytics and
/// the Container Apps environment. The commercial registry is referenced, not
/// created, so a second engine stack in the same environment does not create a
/// second environment or registry.
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

        var logs = new Workspace("platform-logs", new WorkspaceArgs
        {
            ResourceGroupName = resourceGroup.Name,
            WorkspaceName = $"log-elsa-platform-{environmentName}",
            Location = location,
            Sku = new global::Pulumi.AzureNative.OperationalInsights.Inputs.WorkspaceSkuArgs
            {
                Name = "PerGB2018"
            },
            RetentionInDays = 30,
            Tags = tags
        });

        var keys = GetSharedKeys.Invoke(new GetSharedKeysInvokeArgs
        {
            ResourceGroupName = resourceGroup.Name,
            WorkspaceName = logs.Name
        });

        var environment = new ManagedEnvironment("platform-aca", new ManagedEnvironmentArgs
        {
            ResourceGroupName = resourceGroup.Name,
            EnvironmentName = $"aca-elsa-platform-{environmentName}",
            Location = location,
            AppLogsConfiguration = new AppLogsConfigurationArgs
            {
                Destination = "log-analytics",
                LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs
                {
                    CustomerId = logs.CustomerId,
                    SharedKey = keys.Apply(value => value.PrimarySharedKey ?? "")
                }
            },
            WorkloadProfiles =
            {
                new WorkloadProfileArgs
                {
                    Name = "Consumption",
                    WorkloadProfileType = "Consumption"
                }
            },
            ZoneRedundant = false,
            Tags = tags
        });

        _ = new DiagnosticSetting("platform-aca-logs", new DiagnosticSettingArgs
        {
            Name = "container-apps",
            ResourceUri = environment.Id,
            WorkspaceId = logs.Id,
            Logs =
            {
                new LogSettingsArgs { Category = "ContainerAppConsoleLogs", Enabled = true },
                new LogSettingsArgs { Category = "ContainerAppSystemLogs", Enabled = true }
            },
            Metrics =
            {
                new MetricSettingsArgs { Category = "AllMetrics", Enabled = true }
            }
        });

        var registry = GetRegistry.Invoke(new GetRegistryInvokeArgs
        {
            ResourceGroupName = registryResourceGroup,
            RegistryName = registryName
        });

        EnvironmentName = Output.Create(environmentName);
        ContainerEnvironmentKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.SharedEnvironment);
        RegistryKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.SharedRegistry);
        IsolationKind = Output.Create(AzureEngineProgramPolicy.SupportedIsolation);
        LocationKind = Output.Create(location);
        RegistryName = Output.Create(registryName);
        RegistryResourceGroupName = Output.Create(registryResourceGroup);
        ContainerEnvironmentName = environment.Name;
        LogAnalyticsWorkspaceName = logs.Name;
        ResourceGroupName = resourceGroup.Name;
        ContainerEnvironmentId = environment.Id;
        ContainerEnvironmentDefaultDomain = environment.DefaultDomain;
        RegistryId = registry.Apply(value => value.Id);
        RegistryLoginServer = registry.Apply(value => value.LoginServer);
    }

    [Output("environmentName")] public Output<string> EnvironmentName { get; set; }

    [Output("containerEnvironmentKind")] public Output<string> ContainerEnvironmentKind { get; set; }

    [Output("registryKind")] public Output<string> RegistryKind { get; set; }

    [Output("isolationKind")] public Output<string> IsolationKind { get; set; }

    [Output("locationKind")] public Output<string> LocationKind { get; set; }

    [Output("registryName")] public Output<string> RegistryName { get; set; }

    [Output("registryResourceGroupName")] public Output<string> RegistryResourceGroupName { get; set; }

    [Output("containerEnvironmentName")] public Output<string> ContainerEnvironmentName { get; set; }

    [Output("logAnalyticsWorkspaceName")] public Output<string> LogAnalyticsWorkspaceName { get; set; }

    [Output("resourceGroupName")] public Output<string> ResourceGroupName { get; set; }

    [Output(PulumiStackOutputs.ContainerEnvironmentId)] public Output<string> ContainerEnvironmentId { get; set; }

    [Output(PulumiStackOutputs.ContainerEnvironmentDefaultDomain)] public Output<string> ContainerEnvironmentDefaultDomain { get; set; }

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
