# Control-Integrated Studio Sample

This sample shows how an Elsa Studio host wires the upstream
`Elsa.Studio.PlatformIntegration` module so workflow authors can choose
**Submit to Elsa Control** from the workflow editor toolbar, workflow
definition list bulk menu, and workflow definition row menu.
Studio now lives in `elsa-core`.

## Package

Add the Studio integration package to the Elsa Studio host:

```xml
<PackageReference Include="Elsa.Studio.PlatformIntegration" Version="0.0.1" />
```

Studio now lives in `elsa-core`. Use the same package version as the Control backend until an explicit Control API compatibility range is available.

## Program Wiring

```csharp
using System.Net.Http.Headers;
using Elsa.Studio.PlatformIntegration.Extensions;

var controlTokenProvider = new ControlTokenProvider(builder.Configuration);
builder.Services.AddSingleton(controlTokenProvider);

builder.Services.AddPlatformIntegrationModule(options =>
{
    options.Enabled = builder.Configuration.GetValue("ElsaControl:Submit:Enabled", true);
    options.ControlEndpoint = new Uri(builder.Configuration["ElsaControl:Endpoint"]!);
    options.WorkspaceId = Guid.Parse(builder.Configuration["ElsaControl:WorkspaceId"]!);
    options.ProducerName = "Elsa Studio";
    options.ProducerVersion = typeof(Program).Assembly.GetName().Version?.ToString();
    options.ArtifactSchemaVersion = "1.0";
    options.RequiredCapabilities = ["workflow-definition.apply"];

    // Keep direct runtime Publish separate from Control submission.
    options.SubmitOnWorkflowPublished = false;

    options.ConfigureRequestAsync = async (request, cancellationToken) =>
    {
        var accessToken = await controlTokenProvider.GetControlAccessTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    };
});
```

`ControlTokenProvider` is host-owned. Implement it with the same identity provider and secret rules used by the Studio deployment. Do not write bearer tokens or client secrets into artifact metadata.

## Configuration

Start from [appsettings.example.json](appsettings.example.json).

Required values:

- `ElsaControl:Endpoint`: base Control URL.
- `ElsaControl:WorkspaceId`: workspace receiving submitted artifacts.
- `ElsaControl:Submit:Enabled`: feature flag for the Studio module.
- `ElsaControl:Auth:CredentialReference`: reference to a host-owned credential, not a raw secret.

## UX Behavior

When enabled and configured, Studio shows **Submit to Elsa Control** beside existing publish surfaces:

- Workflow editor toolbar.
- Workflow definition list bulk action menu.
- Workflow definition row action menu.

Submission creates an immutable Control artifact. It does not release, promote, deploy, or make the workflow immediately executable.
