# Elsa Control Console

React console for Elsa Control workspace users and operators. The workspace
overview leads with engine health, application environments, and actionable
deployment signals, backed by the existing workspace APIs.

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

Navigation groups available features into Workspace, Library, and Manage.
Planned modules are not shown as disabled navigation. Cmd/Ctrl+K opens page
navigation; the engine fleet has its own name/context/health filters.

## Connect an engine

`/admin/engines/connect` combines endpoint, credentials, and placement in one
form. Placement is disclosed on demand. Existing environment registration links
redirect here with an environment selection. The flow uses current workspace
permissions and APIs; engine registration performs the health check. It does not
claim to discover or verify an endpoint before registration.

Application, environment, credential, and engine writes are separate backend
operations. Confirmed resource IDs are kept for retry within the form. An
ambiguous network failure requires reconciliation rather than guessing a saved
resource by name. Leaving or reloading the page discards in-memory form progress.

## Design preview

With the Vite development server running, open `/admin/design-preview.html` for
a populated preview using the actual console components and sample data. This
separate development entry installs API fixtures before loading the app; the
normal entry continues to use real authentication and APIs. The preview banner
identifies sample data. Simulated changes last only until reload.
The preview entry is not included in the normal production build.

## Console themes

Open **Appearance** in the shell to choose Classic, Operations Canvas, Command
Deck, or Topology Atlas. Each supports Light, Dark, and System mode. Classic also
retains the accent choices. Changes apply immediately while preserving the
current route and form state. Preferences belong to the current browser, persist
across reloads, and synchronize across tabs; they are not account settings.
When browser storage is unavailable, choices last for the current page session.
There is no in-app feedback collection or telemetry.

`src/lib/theme/themes.ts` is the theme registry. Each theme has a stable ID,
display name, version, complete light/dark semantic palettes, font stacks,
corner radius, background pattern, and shell layout. Command Deck uses an icon
rail, Operations Canvas a horizontal navigation bar, and Topology Atlas a grouped
sidebar and grid canvas. Smaller screens use the shared responsive navigation.
To add a theme, extend `ThemeId` and add a complete registry entry;
the selector and previews discover it automatically. Increment a theme's version
when its visual design changes so feedback can identify the revision (the card's
tooltip shows the version). Use already loaded fonts or update `index.html`.

Components use semantic Tailwind utilities such as `bg-surface`, `text-foreground`,
`text-primary`, `font-display`, and `rounded-ui`. Avoid theme-specific component
branches and hardcoded colors. `ThemeProvider` maps registry values to CSS
variables; `initializeTheme()` applies saved preferences before React renders.
`styles.css` contains the Classic light fallback and shared appearance UI styles.
`console-layout.css` defines the reusable sidebar, rail, topbar, and grid treatments.

The versioned `elsa-control-console-appearance` record is authoritative. The old
theme/accent keys are migrated and retained for older bundles. Invalid settings
fall back safely. New palettes should be checked in both modes for text contrast,
focus visibility, controls, validation states, and narrow screens. Run the console
quality gates above after changing the framework.

## Deployment

The production API container builds this app and serves it from `/admin`. Vite is
configured with `/admin/` as its asset base path, and browser API calls remain
same-origin `/api` requests.
