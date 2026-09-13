using ElsaControl.Deployment.Azure;

namespace ElsaControl.Deployment.Azure.Tests;

public sealed class ManagedIdentityAzureSecretResolverTests
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstanceId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid AssignmentId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OperationId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private const string Version = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SecretReference = $"secret://source.vault.azure.net/secrets/sql-connection/{Version}";
    private const string ExternalSecretReference = $"https://source.vault.azure.net/secrets/sql-connection/{Version}";
    private const string ManagedSqlReference = AzureManagedSecretReferences.SqlConnection;

    [Fact]
    public async Task Resolves_an_exact_durable_secret_reference_without_network_in_the_unit()
    {
        var reader = new FakeReader();
        var resolver = CreateResolver(reader);

        await using var lease = await resolver.ResolveAsync(Request());

        Assert.Equal("resolved-value", lease.Value.ToString());
        Assert.Equal(1, reader.Calls);
        Assert.Equal(new Uri("https://source.vault.azure.net/"), reader.VaultUri);
        Assert.Equal("sql-connection", reader.Name);
        Assert.Equal(Version, reader.Version);
    }

    [Fact]
    public void Projects_an_external_key_vault_locator_to_the_canonical_plan_reference()
    {
        Assert.True(AzureKeyVaultSecretLocator.TryParse(ExternalSecretReference, out var locator));
        Assert.NotNull(locator);
        Assert.Equal(SecretReference, locator!.PlanReference);
        Assert.Equal(new Uri("https://source.vault.azure.net/"), locator.VaultUri);
    }

    [Theory]
    [InlineData("Organization")]
    [InlineData("Workspace")]
    [InlineData("Instance")]
    [InlineData("Assignment")]
    [InlineData("Name")]
    [InlineData("Reference")]
    public async Task Rejects_every_non_exact_authorization_tuple_member(string changed)
    {
        var reader = new FakeReader();
        var resolver = CreateResolver(reader);
        var request = Request() with
        {
            OrganizationId = changed == "Organization" ? Guid.NewGuid() : OrganizationId,
            WorkspaceId = changed == "Workspace" ? Guid.NewGuid() : WorkspaceId,
            InstanceId = changed == "Instance" ? Guid.NewGuid() : InstanceId,
            ProviderAssignmentId = changed == "Assignment" ? Guid.NewGuid().ToString("D") : AssignmentId.ToString("D"),
            Name = changed == "Name" ? "identity-signing-key" : "database:connectionstring",
            Reference = changed == "Reference"
                ? $"secret://source.vault.azure.net/secrets/other-secret/{Version}"
                : SecretReference
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(request));

        Assert.Equal("The requested Azure secret is not authorized.", exception.Message);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("https://source.vault.azure.net/secrets/sql-connection")]
    [InlineData("https://source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]
    [InlineData("https://source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?api-version=7.4")]
    [InlineData("https://user:password@source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://source.vault.azure.net:8443/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("secret://user:password@source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("secret://source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?api-version=7.4")]
    [InlineData("secret://source.vault.azure.net/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa#fragment")]
    [InlineData("https://other.example/secrets/sql-connection/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://source.vault.azure.net/secrets/sql-connection/../admin/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Rejects_unversioned_or_unsafe_key_vault_locators(string reference)
    {
        var reader = new FakeReader();
        var resolver = CreateResolver(reader);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync(Request() with { Reference = reference }));

        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Rejects_a_reference_not_retained_by_the_durable_operation()
    {
        var reader = new FakeReader();
        var authorization = Authorization() with
        {
            Operation = Operation() with
            {
                SecretReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["database:connectionstring"] = $"secret://source.vault.azure.net/secrets/sql-connection/{new string('b', 32)}"
                }
            }
        };
        var resolver = new ManagedIdentityAzureSecretResolver(new FakeAuthorizationStore(authorization), reader);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(Request()));

        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("secret://source.vault.azure.net/secrets/SQL-CONNECTION/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("secret://source.vault.azure.net/secrets/sql-connection/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task Rejects_an_alternate_locator_casing_or_version_against_the_exact_persisted_reference(string reference)
    {
        var reader = new FakeReader();
        var resolver = CreateResolver(reader);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(Request() with { Reference = reference }));

        Assert.Equal("The requested Azure secret is not authorized.", exception.Message);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Materializes_the_provider_owned_sql_connection_from_the_assignment_without_key_vault()
    {
        var reader = new FakeReader();
        var resolver = new ManagedIdentityAzureSecretResolver(
            new FakeAuthorizationStore(InternalAuthorization()), reader);

        await using var lease = await resolver.ResolveAsync(Request() with
        {
            Reference = ManagedSqlReference,
            // A caller-supplied snapshot must not influence provider-owned materialization.
            Resources = new AzureProviderResourceReferences(
                SqlServerFqdn: "caller-controlled.database.windows.net",
                WorkloadIdentityClientId: "77777777-7777-7777-7777-777777777777")
        });

        Assert.Equal(
            "Server=tcp:workload-sql.database.windows.net,1433;Initial Catalog=Elsa;Encrypt=True;Authentication=\"Active Directory Managed Identity\";User Id=77777777-7777-7777-7777-777777777777;TrustServerCertificate=False;Connection Timeout=30;",
            lease.Value.ToString());
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("identity:signingkey", AzureManagedSecretReferences.IdentitySigningKey)]
    [InlineData("admin:password", AzureManagedSecretReferences.AdminPassword)]
    public async Task Generates_distinct_provider_owned_secret_material_without_key_vault(
        string name,
        string reference)
    {
        var reader = new FakeReader();
        var instanceA = InstanceId;
        var instanceB = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var resolverA = new ManagedIdentityAzureSecretResolver(
            new FakeAuthorizationStore(ProviderOwnedAuthorization(instanceA)), reader);
        var resolverB = new ManagedIdentityAzureSecretResolver(
            new FakeAuthorizationStore(ProviderOwnedAuthorization(instanceB)), reader);

        await using var leaseA = await resolverA.ResolveAsync(RequestFor(instanceA, name, reference));
        await using var leaseB = await resolverB.ResolveAsync(RequestFor(instanceB, name, reference));

        Assert.False(leaseA.Value.Span.SequenceEqual(leaseB.Value.Span));
        Assert.True(leaseA.Value.Length >= 64);
        Assert.True(leaseB.Value.Length >= 64);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Rejects_a_secret_request_created_before_the_same_operation_was_reclaimed()
    {
        var reader = new FakeReader();
        var originalAuthorization = ProviderOwnedAuthorization();
        var authorizationStore = new SwitchingAuthorizationStore(originalAuthorization);
        var resolver = new ManagedIdentityAzureSecretResolver(authorizationStore, reader);
        var requestCreatedForAttemptA = RequestFor(
            InstanceId,
            AzureManagedSecretReferences.IdentitySigningKeyName,
            AzureManagedSecretReferences.IdentitySigningKey);

        await using (var currentAttemptLease = await resolver.ResolveAsync(requestCreatedForAttemptA))
            Assert.True(currentAttemptLease.Value.Length >= 64);

        authorizationStore.Current = originalAuthorization with
        {
            Operation = originalAuthorization.Operation with
            {
                AttemptNumber = 2,
                Version = 4,
                WorkerId = "recovery-worker",
                LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            }
        };

        var currentAttemptRequest = requestCreatedForAttemptA with { AttemptNumber = 2, OperationId = OperationId };
        Assert.NotEqual(requestCreatedForAttemptA.AttemptNumber, currentAttemptRequest.AttemptNumber);
        Assert.False(await resolver.IsAuthorizedAsync(requestCreatedForAttemptA));
        Assert.True(await resolver.IsAuthorizedAsync(currentAttemptRequest));

        await using (var currentAttemptLease = await resolver.ResolveAsync(currentAttemptRequest))
            Assert.True(currentAttemptLease.Value.Length >= 64);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var staleAttemptLease = await resolver.ResolveAsync(requestCreatedForAttemptA);
        });

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("The requested Azure secret is not authorized.", exception.Message);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("MissingOperation")]
    [InlineData("WrongOperation")]
    [InlineData("MissingAttempt")]
    [InlineData("WrongAttempt")]
    public async Task Rejects_missing_or_mismatched_secret_request_generation(string changed)
    {
        var reader = new FakeReader();
        var resolver = CreateResolver(reader);
        var request = Request() with
        {
            OperationId = changed == "MissingOperation" ? null :
                changed == "WrongOperation" ? Guid.NewGuid() : OperationId,
            AttemptNumber = changed == "MissingAttempt" ? null :
                changed == "WrongAttempt" ? 2 : 1
        };

        Assert.False(await resolver.IsAuthorizedAsync(request));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(request));

        Assert.Equal("The requested Azure secret is not authorized.", exception.Message);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("EmptyOperation")]
    [InlineData("ZeroAttempt")]
    public async Task Rejects_invalid_secret_request_generation(string changed)
    {
        var reader = new FakeReader();
        var resolver = CreateResolver(reader);
        var request = Request() with
        {
            OperationId = changed == "EmptyOperation" ? Guid.Empty : OperationId,
            AttemptNumber = changed == "ZeroAttempt" ? 0 : 1
        };

        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.ResolveAsync(request));
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Keeps_authorization_when_only_the_operation_version_changes()
    {
        var reader = new FakeReader();
        var authorization = Authorization() with
        {
            Operation = Operation() with { Version = 8 }
        };
        var resolver = new ManagedIdentityAzureSecretResolver(new FakeAuthorizationStore(authorization), reader);

        Assert.True(await resolver.IsAuthorizedAsync(Request()));
        await using var lease = await resolver.ResolveAsync(Request());

        Assert.Equal("resolved-value", lease.Value.ToString());
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task Keeps_authorization_after_a_template_only_scope_rotation()
    {
        var reader = new FakeReader();
        var authorization = Authorization() with
        {
            Operation = Operation() with { ProviderScopeFingerprint = new string('z', 64) }
        };
        var resolver = new ManagedIdentityAzureSecretResolver(new FakeAuthorizationStore(authorization), reader);

        Assert.True(await resolver.IsAuthorizedAsync(Request()));
        await using var lease = await resolver.ResolveAsync(Request());

        Assert.Equal("resolved-value", lease.Value.ToString());
        Assert.Equal(1, reader.Calls);
    }

    [Theory]
    [InlineData("identity:signingkey", AzureManagedSecretReferences.AdminPassword)]
    [InlineData("admin:password", AzureManagedSecretReferences.IdentitySigningKey)]
    [InlineData("IDENTITY:SIGNINGKEY", AzureManagedSecretReferences.IdentitySigningKey)]
    [InlineData("identity:signingkey", "secret://azure-managed/Identity-signing-key")]
    public async Task Rejects_provider_owned_secret_reference_for_a_non_exact_slot(
        string name,
        string reference)
    {
        var reader = new FakeReader();
        var resolver = new ManagedIdentityAzureSecretResolver(
            new FakeAuthorizationStore(ProviderOwnedAuthorization()), reader);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync(RequestFor(InstanceId, name, reference)));

        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("identity:signingkey", ManagedSqlReference)]
    [InlineData("database:connectionstring", "secret://azure-managed/other")]
    [InlineData("database:connectionstring", "secret://vault/sql-connection")]
    public async Task Rejects_provider_owned_or_opaque_references_outside_the_exact_slot(string name, string reference)
    {
        var reader = new FakeReader();
        var resolver = new ManagedIdentityAzureSecretResolver(
            new FakeAuthorizationStore(InternalAuthorization()), reader);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync(Request() with { Name = name, Reference = reference }));

        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("MissingSqlServerResourceId")]
    [InlineData("WrongSqlServerResourceId")]
    [InlineData("MissingSqlServerFqdn")]
    [InlineData("WrongSqlServerFqdn")]
    [InlineData("MissingWorkloadIdentityResourceId")]
    [InlineData("WrongWorkloadIdentityResourceId")]
    [InlineData("MissingWorkloadIdentityClientId")]
    [InlineData("WrongWorkloadIdentityClientId")]
    [InlineData("WrongResourceGroup")]
    [InlineData("WrongNamingVersion")]
    [InlineData("EmptySubscriptionId")]
    [InlineData("EmptyClientId")]
    [InlineData("WrongAssignmentWorkspace")]
    [InlineData("WrongAssignmentOrganization")]
    [InlineData("WrongAssignmentInstance")]
    [InlineData("WrongAssignmentState")]
    [InlineData("MissingLastOperationId")]
    [InlineData("WrongOperationId")]
    [InlineData("WrongOperationWorkspace")]
    [InlineData("WrongOperationOrganization")]
    [InlineData("WrongOperationInstance")]
    [InlineData("WrongOperationAssignment")]
    [InlineData("WrongOperationTarget")]
    [InlineData("WrongOperationStatus")]
    [InlineData("DeleteOperation")]
    [InlineData("InvalidOperationAction")]
    [InlineData("BeforeFoundation")]
    [InlineData("ExpiredLease")]
    [InlineData("MissingLease")]
    public async Task Rejects_missing_or_mismatched_provider_authority_evidence(string changed)
    {
        var reader = new FakeReader();
        var authorization = InternalAuthorization();
        var request = Request() with { Reference = ManagedSqlReference };
        switch (changed)
        {
            case "MissingSqlServerResourceId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { SqlServerResourceId = null } } };
                break;
            case "WrongSqlServerResourceId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { SqlServerResourceId = "sql-server-resource" } } };
                break;
            case "MissingSqlServerFqdn":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { SqlServerFqdn = null } } };
                break;
            case "WrongSqlServerFqdn":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { SqlServerFqdn = "other-sql.database.windows.net" } } };
                break;
            case "MissingWorkloadIdentityResourceId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { WorkloadIdentityResourceId = null } } };
                break;
            case "WrongWorkloadIdentityResourceId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { WorkloadIdentityResourceId = "identity-resource" } } };
                break;
            case "MissingWorkloadIdentityClientId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { WorkloadIdentityClientId = null } } };
                break;
            case "WrongWorkloadIdentityClientId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { WorkloadIdentityClientId = "not-a-guid" } } };
                break;
            case "WrongResourceGroup":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { ResourceGroupName = "other-rg" } } };
                break;
            case "WrongNamingVersion":
                authorization = authorization with { Assignment = authorization.Assignment with { NamingVersion = 0 } };
                break;
            case "EmptySubscriptionId":
                authorization = authorization with { Assignment = authorization.Assignment with { SubscriptionId = Guid.Empty.ToString("D") } };
                break;
            case "EmptyClientId":
                authorization = authorization with { Assignment = authorization.Assignment with { Resources = SqlResources() with { WorkloadIdentityClientId = Guid.Empty.ToString("D") } } };
                break;
            case "WrongAssignmentWorkspace":
                authorization = authorization with { Assignment = authorization.Assignment with { WorkspaceId = Guid.NewGuid() } };
                break;
            case "WrongAssignmentOrganization":
                authorization = authorization with { Assignment = authorization.Assignment with { OrganizationId = Guid.NewGuid() } };
                break;
            case "WrongAssignmentInstance":
                authorization = authorization with { Assignment = authorization.Assignment with { InstanceId = Guid.NewGuid() } };
                break;
            case "WrongAssignmentState":
                authorization = authorization with { Assignment = authorization.Assignment with { State = AzureProviderAssignmentState.Active } };
                break;
            case "MissingLastOperationId":
                authorization = authorization with { Assignment = authorization.Assignment with { LastOperationId = null } };
                break;
            case "WrongOperationId":
                authorization = authorization with { Operation = authorization.Operation with { Id = Guid.NewGuid() } };
                break;
            case "WrongOperationWorkspace":
                authorization = authorization with { Operation = authorization.Operation with { WorkspaceId = Guid.NewGuid() } };
                break;
            case "WrongOperationOrganization":
                authorization = authorization with { Operation = authorization.Operation with { OrganizationId = Guid.NewGuid() } };
                break;
            case "WrongOperationInstance":
                authorization = authorization with { Operation = authorization.Operation with { InstanceId = Guid.NewGuid() } };
                break;
            case "WrongOperationAssignment":
                authorization = authorization with { Operation = authorization.Operation with { ProviderAssignmentId = Guid.NewGuid() } };
                break;
            case "WrongOperationTarget":
                authorization = authorization with { Operation = authorization.Operation with { TargetKey = "other-workload" } };
                break;
            case "WrongOperationStatus":
                authorization = authorization with { Operation = authorization.Operation with { Status = AzureProviderOperationStatus.Accepted } };
                break;
            case "DeleteOperation":
                authorization = authorization with { Operation = authorization.Operation with { Action = AzureProviderOperationAction.Delete } };
                break;
            case "InvalidOperationAction":
                authorization = authorization with { Operation = authorization.Operation with { Action = (AzureProviderOperationAction)99 } };
                break;
            case "BeforeFoundation":
                authorization = authorization with { Operation = authorization.Operation with { Phase = AzureProviderOperationPhase.Planned } };
                break;
            case "ExpiredLease":
                authorization = authorization with { Operation = authorization.Operation with { LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } };
                break;
            case "MissingLease":
                authorization = authorization with { Operation = authorization.Operation with { LeaseExpiresAt = null } };
                break;
        }

        var resolver = new ManagedIdentityAzureSecretResolver(new FakeAuthorizationStore(authorization), reader);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await resolver.ResolveAsync(request));

        Assert.Equal("The requested Azure secret is not authorized.", exception.Message);
        Assert.Equal(0, reader.Calls);
    }

    private static ManagedIdentityAzureSecretResolver CreateResolver(FakeReader reader) =>
        new(new FakeAuthorizationStore(Authorization()), reader);

    private static AzureSecretAuthorization InternalAuthorization() => Authorization() with
    {
        Assignment = Authorization().Assignment with { Resources = SqlResources() },
        Operation = Operation() with
        {
            SecretReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AzureManagedSecretReferences.DatabaseConnectionStringName] = ManagedSqlReference
            }
        }
    };

    private static AzureSecretAuthorization ProviderOwnedAuthorization(Guid? instanceId = null) => Authorization() with
    {
        Assignment = Authorization().Assignment with
        {
            InstanceId = instanceId ?? InstanceId,
            Resources = SqlResources()
        },
        Operation = Operation() with
        {
            InstanceId = instanceId ?? InstanceId,
            SecretReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [AzureManagedSecretReferences.DatabaseConnectionStringName] = ManagedSqlReference,
                ["identity:signingkey"] = AzureManagedSecretReferences.IdentitySigningKey,
                ["admin:password"] = AzureManagedSecretReferences.AdminPassword
            }
        }
    };

    private static AzureProviderResourceReferences SqlResources() => new(
        ResourceGroupName: "workload-rg",
        WorkloadIdentityResourceId: "/subscriptions/66666666-6666-6666-6666-666666666666/resourceGroups/workload-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/workload-identity",
        WorkloadIdentityClientId: "77777777-7777-7777-7777-777777777777",
        SqlServerResourceId: "/subscriptions/66666666-6666-6666-6666-666666666666/resourceGroups/workload-rg/providers/Microsoft.Sql/servers/workload-sql",
        SqlServerFqdn: "workload-sql.database.windows.net");

    private static AzureSecretResolutionRequest Request() => new(
        WorkspaceId,
        OrganizationId,
        InstanceId,
        AssignmentId.ToString("D"),
        "database:connectionstring",
        SecretReference)
    {
        OperationId = OperationId,
        AttemptNumber = 1
    };

    private static AzureSecretResolutionRequest RequestFor(Guid instanceId, string name, string reference) => new(
        WorkspaceId,
        OrganizationId,
        instanceId,
        AssignmentId.ToString("D"),
        name,
        reference)
    {
        OperationId = OperationId,
        AttemptNumber = 1
    };

    private static AzureSecretAuthorization Authorization() => new(
        new AzureProviderResourceAssignment(
            AssignmentId,
            WorkspaceId,
            OrganizationId,
            InstanceId,
            new string('c', 64),
            1,
            "66666666-6666-6666-6666-666666666666",
            "workload-rg",
            "workload",
            new string('d', 64),
            "westeurope",
            AzureProviderAssignmentState.Provisioning,
            new AzureProviderResourceReferences(KeyVaultUri: "https://workload-kv.vault.azure.net/"),
            OperationId,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow),
        Operation());

    private static AzureProviderOperation Operation() => new(
        OperationId,
        WorkspaceId,
        "workload",
        AzureProviderOperationAction.Reconcile,
        "idempotency",
        new string('e', 64),
        "operation-identity",
        new string('f', 64),
        new string('0', 64),
        "3.8.0",
        "3.8",
        "combined",
        "dedicated",
        "westeurope",
        "valenceruntimeimages.azurecr.io/runtime-combined",
        "sha256:" + new string('1', 64),
        null,
        null,
        AzureProviderOperationStatus.Running,
        AzureProviderOperationPhase.FoundationSubmitted,
        1,
        1,
        1,
        new AzureProviderResourceReferences(KeyVaultUri: "https://workload-kv.vault.azure.net/"),
        null,
        AzureProviderHealth.Unknown,
        [],
        "test-worker",
        DateTimeOffset.UtcNow.AddMinutes(10),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        SecretReferences: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database:connectionstring"] = SecretReference
        },
        ProviderScopeFingerprint: new string('c', 64),
        OrganizationId: OrganizationId,
        InstanceId: InstanceId,
        LifecycleAction: ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationAction.Create,
        ProviderAssignmentId: AssignmentId);

    private sealed class FakeAuthorizationStore(AzureSecretAuthorization authorization) : IAzureSecretAuthorizationStore
    {
        public Task<AzureSecretAuthorization?> GetAsync(
            Guid workspaceId,
            Guid providerAssignmentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AzureSecretAuthorization?>(authorization);
    }

    private sealed class SwitchingAuthorizationStore(AzureSecretAuthorization authorization) : IAzureSecretAuthorizationStore
    {
        public AzureSecretAuthorization? Current { get; set; } = authorization;

        public Task<AzureSecretAuthorization?> GetAsync(
            Guid workspaceId,
            Guid providerAssignmentId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }

    private sealed class FakeReader : IAzureKeyVaultSecretReader
    {
        public int Calls { get; private set; }
        public Uri? VaultUri { get; private set; }
        public string? Name { get; private set; }
        public string? Version { get; private set; }

        public ValueTask<AzureSecretLease> GetAsync(
            Uri vaultUri,
            string name,
            string version,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            VaultUri = vaultUri;
            Name = name;
            Version = version;
            return ValueTask.FromResult(new AzureSecretLease("resolved-value"));
        }
    }
}
