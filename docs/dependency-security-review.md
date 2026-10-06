# Dependency security review

Last reviewed: 2026-10-06. Tracking: [Control #759](https://github.com/valence-works/elsa-control/issues/759).

## Console remediation

The Console lockfile previously exposed six Dependabot alerts. These packages
execute in developer and CI build/test processes; their development classification
does not establish safety or customer-request reachability.

| Advisory | Previous installed path | Remediation |
| --- | --- | --- |
| [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q), source-map parser exhaustion | PostCSS → source-map-js 1.2.1 | Lock source-map-js 1.2.2 |
| [GHSA-rj75-hqrm-r3gf](https://github.com/advisories/GHSA-rj75-hqrm-r3gf), selector parser exhaustion | Tailwind 3 / postcss-nested → postcss-selector-parser 6.1.4 | Explicit package override to 7.1.6 |
| [GHSA-5gmw-xhrv-c9v3](https://github.com/advisories/GHSA-5gmw-xhrv-c9v3) and [GHSA-85c8-ppgw-ccpr](https://github.com/advisories/GHSA-85c8-ppgw-ccpr), prototype-pollution gadgets | Vitest 3.2.7 → Tinypool 1.1.1 | Upgrade Vitest to 4.1.11, removing Tinypool from the installed graph |
| [GHSA-82fw-gwwq-j7x9](https://github.com/advisories/GHSA-82fw-gwwq-j7x9), development mock-server registration | Vitest / @vitest/mocker 3.2.7 | Upgrade both to 4.1.11 |

The Vitest major upgrade is deliberate. Existing Node 22 and Vite 6 meet the
[versioned Vitest 4 migration prerequisites](https://github.com/vitest-dev/vitest/blob/v4.1.11/docs/guide/migration.md).
Keep clean-install, test and typecheck results on the exact reviewed head.
Tailwind remains on 3.4.19. The selector-parser override is a deliberate major
compatibility adjustment because its callers still request version 6; compare
the complete generated CSS with the pre-upgrade build, not just compilation.
Remove the override when the callers adopt a patched supported parser.

The July review's React Router 7.18.1 exception is historical. The current lockfile
uses 7.18.2 and the current npm audit does not report that advisory. Do not reuse
the old two-record audit result as current evidence.

## Upstream-unpatched braces residual

[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm)
reports stack exhaustion from deeply nested patterns; no patched release was
available at this review. The remaining npm audit is **not clean**: five high
affected-package records (braces, chokidar, fast-glob, micromatch and Tailwind)
propagate this **one** advisory. Critical and moderate records from the fixable
findings are gone. The separate Console E2E lockfile is reviewed independently.

The installed path is Tailwind 3.4.19 → fast-glob 3.3.3 → micromatch 4.0.8 →
braces 3.0.3. Tailwind supplies the repository-owned literal patterns
`./index.html` and `./src/**/*.{ts,tsx}`. Current application sources have no
direct braces/micromatch/fast-glob imports. Those source facts narrow the observed
input path; they are not an exploit test or artifact-absence proof.

The production build rejects the affected build/test packages in Rollup's
actual emitted module graph or external imports, including the inline theme
bootstrap. Regression tests use real Rollup builds with clean and deliberately
contaminated inputs. The API provider image smoke gate separately checks the
actual `/app` payload: the built Console must exist, with no `node_modules`, npm
lockfile or shrinkwrap. Failed container reads fail closed and raw container
output is suppressed. These checks verify the Console boundary, not all third-party
software in the base image or arbitrary copied JavaScript.

Residual disposition: after the exact-head clean install, tests, CSS comparison,
bundle gate and actual-image smoke pass, the known repository-controlled build
path does not require a first-public block. Developer/build-time denial of service
remains a documented residual; do not dismiss the advisory or claim exploitation
is impossible. A forced Tailwind 4 migration is not part of this fix.

Build/test execution must remain on trusted repository inputs. Ordinary CI has
contents:read. Manual deployment runs in a protected environment and its job has
id-token:write before Console build/test; Azure login happening later is **not**
a credential boundary. This change does not weaken those permissions or
environment controls. Reassess before accepting untrusted pattern/config inputs,
exposing development servers, changing deployment protection, or finding a
build-only package in the runtime artifacts.

Residual owner: Elsa Control maintainers / root control room under the authorized
delivery scope. Review by 2026-11-06, or immediately upon a supported upstream fix
or a changed execution boundary. Provider-alert readback and exact artifact/CI
receipts belong on #759; an open upstream-unpatched alert requires this explicit
disposition, not an invented patched version or a development-only dismissal.

## Release checks

Run these checks whenever dependency lockfiles change:

```bash
npm ci --prefix src/Hosting/ElsaControl.Console
npm test --prefix src/Hosting/ElsaControl.Console
npm run typecheck --prefix src/Hosting/ElsaControl.Console
npm run build --prefix src/Hosting/ElsaControl.Console
npm audit --prefix src/Hosting/ElsaControl.Console
npm audit --prefix tests/Hosting/ElsaControl.Console.E2E
scripts/validate-api-provider-image.sh <built-api-image>
```

For .NET dependency changes, also run `dotnet list ElsaControl.sln package
--vulnerable --include-transitive` through the shared machine-wide build-slot
wrapper. npm audit remains nonzero while the braces advisory is unpatched; record
the actual residual rather than reporting a clean audit. This review is neither
legal approval of dependency licences nor a substitute for inspecting the final
distributable artifacts and refreshed provider alerts.
