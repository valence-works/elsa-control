using ElsaControl.Api.Workspace;
using ElsaControl.Deployment.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;

namespace ElsaControl.Api.Tests;

/// <summary>
/// Exercises production composition, resolution and seeding with isolated process and
/// authorization-store fixtures, not durable persistence or live Azure authentication.
/// Synthetic credential comparison files remain private, transient and outside test output.
/// </summary>
public sealed class AzureProviderCredentialIntegrationTests : IDisposable
{
    private static readonly Guid WorkspaceId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OrganizationId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid InstanceA = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid InstanceB = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid AssignmentA = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid AssignmentB = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid OperationA = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid OperationB = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private const string SubscriptionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string RegistrySubscriptionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    private const string ImageRepository = "valenceruntimeimages.azurecr.io/runtime-combined";
    private const string PrincipalA = "99999999-9999-9999-9999-999999999999";
    private const string PrincipalB = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"elsa-azure-credential-proof-{Guid.NewGuid():N}");

    [PosixFact]
    public async Task Production_composition_seeds_distinct_owned_credentials_and_replays_without_writes()
    {
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        WriteAuthorityFiles();
        var cli = WriteCliBoundary();
        var configuration = Configuration(cli);
        var configuredReferences = ConfiguredAzureSecretResolver.ReadNamedReferences(
            configuration,
            requireProviderOwnedCredentials: true);
        var services = new ServiceCollection();
        var authority = AzureProviderRunnerComposition.AddRunner(services, configuration);
        Assert.NotNull(authority);

        var authorization = new RecordingAuthorizationStore(
            Authorization(authority!.ProviderScopeFingerprint, configuredReferences, InstanceA, AssignmentA, OperationA, "managed-a1", "work-a1", "11111111-1111-1111-1111-111111111111", PrincipalA),
            Authorization(authority.ProviderScopeFingerprint, configuredReferences, InstanceB, AssignmentB, OperationB, "managed-b2", "work-b2", "22222222-2222-2222-2222-222222222222", PrincipalB));
        var reader = new RecordingKeyVaultReader();
        services.AddScoped<IAzureSecretAuthorizationStore>(_ => authorization);
        services.AddSingleton<IAzureKeyVaultSecretReader>(_ => reader);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var resolver = Assert.IsType<ManagedIdentityAzureSecretResolver>(scope.ServiceProvider.GetRequiredService<IAzureSecretResolver>());
        var runner = Assert.IsType<AzureBicepProviderRunner>(scope.ServiceProvider.GetRequiredService<IAzureProviderRunner>());
        var plan = Plan(configuredReferences);
        var commandA = Command(authority, plan, InstanceA, AssignmentA, OperationA, "managed-a1", "work-a1", "11111111-1111-1111-1111-111111111111", PrincipalA);
        var commandB = Command(authority, plan, InstanceB, AssignmentB, OperationB, "managed-b2", "work-b2", "22222222-2222-2222-2222-222222222222", PrincipalB);

        var firstA = await runner.RunAsync(commandA);
        var firstB = await runner.RunAsync(commandB);
        using var replayScope = provider.CreateScope();
        var replayRunner = replayScope.ServiceProvider.GetRequiredService<IAzureProviderRunner>();
        Assert.NotSame(runner, replayRunner);
        var replayA = await replayRunner.RunAsync(commandA with { IsResume = true, AttemptNumber = 2 });
        var replayB = await replayRunner.RunAsync(commandB with { IsResume = true, AttemptNumber = 2 });

        Assert.Equal("azure.step.completed", firstA.Code);
        Assert.Equal(AzureProviderRunnerOutcome.Completed, firstA.Outcome);
        Assert.Equal(AzureProviderRunnerOutcome.Completed, firstB.Outcome);
        Assert.Equal(AzureProviderRunnerOutcome.NoOp, replayA.Outcome);
        Assert.Equal(AzureProviderRunnerOutcome.NoOp, replayB.Outcome);
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, ".state", "distinct-admin-password")));
        Assert.Equal("1", File.ReadAllText(Path.Combine(_root, ".state", "distinct-identity-signing-key")));
        Assert.Equal("6", File.ReadAllText(Path.Combine(_root, ".state", "set-count")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, ".state"), "*.first"));
        Assert.Equal(0, reader.Calls);
        Assert.Equal("azure.step.completed", firstB.Code);
        Assert.Equal("azure.step.no-op", replayA.Code);
        Assert.Equal("azure.step.no-op", replayB.Code);

        var crossInstance = new AzureSecretResolutionRequest(
            WorkspaceId,
            OrganizationId,
            InstanceB,
            AssignmentA.ToString("D"),
            AzureManagedSecretReferences.AdminPasswordName,
            AzureManagedSecretReferences.AdminPassword,
            commandA.Resources);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ResolveAsync(crossInstance));
        Assert.Equal(0, reader.Calls);
        Assert.Equal("6", File.ReadAllText(Path.Combine(_root, ".state", "set-count")));
    }

    private void WriteAuthorityFiles()
    {
        File.WriteAllText(Path.Combine(_root, "main.bicep"), "targetScope = 'resourceGroup'\n");
        File.WriteAllText(Path.Combine(_root, "acr-pull-role.bicep"), "targetScope = 'resourceGroup'\n");
        File.WriteAllText(Path.Combine(_root, "sql-bootstrap.sql"), "SELECT 1;\n");
    }

    private string WriteCliBoundary()
    {
        var path = Path.Combine(_root, "fake-az");
        File.WriteAllText(path, CliScript);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This process-boundary integration test requires a POSIX host.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private IConfiguration Configuration(string cli) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Deployment:AzureProvider:WorkerEnabled"] = "true",
            ["Deployment:AzureProvider:Runner:Enabled"] = "true",
            ["Deployment:AzureProvider:Runner:AzureCliPath"] = cli,
            ["Deployment:AzureProvider:Runner:SqlCmdPath"] = cli,
            ["Deployment:AzureProvider:Runner:CurlPath"] = cli,
            ["Deployment:AzureProvider:Runner:TemplateRoot"] = _root,
            ["Deployment:AzureProvider:Runner:SqlBootstrapObjectId"] = "99999999-9999-9999-9999-999999999999",
            ["Deployment:AzureProvider:Runner:SqlBootstrapLogin"] = "bootstrap",
            ["Deployment:AzureProvider:Runner:SqlBootstrapIp"] = "203.0.113.10",
            ["Deployment:AzureProvider:Runner:RuntimeAdminUsername"] = "runtime-admin",
            ["AZURE_CLIENT_ID"] = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            ["Deployment:AzureProvider:Runner:TargetScope:SubscriptionId"] = SubscriptionId,
            ["Deployment:AzureProvider:Runner:TargetScope:ResourceGroupName"] = "managed-root",
            ["Deployment:AzureProvider:Runner:TargetScope:RegistrySubscriptionId"] = RegistrySubscriptionId,
            ["Deployment:AzureProvider:Runner:TargetScope:RegistryResourceGroupName"] = "registry-root",
            ["Deployment:AzureProvider:Runner:TargetScope:RegistryName"] = "valenceruntimeimages",
            ["Deployment:AzureProvider:Runner:TargetScope:Location"] = "westeurope",
            ["Deployment:AzureProvider:Secrets:0:Name"] = AzureManagedSecretReferences.DatabaseConnectionStringName,
            ["Deployment:AzureProvider:Secrets:0:Reference"] = AzureManagedSecretReferences.SqlConnection,
            ["Deployment:AzureProvider:Secrets:1:Name"] = AzureManagedSecretReferences.IdentitySigningKeyName,
            ["Deployment:AzureProvider:Secrets:1:Reference"] = AzureManagedSecretReferences.IdentitySigningKey,
            ["Deployment:AzureProvider:Secrets:2:Name"] = AzureManagedSecretReferences.AdminPasswordName,
            ["Deployment:AzureProvider:Secrets:2:Reference"] = AzureManagedSecretReferences.AdminPassword
        })
        .Build();

    private static AzureWorkloadPlan Plan(IReadOnlyDictionary<string, string> configuredReferences) => new(
        "unused",
        "westeurope",
        "3.8.0",
        "3.8",
        AzureWorkloadPlanTranslator.SupportedTopology,
        AzureWorkloadPlanTranslator.SupportedIsolation,
        ImageRepository,
        new string('1', 64),
        "oci://registry.example/manifest@sha256:" + new string('2', 64),
        "sha256:" + new string('2', 64),
        "oci://registry.example/signature@sha256:" + new string('3', 64),
        "sha256:" + new string('3', 64),
        new Dictionary<string, string>(configuredReferences, StringComparer.OrdinalIgnoreCase),
        new string('4', 64),
        "5.0.1",
        "5.0.2",
        new AzureWorkloadCapacity(1, 1, 500, 1024));

    private static AzureProviderRunnerCommand Command(
        AzureProviderRunnerAuthority authority,
        AzureWorkloadPlan sourcePlan,
        Guid instanceId,
        Guid assignmentId,
        Guid operationId,
        string resourceGroup,
        string workloadName,
        string clientId,
        string principalId)
    {
        var plan = sourcePlan with { WorkloadName = workloadName };
        var resources = Resources(resourceGroup, workloadName, clientId, principalId);
        return new(
            AzureProviderRunnerStep.SeedSecrets,
            plan,
            resources,
            null,
            false,
            1,
            new(
                WorkspaceId,
                OrganizationId,
                instanceId,
                operationId,
                $"op-{workloadName}",
                $"idempotency-{workloadName}",
                workloadName,
                assignmentId.ToString("D"),
                plan.Fingerprint,
                authority.TemplateFingerprint,
                authority.ProviderScopeFingerprint),
            Assignment(authority.ProviderScopeFingerprint, instanceId, assignmentId, operationId, resourceGroup, workloadName, clientId, principalId, resources));
    }

    private static AzureProviderResourceAssignment Assignment(
        string scopeFingerprint,
        Guid instanceId,
        Guid assignmentId,
        Guid operationId,
        string resourceGroup,
        string workloadName,
        string clientId,
        string principalId,
        AzureProviderResourceReferences resources) => new(
        assignmentId,
        WorkspaceId,
        OrganizationId,
        instanceId,
        scopeFingerprint,
        AzureProviderResourceAssignmentNaming.CurrentVersion,
        SubscriptionId,
        resourceGroup,
        workloadName,
        new string('a', 64),
        "westeurope",
        AzureProviderAssignmentState.Provisioning,
        resources,
        operationId,
        1,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    private static AzureProviderResourceReferences Resources(string resourceGroup, string workloadName, string clientId, string principalId) => new(
        ResourceGroupName: resourceGroup,
        FoundationDeploymentId: $"/subscriptions/{SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/elsa-{workloadName}-foundation",
        WorkloadIdentityResourceId: $"/subscriptions/{SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{workloadName}-identity",
        WorkloadIdentityClientId: clientId,
        WorkloadIdentityPrincipalId: principalId,
        KeyVaultResourceId: $"/subscriptions/{SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.KeyVault/vaults/{workloadName}-kv",
        KeyVaultUri: $"https://{workloadName}-kv.vault.azure.net/",
        SqlServerResourceId: $"/subscriptions/{SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Sql/servers/{workloadName}-sql",
        SqlServerFqdn: $"{workloadName}-sql.database.windows.net",
        ContainerAppsEnvironmentResourceId: $"/subscriptions/{SubscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.App/managedEnvironments/{workloadName}-aca",
        RegistryResourceId: $"/subscriptions/{RegistrySubscriptionId}/resourceGroups/registry-root/providers/Microsoft.ContainerRegistry/registries/valenceruntimeimages",
        AcrPullDeploymentId: AcrDeploymentId(resourceGroup, workloadName, principalId),
        AcrPullRoleAssignmentId: $"/subscriptions/{RegistrySubscriptionId}/resourceGroups/registry-root/providers/Microsoft.ContainerRegistry/registries/valenceruntimeimages/providers/Microsoft.Authorization/roleAssignments/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static string AcrDeploymentId(string resourceGroup, string workloadName, string principalId)
    {
        var identity = $"{SubscriptionId}/{resourceGroup}/{principalId}/{RegistrySubscriptionId}/registry-root/valenceruntimeimages";
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        return $"/subscriptions/{RegistrySubscriptionId}/resourceGroups/registry-root/providers/Microsoft.Resources/deployments/elsa-{workloadName}-{suffix}-acr";
    }

    private static AzureSecretAuthorization Authorization(
        string scopeFingerprint,
        IReadOnlyDictionary<string, string> configuredReferences,
        Guid instanceId,
        Guid assignmentId,
        Guid operationId,
        string resourceGroup,
        string workloadName,
        string clientId,
        string principalId) =>
        new(
            Assignment(scopeFingerprint, instanceId, assignmentId, operationId, resourceGroup, workloadName, clientId, principalId, Resources(resourceGroup, workloadName, clientId, principalId)),
            Operation(scopeFingerprint, configuredReferences, instanceId, assignmentId, operationId, workloadName));

    private static AzureProviderOperation Operation(
        string scopeFingerprint,
        IReadOnlyDictionary<string, string> configuredReferences,
        Guid instanceId,
        Guid assignmentId,
        Guid operationId,
        string workloadName) => new(
        operationId,
        WorkspaceId,
        workloadName,
        AzureProviderOperationAction.Reconcile,
        $"idempotency-{workloadName}",
        new string('b', 64),
        $"op-{workloadName}",
        new string('4', 64),
        new string('5', 64),
        "3.8.0",
        "3.8",
        "combined",
        "Dedicated",
        "westeurope",
        ImageRepository,
        "sha256:" + new string('1', 64),
        null,
        null,
        AzureProviderOperationStatus.Running,
        AzureProviderOperationPhase.FoundationSubmitted,
        1,
        1,
        1,
        new AzureProviderResourceReferences(),
        null,
        AzureProviderHealth.Unknown,
        [],
        "integration-worker",
        DateTimeOffset.UtcNow.AddMinutes(10),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null,
        SecretReferences: new Dictionary<string, string>(configuredReferences, StringComparer.OrdinalIgnoreCase),
        ProviderScopeFingerprint: scopeFingerprint,
        OrganizationId: OrganizationId,
        InstanceId: instanceId,
        LifecycleAction: ElsaControl.Deployment.Abstractions.Instances.ElsaInstanceOperationAction.Reconcile,
        ProviderAssignmentId: assignmentId);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class PosixFactAttribute : FactAttribute
    {
        public PosixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "The real process-boundary fixture requires a POSIX host.";
        }
    }

    private sealed class RecordingAuthorizationStore(params AzureSecretAuthorization[] authorizations) : IAzureSecretAuthorizationStore
    {
        private readonly IReadOnlyDictionary<Guid, AzureSecretAuthorization> _authorizations = authorizations.ToDictionary(x => x.Assignment.Id);

        public Task<AzureSecretAuthorization?> GetAsync(Guid workspaceId, Guid providerAssignmentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_authorizations.TryGetValue(providerAssignmentId, out var authorization) && authorization.Assignment.WorkspaceId == workspaceId
                ? authorization
                : null);
    }

    private sealed class RecordingKeyVaultReader : IAzureKeyVaultSecretReader
    {
        public int Calls { get; private set; }

        public ValueTask<AzureSecretLease> GetAsync(Uri vaultUri, string name, string version, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("The integration proof must not read an external Key Vault secret.");
        }
    }

    private const string CliScript = """
#!/bin/sh
set -eu
umask 077
root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
state="$root/.state"
mkdir -p "$state"
operation="${3:-}"
vault=''
name=''
file=''
query=''
managed=''
assignment=''
instance=''
slot=''
generation=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    --vault-name) vault="$2"; shift 2 ;;
    --name) name="$2"; shift 2 ;;
    --file) file="$2"; shift 2 ;;
    --query) query="$2"; shift 2 ;;
    --tags)
      shift
      for _ in 1 2 3 4 5; do
        tag="$1"
        case "$tag" in
          managed-by=*) managed="${tag#managed-by=}" ;;
          provider-assignment=*) assignment="${tag#provider-assignment=}" ;;
          instance=*) instance="${tag#instance=}" ;;
          secret-slot=*) slot="${tag#secret-slot=}" ;;
          generation=*) generation="${tag#generation=}" ;;
        esac
        shift
      done ;;
    *) shift ;;
  esac
done
if [ "$operation" = "list" ] && [ -n "$query" ]; then
  case "$query" in
    *admin-password*) name='admin-password' ;;
    *identity-signing-key*) name='identity-signing-key' ;;
    *sql-connection*) name='sql-connection' ;;
    *) exit 1 ;;
  esac
  metadata="$state/$vault.$name.json"
  if [ -f "$metadata" ]; then cat "$metadata"; else printf '[]'; fi
  exit 0
fi
if [ "$operation" = "set" ] && [ -n "$file" ]; then
  count_file="$state/set-count"
  count=0
  if [ -f "$count_file" ]; then count=$(cat "$count_file"); fi
  count=$((count + 1))
  printf '%s' "$count" > "$count_file"
  first="$state/$slot.first"
  if [ -f "$first" ]; then
    if cmp -s "$file" "$first"; then printf '0' > "$state/distinct-$slot"; else printf '1' > "$state/distinct-$slot"; fi
    rm -f "$first"
  else
    cp "$file" "$first"
  fi
  printf '[{"managedBy":"%s","assignmentId":"%s","instanceId":"%s","secretSlot":"%s","generation":"%s"}]' "$managed" "$assignment" "$instance" "$slot" "$generation" > "$state/$vault.$name.json"
  exit 0
fi
exit 1
""";
}
