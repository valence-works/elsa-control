# Elsa Cloud compatibility contract

`GET /api/cloud/compatibility` is a static, additive contract for the Cloud BFF.
It returns this envelope with `Cache-Control: no-store`:

```json
{
  "contractVersion": 1,
  "capabilities": [
    "cloud.bootstrap.v1",
    "hosted.instances.list.v1",
    "hosted.instances.create.v1",
    "hosted.instances.status.v1",
    "hosted.instances.provisioning-progress.v1",
    "hosted.instances.overview.v1",
    "hosted.studio.handoff.issue.v1",
    "hosted.instances.quota-problem.v1",
    "hosted.instances.confirmed-delete.v1",
    "hosted.instances.reconciliation-cleanup.v1",
    "hosted.subscription.manage.v1",
    "hosted.deployments.audit.v1"
  ]
}
```

The response contains only the numeric contract version and stable capability
identifiers. It does not reveal environment, customer, provider, deployment,
release, or configuration state. A listed capability means that Control's API
contract is implemented; it does not indicate endpoint health, feature
readiness, customer entitlement, workspace permission, or user authorization.
Those checks continue at the corresponding API boundary for every request.

## Capability mapping

| Capability | Control contract |
| --- | --- |
| `cloud.bootstrap.v1` | `POST /api/cloud/bootstrap` returns the caller's Cloud organization and workspace. |
| `hosted.instances.list.v1` | `GET /api/workspaces/{workspaceId}/instances` returns a permission-filtered, paginated list. |
| `hosted.instances.create.v1` | `GET /api/workspaces/{workspaceId}/instances/onboarding-options` and `POST /api/workspaces/{workspaceId}/instances` provide managed-instance setup and creation. |
| `hosted.instances.status.v1` | The instance summaries returned by the list route expose safe lifecycle and health status. |
| `hosted.instances.provisioning-progress.v1` | `GET /api/workspaces/{workspaceId}/instances/{instanceId}/provisioning-progress` returns the customer-safe provisioning progress projection. |
| `hosted.instances.overview.v1` | `GET /api/workspaces/{workspaceId}/instances/{instanceId}/overview`, `GET .../available-releases`, `GET .../activity`, `POST .../restart`, and `POST .../apply-release` provide the customer instance overview. |
| `hosted.studio.handoff.issue.v1` | `POST /api/managed-elsa/handoff/issue` issues a short-lived, single-use Studio handoff when configured and authorized. |
| `hosted.instances.quota-problem.v1` | Managed-instance create returns the stable `instance_limit_reached` problem when the account's instance limit is reached; the response may include its current and maximum counts. Other commercial denials remain endpoint-specific. |
| `hosted.instances.confirmed-delete.v1` | `POST /api/workspaces/{workspaceId}/instances/{instanceId}/delete-confirmations`, then `POST .../{instanceId}/delete`, followed by `GET .../{instanceId}/delete-operations/{operationId}`. Confirmation, permission, workspace scope, ETag, idempotency, and lifecycle checks still apply. |
| `hosted.instances.reconciliation-cleanup.v1` | The customer-facing Create/Delete reconciliation lookup and the two Delete cleanup reads documented below. All three require normal workspace access. |
| `hosted.subscription.manage.v1` | `GET /api/organizations/{organizationId}/billing/hosted-subscription` returns Hosted billing-linkage and copy hooks; `POST .../hosted-portal` opens a Stripe Customer Portal session for the caller's billing customer and a validated Elsa Cloud return URL. `POST .../billing/delete` remains the Hosted billing-deletion request on the same allowlist. |
| `hosted.deployments.audit.v1` | `GET /api/organizations/{organizationId}/deployments/audit` returns a sanitized, paginated Cloud function-deploy audit feed. Empty organizations return an empty page, not 404. |

The exact three GET routes for `hosted.instances.reconciliation-cleanup.v1`
are:

- `GET /api/workspaces/{workspaceId}/instances/operations/by-idempotency-key` with `action=Create&slug={submittedSlug}` and the original Create `Idempotency-Key`, or `action=Delete&instanceId={instanceId}` and the stable Delete `Idempotency-Key`. It resolves only the matching accepted operation.
- `GET /api/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-receipt`. This returns historical cleanup evidence.
- `GET /api/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-observation`. When its provider observer is available, this queries the exact owned resource group.

Each request uses workspace authorization and performs its own evidence checks.
The reads do not mutate lifecycle or provider state.

The Cloud BFF token is admitted only on the explicit route allowlist in
[`cloud-bff-auth-contract.md`](cloud-bff-auth-contract.md). Capabilities are
descriptive contract identifiers, not substitutes for that allowlist or for
normal Control authorization.

The provisioning-progress response keeps `startedAt` as the original lifecycle
acceptance time. Its optional `attemptNumber` identifies the current lifecycle
attempt, and `attemptStartedAt` is the acceptance time for that attempt. The
first attempt uses lifecycle acceptance; a recovery attempt uses its matching
append-only recovery ledger entry. If legacy data has no unambiguous ledger
authority, `attemptStartedAt` is omitted. These fields are presentation
metadata and do not change the existing stale-progress clock. `currentStage`
and the ordered stage entries use stable customer tokens; provider phases and
runner steps are mapped inside Control, and unknown current mapping leaves the
stage status unknown rather than guessing from historical activity.

## Evolution and rollout

- `contractVersion` versions the envelope schema, not the Cloud application,
  Control build, or an individual customer's readiness. Version `1` stays
  numeric and stable. Clients must handle an unknown contract version safely;
  version 1 clients ignore unknown capability identifiers.
- Add capabilities without changing the meaning or spelling of existing ones.
  A semantic or incompatible contract change needs a new capability identifier
  and, if the envelope shape or interpretation changes incompatibly, a new
  contract version. Do not silently remove a capability from a supported
  contract.
- Deprecate through the Cloud BFF release notes and a dated migration plan.
  Keep the old route and capability available while any supported BFF client
  depends on it. Remove them only after the minimum supported client policy has
  advanced past those clients and the next contract version communicates the
  breaking boundary.
- The minimum supported Cloud client is a Cloud release policy; it is not
  reported by this endpoint. Do not derive a user's access, entitlement, or
  feature readiness from either the contract version or capability list.
- Deploy additive Control routes and capabilities before deploying a BFF that
  consumes them. Roll back the BFF first if needed; only roll back Control after
  the serving BFF no longer depends on the newer contract. This keeps an older
  client compatible with the additive version 1 envelope and avoids a BFF
  calling routes absent from a rolled-back Control deployment.
- For `hosted.instances.reconciliation-cleanup.v1`, the advertisement adds no
  route, schema, or lifecycle behavior. Deploy this Control change before a BFF
  release gates actions on the capability. If Control is rolled back to a
  version that omits it, a capability-aware BFF disables only these optional
  reconciliation and cleanup actions. Roll back the BFF first if it cannot
  safely handle the capability disappearing.

## Staging compatibility fixture

The staging-only `CloudCompatibility:StagingFixture` switch can make
staging Control advertise `missing-capability` or `older-contract` so
Cloud can prove version-skew handling. It is armed only by the manual
`Staging Control compatibility fixture` workflow on the GitHub `test`
environment. See
[the fixture runbook](../deployment/azure-app-service.md#staging-control-compatibility-fixture)
for when to run it, its inputs, what it restores, and the time cap.

`GET /health` may include `compatibilityFixture` (`null` or a known mode
name) as a recycle witness. No code path gates on that field. Arm and
restore proofs use the authenticated compatibility response (and BFF
smoke `compatible` after restore), not `/health` alone.
