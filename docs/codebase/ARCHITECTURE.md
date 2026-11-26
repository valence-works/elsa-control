# Architecture

## 1) Architectural Style

- Primary style: modular control plane with layered ports/adapters inside bounded subsystems and an artifact-driven, outbound-pull deployment path.
- Why this classification: the solution separates Deployment, Package Catalog, Runtime Builder, Hosting, and integration packages; API endpoints compose domain services and EF stores; remote runtimes claim durable commands rather than the control plane applying workflow artifacts in-process.
- Primary constraints: control-plane/data-plane separation; workspace/organization authorization; no raw artifacts, credentials, or secrets in control-plane history; provider-neutral desired state.

## 2) System Flow

```text
Studio/console/API -> immutable artifact + desired-state revision -> validation/promotion -> deployment run + durable command -> outbound runtime/provider consumer -> safe result/health report
```

1. A producer submits an artifact or a user creates desired state through workspace APIs.
2. Deployment core validates workspace permissions, structured records, artifact compatibility, and target requirements.
3. Persistence stores immutable revision metadata and a run/command record.
4. A runtime/provider integration polls, claims, verifies and applies the command locally.
5. The integration reports progress and per-artifact outcomes; Elsa Control updates run history and deployed revision.
6. The console reads the resulting deployment, health and diagnostic views.

## 3) Layer/Module Responsibilities

| Layer or module | Owns | Must not own | Evidence |
|-----------------|------|--------------|----------|
| Deployment Abstractions/Core | Desired-state language, artifacts, validation, promotion, run lifecycle | Provider credentials or runtime execution state | `src/Deployment/`; ADR-0004 |
| Package Catalog Core/Persistence | Package governance and relational control-plane state | Deployment-provider implementation | `src/PackageCatalog/`; ADR-0001 |
| Runtime Builder | Image/package selection planning and rendered outputs | Direct environment mutation | `src/RuntimeBuilder/` |
| API/Console | Transport, auth/session, composition, operator UX | Reimplementation of domain policy | `src/Hosting/` |
| Runtime Applier | Target-side command consumption and artifact application | Organization/billing policy | `src/Workflows/ElsaControl.Workflows.RuntimeApplier/` |
| Provisioning | Cloud-neutral engine realization (Pulumi Automation API; Azure first; flag off) | Customer, billing, Hub, or Bicep runner concerns | ADR-0021; `src/Provisioning/` |

## 4) Reused Patterns

| Pattern | Where found | Why it exists |
|---------|-------------|---------------|
| Repository/store ports | `IWorkspaceDeploymentStore`, account/catalog stores | Keep core services testable and persistence replaceable |
| Strategy/registry | artifact type and manifest mapper registries | Add artifact/resource kinds without central switch growth |
| Immutable revision + command | deployment workspace models/services | Retry, audit and rollback without mutating historical intent |
| Composite identity reader | API authentication composition | Support JWT, cookie and trusted-header deployment modes |
| Outbound pull worker | runtime command sync/applier | Avoid broad inbound access to customer infrastructure |

## 5) Known Architectural Risks

- `DeploymentsPage.tsx` and `DeploymentWorkspaceStore.cs` are very large, high-churn concentration points that raise regression and ownership risk.
- ADR-0004 records an incomplete migration from duplicate JSON/string reconciliation logic to a shared typed analytical core.
- Current runtime image topology/version metadata is application configuration with only `latest` tags; it is not yet a governed multi-major version catalog.
- Current Azure assets host Elsa Control on App Service; they do not prove provisioning or reconciliation of Elsa workloads in Azure.

## 6) Evidence

- `README.md`
- `docs/adr/0004-deployment-engine-typed-reconciliation-hybrid.md`
- `src/Hosting/ElsaControl.Api/Program.cs`
- `src/Deployment/ElsaControl.Deployment.Core/Workspace/DeploymentRunService.cs`
- `src/Workflows/ElsaControl.Workflows.RuntimeApplier/`
- `docs/codebase/.codebase-scan.txt`
