# Historical managed Delete cleanup receipt

Tracking: [#594](https://github.com/valence-works/elsa-control/issues/594).
This is one private read-side component of the resumable staging rehearsal;
it does not complete that rehearsal or the first-public gate.

## Operator lookup

`GET /api/admin/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-receipt/{organizationId}`

The existing Admin authorization policy applies. This operator route does not
change lifecycle, provider, or billing state and does not call Azure. It requires
no additional grants or schema.

The caller supplies the exact retained organization, workspace, instance and
accepted lifecycle Delete operation. It must not discover a candidate by picking
the sole active engine or by assuming an empty customer inventory proves absence.

A successful response requires:

- The exact organization's retained instance tombstone, desired Deleting and
  observed Deleted, with its last operation equal to the requested Delete.
- The exact organization's successful lifecycle Delete and matching completion
  and tombstone timestamps.
- Either the provider-backed path, the local-absent path, or the
  confirmed-absent-without-provider-Delete path below. There is no fallback
  from one to another.

Provider-backed receipts additionally require:

- Exactly one retained provider assignment for that organization, workspace and
  instance. Deletion clears the instance's placement reference, so the retained
  assignment is read independently. Ambiguity is refused.
- The assignment's exact last provider operation, correlated to the lifecycle
  Delete through its canonical key or validated retry lineage, with matching
  owner, workload, assignment and provider scope.
- Terminal successful provider cleanup with CleanupVerified, no attempted step
  or endpoint, and both persisted inventories reduced to the owned group name.

Local-absent receipts (`proofKind` `local-no-owned-resources`) apply when the
instance has no retained provider assignment and the Delete finalized with
`deletion.local.absent` and zero `AzureProviderOperations` for that workspace
and instance.

Confirmed-absent receipts without a provider Delete (`proofKind`
`provider-confirmed-absent`, `providerDeleteSucceeded` false) apply when the
instance has no retained provider assignment and the Delete finalized with
`deletion.provider-confirmed-absent`. That includes:

- never-provisioned Deletes (normal or admin-recovered) with no assignment, and
- Deletes that left a placement assignment from a failed Create. Commit
  marks those assignments `Released` in the same transaction. On read, a
  leftover assignment whose instance is Deleted with the last Delete
  Succeeded and `deletion.provider-confirmed-absent` is treated as released,
  so existing tombstones receive a receipt without a manual SQL fix-up.

They attest that no provider Delete ran: `providerDeleteSucceeded` is false
and `providerCompletedAt` is null. `providerAbsenceVerifiedAtCompletion` is
true because that empty or released assignment provenance was verified in the
completion transaction, or is equivalent on read. A verified `Deleted`
assignment from terminal provider cleanup stays on the provider-backed path.
Genuinely live assignments, or any other deletion diagnostic, stay
fail-closed.

Missing, mismatched, unfinished or ambiguous evidence returns an opaque 404,
without a partial receipt. Unsupported lightweight stores return no receipt.
Provider-scope configuration rotation does not rewrite historical evidence.

## Receipt meaning

The response contains bounded success flags, completion timestamps and a
deterministic SHA-256 digest. It excludes raw customer/provider identifiers,
resource names, scopes, operation keys, credentials and provider diagnostics.
Retain even the digest and timestamps privately; public Issue Bus evidence needs
only the status class and sanitized result.

`providerAbsenceVerifiedAtCompletion` attests to absence verification at Delete
completion: the trusted provider runner's CleanupVerified result on the
provider-backed path, or empty or released Azure provenance on the local-absent
and confirmed-absent-without-provider-Delete paths.
**It is not a fresh Azure Resource Manager observation.** The digest binds
retained evidence; it is not a signature and does not authorize mutation or
prove present absence by itself. Provider-backed receipts use canonical prefix
`elsa-managed-delete-cleanup-receipt-v1`. Receipts without a provider Delete
use `elsa-managed-delete-cleanup-receipt-local-v1`. `proofKind` is
`local-no-owned-resources` for `deletion.local.absent`, and
`provider-confirmed-absent` for `deletion.provider-confirmed-absent` and for
terminal verified provider cleanup.

A coordinator requiring fresh provider absence still needs an independently
authorized exact-resource observation. It also needs its exact Stripe cleanup
receipt and all other completion guards. Do not release a pending staging run,
admit another Create, or mark a failed customer proof Passed from this historical
lookup alone. Unknown Create/Delete responses and checkout-only interruptions
remain separate reconciliation paths under #594.

## Fresh provider observation

`GET /api/admin/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-observation/{organizationId}`

Workspace readers also have a customer-authenticated facade for the same bounded
evidence:

- `GET /api/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-receipt`
- `GET /api/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-observation`

These routes use the normal workspace access check and resolve organization from
that authenticated workspace membership. They accept no organization, provider,
receipt, or observation values from the caller. The Admin endpoints remain
available to operators under their existing policy.

The fresh-observation routes require the same complete historical evidence before
invoking the configured provider observer. They are unavailable when the concrete
Azure worker runner is not composed. No credentials, provider scope or resource
name is accepted from the caller.

Provider-backed observations independently validate the terminal provider Delete,
lifecycle key, retained assignment and current execution authority, then issue
one exact owned resource-group existence query. Local-absent and confirmed-absent-without-provider-Delete observations issue
one fresh existence query for the deterministic current-version resource group
named from the configured prefix and instance id in the configured workload
subscription. Neither path logs in, retries deletion, runs cleanup or changes
provider state. A successful parsed `false` is `Absent`, `true` is `Present`;
failed commands, malformed output and authority drift are `Unknown`. Local
receipts never return `Absent` without that fresh read. Cancellation remains
cancellation. Only successful observations carry an evidence digest.

The response contains the bounded observation state, time, fixed reason code,
observation digest and historical receipt digest. The route rereads historical
evidence after the network query and refuses a changed ownership/completion
binding. Responses are not cacheable. Provider errors and private CLI output are
never returned.

`Absent` describes the exact resource group at the observation time. It is not a
permanent guarantee, a signed authorization, a Stripe cleanup receipt or a
coordinator completion action. The trusted rehearsal executor must check the
retained run binding, freshness and every other cleanup receipt before releasing
its pending run. This API does not complete or restart any run by itself.

## Unknown Create and Delete responses

The customer-facing lifecycle reconciliation read is separate from both Admin
cleanup reads above. It uses the caller's existing workspace identity and never
resubmits a lifecycle mutation:

```text
GET /api/workspaces/{workspaceId}/instances/operations/by-idempotency-key?action=Create&slug={submittedSlug}
Idempotency-Key: {originalCreateKey}

GET /api/workspaces/{workspaceId}/instances/operations/by-idempotency-key?action=Delete&instanceId={instanceId}
Idempotency-Key: {stableDeleteKey}
```

The route resolves the organization from the authenticated workspace access,
then matches the persisted action, idempotency scope, organization, workspace,
and submitted slug or instance ID. It returns `200` only for that exact safe
operation and instance projection. The response reuses the customer
`instance` and `operation` projections; it never returns the idempotency key,
request hash, or provider evidence. An invalid or ambiguous request returns
`400`; an unmatched or mismatched lookup is concealed as `404`.

A `404` only means no matching operation was readable at lookup time. It does
not prove the mutation was never accepted and is not permission to retry Create
or Delete. Guided engine provisioning is not covered by this contract. The
lookup reads the catalog only and does not enqueue, resume, or mutate lifecycle,
outbox, provider, or billing state. It also does not attest to provider cleanup
or fresh Azure absence.
