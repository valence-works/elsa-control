# Provision engine and Runtime Builder

The workspace **Provision engine** action and Runtime Builder's **Provision** action
enter the same managed creation flow. Runtime Builder continues to own saved
configurations, feature selection and export. Managed creation owns review,
acceptance, operation progress and engine registration.

## Flow

1. Choose an existing empty application environment, a governed release and the
   placement offered by the host. Start with defaults, reuse a saved configuration,
   or customize the runtime features. Preview releases require explicit consent.
2. Review the server's managed configuration and compatibility findings. Export
   metadata such as a local port does not control managed hosting. Custom sources,
   local packages, raw secrets and unsupported provider overrides cannot be forced
   through this boundary.
3. Provision with the reviewed digest and an idempotency key. Acceptance binds the
   environment, registers a pending engine and saves the immutable configuration
   with the lifecycle operation in one transaction.
4. Follow the durable operation. Progress is control-plane state, not a simulated
   percentage. The engine gains an endpoint and health only from provider observations.

Editing a saved configuration does not edit an accepted instance's snapshot.
The worker checks the resolved plan against the reviewed digest before submitting
to a provider. If catalog metadata or host references change the result, the
operation fails with `provisioning.plan-changed` instead of deploying that change.
Updating an existing instance is the separate Apply-release workflow.

Before acceptance, the console preserves the server-reviewed, managed-safe request
and its idempotency key in workspace-scoped session storage. If the response is
lost, a reload replays that exact request without requiring the now-occupied
environment to remain selectable. Raw Builder handoff data is not saved there.
After acceptance, the console refreshes target availability and follows the stored
operation. Engine display names are limited to 200 characters throughout the flow.

## Boundaries

`GET /api/workspaces/{workspaceId}/engine-provisioning/targets` returns eligible
application/environment identities. Discovery and transactional acceptance share
the same occupancy predicate, including existing bindings, engines, revisions,
observability bindings and drift reports. Acceptance checks it again inside the
transaction, so a previously available target can still be rejected after a race.

`POST /api/workspaces/{workspaceId}/engine-provisioning/preview` uses the managed
configuration adapter and the same governed plan resolver as the lifecycle worker.
The enabled provider also runs its pure plan admission checks during preview, so
unsupported Azure networking or release evidence blocks review before acceptance.
Concrete workload identity is derived and validated again during provider submission.
`POST /api/workspaces/{workspaceId}/engine-provisioning` requires that review and
uses the existing lifecycle acceptance and worker/provider path. All three endpoints
require workspace setup permission and an enabled provisioning module.

The Azure module contributes provider availability only when the host has composed
its provisioning capability. Installing the UI does not enable a local Azure runner.
Use the [Azure runner configuration](azure-provider-runner.md) for host setup.

This flow is tracked by [task #410](https://github.com/valence-works/elsa-control/issues/410).
Release admission, Apply to existing instances, Open handoff changes and live Azure
proof belong to their own tasks. A passing local test suite does not establish that
an Azure environment has been configured or successfully provisioned.
