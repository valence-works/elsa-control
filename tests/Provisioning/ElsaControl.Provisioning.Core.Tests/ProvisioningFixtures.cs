using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

internal static class ProvisioningFixtures
{
    public static readonly Guid EngineA = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-111111111111");
    public static readonly Guid EngineB = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-222222222222");

    public static string EngineName(Guid? instanceId = null) =>
        ProvisioningStackName.ForEngine(instanceId ?? EngineA);

    public static EngineSpec EngineSpec() =>
        new(
            "elsa-server:test",
            new HashSet<string>(StringComparer.Ordinal)
            {
                ProvisioningCapability.ContainerRuntime,
                ProvisioningCapability.ManagedSql,
                ProvisioningCapability.Secrets,
                ProvisioningCapability.Ingress,
                ProvisioningCapability.Scale
            },
            databaseKind: "managed-sql",
            secretsKind: "managed-secrets",
            endpointKind: "https-ingress",
            scaleKind: "consumption");

    public static EngineSpec PlatformSpec(string environmentName = "staging") =>
        ElsaControl.Provisioning.Abstractions.EngineSpec.PlatformEnvironment(environmentName);

    public static ProvisioningTarget PlatformTarget(string stackName = "staging") =>
        new(
            FakeInfrastructureProvider.Id,
            ProvisioningStackKind.Platform,
            stackName,
            new CredentialReference(CredentialReference.InMemoryKind),
            new StateBackendReference(StateBackendReference.InMemoryKind, "memory://provisioning"),
            new SecretsProviderReference(SecretsProviderReference.InMemoryKind, "memory://secrets"));

    public static ProvisioningTarget EngineTarget(string? stackName = null, string platformStackName = "staging") =>
        new(
            FakeInfrastructureProvider.Id,
            ProvisioningStackKind.Engine,
            stackName ?? EngineName(),
            new CredentialReference(CredentialReference.InMemoryKind),
            new StateBackendReference(StateBackendReference.InMemoryKind, "memory://provisioning"),
            new SecretsProviderReference(SecretsProviderReference.InMemoryKind, "memory://secrets"),
            platformStackName);

    public static ProvisioningRequest Request(
        ProvisioningCommand command,
        ProvisioningTarget target,
        EngineSpec spec,
        string idempotencyKey,
        IReadOnlyDictionary<string, string>? config = null) =>
        new(command, spec, target, idempotencyKey, config);
}
