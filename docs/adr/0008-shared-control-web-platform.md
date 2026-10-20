# ADR-0008: Evolve the Existing Console into a Shared Control Web Platform

## Status

Accepted. For the managed product mode, [ADR-0018](0018-elsa-hub-is-the-customer-front-door.md) (Proposed, 2026-10-20) supersedes the frontend decision once accepted.

## Date

2026-08-29

## Context

The existing React console is served by Elsa Control and already contains authentication/session handling, workspace context, package/catalog, Runtime Builder, deployments, credentials, artifacts and logs/operations concepts. The product family needs SaaS onboarding and billing, hosted customer-infrastructure management and self-hosted operation.

Creating a separate Elsa Cloud frontend would duplicate core control-plane UX and contracts. Forcing every mode into identical screens would expose irrelevant SaaS or infrastructure concerns.

## Decision

Use the existing Elsa Control console as the common frontend platform.

- Shared modules cover organizations/workspaces, packages/features, application definitions, artifacts, desired state, environments, deployments, credentials, health and audit.
- SaaS, hosted-control and self-hosted shells compose routes/navigation/capabilities for their operating mode.
- Valence billing, subscription and managed-stamp operations are optional SaaS modules.
- Product-mode differences are expressed through capabilities and server contracts, not scattered plan-name or hosting-mode conditionals.
- `elsaworkflows.io` remains the public acquisition/content site; `cloud.elsaworkflows.io` is the authenticated product.

## Alternatives Considered

### Separate Elsa Cloud web repository

Rejected because it creates two control-plane products and duplicates deployment, feature, health and operations UX.

### One identical shell for every mode

Rejected because SaaS billing/Valence infrastructure and self-hosted provider administration are legitimately different.

## Consequences

- Console CI and modularity become launch-critical.
- Existing missing/placeholder modules must be audited before building onboarding.
- Large current deployment pages should be split along stable feature boundaries as they are changed.
- Marketing technology can remain independent from the product app.
