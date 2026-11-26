using System.Text.Json;
using ElsaControl.Provisioning.Abstractions;

namespace ElsaControl.Provisioning.Core;

public static class ProvisioningRequestSerializer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(ProvisioningRequest request) =>
        JsonSerializer.Serialize(RequestRecord.From(request), Json);

    public static ProvisioningRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<RequestRecord>(json, Json)!.ToRequest();

    private sealed record RequestRecord(
        ProvisioningCommand Command,
        string RuntimeImage,
        string[] Capabilities,
        string? DatabaseKind,
        string? SecretsKind,
        string? EndpointKind,
        string? ScaleKind,
        string ProviderId,
        ProvisioningStackKind StackKind,
        string StackName,
        string CredentialKind,
        string? CredentialLocator,
        string StateBackendKind,
        string StateBackendLocator,
        string SecretsProviderKind,
        string SecretsProviderLocator,
        string? PlatformStackName,
        string IdempotencyKey,
        Dictionary<string, string> StackConfig)
    {
        public static RequestRecord From(ProvisioningRequest request) =>
            new(
                request.Command,
                request.Spec.RuntimeImage,
                request.Spec.RequiredCapabilities.Order(StringComparer.Ordinal).ToArray(),
                request.Spec.DatabaseKind,
                request.Spec.SecretsKind,
                request.Spec.EndpointKind,
                request.Spec.ScaleKind,
                request.Target.ProviderId,
                request.Target.StackKind,
                request.Target.StackName,
                request.Target.Credentials.Kind,
                request.Target.Credentials.Locator,
                request.Target.StateBackend.Kind,
                request.Target.StateBackend.Locator,
                request.Target.SecretsProvider.Kind,
                request.Target.SecretsProvider.Locator,
                request.Target.PlatformStackName,
                request.IdempotencyKey,
                new Dictionary<string, string>(request.StackConfig, StringComparer.Ordinal));

        public ProvisioningRequest ToRequest() =>
            new(
                Command,
                new EngineSpec(
                    RuntimeImage,
                    new HashSet<string>(Capabilities, StringComparer.Ordinal),
                    DatabaseKind,
                    SecretsKind,
                    EndpointKind,
                    ScaleKind),
                new ProvisioningTarget(
                    ProviderId,
                    StackKind,
                    StackName,
                    new CredentialReference(CredentialKind, CredentialLocator),
                    new StateBackendReference(StateBackendKind, StateBackendLocator),
                    new SecretsProviderReference(SecretsProviderKind, SecretsProviderLocator),
                    PlatformStackName),
                IdempotencyKey,
                StackConfig);
    }
}
