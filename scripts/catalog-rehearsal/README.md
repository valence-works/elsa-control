# Catalog migration rehearsal harness

Operator tooling for a no-ingress Azure Container Instances rehearsal of a Control API
candidate against a restored clone of the production Catalog database. It contains no
connection string, token, secret, or raw Azure output; every environment identity is an
explicit input. This is the versioned home of the harness that #266 and #279 used from a
temporary directory (#303).

The rehearsal runs the **candidate** API image first against the clone at its baseline
migration count, then the retained **previous** image second against the migrated clone.
Each phase is one fresh two-container ACI group in the proof-host resource group with an
`emptyDir` barrier volume and no `ipAddress`:

`CATALOG_SERVER` is the full host name (`<server>.database.windows.net`); the renderer rejects a short
name. The previous phase starts from the migrated clone, so its baseline counts are the candidate's target
counts, as in the flow below.

1. The probe sidecar audits the clone with the attached managed identity before the API
   starts: exact migration identities, preview-consent columns, common table counts,
   duplicate active `WorkspaceId+TargetKey` and `WorkspaceId+TargetKey+OperationIdentity`
   groups across the five active statuses (including `EntitlementHeld`), the two filtered
   unique indexes, and the identity's principal and migration permissions.
2. The API container waits for `/rehearsal/start-api`, runs the exact image entrypoint in
   `Production` with every mutation worker and exporter disabled, discards its own output,
   and records its exit code in `/rehearsal/api-exited` so a crash never leaves the probe in
   its full health timeout.
3. The probe requires two stable loopback `/health` responses bound to the expected baked
   source identifier and build number.
4. It audits again (foreign-key and check-constraint integrity through the catalog views,
   orphan provider assignments and operation transitions, non-null billing event states, the
   provider-assignment schema, the recovery-observation columns/foreign keys/natural-key
   index/append-only trigger and recovery-request columns), writes `/rehearsal/stop-api`,
   and emits exactly one value-free JSON result line.

Before it exits, the probe waits (up to 45 seconds) for a started API to record its own exit code, because
Container Instances terminates the remaining containers of a group when one exits; otherwise the API wrapper
would report 143 regardless of how the API stopped. `run-phase.sh` never deletes the group; cleanup is a separate operator step that must
verify the exact group is absent. A passing result also requires the group state
`Succeeded` and both containers terminal at exit code zero.

## Inputs

Copy `rehearsal.env.example` and replace every `REQUIRED` value. The renderer constructs
the canonical clone connection string itself (Entra managed identity, encryption enabled);
no connection material is accepted. Images must be immutable `@sha256:` references; the
probe image must live in the same registry. Produce the target migration list from the
exact candidate source checkout:

```sh
python3 scripts/catalog-rehearsal/list-migration-ids.py <candidate-checkout> expected-migrations.txt
```

Group names must start with `catalog-rehearsal-candidate` or `catalog-rehearsal-previous`;
the parser and comparison gate bind each result to its phase prefix and reject a reused group.

## Offline checks

```sh
python3 scripts/tests/test_catalog_rehearsal_harness.py
bash -n scripts/catalog-rehearsal/run-phase.sh
```

The tests use fake `az`, `sqlcmd` and `curl` boundaries only.

## Operator flow

```sh
set -a; . ./rehearsal.env; set +a
export REHEARSAL_PHASE=candidate REHEARSAL_GROUP_NAME=catalog-rehearsal-candidate-<build>
scripts/catalog-rehearsal/run-phase.sh
# delete the exact group and verify absence, then:
export REHEARSAL_PHASE=previous REHEARSAL_GROUP_NAME=catalog-rehearsal-previous-<build> \
  EXPECTED_BASELINE_MIGRATIONS=<target count> EXPECTED_BASELINE_PREVIEW_COLUMNS=<target preview columns>
scripts/catalog-rehearsal/run-phase.sh
python3 scripts/catalog-rehearsal/compare-results.py candidate-result.json previous-result.json
```

## Durable evidence

Keep results, ledgers, rollback captures and the rendered specs in a durable private
operator directory such as `~/.elsa-control-ops/` (directory 0700, files 0600).
`/private/tmp` and `/tmp` are not acceptable: on 2026-09-07 every artifact of the build
92 to 96 rehearsals, including the original harness and a production auth rollback
capture, was lost when `/private/tmp` was cleared (#303). `run-phase.sh` therefore writes
its temporary files under `REHEARSAL_TMPDIR` (default `~/.elsa-control-ops/tmp`).

## Limits

- The build number is a configured label (`Application__BuildNumber` is injected by the
  renderer from the same input the probe expects); the baked source identifier reported by
  `/health` is the real image identity binding.
- The probe retries a SQL query up to four times, 45 seconds apart, only when the clone answers error 40613
  (a serverless database resuming from auto-pause); the retained clone pauses after an hour idle.
- Integrity is audited through catalog views and dynamic `NOT EXISTS`/`NOT (definition)`
  counts over every enabled foreign key (including composite keys) and check constraint.
  Disabled constraints are not evaluated.
