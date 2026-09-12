# Production Azure workload templates

This directory is the bounded Azure resource authority used by the managed-instance provider runner. It is separate from the disposable validation stack and has no lifecycle or cleanup semantics tied to a temporary run.

## Contract

- `main.bicep` is a resource-group deployment for one managed workload: user-assigned identity, Key Vault, Azure SQL, Log Analytics, a Container Apps environment and (when `deployWorkload` is true) the HTTPS workload app.
- `acr-pull-role.bicep` is deployed in the existing runtime registry resource group because the `AcrPull` assignment is scoped to the registry. Its contract is `registryName`, `workloadIdentityId` and `workloadPrincipalId`.
- `sql-bootstrap.sql` is executed once by the configured Microsoft Entra SQL administrator. The host substitutes `__WORKLOAD_IDENTITY_NAME__` and `__WORKLOAD_IDENTITY_CLIENT_ID__`; no secret or password is part of the file.
- `modules/` contains only the resource-shaped modules referenced by `main.bicep`. The three files above remain at the directory root because the provider runner resolves them by name.

The image is immutable: callers provide a repository and a 64-character SHA-256 digest, and the app receives `repository@sha256:digest`. Outputs are limited to resource identifiers, endpoint and safe fingerprint metadata. Secret values, connection strings and provider credentials stay outside the template and its outputs.

## Release data

The template does not select an Elsa generation or feed by branching on a version. `elsaVersion`, `releaseLine`, `sqlWorkflowPackageVersion` and `sqlQuartzPackageVersion` are caller-supplied values and are included in the deterministic plan identity. `releaseVersion` can carry a producer release version independently of the runtime version. The release package feed name and service index are configurable; the generic public NuGet index is only the safe default and can be replaced by the producer-owned feed for a release.

The workload name, owner, plan fingerprint and release values are retained as safe resource tags and as `ELSA_RELEASE_LINE` / `ELSA_RELEASE_VERSION` environment metadata. This keeps ownership and release provenance visible without embedding a particular Elsa release line in infrastructure code.

## Workload capacity

The workload is sized from the resolved plan's governed capacity, never from template literals. `workloadMinReplicas`, `workloadMaxReplicas`, `workloadCpu` and `workloadMemory` are required and have no defaults, so a caller that omits them fails the deployment instead of scaling to zero. The provider runner maps the plan's millicores and MiB to the exact Azure Container Apps consumption pair (0.25/0.5Gi through 2/4Gi in 0.25 vCPU steps) and refuses a plan with no exact pair, a minimum outside 0-300 or a maximum outside 1-300 before any Azure call. The container-app module additionally selects its resources by the requested pair, so an unlisted combination fails the deployment rather than being rounded.

Consumption ephemeral storage is derived from the CPU size (up to 2 GiB for 0.5 vCPU, 4 GiB for 1 vCPU, 8 GiB above that) and cannot be set, so it is not a template parameter; plan admission rejects a capacity that asks for more than its CPU size provides. Capacity is part of the provider plan fingerprint and of `planFingerprint` here, so a capacity change always produces a new revision.

## Managed Elsa handoff

The console's Open action sends the browser to the workload's `/managed-elsa/handoff/start`. The runtime maps that endpoint only when its `ManagedElsa:Handoff` section is enabled and complete, so the template configures it from typed, non-secret parameters. `managedHandoffEnabled` is required and has no default. The provider runner sets it to true only when the admitted release declares `managed-elsa-handoff-v1` and the workload runs as a single replica (the runtime keeps handoff state and sessions in process); otherwise the app receives `ManagedElsa__Handoff__Enabled=false` explicitly.

When enabled, the runner supplies:

| Parameter | Runtime setting | Source |
| --- | --- | --- |
| `managedHandoffInstanceId` | `InstanceId` | Control's instance ID (lowercase canonical) |
| `managedHandoffAudience` | `Audience` | `urn:elsa:instance:<id>`, Control's identity-binding rule |
| `managedHandoffControlBaseUrl` | `ControlBaseUrl` | Control's `ControlPlane:Origin`; the runtime redeems at `/api/managed-elsa/handoff/redeem` |
| `managedHandoffControlContinuationUrl` | `ControlContinuationUrl` | `ControlPlane:Origin` + `/admin/runtimes`, the console page that completes the handoff |
| `managedHandoffRuntimeMaximumLifetime` | `RuntimeMaximumLifetime` | Control's `ManagedElsa:Handoff:RuntimeSessionMaximumLifetime` (at most 8 hours) |
| `managedHandoffRuntimePermissions` | `RuntimePermissions__N` | Control's operator grant |

`CallbackUri` is not a parameter. The template derives it as `https://<workloadName>-app.<environment default domain>/managed-elsa/handoff/callback`, which is the origin Container Apps serves the external app on and the origin Control binds the instance's handoff callback to; the environment exists from the foundation phase, so no ordering gap arises. The runner rejects a workload deployment whose `managedHandoffCallbackUri` output differs from the callback derived from its `containerAppEndpoint` output. `UpstreamAuthenticationScheme` (`Jwt-or-ApiKey`), `SuccessPath` (`/`) and `StateLifetime` (`00:05:00`) are the runtime's documented values, and `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` lets the runtime observe the HTTPS scheme that Container Apps ingress terminates. An enabled handoff with any empty input, or on more than one replica, fails the deployment instead of producing a revision whose startup validation would reject it or whose replicas would reject each other's callbacks and sessions.

## Identity and data protection

The SQL server uses Microsoft Entra-only administration and the workload identity is created as a contained service-principal user by `sql-bootstrap.sql`. Key Vault uses RBAC: the workload can read secrets and the bootstrap operator can seed them, but neither receives broad vault administration through the template. SQL backup retention remains explicit so the provider can make its own recovery decision.

Production deployment is deliberately bounded to the provider's governed regions and public HTTPS ingress profile. Private networking, edge routing, topology expansion and release admission remain separate provider/catalog decisions.

The provider's user-assigned managed identity must live in the workload subscription. Startup verifies the CLI account's selected client ID against the pinned CLI account format, resolves its object ID through Azure Resource Manager, and requires that object ID to equal the SQL/vault bootstrap authority. Role checks use the object ID without Microsoft Graph lookups. The initial profile requires Contributor plus role-assignment administration (or Owner) at the workload subscription because each instance creates a new dedicated resource group. Registry deployment and pull-role authority are checked separately. The configured anchor resource group must already exist; permissions on that group alone are insufficient for instance provisioning.
