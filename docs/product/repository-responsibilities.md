# Elsa Commercial Platform Repository Responsibilities

Date: 2026-08-29

## Authority Map

| Repository | Canonical responsibility | Must not become | Current action |
|------------|--------------------------|-----------------|----------------|
| [`valence-works/elsa-control`](https://github.com/valence-works/elsa-control) | Commercial control plane, PRD/program, organizations/workspaces, package governance, Runtime Builder, desired state, deployment/provider orchestration, operations and shared web product | Elsa workflow runtime or a cloud-provider-specific application model | canonical program home |
| [`elsa-workflows/elsa-specifications`](https://github.com/elsa-workflows/elsa-specifications) | Data-only package manifest schema, validation and build-time generator | catalog persistence, Nuplane, image or SaaS policy | consume as shared contract; keep runtime-kind/version evolution coordinated |
| [`elsa-workflows/elsa-core`](https://github.com/elsa-workflows/elsa-core) | Elsa 3 runtime/modules + Studio + Studio-to-Control integration | Valence SaaS tenancy/billing/provisioning | add only stable runtime hooks that benefit general Elsa |
| [`valence-works/nuplane`](https://github.com/valence-works/nuplane) | Host-neutral deterministic NuGet acquisition, reconciliation, storage and loading | Elsa-specific feature policy, package sandbox or control-plane deployment provider | define explicit Elsa Control/runtime boundary before integration |
| [`valence-works/elsa-production-image`](https://github.com/valence-works/elsa-production-image) | Current commercial Elsa 3.8 Server, Studio and Combined image definitions/pipeline, CE/paid publication, SBOM/provenance/scanning/signing and producer release facts | control-plane lifecycle catalog or customer desired state | authoritative commercial image source; publish the signed release manifest described in the [commercial image audit](commercial-image-release-audit.md) |
| [`valence-works/elsa-pro-docker`](https://github.com/valence-works/elsa-pro-docker) | Historical commercial image precursor | active image source | deprecate/archive after consumer audit |
| [`elsa-workflows/elsa-foundation`](https://github.com/elsa-workflows/elsa-foundation) | Elsa 4/Foundation runtime host and modular platform building blocks | automatic replacement for Elsa 3 contracts without migration design | define the Elsa 4 commercial distribution path explicitly |
| [`elsa-workflows/elsa-foundation-studio`](https://github.com/elsa-workflows/elsa-foundation-studio) | Elsa 4 modular Studio host/frontend | Elsa Control web or SaaS portal | coordinate identity/open-Elsa and submission/runtime contracts |
| [`elsa-workflows/elsa-apps`](https://github.com/elsa-workflows/elsa-apps) | Retired; folded into elsa-core. Historical official OSS Elsa app/Docker image definitions | Valence commercial support/provenance policy | retired; folded into elsa-core |
| Planned [`elsa-workflows/elsaworkflows.io`](https://github.com/elsa-workflows/elsaworkflows.io) | Canonical public acquisition source, docs/product navigation, pricing and signup calls to action | authenticated control-plane product state | create the repository, import the currently deployed source and establish GitHub-controlled Cloudflare Pages deployment |
| [`elsa-workflows/elsa-package-catalog`](https://github.com/elsa-workflows/elsa-package-catalog) | Historical catalog source/history | active catalog authority | already deprecated by ADR-0003; archive after consumer confirmation |

The deleted `valence-works/elsa-platform-saas` repository was scaffolding only. It has no active responsibility and creates no migration workstream; billing and subscription capabilities are designed directly in Elsa Control.

## Machine-Readable Flow

```text
package source
  -> elsa-specifications generator emits elsa-package.json
  -> Elsa Control ingests/validates/approves and projects features
  -> Runtime Builder resolves image + packages + compatibility
  -> commercial image provides signed runtime topology and Nuplane host
  -> Elsa Control creates immutable desired-state/deployment command
  -> runtime/provider consumes, verifies, applies and reports
```

Elsa Control does not currently call Nuplane. For the initial proof this is intentional: Nuplane remains runtime/image-side while the control plane catalogs and plans package intent. Direct Control-to-Nuplane orchestration requires a bounded API/ownership spike before the Private/custom-package phase.

## Current Commercial Image Facts

- `elsa-production-image` publishes paid `runtime-server`, `runtime-studio` and `runtime-combined` images to `valenceruntimeimages.azurecr.io` and CE equivalents to GitHub Container Registry.
- The pipeline builds linux/amd64 and linux/arm64 images, scans HIGH/CRITICAL vulnerabilities, emits SBOM/provenance, signs with keyless cosign and verifies CE/paid digest equality.
- Server/Combined contain runtime, Nuplane and database state paths; Studio-only does not contain the Elsa runtime or Nuplane.
- Current configured Elsa 3.8 component versions are separate preview lines; Foundation/Elsa 4 has an independent package/image ecosystem.
- Elsa Control still advertises obsolete `elsaworkflows/elsa-pro-* : latest` metadata, which does not match the current image repository/registry contract.
- The latest observed commercial build (2026-08-17, production-image commit `1aeee8df455b`) publishes build-79 immutable paid tags and mutable Community/version aliases. Server/Combined target Elsa Core `3.8.0-preview.5413`; Studio targets Elsa Studio `3.8.0-preview.1667`; each topology has its own multi-platform index digest.
- The workflow enables SBOM, max provenance, Trivy fixable HIGH/CRITICAL gating and keyless cosign signatures, but does not publish a single signed machine-readable release manifest. See the [commercial image release and topology authority audit](commercial-image-release-audit.md) for exact digests, registry references and verification evidence.

## Cross-Repository Contract Risks

1. Studio-to-Control uses route strings, JSON, ZIP layout, artifact schema `1.0` and capability `loom.recipe.apply` without a generated/shared client contract.
2. No central matrix governs Elsa engine, Studio, extension, Nuplane and CShells feature-framework version compatibility across Elsa 3 and Elsa 4.
3. Runtime Builder does not model image digest, signature, SBOM, provenance or release channel.
4. Separate Server/Studio topology needs both browser-facing and container-facing backend URLs plus CORS; current generated metadata/templates do not clearly model both.
5. The current `elsaworkflows.io` production bundle has no discoverable authoritative GitHub source. The planned canonical repository and Cloudflare Pages pipeline must import and verify that source before public product changes.

## Required Governance

- Changes to shared wire/package/image contracts require linked issues in both owning and consuming repositories.
- Elsa Control issues own product outcome and dependency tracking; implementation issues live in the repository that owns the changed contract.
- Commercial image releases must publish immutable metadata consumable by the Elsa Control release catalog.
- The commercial image release manifest is producer-owned; Elsa Control verifies and projects it, while the package specifications repository remains authoritative only for package-manifest and package-compatibility facts.
- Elsa 3 and Elsa 4 remain explicit product generations with separate compatibility facts; common abstractions may unify them only where evidence supports it.

## Evidence

- `valence-works/elsa-control`: `README.md`, `Directory.Packages.props`, `src/PackageCatalog/`, `src/RuntimeBuilder/`, `src/Deployment/`
- `valence-works/elsa-production-image`: `config/`, Dockerfiles, `.github/workflows/build-and-push.yml`
- `valence-works/nuplane`: repository README and core acquisition/reconciliation packages
- `elsa-workflows/elsa-core`, `elsa-foundation`, `elsa-foundation-studio`: local source and release workflows
- `elsa-workflows/elsa-specifications`: README, package projects and CI/release workflows
- `docs/product/commercial-image-release-audit.md`: cross-repository evidence, current examples and the proposed release-manifest ownership boundary
- ADR-0001 and ADR-0003 in this repository
