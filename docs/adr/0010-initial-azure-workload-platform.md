# ADR-0010: Initial Azure Workload Platform and Stamp Topology

## Status

Accepted. **Amendment (2026-11-26):** [ADR-0021](0021-pulumi-provisioning-module.md)
supersedes the "checked-in Bicep is the production resource-realization source
of truth" decision below for **engine** workloads. This ADR remains in force
for Control host infrastructure and for the initial Azure Container Apps,
Azure SQL, Key Vault, and stamp-topology product shape. The Bicep runner
stays the default production engine-realization path until the Pulumi module
in ADR-0021 is cut over.

## Date

2026-08-29

## Context

Current Azure assets deploy the Elsa Control host to Azure App Service and Azure SQL. They do not provision Elsa workloads. The first deployment proof needs container compute, relational persistence, managed identity/secrets, routing/TLS, telemetry and durable provider reconciliation while preserving future dedicated/private and regional evolution.

Azure Container Apps, Azure SQL, Key Vault/managed identity, Azure Monitor/OpenTelemetry and checked-in Bicep were the preferred hypothesis. The bounded #108 proof has now validated the initial compute, relational persistence, identity/secret, revision, health, idempotency, cleanup, quota and IaC ownership boundary.

The [#108 conclusion](../spikes/108-azure-workload-provider-preflight.md) records the completed West Europe proof. A signed SQL-capable Elsa 3.8 Combined image ran with Azure SQL persistence, managed identity, Key Vault references, SQL-aware readiness, multiple-revision traffic protection, safe identical reapply, and complete owned-resource cleanup.

## Decision

- Use Azure Container Apps for the initial Azure workload provider and managed Elsa vertical slice.
- Use Azure SQL with a database per Dedicated instance initially; later isolation profiles require separate evidence.
- Use direct Container Apps HTTPS ingress for the provider slice. Persist its managed-TLS origin only; health paths and Azure resource identifiers remain provider-owned and are not part of the customer endpoint contract. Front Door, custom domains and managed edge routing remain a separate concern.
- Use managed identity and Key Vault for provider/workload secrets; store only safe references in control-plane history.
- Treat a deployment stamp as a provider-owned unit of region, capacity, failure containment and isolation.
- Use checked-in Bicep/provider modules as the production resource-realization source of truth **for Control host infrastructure**, with Elsa Control provider commands orchestrating idempotent plan/apply/checkpoint behavior. Aspire remains local orchestration/developer experience. Engine workloads move to the Pulumi Automation API module in [ADR-0021](0021-pulumi-provisioning-module.md); the Bicep runner remains the default production engine path until that module is cut over.
- Defer AKS until a concrete requirement cannot be met safely/economically by Container Apps.

## Acceptance Evidence

- A signed immutable Elsa endpoint was deployed and a workflow was published and executed successfully.
- Durable workflow definition and instance state survived a healthy revision replacement.
- Healthy, failed-SQL and recovery behavior proved the readiness/liveness and traffic boundary.
- Identical apply, interrupted/uncertain operations, rollback and cleanup convergence were exercised.
- Temporary bootstrap access, managed identity, Key Vault references and complete resource absence were verified.
- Scope, cost guardrails, quotas and deferred private/edge/HA concerns are recorded in the #108 conclusion.

## Consequences

- Azure provider implementation proceeds in PR-sized resource/lifecycle slices.
- Stamp and placement facts remain below the provider boundary established by ADR-0007.
- The current Elsa Control host infrastructure remains separate from managed Elsa workload resources.
