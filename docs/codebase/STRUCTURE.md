# Codebase Structure

## 1) Top-Level Map

| Path | Purpose | Evidence |
|------|---------|----------|
| `src/Deployment/` | Desired-state, artifact, reconciliation, promotion, run, and runtime-command contracts/services | project files and `README.md` |
| `src/Provisioning/` | Cloud-neutral engine provisioning module (Pulumi Automation API; Azure first; feature-flagged off) | ADR-0021; `ElsaControl.Provisioning.*` |
| `src/PackageCatalog/` | Package metadata, compatibility, accounts/organizations/workspaces, and EF persistence | project files and ADR-0001 |
| `src/RuntimeBuilder/` | Image metadata, package planning, saved configurations, bundles, and output templates | project files and `README.md` |
| `src/Hosting/` | API, React console, Aspire AppHost, and shared service defaults | `src/Hosting/ElsaControl.Api/Program.cs` |
| `src/Studio/`, `src/Workflows/` | Producer and runtime-applier integration packages | `docs/elsa-control-integration-packaging.md` |
| `src/Weaver/` | Assisted investigation and plan drafting subsystem | `docs/weaver-configuration.md` |
| `tests/` | Mirrors production subsystem boundaries | test project directories |
| `specs/` | Spec Kit feature history and active delivery specifications | `specs/*/spec.md` |
| `infra/` | Current generated/customized Azure deployment for the Elsa Control host | `infra/main.bicep`; `azure.yaml` |
| `docs/adr/` | Accepted architecture decisions | ADR files |

## 2) Entry Points

- Main API runtime: `src/Hosting/ElsaControl.Api/Program.cs`.
- Local/publish orchestrator: `src/Hosting/ElsaControl.AppHost/AppHost.cs`.
- Console bootstrap: `src/Hosting/ElsaControl.Console/src/main.tsx` and `src/Hosting/ElsaControl.Console/src/app/routes.tsx`.
- Runtime command consumer: `src/Workflows/ElsaControl.Workflows.RuntimeApplier/`.
- Entry selection is through `dotnet run --project ...`, Aspire, Vite scripts, or host package registration.

## 3) Module Boundaries

| Boundary | What belongs here | What must not be here |
|----------|-------------------|------------------------|
| Deployment | Provider-neutral desired state, immutable artifacts, validation/diff, promotion and command lifecycle | Azure-specific product concepts or Elsa workflow execution state |
| Package Catalog | Package/source governance, compatibility and customer control-plane persistence | Runtime apply behavior |
| Runtime Builder | Resolve selections into plans, bundles and deployment outputs | Bypass deployment validation/history |
| Hosting | Composition, transport, authentication and UI | Duplicate domain rules already owned by core services |
| Provisioning | Cloud-neutral engine spec, operation state machine, pluggable providers, state backends, and credentials | Customer, billing, Hub, or Bicep-runner concerns; secret values |
| Integrations | Elsa Studio submission and runtime-side application | General control-plane policy |

## 4) Naming and Organization Rules

- C# projects, files, namespaces and public types use PascalCase under `ElsaControl.<Subsystem>`.
- React feature folders use kebab-case; component files and exported components use PascalCase.
- C# is organized by bounded subsystem then layer; console code is organized by product feature.
- TypeScript uses the `@/` alias defined by the console TypeScript/Vite configuration.

## 5) Evidence

- `ElsaControl.sln`
- `README.md`
- `docs/adr/0001-package-catalog-control-consolidation.md`
- `src/Hosting/ElsaControl.Console/src/app/routes.tsx`
- `src/Hosting/ElsaControl.Api/Program.cs`
