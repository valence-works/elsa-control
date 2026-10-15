# ADR-0011: Customer-Side Deployment Agent for Elsa Control Cloud

## Status

Proposed (original decision below). **Amendment 1 (2026-10-15): Accepted** by the Elsa Control CEO, confirming the 13 Oct acceptance ([#732 comment 6186212953](https://github.com/valence-works/elsa-control/issues/732#issuecomment-6186212953)). Amendment 1 sets the agent direction. The original infrastructure-reconciler decision stays Proposed and deferred, as Amendment 1 states.

## Date

2026-08-29

## Context

Elsa Control Cloud must manage customer-owned Azure, Kubernetes, Docker or server environments without broad privileged inbound access from Valence. Elsa Control already has an outbound runtime command poll/claim/report pattern, scoped engine credentials and safe diagnostics. That may be a foundation for a customer-side infrastructure/workload reconciler, but current runtime commands primarily apply application artifacts to pre-registered engines.

## Proposed Decision

- Prefer a customer-side agent/reconciler that establishes an outbound authenticated connection to Elsa Control Cloud.
- Reuse the durable command/lease/result pattern where its trust and lifecycle semantics fit.
- Scope every agent identity to an organization, deployment environment and allowed provider capabilities.
- Agents resolve local credentials/secrets and call local infrastructure APIs; Elsa Control stores safe references and reported facts only.
- Enrollment, key rotation, revocation, agent updates, offline behavior, command signing/replay protection, least privilege and support access require explicit design and threat tests.
- Do not introduce an agent if the spike proves existing provider/GitOps mechanisms meet the same trust requirements more safely.

## Alternatives Considered

### Direct inbound Valence access to customer infrastructure

Rejected as the default because private networks, governance and least-privilege requirements make broad inbound control undesirable.

### Treat current runtime applier as a complete infrastructure agent

Rejected without evidence: artifact apply and infrastructure lifecycle have different privilege, drift and recovery responsibilities.

## Evidence Required Before Acceptance

- Enrollment and revocation proof.
- Scoped authorization and replay-resistance tests.
- Offline/reconnect and interrupted-reconciliation behavior.
- Customer Azure/Kubernetes/Docker permission models and update trust.

## Amendment 1 (2026-10-15): The connector is the engine-level agent; desired state rides protocol 1

Status: **Accepted** (Elsa Control CEO, 15 Oct 2026, confirming the 13 Oct acceptance; recorded on #732, comment 6186212953). It merges under the standard rule: Elsa Control Code Review **Verdict: APPROVE** on the exact head plus required-green CI.

Refs #732 (D0), #731, #316, #317, #318, #32, #340, #484, #499, #501, #614, #635, ADR-0004, ADR-0007.

### Context for this amendment

- **The external-engine connector is already a scoped outbound agent.** Since the original decision was proposed, the connector (#499; `valence-works/elsa-control-connector`) has shipped:
  - an outbound-only, proof-authenticated agent identity bound to organization, workspace and connection;
  - single-use pairing, rotation and revocation;
  - a runner lease and heartbeat (`docs/security/external-engine-enrollment-protocol.md`; `docs/protocol/external-engine-connector/v1/README.md`).
- **What it lacks:** it advertises only `connection.status` and `studio.open`, and it receives nothing from Control.
- **#731 needs a delivery path** for configuration and workflow definitions to connected engines and to managed engines (Hosted and BYO).
- **#501 needs a channel** for runtime commands.

### Decision A: ADR-0011 direction

1. **The connector is the engine-level agent.** Its identity, runner lease and heartbeat are the only engine-side trust anchor. No second agent identity, enrollment model or channel is introduced.
2. **Two message classes share the connector's outbound pull transport.**
   - **Declarative desired-state revisions (#731):**
     - permission `ExecuteDeployment` (#484);
     - capability `desired-state.apply`;
     - their own confirmation and audit.
   - **Imperative allowlisted runtime commands (#501):**
     - permission `ExecuteControls` (#484);
     - their own capability, named in #501's design;
     - the existing durable command, lease and replay contract (ADR-0004, #318);
     - their own confirmation and audit.

   Holding one permission or capability never grants the other.
3. **The customer-side infrastructure reconciler (#317–#321) stays deferred.** It is not folded into the connector. The connector never gets image, infrastructure or cloud-credential authority. Image and infrastructure changes stay with the provider, for managed engines only. The original Proposed decision above remains the placeholder for that reconciler and still needs its own evidence before acceptance.
4. **#317 is re-scoped onto the connector identity.** #317's DeploymentAgent aggregate isn't built as a second enrollment model. Its acceptance criteria map as follows:

   | #317 AC | Connector identity |
   |---|---|
   | Single-use enrollment token | Met: a single-use pairing challenge, stored as a hash. |
   | Token expiry of 15 minutes or less | Met: 10 minutes by default, never more than 15 (`external-engine-enrollment-protocol.md:43`). |
   | No raw credential stored | Met: Control stores only the connector public key and never the private key (`:11`, `:103`). |
   | Revoke fails the next call | Met: every subsequent proof fails immediately (`:91`). |
   | Cross-organization negative tests | Met: proofs are scope-bound (`:81`). |
   | Binding to a deployment environment | **Remaining work.** The connector is scoped to organization, workspace and connection. The binding of a connection to a #340 environment is added in the #731 build. |

5. **Outbound only.** No inbound port, no webhooks into the engine, and Control never dials the engine.

### Decision B: drift policy and packaging

- **Connected (customer-run) engines:** **detect and report** by default.
  - The connector computes a value-free diff against local state and reports it.
  - The report stays `SelfReported` (#614).
  - **Enforce** is opt-in per connection, set by an `ExecuteDeployment` holder.
- **Managed engines (Hosted and BYO):** **enforce on next apply.**
  - Control is the source of truth.
  - Detected drift raises a drift alert first.
  - The next apply restores the desired revision.
- **Packaging:** desired-state delivery is included in the **Dedicated and BYO** paid plans and is **not** included in the Shared trial. Connected engines need a paid plan. Commercial terms are set outside this ADR.

### Decision C: protocol, staying on protocol `"1"`

Desired-state delivery is **additive within connector protocol `"1"`**. There is no protocol version bump.

1. **Control → connector support signal.**
   - The heartbeat `200` response (the unsigned `ExternalEngineConnectionResponse`, `src/Hosting/ElsaControl.Api/Workspace/ExternalEngineConnectionEndpoints.cs:214`) gains an optional `desiredState` object containing:
     - `supported` (bool);
     - when the connection is enabled and eligible, the current desired-revision pointer (revision id and bundle digest);
     - the signing key-id denylist.
   - Protocol-1 connectors already ignore this body. They send the heartbeat with `requireBody: false` and never deserialize the response (elsa-control-connector `c527a35`, `src/ValenceWorks.ElsaControl.Connector.Core/Client/ConnectorControlClient.cs:46-57`, `:140-141`). An older connector therefore sees no pointer and gets no error loop.
2. **The connector advertises `desired-state.apply` only after Control says it's supported.**
   - It adds the capability only after a heartbeat response carries `desiredState.supported = true`.
   - It drops the capability on the next beat if a response lacks that flag, for example after a Control rollback under #730.
   - This rule exists because Control rejects capabilities outside its allowlist with `InvalidReport` → HTTP 400 (`src/Deployment/ElsaControl.Deployment.Core/ExternalConnections/ExternalEngineHeartbeatService.cs:35-36`, `:103-104`; `ExternalEngineConnectionEndpoints.cs:228`). The connector treats a heartbeat 400 as a permanent rejection and backs off (`ConnectorControlClient.cs:127`, `:218-223`; `Hosting/ConnectorRunner.cs:504-516`).
   - Control adds `desired-state.apply` to its allowlist **before** it ever sets `supported = true`.
3. **Canonical bytes don't change.**
   - Capabilities are a string array inside the signed heartbeat report (`ExternalEngineHeartbeatService.cs:226-229`). A new value needs new golden vectors in `docs/protocol/external-engine-connector/v1/vectors.json`, but no new encoding, algorithm or key version.
   - So the downgrade and algorithm-agility rule (`docs/security/external-engine-enrollment-protocol.md:33`, `:107`) isn't triggered.
4. **New operations get new signed domains.**
   - Bundle fetch, value-free diff report, apply report and confirmation are new connector → Control operations with their own operation domains, for example `external-engine.desired-state.fetch` and `external-engine.desired-state.report`.
   - They use the existing proof binding: identity, organization, workspace, connection, audience, payload digest, single-use nonce and runner lease.
   - Existing operations and vectors are untouched.
5. **The pointer is a hint. The bundle carries the trust.**
   - Bundles are signed by Control under a key id **separate from the handoff key** (#318), with organization, workspace, connection, revision and parent revision inside the signed content.
   - The connector verifies the signature and digest before applying.
   - It refuses any revision that isn't newer than the last one it applied, except a hash-equal rollback revision issued by Control.
6. **Ineligible connections get no pointer and can't fetch.** That covers revoked, stale, offline, incompatible and not-enabled connections, and a protocol-1 connector without the capability. They never get an error loop.

### Current state: managed engines don't run the #32 applier today

**Answer: no.** Today no managed Hosted or BYO engine runs the #32 workflow-artifact runtime applier, and no engine-side code polls Control's runtime-command channel.

| Where | Finding |
|---|---|
| Control, the applier itself | The applier exists only as a library: `src/Workflows/ElsaControl.Workflows.RuntimeApplier/`, with `WorkflowArtifactCommandProcessor.cs:6`, `WorkflowRuntimeCommandHttpClient.cs:9` and `JournaledWorkflowDefinitionApplier` in `WorkflowArtifactApplyJournal.cs:31`. |
| Control, storage | The only implementations of its journal and store are in memory: `InMemoryWorkflowArtifactApplyJournal` (`WorkflowArtifactApplyJournal.cs:6-8`) and `InMemoryWorkflowDefinitionRuntimeStore` (`InMemoryWorkflowDefinitionRuntimeStore.cs:5`), behind the `IWorkflowDefinitionRuntimeStore` interface (`WorkflowArtifactRuntimeContracts.cs:46`). There's no adapter to an Elsa workflow-definition store. |
| Control, references and packaging | The project is referenced only by test projects: `tests/Workflows/ElsaControl.Workflows.RuntimeApplier.Tests/...csproj:13` and `tests/Hosting/ElsaControl.Api.Tests/ElsaControl.Api.Tests.csproj:18`. No workflow in `.github/workflows/` packs or publishes it. |
| Control, server side | Control does serve the engine-facing runtime-command API (`src/Hosting/ElsaControl.Api/Workspace/RuntimeCommandEndpoints.cs:16-233`: list, claim, download, heartbeat, progress, complete, fail, reject). So the server half exists. |
| Managed engine provisioning | The Container App environment that Control provisions carries Nuplane feed, CShells persistence and identity, and `ManagedElsa__Handoff__*` settings only. There's no runtime-command, engine-credential or applier configuration (`infra/azure-production/modules/container-app.bicep:191-341`, `:412-432`). The release feed include patterns admit only `Elsa.Persistence.EFCore.SqlServer` and `Elsa.Scheduling.Quartz.EFCore.SqlServer` (`:229-235`). |
| Valence Runtime image | `valence-works/elsa-production-image` at `b8353d1` has no reference to `ElsaControl.Workflows.RuntimeApplier`, runtime commands or a Control command client. Its host projects reference Elsa, CShells and Microsoft packages only (`src/ElsaProServer/ElsaProServer.csproj:10-11`, `src/ElsaProCombined/ElsaProCombined.csproj:10-11`). |
| Connector | `valence-works/elsa-control-connector` at `c527a35` contains no applier and no command handling. It heartbeats and hands off to Studio only. |

**Consequences:**
- Managed parity requires **D5**: build the connector, with a real Elsa-backed definition store and a durable apply journal, into the Valence Runtime image.
- Until D5 ships, managed engines receive changes only through the provider (image and infrastructure). "Enforce on next apply" (Decision B) takes effect for managed engines from D5 onward.
- #32's apply-journal semantics (idempotent on command or revision id plus digest) are reused, but the in-memory journal is not durable enough for production and must be replaced in D3 and D4.

### Alternatives considered

- **A second agent identity or channel for commands (#317 as originally scoped).** Rejected: it doubles the enrollment, rotation and revocation surface already shipped and proven for the connector.
- **Inbound webhooks, or Control dialling the engine.** Rejected: they break outbound-only and require inbound access to customer networks.
- **A long-lived WebSocket or SignalR push.** Rejected as a channel: it adds stateful Control infrastructure, and it's fragile through customer proxies. It may return later as a latency optimisation behind the same pull contract.
- **GitOps-only** (the customer's repository is the source and Control observes). Rejected: no confirmation gate, value-free diff, audit or last-known-good rollback, and managed engines would need a second path.
- **Enforce by default on connected engines.** Rejected: it silently overwrites local Studio edits on engines Valence doesn't operate, and Control can't claim more than `SelfReported` there.
- **Detect-only on managed engines.** Rejected: recovery and rollback need a reproducible desired state, and Valence is the operator.
- **Desired-state delivery in every plan, including the Shared trial.** Rejected: support load would be unbounded, and Shared runs only the governed image under quotas.
- **Protocol `"2"` with negotiation.** Rejected: today an unsupported protocol gets HTTP 426 and the connector rechecks every 6 h (`ExternalEngineHeartbeatService.cs:74-100`). A bump would force every connector to upgrade, or Control to speak two protocols, for an additive change. A bump is reserved for canonicalization, algorithm or key-ceremony changes.
- **Make Control silently accept unknown capabilities.** Rejected: it weakens a fail-closed v1 rule to solve an ordering problem that the support signal already solves.
- **Carry the pointer inside a signed heartbeat proof.** Rejected: proofs go from connector to Control. Control's signature belongs on the bundle, where it protects content and not just a hint.
- **Fold the infrastructure reconciler into the connector.** Rejected: artifact apply and infrastructure lifecycle have different privilege, drift and recovery responsibilities (see the original alternatives above).

### Test sketches (D3/D4)

1. **Back-compat:** a connector at `c527a35` against a Control that returns `desiredState` keeps getting heartbeat 200, stays fresh, and never fetches.
2. **Support gating:** a new connector against a Control without support never sends `desired-state.apply`, and heartbeats stay 200.
3. **Rollback:** `desiredState.supported` disappears from responses. The connector drops the capability on the next beat, with no 400 and no stale projection.
4. **Golden vectors:** new vectors for a heartbeat carrying `desired-state.apply` and for the fetch and report proofs. CI regenerates them and diffs.
5. **Eligibility:** revoked, stale, offline, incompatible and not-enabled connections get no pointer, and their fetch is denied with a safe code.
6. **Bundle integrity:** a wrong signature, wrong digest, a revision for another connection, a non-monotonic revision or a denylisted key id is refused before apply, with an audit event.
7. **Permission split:** an `ExecuteDeployment` holder can't issue #501 commands, and an `ExecuteControls` holder can't publish or confirm desired state.
8. **Drift:** a connected engine in detect mode reports a value-free diff and nothing is overwritten. A managed engine (from D5) raises a drift alert, and the next apply restores the desired revision.

### Remaining evidence before the #731 build is armed

- **D0 delivery ADR:** bundle format, allowlist, interim signer and its stated limits, apply semantics, and evidence grades for Control-deployed images (#614).
- **D5 design:** connector-in-image, Elsa-backed store, durable journal, and how a managed engine gets connector credentials with no human pairing step.
- **Environment binding** of a connection (the #317 remaining item).
