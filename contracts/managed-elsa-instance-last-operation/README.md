# Managed instance lastOperation contracts

These JSON files lock the Hub-facing `lastOperation` object on
`ManagedElsaInstanceResponse` (canonical list and detail). The shape and names
match overview `ManagedElsaInstanceOverviewLastOperationResponse`: `action`,
`state`, `completedAt`, `failureCode`, and optional `reasonCode` when the state
is `Failed` or `RecoveryRequired`. Hub tests should load these files.

The field is additive and nullable. An instance with no completed operation
omits a last-operation object (`lastOperation` is JSON `null`). The legacy
`/managed-elsa/instances` list may leave the field null.

Assumptions:

- Enum values are the Control JSON names (`Create`, `Retry`, `Restart`, `Failed`).
- `failureCode` is the customer-safe mapped code, never a provider identifier.
- `reasonCode` is omitted when null.
- Timestamps are the stable example `2026-11-27T12:00:00+00:00`.

Regenerate locally with
`ELSA_CONTROL_REGENERATE_MANAGED_ELSA_INSTANCE_LAST_OPERATION_CONTRACTS=1` on the
`ManagedElsaInstanceLastOperationContractTests` filter. Regeneration is refused
in CI.
