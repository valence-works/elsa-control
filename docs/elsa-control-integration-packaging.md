# Elsa Control Integration Packaging And Host Configuration

Status: initial packaging guidance

Last updated: 2026-05-31

Tracking issue: [#41](https://github.com/valence-works/elsa-control/issues/41)

## Package Decisions

The artifact-driven workflow path has two installable integration roles:

| Role | Package ID | Source repository | Purpose |
| --- | --- | --- | --- |
| Studio producer integration | `Elsa.Studio.PlatformIntegration` | `elsa-core` | Adds **Submit to Elsa Control** actions to Elsa Studio and submits `elsa.workflow-definition` artifact metadata to Elsa Control. |
| Workflow runtime consumer integration | `ElsaControl.Workflows.RuntimeApplier` | `elsa-control` | Polls Elsa Control runtime commands, fetches workflow artifact payloads, verifies them, applies them through a runtime store boundary, and reports results. |

The current Elsa Control repository also contains `ElsaControl.Studio.Submit`. Treat it as shared producer-side primitives for non-Studio producers or a future extraction point. The Studio UI module currently owns its host-specific widgets, notification handling, and request customization in `elsa-core`.

Version compatibility is same-version by default: integration package versions should match the Elsa Control backend package version until the API exposes an explicit compatibility/version negotiation contract.

## Studio Producer Configuration

Install `Elsa.Studio.PlatformIntegration` into a Elsa Control-integrated Elsa Studio host.

Register it with the Studio service collection:

```csharp
using System.Net.Http.Headers;
using Elsa.Studio.PlatformIntegration.Extensions;

builder.Services.AddPlatformIntegrationModule(options =>
{
    options.PlatformEndpoint = new Uri(builder.Configuration["ElsaControl:Endpoint"]!);
    options.WorkspaceId = Guid.Parse(builder.Configuration["ElsaControl:WorkspaceId"]!);
    options.ProducerName = "Elsa Studio";
    options.ProducerVersion = typeof(Program).Assembly.GetName().Version?.ToString();
    options.RequiredCapabilities = ["workflow-definition.apply"];

    options.ConfigureRequestAsync = async (request, cancellationToken) =>
    {
        var accessToken = await tokenProvider.GetElsaControlAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    };
});
```

Recommended settings:

- `PlatformEndpoint`: the external Elsa Studio integration package's existing
  option name for the base Elsa Control URL, for example
  `https://control.example.com`.
- `WorkspaceId`: the workspace that receives submitted artifacts.
- `ConfigureRequestAsync`: host-owned authentication hook. Attach a short-lived Elsa Control access token or trusted service token here.
- `SubmitOnWorkflowPublished`: set to `false` when direct runtime **Publish** remains available as a separate fallback and Elsa Control submission should happen only through the explicit **Submit to Elsa Control** action.
- `PayloadProvider` and `PayloadUriScheme`: keep the defaults unless the host provides a payload service that the runtime applier can fetch and verify.

Security rules:

- Do not store bearer tokens, API keys, or provider credentials in Studio artifact metadata.
- Do not treat **Submit to Elsa Control** as release, promotion, deployment, or runtime publish.
- Elsa Control-integrated Studio should keep direct runtime **Publish** visibly separate from **Submit to Elsa Control** when both are enabled.

## Runtime Applier Configuration

Install `ElsaControl.Workflows.RuntimeApplier` into the Elsa Workflows runtime host that should consume Elsa Control deployment commands.

The runtime host owns four boundaries:

- Elsa Control command authentication.
- Artifact payload transport trust.
- Local Elsa workflow definition persistence.
- Durable apply journal storage.

Minimal registration sketch:

```csharp
using System.Net.Http.Headers;
using ElsaControl.Workflows.RuntimeApplier;

builder.Services.AddTransient<ControlAuthenticationHandler>();

var runtimeOptions = new WorkflowArtifactRuntimeOptions
{
    ControlEndpoint = new Uri(builder.Configuration["ElsaControl:Endpoint"]!),
    WorkspaceId = Guid.Parse(builder.Configuration["ElsaControl:WorkspaceId"]!),
    EngineId = Guid.Parse(builder.Configuration["ElsaControl:EngineId"]!),
    RuntimeVersion = typeof(Program).Assembly.GetName().Version?.ToString(),
    AllowedPayloadReferenceProviders = ["producer-managed"],
    AllowedPayloadHosts = builder.Configuration.GetSection("ElsaControl:AllowedPayloadHosts").Get<string[]>() ?? []
};
runtimeOptions.Validate();

builder.Services.AddSingleton(runtimeOptions);

builder.Services.AddHttpClient<IWorkflowRuntimeCommandClient, WorkflowRuntimeCommandHttpClient>(httpClient =>
{
    httpClient.BaseAddress = runtimeOptions.ControlEndpoint;
}).AddHttpMessageHandler<ControlAuthenticationHandler>();

builder.Services.AddHttpClient<IWorkflowArtifactEnvelopeProvider, WorkflowArtifactControlEnvelopeClient>(httpClient =>
{
    httpClient.BaseAddress = runtimeOptions.ControlEndpoint;
}).AddHttpMessageHandler<ControlAuthenticationHandler>();

builder.Services.AddSingleton<IWorkflowArtifactPayloadFetcher, WorkflowArtifactHttpPayloadFetcher>();
builder.Services.AddSingleton<IWorkflowArtifactSchemaValidator, WorkflowArtifactRuntimeContractValidator>();
builder.Services.AddSingleton<IWorkflowDefinitionApplier, WorkflowDefinitionJsonApplier>();
builder.Services.AddSingleton<IWorkflowArtifactApplyJournal, DurableWorkflowArtifactApplyJournal>();
builder.Services.AddSingleton<IWorkflowDefinitionRuntimeStore, ElsaWorkflowDefinitionRuntimeStore>();
builder.Services.AddSingleton<WorkflowArtifactCommandProcessor>();
```

Example authentication handler:

```csharp
using System.Net.Http.Headers;

public sealed class ControlAuthenticationHandler(IControlTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var accessToken = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
```

The code above is a wiring shape, not a full worker loop. The host should run a background service that polls commands, claims one command, and passes the claim to `WorkflowArtifactCommandProcessor.ProcessAsync`.

`IControlTokenProvider`, `DurableWorkflowArtifactApplyJournal`, and `ElsaWorkflowDefinitionRuntimeStore` are host-provided examples. The integration package defines the contracts and default safety helpers; the host decides how tokens are acquired and how Elsa workflow definitions and apply journal entries are persisted.

Required runtime options:

- `ControlEndpoint`: the base Elsa Control URL.
- `WorkspaceId`: the workspace that owns the runtime engine and commands.
- `EngineId`: the registered Elsa Control deployment engine ID for this runtime.
- `WorkerId`: stable worker identity for command leases. The default is machine/process based; production hosts should set a stable value.
- `Capabilities`: must include `workflow-definition.apply` for workflow artifact deployment.
- `AllowedPayloadReferenceProviders`: approved artifact payload providers.
- `AllowedPayloadHosts`: approved public payload hosts.

Operational guidance:

- Use a durable `IWorkflowArtifactApplyJournal`; the in-memory implementation is for tests and local experiments only.
- Implement `IWorkflowDefinitionRuntimeStore` against the real Elsa Workflows definition store for the target runtime.
- The default HTTP payload fetcher rejects redirects, proxy use, unapproved hosts, non-public addresses, invalid media types, expired references, and oversized payloads.
- If artifacts live on private infrastructure, provide a host-owned `IWorkflowArtifactPayloadFetcher` with equivalent trust checks instead of weakening the default fetcher.
- Heartbeat and claim lease settings must leave enough margin for local validation and apply. Increase `ClaimLeaseDuration` before increasing payload size or apply complexity.

## Advisory Webhook Dispatch

Runtime pull remains the authoritative transport. Elsa Control can optionally deliver advisory webhook notifications to reduce polling latency, but the webhook only says "a command may be available." The runtime must still call the runtime command poll/claim API before applying anything.

Elsa Control host configuration:

```json
{
  "Deployment": {
    "WebhookDispatch": {
      "Enabled": false,
      "BatchSize": 25,
      "PollInterval": "00:00:15",
      "NotificationPath": "/api/elsa-control/deployment-command-notifications"
    }
  }
}
```

When enabled, Elsa Control posts the existing safe notification payload to each target engine's registered `BaseUrl` plus `NotificationPath`. The payload contains workspace ID, engine ID, command hint, and reason only. It never contains lease tokens, raw workflow content, artifact payloads, credentials, or secret values.

Runtime hosts that opt into webhook-triggered fetch should expose `NotificationPath`, validate that the notification came from a trusted Elsa Control instance using host-owned authentication or network policy, and then wake the normal poll/claim loop. The endpoint should be idempotent because duplicate webhook delivery is expected.

Use [Runtime Transport Trust Policy](runtime-transport-trust-policy.md) for the credential bootstrap, rotation, payload trust, and webhook network trust checklist.

## Configuration Shape

Suggested host configuration keys:

```json
{
  "ElsaControl": {
    "Endpoint": "https://control.example.com",
    "WorkspaceId": "00000000-0000-0000-0000-000000000001",
    "EngineId": "00000000-0000-0000-0000-000000000101",
    "AllowedPayloadHosts": [ "artifacts.example.com" ],
    "Auth": {
      "Mode": "ClientCredentials",
      "CredentialReference": "elsa-control-runtime-client"
    }
  }
}
```

Secrets referenced by `ElsaControl:Auth` must resolve through the host's secret provider. They must not be written into artifact records, desired-state records, command records, or diagnostics.

## Publishing Checklist

Before publishing the integration packages externally:

- Confirm package IDs and repository ownership match the table above.
- Publish `Elsa.Studio.PlatformIntegration` from `elsa-core` (confirm publish source and package ID before release).
- Publish `ElsaControl.Workflows.RuntimeApplier` from `elsa-control`.
- Keep `ElsaControl.Studio.Submit` internal unless a non-Studio producer or shared-contract extraction needs it.
- Document the same-version compatibility rule in package release notes.
- Keep sample host wiring for Studio and runtime applications current.
- Execute the E2E smoke path before declaring the packages ready for production use.
- Keep advisory webhook dispatch disabled until runtime endpoint trust, network reachability, and credential rotation policies are configured for the target environment.
- Apply the runtime transport trust checklist before enabling production runtime command sync.
