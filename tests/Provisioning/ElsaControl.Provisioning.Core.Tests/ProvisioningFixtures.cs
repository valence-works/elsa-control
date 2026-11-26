using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core.Tests;

internal static class ProvisioningFixtures
{
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

    public static ProvisioningTarget EngineTarget(string stackName, string platformStackName = "staging") =>
        new(
            FakeInfrastructureProvider.Id,
            ProvisioningStackKind.Engine,
            stackName,
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
