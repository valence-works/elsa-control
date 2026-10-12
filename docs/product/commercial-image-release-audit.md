# Commercial Elsa Image Release and Topology Authority Audit

Date: 2026-08-29
Issue: [#105](https://github.com/valence-works/elsa-control/issues/105)
Status: Evidence complete; producer release-manifest follow-up completed

## Executive finding

The current commercial Elsa 3 image authority is
[`valence-works/elsa-production-image`](https://github.com/valence-works/elsa-production-image).
It owns the image definitions, component pins, build workflow, registry publication and
supply-chain steps for the commercial `runtime-server`, `runtime-studio` and
`runtime-combined` images. Elsa Control owns the future catalog projection and customer
desired state; it must not become a second hand-maintained image-release authority.

At the time of this audit, release facts were distributed across
`Directory.Packages.props`, the GitHub Actions matrix, Dockerfiles, OCI labels and
registry state. [`elsa-production-image#27`](https://github.com/valence-works/elsa-production-image/issues/27)
subsequently delivered the producer-published signed release manifest. The #147 proof
verified a manifest, its immutable images, signatures and retained supply-chain evidence
as a unit. Elsa Control catalog admission and projection remain follow-up implementation.

The first observed commercial line was Elsa 3.8 preview. At the time of the audit, the
exact examples were the build-79 images published from production-image commit
[`1aeee8df455b`](https://github.com/valence-works/elsa-production-image/commit/1aeee8df455b21cf3bf3d2b26dfbd512d76da27b).
The Elsa 4 Foundation and Foundation Studio repositories are a separate, upstream
preview ecosystem. No approved Elsa 4 commercial distribution authority was found.

## Method and evidence boundary

The audit cross-checked the default branches and published metadata of the following
repositories and registries on 2026-08-29 UTC. Remote facts are cited at the source;
local Elsa Control paths identify the stale consumer behavior that this task does not
replace.

- [Commercial image workflow](https://github.com/valence-works/elsa-production-image/blob/main/.github/workflows/build-and-push.yml)
- [Commercial image package pins](https://github.com/valence-works/elsa-production-image/blob/main/Directory.Packages.props)
- [Commercial image README](https://github.com/valence-works/elsa-production-image/blob/main/README.md)
- [Official OSS application-image workflows](https://github.com/elsa-workflows/elsa-apps/tree/main/.github/workflows) (historical evidence; `elsa-apps` is retired and folded into `elsa-core`)
- [Elsa 3 Core releases](https://github.com/elsa-workflows/elsa-core/releases)
- [Elsa 3 Studio releases](https://github.com/elsa-workflows/elsa-core/releases)
- [Elsa 4 Foundation Docker workflow](https://github.com/elsa-workflows/elsa-foundation/blob/main/.github/workflows/docker.yml)
- [Elsa 4 Foundation package workflow](https://github.com/elsa-workflows/elsa-foundation/blob/main/.github/workflows/packages.yml)
- [Elsa 4 Foundation Studio Docker workflow](https://github.com/elsa-workflows/elsa-foundation-studio/blob/main/.github/workflows/docker.yml)
- [Shared package-manifest schema](https://github.com/elsa-workflows/elsa-specifications/tree/main/src/Elsa.Specifications.PackageManifests)
- [Elsa Control runtime-image configuration](https://github.com/valence-works/elsa-control/blob/main/src/Hosting/ElsaControl.Api/appsettings.json)
- [Elsa Control image catalog model](https://github.com/valence-works/elsa-control/tree/main/src/RuntimeBuilder)

Registry observations were made against the public GHCR Community images and the
authenticated ACR subscription already configured for this workstation. No image was
published, changed or deleted as part of this audit.

## Authority captured by the initial audit

The gaps in this table are the 2026-08-29 audit findings. The signed-manifest gaps
were subsequently closed by `elsa-production-image#27`; consumer-side gaps remain.

| Metadata | Current source of truth | Current evidence | Required Elsa Control behavior | Gap / owner |
|---|---|---|---|---|
| Commercial image definitions and topology build inputs | `valence-works/elsa-production-image` | The workflow matrix names `runtime-server`, `runtime-studio` and `runtime-combined`; the three Dockerfiles define their composition. | Project only verified producer metadata into the catalog. | At audit time no signed release manifest existed; `elsa-production-image#27` later closed this producer gap. |
| Elsa runtime package set | `elsa-production-image/Directory.Packages.props` | Elsa Core packages are pinned to `3.8.0-preview.5413` at lines 23–44. | Store the exact component set and release line, not a mutable tag. | Package pins are source files, not a versioned release record; producer owns the projection into a manifest. |
| Elsa Studio package set | `elsa-production-image/Directory.Packages.props` | Elsa Studio packages are pinned separately to `3.8.0-preview.1667` at lines 46–61. | Treat Studio version as an independent component identity. | A Studio image cannot safely reuse the Server/Combined tag: its version tag is different. |
| Nuplane and CShells host dependencies | `elsa-production-image/Directory.Packages.props` | Current pins are Nuplane `0.0.10` and CShells `0.0.28` at lines 63–78. | Include exact host dependency versions in the resolved plan or component-set digest. | No central compatibility matrix ties these dependencies to each Elsa release. |
| Paid image registry/reference | Commercial image workflow and ACR | `valenceruntimeimages.azurecr.io/runtime-server`, `runtime-studio` and `runtime-combined`. | Resolve the paid image by immutable digest after entitlement/access checks. | The registry reference and customer access policy are not in a release manifest. |
| Community image registry/reference | Commercial image workflow and GHCR | `ghcr.io/valence-works/runtime-ce-server`, `runtime-ce-studio` and `runtime-ce-combined`. | Keep Community and paid references distinct while verifying digest parity when promised. | Community has no exact-build tags by policy; this is not suitable for reproducible paid deployment. |
| Image tags | `build-and-push.yml` metadata steps | Paid receives semver, `sha-*`, `latest`, Elsa-version and `<version>-build.<run>` tags; Community receives only `latest` and Elsa-version tags. | Tags may be discovery hints only; provider resolution uses a verified digest. | `latest` and Elsa-version tags move. The production-image repository currently has no Git tag or GitHub Release, despite the workflow accepting `v*` pushes. |
| Image digest | Registry manifest | Build-79 index digests are listed below and are equal between ACR and GHCR for each corresponding image. | Store and deploy the multi-platform index digest; retain platform manifest digests if platform-specific evidence is needed. | No catalog ingestion or signature-to-digest verification exists yet. |
| Source revision and image labels | OCI config labels emitted by Docker metadata | Build-79 images identify source revision `1aeee8df455b`; `org.opencontainers.image.version` is `latest`, while the description carries the Elsa version. | Bind the release manifest to the source commit/workflow run and never infer Elsa version from the image label alone. | The version label is misleading for immutable version tags. |
| Release channel and lifecycle | Product policy in Elsa Control | Product decisions define `Preview`, `Supported`, `Maintenance` and `End of Support`; image workflow only exposes tag/channel hints. | Project lifecycle, dates and eligibility as policy-owned catalog data. | No producer release record maps an image to lifecycle/support dates. |
| Topology composition | Dockerfiles, README and workflow matrix | Server has API/runtime; Studio has the Studio host only; Combined has API/runtime and Studio in one container. Studio supports Blazor Server or WebAssembly. | Keep version and topology separate and model component endpoints/capabilities explicitly. | Composition is implicit in image names and source files, not machine-readable release metadata. |
| Package compatibility | `elsa-specifications` package manifest contract plus runtime/package source repositories | Manifest v1 supports `runtimeKinds`, `elsaVersionRange`, `dockerImageVersionRange`, `runtimeCapabilities` and package rules. | Use package metadata as one input to compatibility resolution. | The package contract does not own image digest, topology, SBOM, provenance, signature or release lifecycle. No cross-repository engine/Studio/Nuplane matrix exists. |
| SBOM | Commercial image workflow / OCI attestations | `docker/build-push-action` runs with `sbom: true`; published OCI indexes contain attestation manifests. | Require an immutable SBOM locator/digest and retain verification evidence with the projection. | At audit time SBOM identity was not exposed through a producer release manifest; `elsa-production-image#27` later closed this producer gap. |
| Build provenance | Commercial image workflow / OCI attestations | `provenance: mode=max` is enabled; OCI indexes contain attestation manifests. | Require provenance subject, builder/workflow identity and immutable predicate reference. | Provenance is not a catalog field and is not currently consumed by Elsa Control. |
| Image signatures | Commercial image workflow / Sigstore | Each paid and Community image is signed by digest with keyless cosign. Public verification succeeded for all three Community build-79 index digests using the production-image GitHub Actions workflow identity and Fulcio issuer. | Verify the signature against the digest and approved identity before provider resolution. | Registry-specific signature references and the verification policy are not represented in catalog data. |
| Vulnerability gate | Commercial image workflow / Trivy | Before push, the workflow scans the amd64 image for fixable HIGH/CRITICAL vulnerabilities and fails on findings; unfixed findings are ignored. | Treat the scan result as release evidence, not a substitute for runtime compatibility or isolation policy. | No immutable scan report/result is attached to a release record. |

## Current published examples

The following values were queried on 2026-08-29. The digest is the multi-platform OCI
index digest. ACR's manifest metadata and GHCR's public manifest inspection returned the
same value for each paid/Community pair.

| Topology | Paid exact-build tag | Community version tag | Elsa component versions | Index digest |
|---|---|---|---|---|
| Server | `valenceruntimeimages.azurecr.io/runtime-server:3.8.0-preview.5413-build.79` | `ghcr.io/valence-works/runtime-ce-server:3.8.0-preview.5413` | Elsa Core `3.8.0-preview.5413`; no Studio host | `sha256:5c75e7678da7c7bd24ca6972bd9167ad7faa255b6b89f4b4e2249f16e8aa3b7d` |
| Studio | `valenceruntimeimages.azurecr.io/runtime-studio:3.8.0-preview.1667-build.79` | `ghcr.io/valence-works/runtime-ce-studio:3.8.0-preview.1667` | Elsa Studio `3.8.0-preview.1667`; no Elsa runtime | `sha256:95eee938b3f6dc602b644fc3eaaae639f3749e6d3bf6097dcfe042a4dbfeaa80` |
| Combined | `valenceruntimeimages.azurecr.io/runtime-combined:3.8.0-preview.5413-build.79` | `ghcr.io/valence-works/runtime-ce-combined:3.8.0-preview.5413` | Elsa Core `3.8.0-preview.5413` plus Studio `3.8.0-preview.1667` | `sha256:c33707c58c5e5a5b115499874d3114911db5ca7d69135aeb29d5a142a7d893e0` |

The `latest` tag currently resolves to the same digest as the corresponding version
tag, but remains mutable by definition. The paid registry also exposes `sha-1aeee8d`
and the build-79 tags; Community intentionally does not receive exact-build tags.
The Studio version is deliberately different from the Server/Combined version. A
consumer must not copy the Server tag onto the Studio image.

Supply-chain verification performed during this audit:

- `cosign verify` succeeded for the three GHCR Community index digests above with the
  certificate identity matching
  `https://github.com/valence-works/elsa-production-image/.github/workflows/build-and-push.yml@...`
  and issuer `https://token.actions.githubusercontent.com`.
- OCI inspection showed linux/amd64 and linux/arm64 manifests plus unknown-platform
  attestation manifests for each image, consistent with the workflow's SBOM and
  max-provenance settings.
- Paid ACR manifest metadata returned the same three index digests and creation times
  around 2026-08-17 UTC. This proves the current registry observation, not a permanent
  guarantee; future releases must verify parity rather than assume it.

## Elsa 3, Elsa 4 and existing topology authorities

### Elsa 3 commercial distribution

`elsa-production-image` is the only repository found that currently combines commercial
Elsa 3 image definitions, paid/Community publication and supply-chain gates. Its
README documents three distributions:

- `runtime-server` / `runtime-ce-server`: backend workflow runtime and management API;
- `runtime-studio` / `runtime-ce-studio`: standalone Studio UI, with no Elsa API or
  Nuplane runtime; and
- `runtime-combined` / `runtime-ce-combined`: one-container API/runtime plus Studio.

For separate Server + Studio, WebAssembly mode makes the browser call the API directly,
so the backend URL must be browser-reachable and cross-origin configuration must be
correct. Blazor Server mode makes the Studio container call the backend over the
container network. This is a topology/runtime contract and must not be collapsed into
the version identity.

The image repository's current pins are still preview builds from 2026-08-17. The
upstream public Elsa Core and Studio repositories have since published `3.8.0-rc2`
releases on 2026-08-21 and 2026-08-22 respectively. That is evidence of independent
release cadence and a lifecycle/catalog gap; it is not evidence that the commercial
image should be advanced without the producer's compatibility and release gate.

### Elsa 4 Foundation ecosystem

`elsa-workflows/elsa-foundation` and `elsa-workflows/elsa-foundation-studio` are
separate Elsa 4 source/release ecosystems:

- Foundation's Docker workflow publishes `elsaworkflows/elsa-workbench` (explicitly a
  development/demo host) and `elsaworkflows/elsa-foundation-host` to Docker Hub with
  `latest` and `4.0.0-preview.<run>` tags.
- Foundation's package workflow publishes `4.0.0-preview.<run>` packages to the
  Elsa 4 Feedz feed and publishes tagged GitHub Releases to NuGet.org.
- Foundation Studio's Docker workflow publishes `elsaworkflows/elsa-studio` to Docker
  Hub with `latest`, SHA and `4.0.0-preview.<run>`/semver tags.
- Neither repository has a currently published GitHub Release establishing an approved
  commercial Elsa 4 distribution, and neither is the Elsa 3 commercial image source.

Foundation and Foundation Studio therefore provide upstream component and topology
evidence only. An Elsa 4 commercial source, registry/access boundary, support policy,
compatibility matrix and signed release-manifest owner still require an explicit
decision before the Control catalog can offer Elsa 4.

### Official OSS `elsa-apps`

`elsa-workflows/elsa-apps` is retired and folded into `elsa-core`. The following
remains historical evidence: it was an official OSS application-image repository, not the
commercial distribution authority. Its workflows publish Docker Hub images with
hard-coded historical `3.6.1`/`3.6.4` metadata and SHA tags; the README also documents
legacy `elsaworkflows/elsa-* :latest` names. It demonstrates Server, standalone Studio,
and Server + Studio (Blazor Server/WASM) compositions, but it does not provide the
commercial paid/Community registry boundary, commercial supply-chain contract or
Elsa Control lifecycle data.

### Package specifications

`elsa-workflows/elsa-specifications` owns the data-only `elsa-package.json` manifest
schema and generator. Its compatibility object intentionally covers package/runtime
kind and version ranges, including `elsaVersionRange` and `dockerImageVersionRange`.
It does not own container image publication, release channels, image digests, SBOMs,
provenance or signatures. Elsa Control should consume it as package compatibility
input, not use it as a substitute for a commercial image release manifest.

## Initial facts versus required target contract

The following distinction is deliberate:

| Current fact | Required target contract |
|---|---|
| Build workflow and source files imply the image/component/topology relationship. | The producer publishes one versioned, signed release manifest for each commercial distribution release. |
| Registry tags and OCI labels expose enough information for a human to reconstruct a release. | Elsa Control consumes only a verified manifest and stores the exact image index digest, component set, source revision and evidence references. |
| `runtime-server`, `runtime-studio` and `runtime-combined` are stable current topology names. | Topology is a first-class data identity independent from version, with explicit components, runtime kinds, endpoints, capabilities, persistence needs and companion relationships. |
| Tags include mutable `latest` and Elsa-version aliases; paid has immutable build/SHA aliases. | Tags are discovery metadata only. Provider desired state always uses a verified immutable digest; promotion records the release-manifest digest. |
| Workflow configuration enables SBOM, provenance, scanning and cosign signatures. | The release manifest identifies each attestation/report/signature by immutable subject and records the verification policy/identity. |
| Package manifests describe package compatibility. | The resolved application plan combines package compatibility with image compatibility, topology constraints, lifecycle/channel policy and isolation/provider requirements. |
| Elsa 3 commercial and Elsa 4 Foundation are parallel ecosystems. | Elsa 4 enters the catalog only after a separately approved commercial distribution authority and release gate; no Elsa-major enum or hard-coded branch is introduced. |

## Machine-readable ownership contract

The producer now publishes a signed release-manifest OCI artifact from
`elsa-production-image`, with a schema version independent from Elsa package versions.
The required semantics established by the audit are:

The `components` block below is intentionally compact. In the real manifest it must be
either a complete package/module-ID-to-version map or an immutable lockfile reference
whose digest makes that complete set reproducible; recording only a marketing version
such as `Elsa 3.8` is insufficient.

```json
{
  "schemaVersion": "1",
  "distribution": {
    "id": "valence-runtime",
    "generation": "elsa-3",
    "releaseLine": "3.8",
    "releaseVersion": "3.8.0-preview.5413",
    "channel": "preview",
    "lifecycle": "Preview",
    "source": {
      "repository": "https://github.com/valence-works/elsa-production-image",
      "commit": "1aeee8df455b21cf3bf3d2b26dfbd512d76da27b",
      "workflow": ".github/workflows/build-and-push.yml",
      "runId": "<immutable-actions-run-id>"
    }
  },
  "topologies": [
    {
      "id": "server",
      "runtimeKinds": ["elsa.server"],
      "images": [
        {
          "registryClass": "paid",
          "reference": "valenceruntimeimages.azurecr.io/runtime-server@sha256:<index>",
          "indexDigest": "sha256:<index>",
          "platformDigests": {"linux/amd64": "sha256:<manifest>"}
        },
        {
          "registryClass": "community",
          "reference": "ghcr.io/valence-works/runtime-ce-server@sha256:<index>",
          "indexDigest": "sha256:<index>"
        }
      ],
      "components": {
        "elsaCore": "3.8.0-preview.5413",
        "nuplane": "0.0.10",
        "cshells": "0.0.28"
      },
      "endpoints": {"api": "/elsa/api", "health": "/health", "liveness": "/alive"},
      "compatibility": {
        "packageManifestSchema": "1.0",
        "runtimeCapabilities": ["elsa.server"]
      },
      "supplyChain": {
        "sbom": {"uri": "<immutable-attestation-ref>", "digest": "sha256:<attestation>"},
        "provenance": {"uri": "<immutable-attestation-ref>", "digest": "sha256:<attestation>"},
        "signatures": [{"registryClass": "paid", "identity": "<approved-workflow>", "uri": "<ref>"}],
        "vulnerabilityScan": {"tool": "trivy", "policy": "fixable-high-critical", "report": "<immutable-ref>"}
      }
    }
  ],
  "lifecycleDates": {
    "previewSince": "<date>",
    "supportedUntil": "<date>",
    "maintenanceUntil": "<date>",
    "endOfSupportAfter": "<date>"
  }
}
```

The example is illustrative, not a proposed hard-coded Elsa 3/4 enum. The manifest
must support any number of release lines and topologies. In particular:

1. **Producer-owned facts:** distribution identity, generation, release line/version,
   exact component set, topology composition, registry references, multi-platform
   digests, source/workflow provenance, SBOM/provenance/signature subjects and security
   gate results.
2. **Specification-owned facts:** package manifest schema, package metadata and package
   compatibility declarations.
3. **Control-owned policy/projection:** lifecycle/support eligibility, entitlement and
   channel availability, verified catalog projection, customer desired state and
   compatibility decision using the producer/specification facts.
4. **Provider-owned realization:** provider-specific resource IDs, credentials,
   networking and runtime placement. Providers receive the resolved immutable digest;
   they do not select a tag or reinterpret producer metadata.

Control ingestion should verify the release-manifest signature, image digest, attestation
subjects and registry parity before making a release selectable. It should preserve the
manifest reference and verification result so an operator can reproduce why a release was
accepted. A manifest may be rejected without changing producer repositories or images.

## Gaps and follow-up work

This audit deliberately does not implement the following:

- **[#106](https://github.com/valence-works/elsa-control/issues/106):** consume the
  governed release facts in a provider-neutral resolved Elsa application plan. It should
  replace `elsa-pro-* : latest` assumptions with verified catalog identities.
- **[#114](https://github.com/valence-works/elsa-control/issues/114):** persist exact
  release/topology identity in the Elsa Instance lifecycle aggregate.
- **[#125](https://github.com/valence-works/elsa-control/issues/125):** pass only the
  resolved immutable digest/topology to the Azure provider and validate the release
  evidence at deployment time.
- **[#110](https://github.com/valence-works/elsa-control/issues/110):** keep image
  provenance/compatibility separate from the executable-code isolation decision.
- **Completed — [`elsa-production-image#27`](https://github.com/valence-works/elsa-production-image/issues/27):**
  the producer publishes and signs the v1 release manifest with immutable SBOM,
  provenance, signature-policy and compatibility/topology evidence. The #147 proof
  verified the resulting contract; #114/#125 consume it in Control's lifecycle/provider path.
- **Elsa 4 authority decision (to be filed with the owning Foundation/Studio/image
  repositories):** approve the commercial Elsa 4 distribution source, registry/access
  model and lifecycle/compatibility gate before adding Elsa 4 to the Control catalog.

No producer pipeline, image, runtime repository or registry state was changed by #105.
