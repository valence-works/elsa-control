# Historical managed Delete cleanup receipt

Tracking: [#594](https://github.com/valence-works/elsa-control/issues/594).
This is one private read-side component of the resumable staging rehearsal;
it does not complete that rehearsal or the first-public gate.

## Operator lookup

`GET /api/admin/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-receipt/{organizationId}`

The existing Admin authorization policy applies. Customer credentials cannot
obtain this receipt. The route does not change lifecycle, provider, or billing
state and does not call Azure. It requires no additional grants or schema.

The caller supplies the exact retained organization, workspace, instance and
accepted lifecycle Delete operation. It must not discover a candidate by picking
the sole active engine or by assuming an empty customer inventory proves absence.

A successful response requires:

- The exact organization's retained instance tombstone, desired Deleting and
  observed Deleted, with its last operation equal to the requested Delete.
- The exact organization's successful lifecycle Delete and matching completion
  and tombstone timestamps.
- Exactly one retained provider assignment for that organization, workspace and
  instance. Deletion clears the instance's placement reference, so the retained
  assignment is read independently. Ambiguity is refused.
- The assignment's exact last provider operation, correlated to the lifecycle
  Delete through its canonical key or validated retry lineage, with matching
  owner, workload, assignment and provider scope.
- Terminal successful provider cleanup with CleanupVerified, no attempted step
  or endpoint, and both persisted inventories reduced to the owned group name.

Missing, mismatched, unfinished or ambiguous evidence returns an opaque 404,
without a partial receipt. Unsupported lightweight stores return no receipt.
Provider-scope configuration rotation does not rewrite historical evidence.

## Receipt meaning

The response contains bounded success flags, completion timestamps and a
deterministic SHA-256 digest. It excludes raw customer/provider identifiers,
resource names, scopes, operation keys, credentials and provider diagnostics.
Retain even the digest and timestamps privately; public Issue Bus evidence needs
only the status class and sanitized result.

`providerAbsenceVerifiedAtCompletion` attests to the trusted provider runner's
absence verification at Delete completion. **It is not a fresh Azure Resource
Manager observation.** The digest binds retained evidence; it is not a signature
and does not authorize mutation or prove present absence by itself.

A coordinator requiring fresh provider absence still needs an independently
authorized exact-resource observation. It also needs its exact Stripe cleanup
receipt and all other completion guards. Do not release a pending staging run,
admit another Create, or mark a failed customer proof Passed from this historical
lookup alone. Unknown Create/Delete responses and checkout-only interruptions
remain separate reconciliation paths under #594.

## Fresh provider observation

`GET /api/admin/workspaces/{workspaceId}/instances/{instanceId}/operations/{operationId}/cleanup-observation/{organizationId}`

This separate Admin read requires the same complete historical evidence before
invoking the configured provider observer. It is unavailable when the concrete
Azure worker runner is not composed. No credentials, provider scope or resource
name is accepted from the caller.

The observer independently validates the terminal provider Delete, lifecycle key,
retained assignment and current execution authority. It issues one exact owned
resource-group existence query using the existing configured runner identity;
it does not log in, retry deletion, run cleanup or change provider state. A
successful parsed `false` is `Absent`, `true` is `Present`; failed commands,
malformed output and authority drift are `Unknown`. Cancellation remains
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
