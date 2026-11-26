using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.KeyVault.Inputs;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Sql.Inputs;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// <c>elsa-engine</c> program. One stack per engine, named from the immutable
/// instance ID. Org and slug are tags only. Shared platform outputs are read
/// through <see cref="StackReference"/> so a second engine does not create a
/// second environment or registry.
/// </summary>
public sealed class ElsaEngineStack : Stack
{
    private const string AcrPullRoleId = "7f951dda-4ed3-4680-a7ca-43fe172d538d";
    private const string KeyVaultSecretsUserRoleId = "4633458b-17de-408a-b874-0445c86b69e6";

    public ElsaEngineStack()
    {
        var config = new Config();
        var values = ReadConfig(config);
        AzureEngineProgramPolicy.Validate(values);

        var instanceId = values["instanceId"];
        var parsed = Guid.Parse(instanceId);
        var location = values["location"];
        var platformStack = values["platformStack"];
        var resourceGroupName = values.GetValueOrDefault("resourceGroupName")
                                ?? $"elsa-engine-{parsed:N}";
        var tags = DefaultTags(values);
        var platform = new StackReference(platformStack);
        var environmentId = platform.RequireOutput("containerEnvironmentId").Apply(static value => value.ToString() ?? "");
        var registryId = platform.RequireOutput("registryId").Apply(static value => value.ToString() ?? "");
        var registryLoginServer = platform.RequireOutput("registryLoginServer").Apply(static value => value.ToString() ?? "");

        var resourceGroup = new ResourceGroup("engine-rg", new ResourceGroupArgs
        {
            ResourceGroupName = resourceGroupName,
            Location = location,
            Tags = tags
        });

        var identity = new UserAssignedIdentity("engine-identity", new UserAssignedIdentityArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ResourceName = $"id-engine-{parsed:N}",
            Location = location,
            Tags = tags
        });

        var vault = new Vault("engine-kv", new VaultArgs
        {
            ResourceGroupName = resourceGroup.Name,
            VaultName = VaultName(parsed),
            Location = location,
            Properties = new VaultPropertiesArgs
            {
                Sku = new Pulumi.AzureNative.KeyVault.Inputs.SkuArgs { Family = "A", Name = SkuName.Standard },
                TenantId = identity.TenantId,
                EnableRbacAuthorization = true,
                EnableSoftDelete = true,
                SoftDeleteRetentionInDays = 7,
                PublicNetworkAccess = "Enabled"
            },
            Tags = tags
        });

        _ = new RoleAssignment("engine-kv-secrets", new RoleAssignmentArgs
        {
            PrincipalId = identity.PrincipalId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = $"/providers/Microsoft.Authorization/roleDefinitions/{KeyVaultSecretsUserRoleId}",
            Scope = vault.Id
        });

        var server = new Server("engine-sql", new ServerArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerName = SqlName(parsed),
            Location = location,
            Version = "12.0",
            MinimalTlsVersion = "1.2",
            PublicNetworkAccess = "Enabled",
            Administrators = new ServerExternalAdministratorArgs
            {
                AdministratorType = AdministratorType.ActiveDirectory,
                AzureADOnlyAuthentication = true,
                PrincipalType = Pulumi.AzureNative.Sql.PrincipalType.Application,
                Sid = identity.PrincipalId,
                Login = $"engine-{parsed:N}"[..16]
            },
            Tags = tags
        });

        var database = new Database("engine-db", new DatabaseArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerName = server.Name,
            DatabaseName = "Elsa",
            Location = location,
            Sku = new Pulumi.AzureNative.Sql.Inputs.SkuArgs
            {
                Name = "S0",
                Tier = "Standard",
                Capacity = 10
            },
            RequestedBackupStorageRedundancy = "Local",
            ZoneRedundant = false,
            Tags = tags
        });

        _ = new BackupShortTermRetentionPolicy("engine-db-retention", new BackupShortTermRetentionPolicyArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerName = server.Name,
            DatabaseName = database.Name,
            PolicyName = "default",
            RetentionDays = 35,
            DiffBackupIntervalInHours = 12
        });

        _ = new FirewallRule("engine-sql-azure", new FirewallRuleArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerName = server.Name,
            FirewallRuleName = "AllowAzureServices",
            StartIpAddress = "0.0.0.0",
            EndIpAddress = "0.0.0.0"
        });

        var cpu = values.GetValueOrDefault("cpu") ?? "0.5";
        var memory = values.GetValueOrDefault("memory") ?? "1Gi";
        var minReplicas = int.Parse(values.GetValueOrDefault("minReplicas") ?? "0");
        var maxReplicas = int.Parse(values.GetValueOrDefault("maxReplicas") ?? "1");
        var revisionSuffix = values.GetValueOrDefault("revisionSuffix") ?? values["planFingerprint"][..24];
        var image = AzureEngineProgramPolicy.ImmutableImage(values);
        var sqlRef = SecretRef(values, "sqlConnectionSecretName", "sql-connection");
        var signingRef = SecretRef(values, "signingKeySecretName", "identity-signing-key");
        var adminRef = SecretRef(values, "adminPasswordSecretName", "admin-password");

        var app = new ContainerApp("engine-app", new ContainerAppArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ContainerAppName = AppName(parsed),
            Location = location,
            WorkloadProfileName = "Consumption",
            Identity = new Pulumi.AzureNative.App.Inputs.ManagedServiceIdentityArgs
            {
                Type = Pulumi.AzureNative.App.ManagedServiceIdentityType.UserAssigned,
                UserAssignedIdentities = identity.Id
            },
            EnvironmentId = environmentId,
            Configuration = new ConfigurationArgs
            {
                ActiveRevisionsMode = ActiveRevisionsMode.Multiple,
                Ingress = new IngressArgs
                {
                    External = true,
                    TargetPort = 8080,
                    Transport = IngressTransportMethod.Auto,
                    AllowInsecure = false,
                    Traffic =
                    {
                        new TrafficWeightArgs
                        {
                            LatestRevision = true,
                            Weight = 100
                        }
                    }
                },
                Registries =
                {
                    new RegistryCredentialsArgs
                    {
                        Server = registryLoginServer,
                        Identity = identity.Id
                    }
                },
                Secrets =
                {
                    KeyVaultSecret(sqlRef, vault, identity.Id),
                    KeyVaultSecret(signingRef, vault, identity.Id),
                    KeyVaultSecret(adminRef, vault, identity.Id)
                }
            },
            Template = new TemplateArgs
            {
                RevisionSuffix = revisionSuffix,
                Scale = new ScaleArgs
                {
                    MinReplicas = minReplicas,
                    MaxReplicas = maxReplicas
                },
                Containers =
                {
                    new ContainerArgs
                    {
                        Name = AzureEngineProgramPolicy.SupportedTopology,
                        Image = image,
                        Resources = new ContainerResourcesArgs
                        {
                            Cpu = double.Parse(cpu, System.Globalization.CultureInfo.InvariantCulture),
                            Memory = memory
                        },
                        Env = EnvironmentVariables(values, sqlRef, signingRef, adminRef),
                        Probes =
                        {
                            StartupProbe(),
                            ReadinessProbe(),
                            LivenessProbe()
                        }
                    }
                }
            },
            Tags = tags
        });

        _ = new RoleAssignment("engine-acr-pull", new RoleAssignmentArgs
        {
            PrincipalId = identity.PrincipalId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = $"/providers/Microsoft.Authorization/roleDefinitions/{AcrPullRoleId}",
            Scope = registryId
        });

        EngineName = Output.Create(values.GetValueOrDefault("engineName") ?? $"engine-{parsed:N}");
        PlatformEnvironmentKind = platform.RequireOutput("containerEnvironmentKind")
            .Apply(static value => value.ToString() ?? Provisioning.Abstractions.ProvisioningCapability.SharedEnvironment);
        EndpointKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.Ingress);
        DatabaseKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.ManagedSql);
        SecretsKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.Secrets);
        RuntimeKind = Output.Create(Provisioning.Abstractions.ProvisioningCapability.ContainerRuntime);
        IsolationKind = Output.Create(AzureEngineProgramPolicy.SupportedIsolation);
        ImageIdentityKind = Output.Create("digest-pinned");
        HandoffKind = Output.Create(
            string.Equals(values.GetValueOrDefault("managedHandoff"), "true", StringComparison.OrdinalIgnoreCase)
                ? "managed-handoff"
                : "disabled");
        PlanFingerprint = Output.Create(values["planFingerprint"]);
        OwnedResourceGroupKind = Output.Create("dedicated-resource-group");
        OwnedResourceGroupName = resourceGroup.Name;
        _ = app;
        _ = database;
    }

    [Output] public Output<string> EngineName { get; set; }

    [Output] public Output<string> PlatformEnvironmentKind { get; set; }

    [Output] public Output<string> EndpointKind { get; set; }

    [Output] public Output<string> DatabaseKind { get; set; }

    [Output] public Output<string> SecretsKind { get; set; }

    [Output] public Output<string> RuntimeKind { get; set; }

    [Output] public Output<string> IsolationKind { get; set; }

    [Output] public Output<string> ImageIdentityKind { get; set; }

    [Output] public Output<string> HandoffKind { get; set; }

    [Output] public Output<string> PlanFingerprint { get; set; }

    [Output] public Output<string> OwnedResourceGroupKind { get; set; }

    [Output] public Output<string> OwnedResourceGroupName { get; set; }

    private static Dictionary<string, string> ReadConfig(Config config)
    {
        var keys = new[]
        {
            "instanceId", "location", "isolation", "topology", "imageRepository", "imageDigest",
            "planFingerprint", "platformStack", "engineName", "organizationSlug", "engineSlug",
            "resourceGroupName", "cpu", "memory", "minReplicas", "maxReplicas", "revisionSuffix",
            "managedHandoff", "handoffConfigured", "sqlWorkflowPackageVersion", "sqlQuartzPackageVersion",
            "elsaVersion", "sqlConnectionSecretName", "signingKeySecretName", "adminPasswordSecretName",
            "adminUsername"
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            var value = config.Get(key);
            if (!string.IsNullOrWhiteSpace(value))
                values[key] = value;
        }

        return values;
    }

    private static InputMap<string> DefaultTags(IReadOnlyDictionary<string, string> values)
    {
        var tags = new InputMap<string>
        {
            ["managed-by"] = "elsa-control-pulumi",
            ["stack-kind"] = "engine",
            ["instance-id"] = values["instanceId"]
        };
        if (values.TryGetValue("organizationSlug", out var org))
            tags["organization-slug"] = org;
        if (values.TryGetValue("engineSlug", out var slug))
            tags["engine-slug"] = slug;
        return tags;
    }

    private static string VaultName(Guid instanceId) =>
        "kv" + instanceId.ToString("N")[..20];

    private static string SqlName(Guid instanceId) =>
        "sql-engine-" + instanceId.ToString("N")[..16];

    private static string AppName(Guid instanceId) =>
        ("ca-eng-" + instanceId.ToString("N"))[..32];

    private static string SecretRef(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.GetValueOrDefault(key) ?? fallback;

    private static global::Pulumi.AzureNative.App.Inputs.SecretArgs KeyVaultSecret(
        string name,
        Vault vault,
        Output<string> identityId) =>
        new()
        {
            Name = name,
            Identity = identityId,
            KeyVaultUrl = vault.Properties.Apply(properties => $"{properties.VaultUri}secrets/{name}")
        };

    private static InputList<EnvironmentVarArgs> EnvironmentVariables(
        IReadOnlyDictionary<string, string> values,
        string sqlRef,
        string signingRef,
        string adminRef)
    {
        var env = new InputList<EnvironmentVarArgs>
        {
            new EnvironmentVarArgs { Name = "ASPNETCORE_ENVIRONMENT", Value = "Production" },
            new EnvironmentVarArgs { Name = "ASPNETCORE_HTTP_PORTS", Value = "8080" },
            new EnvironmentVarArgs { Name = "ELSA_TOPOLOGY", Value = AzureEngineProgramPolicy.SupportedTopology },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__SqliteWorkflowPersistence",
                Value = "false"
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__SqliteIdentityPersistence",
                Value = "false"
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__QuartzSqlite",
                Value = "false"
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__SqlServerWorkflowPersistence__ConnectionString",
                SecretRef = sqlRef
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__SqlServerIdentityPersistence__ConnectionString",
                SecretRef = sqlRef
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__QuartzSqlServer__ConnectionString",
                SecretRef = sqlRef
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__Identity__SigningKey",
                SecretRef = signingRef
            },
            new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__DefaultAdminUser__AdminPassword",
                SecretRef = adminRef
            }
        };
        if (values.TryGetValue("elsaVersion", out var elsaVersion))
            env.Add(new EnvironmentVarArgs { Name = "ELSA_VERSION", Value = elsaVersion });
        if (values.TryGetValue("adminUsername", out var adminUsername))
        {
            env.Add(new EnvironmentVarArgs
            {
                Name = "CShells__Shells__Default__Features__DefaultAdminUser__AdminUsername",
                Value = adminUsername
            });
        }

        if (values.TryGetValue("sqlWorkflowPackageVersion", out var workflowVersion)
            && values.TryGetValue("sqlQuartzPackageVersion", out var quartzVersion))
        {
            env.Add(new EnvironmentVarArgs { Name = "Nuplane__Setup__Feeds__0__Name", Value = "local-packages" });
            env.Add(new EnvironmentVarArgs { Name = "Nuplane__Setup__Feeds__0__DirectoryPath", Value = "packages" });
            env.Add(new EnvironmentVarArgs { Name = "Nuplane__Setup__Feeds__1__Name", Value = "nuget.org" });
            env.Add(new EnvironmentVarArgs
            {
                Name = "Nuplane__Setup__Feeds__1__ServiceIndex",
                Value = "https://api.nuget.org/v3/index.json"
            });
            env.Add(new EnvironmentVarArgs { Name = "Nuplane__Setup__Feeds__2__Name", Value = "feedz.io" });
            env.Add(new EnvironmentVarArgs
            {
                Name = "Nuplane__Setup__Feeds__2__ServiceIndex",
                Value = "https://f.feedz.io/elsa-workflows/elsa-3/nuget/index.json"
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "Nuplane__Setup__Feeds__2__IncludePatterns__0",
                Value = $"Elsa.Persistence.EFCore.SqlServer [{workflowVersion}]"
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "Nuplane__Setup__Feeds__2__IncludePatterns__1",
                Value = $"Elsa.Scheduling.Quartz.EFCore.SqlServer [{quartzVersion}]"
            });
        }

        return env;
    }

    private static ContainerAppProbeArgs StartupProbe() =>
        new()
        {
            Type = "Startup",
            HttpGet = new ContainerAppProbeHttpGetArgs { Path = "/alive", Port = 8080, Scheme = "HTTP" },
            InitialDelaySeconds = 5,
            PeriodSeconds = 10,
            FailureThreshold = 30,
            TimeoutSeconds = 5
        };

    private static ContainerAppProbeArgs ReadinessProbe() =>
        new()
        {
            Type = "Readiness",
            HttpGet = new ContainerAppProbeHttpGetArgs { Path = "/health", Port = 8080, Scheme = "HTTP" },
            InitialDelaySeconds = 10,
            PeriodSeconds = 10,
            FailureThreshold = 6,
            TimeoutSeconds = 5
        };

    private static ContainerAppProbeArgs LivenessProbe() =>
        new()
        {
            Type = "Liveness",
            HttpGet = new ContainerAppProbeHttpGetArgs { Path = "/alive", Port = 8080, Scheme = "HTTP" },
            InitialDelaySeconds = 30,
            PeriodSeconds = 30,
            FailureThreshold = 3,
            TimeoutSeconds = 5
        };
}
