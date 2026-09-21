# Contract: Elsa Cloud Provisioning Progress UX

## BFF Action

Request:

```json
{
  "action": "getInstanceProvisioningProgress",
  "organizationId": "<uuid>",
  "workspaceId": "<uuid>",
  "instanceId": "<uuid>"
}
```

The BFF requires a valid Elsa Cloud session, verifies the existing organization link, requires `hosted.instances.provisioning-progress.v1`, calls the scoped Control route, and returns a newly constructed allowlisted response.

Unknown fields, enum values, malformed timestamps, negative/non-integer sequences, and arbitrary messages are never passed through. Invalid required snapshot fields yield the existing safe BFF error. Invalid optional activity rows may be discarded while the valid snapshot remains usable.

## Compact Timeline

For a nonterminal managed engine, the engine card displays:

- Existing lifecycle label.
- `Step N of 7` and the current product-owned stage label.
- Completed, current, and pending stage markers without relying on color alone.
- Indeterminate animation within the current stage, disabled when reduced motion is preferred.
- Elapsed time from `startedAt` and `Updated <relative time>` from `lastUpdatedAt` when present.

The list and existing actions render before progress completes loading. Progress failure does not hide the engine or change Create/Open/Delete eligibility.

## Activity Disclosure

- Label: `View provisioning activity` / `Hide provisioning activity`.
- Collapsed by default for every page load.
- Uses an accessible disclosure relationship with `aria-expanded` and an associated region.
- Presents product-owned text selected by `messageCode`; it never renders an upstream message.
- Displays local time and elapsed time where available.
- Keeps stable chronological order.
- Long entries wrap within the card; the page does not horizontally scroll at 390 x 844.

Example presentation:

```text
00:00  Request accepted
00:08  Preparing hosting foundation
04:32  Hosting foundation ready
04:35  Deploying Elsa runtime
```

This visual treatment has no command input and must not be described as a live shell.

## Polling

- Start after the engine list identifies a managed engine whose lifecycle is pending or provisioning.
- Poll every five seconds while the document is visible and progress state is `queued` or `active`.
- Stop after `ready` or `failed`.
- Pause while hidden; issue one immediate refresh when visible again.
- For `stale`, stop active polling and provide `Check again` using the same read action.
- Ignore late responses for a previous workspace or signed-out session.
- Keep the last good snapshot during a temporary read failure within the same page session and mark its freshness clearly.

## Customer Copy

Stage labels:

- Request accepted
- Preparing hosting foundation
- Configuring registry, secrets, network, and database
- Deploying Elsa runtime
- Verifying health
- Routing traffic
- Ready

Exceptional outcomes:

- `stale`: `Provisioning needs attention. Check again or contact Valence Works if it remains here.`
- `failed`: `Provisioning failed at <stage>. Review the safe guidance below or contact Valence Works.`
- `unavailable`: `Detailed provisioning progress is temporarily unavailable. The engine status above remains current.`

## Accessibility and Responsive Behavior

- Current state is available in text and programmatic state, not color alone.
- New stage announcements use a polite live region that announces only the new current stage.
- Historical activity is not repeatedly announced during polling.
- The disclosure is keyboard operable and preserves visible focus.
- Reduced-motion preference removes spinner rotation/pulsing without removing the current-state text.
- At 390 x 844, stages stack or fit inside the engine card without page-level overflow.
