# Elsa Control Console

Unified React console for Elsa Control workspace users and operators. Package
Catalog is the first active module; deployment artifacts, Runtime Builder,
environment workbenches, managed runtimes, runtime operations, and audit views
are reserved as Elsa Control modules from the beginning.

## Development

```bash
npm install
npm run dev
```

The Vite dev server proxies relative `/api` requests to the Elsa Control API
host at `http://localhost:5220` by default so the browser client can avoid CORS
requirements. Override `CATALOG_API_PROXY_TARGET` in a local `.env` file
when the API runs elsewhere. In development, the proxy also forwards local
trusted workspace identity headers for workspace-scoped Runtime Builder APIs.
Admin access is provided by the API host's dashboard session cookie, not by a
browser-readable API key.

When running `src/ElsaControl.AppHost`, Aspire starts this Vite app as the
`console` resource and injects the API endpoint as `CATALOG_API_PROXY_TARGET`.

## Verification

```bash
npm ci
npm test
npm run typecheck
npm run build
```

These are the same required quality gates run by the `Console quality gates`
job in GitHub Actions. `npm ci` installs exactly the dependency graph recorded
in `package-lock.json`; changing that lockfile invalidates the CI dependency
cache.

Package details coverage lives in `src/features/packages/PackageDetailsPage.test.tsx`
and the Playwright smoke test in `tests/ElsaControl.Console.E2E/package-details.spec.ts`.
The page covers canonical package casing, version routes, visibility blockers,
validation findings, feature/settings inspection, manifest review, and
version-scoped approval/rejection actions.

The first active module exposes Overview, Sources, Packages, and Sync Runs.
Deployment, artifact, Runtime Builder, target, runtime, operations, and audit
modules may be visible as roadmap affordances but must not imply implemented
mutations before backend contracts exist. The catalog module must not include
Settings, package identity approval controls, hard-delete source controls,
realtime streaming logs, or manifest editing.

## Console themes

Open **Appearance** in the shell to choose Classic, Operations Canvas, Command
Deck, or Topology Atlas. Each supports Light, Dark, and System mode. Classic also
retains the existing accent choices. Changes apply immediately without replacing
page content or navigation. Preferences belong to the current browser, persist
across reloads, and synchronize across tabs; they are not account settings.
When browser storage is unavailable, choices last for the current page session.
There is no in-app feedback collection or telemetry.

`src/lib/theme/themes.ts` is the theme registry. Each theme has a stable ID,
display name, version, complete light/dark semantic palettes, font stacks, and a
corner radius. To add a theme, extend `ThemeId` and add a complete registry entry;
the selector and previews discover it automatically. Increment a theme's version
when its visual design changes so feedback can identify the revision (the card's
tooltip shows the version). Use already loaded fonts or update `index.html`.

Components use semantic Tailwind utilities such as `bg-surface`, `text-foreground`,
`text-primary`, `font-display`, and `rounded-ui`. Avoid theme-specific component
branches and hardcoded colors. `ThemeProvider` maps registry values to CSS
variables; `initializeTheme()` applies saved preferences before React renders.
`styles.css` contains the Classic light fallback and shared appearance UI styles.

The versioned `elsa-control-console-appearance` record is authoritative. The old
theme/accent keys are migrated and retained for older bundles. Invalid settings
fall back safely. New palettes should be checked in both modes for text contrast,
focus visibility, controls, validation states, and narrow screens. Run the console
quality gates above after changing the framework.

## Deployment

The production API container builds this app and serves it from `/admin`. Vite is
configured with `/admin/` as its asset base path, and browser API calls remain
same-origin `/api` requests.
