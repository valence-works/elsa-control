using ElsaControl.Provisioning.Core;
using ElsaControl.Provisioning.Providers.Azure;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.KeyVault;
using Pulumi.AzureNative.KeyVault.Inputs;
using Pulumi.AzureNative.ManagedIdentity;
using Pulumi.AzureNative.Monitor;
using Pulumi.AzureNative.Monitor.Inputs;
using Pulumi.AzureNative.OperationalInsights;
using Pulumi.AzureNative.Resources;
using Pulumi.AzureNative.Sql;
using Pulumi.AzureNative.Sql.Inputs;
using Pulumi.Random;
using SkuName = Pulumi.AzureNative.KeyVault.SkuName;

namespace ElsaControl.Provisioning.Providers.Azure.Programs;

/// <summary>
/// <c>elsa-engine</c> program. One stack per engine, named from the immutable
/// instance ID. Org and slug are tags only. Dedicated isolation: each engine
/// owns its Container Apps environment and Log Analytics workspace, both
/// destroyed with the engine. The platform stack is the shared registry only.
/// </summary>
public sealed class ElsaEngineStack : Stack
{
    private const string AcrPullRoleId = "7f951dda-4ed3-4680-a7ca-43fe172d538d";
    private const string KeyVaultSecretsUserRoleId = "4633458b-17de-408a-b874-0445c86b69e6";
    private const string KeyVaultSecretsOfficerRoleId = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7";

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
                                ?? ProvisioningEngineResourceGroup.ForInstance(parsed);
        var tags = DefaultTags(values);
        var platform = new StackReference(platformStack);
        var registryId = platform.RequireOutput(PulumiStackOutputs.RegistryId).Apply(static value => value.ToString() ?? "");
        var registryLoginServer = platform.RequireOutput(PulumiStackOutputs.RegistryLoginServer).Apply(static value => value.ToString() ?? "");
        var bootstrapObjectId = values["sqlBootstrapObjectId"];
        var bootstrapLogin = values["sqlBootstrapLogin"];

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

        var keyVaultOfficer = new RoleAssignment("engine-kv-officer", new RoleAssignmentArgs
        {
            PrincipalId = bootstrapObjectId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = $"/providers/Microsoft.Authorization/roleDefinitions/{KeyVaultSecretsOfficerRoleId}",
            Scope = vault.Id
        });

        var keyVaultSecrets = new RoleAssignment("engine-kv-secrets", new RoleAssignmentArgs
        {
            PrincipalId = identity.PrincipalId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = $"/providers/Microsoft.Authorization/roleDefinitions/{KeyVaultSecretsUserRoleId}",
            Scope = vault.Id
        });

        var sqlRef = SecretRef(values, "sqlConnectionSecretName", "sql-connection");
        var signingRef = SecretRef(values, "signingKeySecretName", "identity-signing-key");
        var adminRef = SecretRef(values, "adminPasswordSecretName", "admin-password");
        var signingKey = new RandomBytes("engine-signing-key", new RandomBytesArgs { Length = 32 });
        Output<string> adminPasswordValue;
        if (!string.IsNullOrWhiteSpace(config.Get(AzureEngineProgramPolicy.AdminPasswordOverrideKey)))
        {
            adminPasswordValue = config.GetSecret(AzureEngineProgramPolicy.AdminPasswordOverrideKey)
                ?? throw new InvalidOperationException("Admin password override is missing.");
        }
        else
        {
            adminPasswordValue = new RandomPassword("engine-admin-password", new RandomPasswordArgs
            {
                Length = 32,
                Special = true
            }).Result;
        }

        var signingSecret = SeedVaultSecret(
            "engine-signing-secret",
            signingRef,
            signingKey.Base64,
            resourceGroup,
            vault,
            keyVaultOfficer);
        var adminSecret = SeedVaultSecret(
            "engine-admin-secret",
            adminRef,
            adminPasswordValue,
            resourceGroup,
            vault,
            keyVaultOfficer);

        var logs = new Workspace("engine-logs", new WorkspaceArgs
        {
            ResourceGroupName = resourceGroup.Name,
            WorkspaceName = LogName(parsed),
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

        var environment = new ManagedEnvironment("engine-aca", new ManagedEnvironmentArgs
        {
            ResourceGroupName = resourceGroup.Name,
            EnvironmentName = EnvironmentName(parsed),
            Location = location,
            AppLogsConfiguration = new AppLogsConfigurationArgs
            {
                Destination = "log-analytics",
                LogAnalyticsConfiguration = new LogAnalyticsConfigurationArgs
                {
                    CustomerId = logs.CustomerId,
                    SharedKey = keys.Apply(value => Output.CreateSecret(value.PrimarySharedKey ?? ""))
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

        _ = new DiagnosticSetting("engine-aca-logs", new DiagnosticSettingArgs
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
                Sid = bootstrapObjectId,
                Login = bootstrapLogin
            },
            Tags = tags
        });

        var database = new Database("engine-db", new DatabaseArgs
        {
            ResourceGroupName = resourceGroup.Name,
            ServerName = server.Name,
            DatabaseName = "Elsa",
            Location = location,
            // Dedicated engine DBs stay on Standard S0 on purpose. Serverless
            // GP_S_Gen5 on always-on engines costs about 10x. Bicep SKU
            // alignment is a separate follow-up, not this change.
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

        var sqlConnection = Output.Tuple(server.FullyQualifiedDomainName, database.Name).Apply(tuple =>
            Output.CreateSecret(
                $"Server={tuple.Item1};Database={tuple.Item2};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"));
        var sqlSecret = SeedVaultSecret(
            "engine-sql-secret",
            sqlRef,
            sqlConnection,
            resourceGroup,
            vault,
            keyVaultOfficer);

        var runtimeUser = new EngineSqlContainedUser(
            "engine-sql-user",
            new EngineSqlContainedUserArgs
            {
                FullyQualifiedDomainName = server.FullyQualifiedDomainName,
                ClientId = identity.ClientId,
                DisplayName = identity.Name,
                DatabaseId = database.Name
            },
            new ComponentResourceOptions
            {
                DependsOn = { database, server }
            });

        var cpu = values.GetValueOrDefault("cpu") ?? "0.5";
        var memory = values.GetValueOrDefault("memory") ?? "1Gi";
        var minReplicas = int.Parse(values.GetValueOrDefault("minReplicas") ?? "0");
        var maxReplicas = int.Parse(values.GetValueOrDefault("maxReplicas") ?? "1");
        var revisionSuffix = values.GetValueOrDefault("revisionSuffix")
                             ?? ProvisioningRevisionSuffix.FromFingerprint(values["planFingerprint"]);
        var image = AzureEngineProgramPolicy.ImmutableImage(values);
        var handoffEnabled = string.Equals(
            values.GetValueOrDefault("managedHandoff"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        Output<string>? handoffCallback = null;
        if (handoffEnabled)
        {
            var appName = AppName(parsed);
            handoffCallback = environment.DefaultDomain.Apply(domain =>
                $"https://{appName}.{domain}/managed-elsa/handoff/callback");
        }

        var acrPull = new RoleAssignment("engine-acr-pull", new RoleAssignmentArgs
        {
            PrincipalId = identity.PrincipalId,
            PrincipalType = "ServicePrincipal",
            RoleDefinitionId = $"/providers/Microsoft.Authorization/roleDefinitions/{AcrPullRoleId}",
            Scope = registryId
        });

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
            EnvironmentId = environment.Id,
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
                        Env = EnvironmentVariables(values, sqlRef, signingRef, adminRef, handoffCallback, runtimeUser.DisplayName),
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
        }, new CustomResourceOptions
        {
            DependsOn =
            {
                keyVaultSecrets,
                keyVaultOfficer,
                sqlSecret,
                signingSecret,
                adminSecret,
                acrPull,
                runtimeUser
            }
        });
        _ = app;
        _ = database;

        EngineName = Output.Create(values.GetValueOrDefault("engineName") ?? $"engine-{parsed:N}");
        PlatformEnvironmentKind = Output.Create("dedicated-container-environment");
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
            "managedHandoff", "handoffConfigured", "handoffControlBaseUrl", "handoffControlContinuationUrl",
            "handoffAudience", "handoffRuntimeMaximumLifetime", "handoffAllowedRuntimePermissions",
            "sqlWorkflowPackageVersion", "sqlQuartzPackageVersion",
            "elsaVersion", "sqlConnectionSecretName", "signingKeySecretName", "adminPasswordSecretName",
            "adminUsername", "sqlBootstrapObjectId", "sqlBootstrapLogin",
            AzureEngineProgramPolicy.SqlConnectionSecretValueKey,
            AzureEngineProgramPolicy.SigningKeySecretValueKey,
            AzureEngineProgramPolicy.AdminPasswordSecretValueKey,
            AzureEngineProgramPolicy.AdminPasswordOverrideKey
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
        Truncate("ca-eng-" + instanceId.ToString("N"), 32);

    private static string EnvironmentName(Guid instanceId) =>
        Truncate("aca-eng-" + instanceId.ToString("N"), 32);

    private static string LogName(Guid instanceId) =>
        Truncate("log-eng-" + instanceId.ToString("N"), 63);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static Pulumi.AzureNative.KeyVault.Secret SeedVaultSecret(
        string resourceName,
        string secretName,
        Input<string> value,
        ResourceGroup resourceGroup,
        Vault vault,
        RoleAssignment officer) =>
        new(
            resourceName,
            new Pulumi.AzureNative.KeyVault.SecretArgs
            {
                ResourceGroupName = resourceGroup.Name,
                VaultName = vault.Name,
                SecretName = secretName,
                Properties = new SecretPropertiesArgs { Value = value }
            },
            new CustomResourceOptions { DependsOn = { officer } });

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
        string adminRef,
        Output<string>? handoffCallback,
        Output<string> runtimeUser)
    {
        var env = new InputList<EnvironmentVarArgs>
        {
            new EnvironmentVarArgs { Name = "ASPNETCORE_ENVIRONMENT", Value = "Production" },
            new EnvironmentVarArgs { Name = "ASPNETCORE_HTTP_PORTS", Value = "8080" },
            new EnvironmentVarArgs { Name = "ELSA_TOPOLOGY", Value = AzureEngineProgramPolicy.SupportedTopology },
            new EnvironmentVarArgs { Name = "ELSA_SQL_RUNTIME_USER", Value = runtimeUser },
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

        if (string.Equals(values.GetValueOrDefault("managedHandoff"), "true", StringComparison.OrdinalIgnoreCase)
            && handoffCallback is not null)
        {
            env.Add(new EnvironmentVarArgs { Name = "ManagedElsa__Handoff__Enabled", Value = "true" });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__ControlBaseUrl",
                Value = values["handoffControlBaseUrl"]
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__ControlContinuationUrl",
                Value = values["handoffControlContinuationUrl"]
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__InstanceId",
                Value = values["instanceId"]
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__Audience",
                Value = values["handoffAudience"]
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__CallbackUri",
                Value = handoffCallback
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__UpstreamAuthenticationScheme",
                Value = "Jwt-or-ApiKey"
            });
            env.Add(new EnvironmentVarArgs { Name = "ManagedElsa__Handoff__SuccessPath", Value = "/" });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__StateLifetime",
                Value = "00:05:00"
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ManagedElsa__Handoff__RuntimeMaximumLifetime",
                Value = values["handoffRuntimeMaximumLifetime"]
            });
            env.Add(new EnvironmentVarArgs
            {
                Name = "ASPNETCORE_FORWARDEDHEADERS_ENABLED",
                Value = "true"
            });
            var permissions = values["handoffAllowedRuntimePermissions"]
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < permissions.Length; index++)
            {
                env.Add(new EnvironmentVarArgs
                {
                    Name = $"ManagedElsa__Handoff__AllowedRuntimePermissions__{index}",
                    Value = permissions[index]
                });
            }
        }
        else
        {
            env.Add(new EnvironmentVarArgs { Name = "ManagedElsa__Handoff__Enabled", Value = "false" });
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
