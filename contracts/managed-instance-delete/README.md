# Managed-instance delete contracts

These JSON files lock the Hub-facing delete confirmation, accepted delete, and
each delete refusal Control already returns. `elsa-hub` tests (elsa-hub#219,
#217, #230) must load the same files. An accidental field, enum, status, or
`code` rename fails CI here rather than on the Hub.

Assumptions:

- Cloud billing copy is the engine-deletion notice (`billingNotice` on the
  confirmation). No prices, plan names, or Stripe references.
- Ids and timestamps in the goldens are placeholders. Control tests compare
  bodies byte for byte after replacing only ids and timestamps.
- Problem bodies are the ASP.NET `ProblemDetails` JSON Control actually
  serializes, including the stable `code` extension.
- Organization, workspace, instance, confirmation, and operation ids are the
  stable example `aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa`.
- No exception text, provider identifiers, or secrets.

## Goldens Hub tests must load

| File | HTTP | Stable code | Endpoint |
| --- | --- | --- | --- |
| `confirmation-success.json` | 200 | (none) | `POST .../delete-confirmations` |
| `delete-accepted.json` | 202 | (none) | `POST .../delete` |
| `delete-permission-required.json` | 403 | `instance.delete-permission-required` | `POST .../delete-confirmations` and `POST .../delete` |
| `delete-confirmation-required.json` | 400 | `instance.delete-confirmation-required` | `POST .../delete` |
| `delete-confirmation-invalid.json` | 409 | `instance.delete-confirmation-invalid` | `POST .../delete` |
| `operation-active.json` | 409 | `instance.operation-active` | `POST .../delete` while a blocking delete is in flight |
| `invalid-state.json` | 409 | `instance.invalid-state` | Shared lifecycle-conflict problem body (see below) |
| `changed-since-read.json` | 412 | `instance.changed-since-read` | `POST .../delete` after a customer mutation since If-Match |
| `rate-limit-exceeded.json` | 429 | `rate-limit.exceeded` | Lifecycle rate limiter (see below) |
| `overview-deleting.json` | 200 | `instance.deleting` | `GET .../overview` field slice (`allowedActions.delete`, `activeOperation`, `lastOperation`), not the full body |

`catalog.json` repeats that table in machine-readable form. Hub tests should
load it for the HTTP status next to each body.

## Operation ids Hub can show as a reference

- `delete-accepted.json` includes `operationId` (and `operationUrl`).
- Problem bodies (`delete-permission-required`, `delete-confirmation-required`,
  `delete-confirmation-invalid`, `operation-active`, `invalid-state`,
  `changed-since-read`, `rate-limit-exceeded`) do **not** include an operation
  id. Control's delete `Problem` helper only sets `code`.
- `overview-deleting.json` is the overview denial, not a problem body. A
  completed tombstone has no `activeOperation`. `lastOperation` has action and
  state but no operation id.
- `overview-deleting.json` is a **field slice** of the `GET .../overview`
  response, not the full body. It pins only `allowedActions.delete`,
  `activeOperation` and `lastOperation`. Hub tests should assert those fields
  and must not compare the whole overview response against this file; other
  overview fields can be added or changed without updating the golden.

## Notes Hub should not treat as behaviour changes

- Confirmation serializes `confirmationId`, not `id`. That is the field Hub
  #217 had to read.
- `instance.invalid-state` is the shared lifecycle-conflict problem
  (`ConflictCode` + the delete/operations `Problem` helper). POST `/delete`
  currently raises this less often than other lifecycle actions (for example
  Start on a Ready engine). The golden is that shared 409 body so Hub can map
  the code if Delete emits it.
- `rate-limit.exceeded` (429) is the managed-instance lifecycle limiter
  (`ManagedElsaInstanceLifecycleRateLimitDefaults`). Delete and
  delete-confirmations are not on that policy today; restart and apply-release
  are. The golden is the lifecycle 429 body Control already emits, so Hub can
  map it if a delete call is limited.

Regenerate locally with
`ELSA_CONTROL_REGENERATE_MANAGED_INSTANCE_DELETE_CONTRACTS=1` on the
`ManagedElsaInstanceDeleteContractTests` filter. Regeneration is refused in CI.
