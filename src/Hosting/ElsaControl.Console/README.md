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
Billing and managed runtime pages show their initial empty states; billing actions
and managed provisioning are unavailable in this preview. New sample engines are
unverified, with no heartbeat, because the preview never contacts an engine.
The preview entry is not included in the normal production build.

## Console appearance

Aperture is the shared console design: a compact top bar, engine inventory with
an inspector, application cards, and a single connection form. Workspace and
Applications are always available in the header. **More** opens all pages and
the organization/workspace switcher; Cmd/Ctrl+K opens quick navigation.

Open **Appearance** for Lime, Glacier, Iris, or Ember accents and Light, Dark, or
System mode. Fresh preferences start with charcoal and Lime. Changes apply
immediately without remounting the current route or resetting form state.
Preferences persist in the browser and synchronize across tabs. They are not
account settings; blocked storage falls back to the current session. Feedback
is collected externally by email; no in-app feedback or telemetry is added.

### Extending the framework

`src/lib/theme/themes.ts` separates style definitions from curated accent
palettes. A style owns complete light/dark surfaces, semantic status colors,
header band colors, display/body/mono font stacks, and corner radius. An accent
owns its fill, the text on that fill, and readable accent text for each mode.
Success, warning, and destructive colors remain independent of accents.

To add an accent, extend `ThemeAccent` and `accentDefinitions`. To add a style,
extend `ThemeId` and `themes`; the picker shows style choices when there is more
than one. Keep the shared page layout and navigation. Different fonts, surfaces,
and corners belong in tokens, not separate page implementations. Native fonts
are used by default, with no remote font request.

`ThemeProvider` applies those tokens before React renders, and the shared CSS
consumes them. Use `bg-primary` for accent fills, `text-primary-foreground` for
text on those fills, and `text-primary` for accent links (mapped separately to
`--primary-text` for light-mode contrast). Use `font-display`, `font-mono`, and
`rounded-ui` rather than fixed fonts or corner sizes in new components.

The version-2 `elsa-control-console-appearance` record is authoritative. Earlier
style choices migrate to Aperture, preserving mode and mapping teal to Lime,
blue to Glacier, violet to Iris, and amber/rose to Ember. Legacy keys remain
readable by older bundles. Invalid settings fall back safely.

Verify new combinations for text and focus contrast, form/error states,
keyboard navigation, text wrapping, and narrow screens. Run the console quality
gates above after changing the framework. The development design preview renders
the same production components with sample data for visual checks.

## Deployment

The production API container builds this app and serves it from `/admin`. Vite is
configured with `/admin/` as its asset base path, and browser API calls remain
same-origin `/api` requests.
