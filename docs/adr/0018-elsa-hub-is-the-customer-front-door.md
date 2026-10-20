# ADR-0018: The Elsa Hub Is the Customer Front Door; the Control Console Is the Operator Tool

## Status

Proposed. Supersedes the frontend decision of [ADR-0008](0008-shared-control-web-platform.md) for the managed (Elsa Cloud) product mode. ADR-0008 still governs the Control console for hosted-control and self-hosted operation.

## Date

2026-10-20

## Context

ADR-0008 (2026-08-29) chose the existing Control console as the common frontend for every product mode, with `cloud.elsaworkflows.io` as the authenticated product. Delivery went another way:

- The customer product shipped as a separate app, elsacloud.app (`valence-works/elsa-cloud`), behind a purpose-built Cloud BFF.
- The Elsa Hub (`elsa-workflows/elsa-hub`, www.elsaworkflows.io) now creates, manages and deletes managed engines at `/dashboard/cloud` through the Hub BFF. Control recognises Hub sessions (#829), links Cloud accounts to Hub identities (#851), and projects Hub Stripe entitlements (#843).
- The roadmap (#729) states the standing rule: Cloud is the customer product and the Control console is an operator tool. Cloud never proxies the console. A customer need becomes a purpose-built Control endpoint on a BFF allowlist.
- On 2026-10-20 Sipke decided that the first-public Hosted launch (#508) ships on the Hub, and that elsacloud.app takes P0 fixes only until it retires (#828).

Agents follow ADRs literally, so the accepted ADR-0008 text keeps generating console-as-customer-frontend work.

## Decision

1. The **Elsa Hub** is the customer front door for the managed product: sign-up, sign-in, organizations, billing, managed engines and Open Studio.
2. The **Control console** is the operator tool for Valence operators, Elsa Control Cloud and self-hosted Elsa Control. It is not served to managed-product customers, and the Hub never proxies it.
3. The Hub reaches Control only through the Hub BFF and purpose-built Control endpoints on its allowlist, each with its own authorization and tests. They are added only when a real customer need exists.
4. Control remains the single system of record for organizations, entitlements, instances and lifecycle (ADR-0006). The Hub owns identity (Supabase), the public funnel and presentation only.
5. elsacloud.app and its Cloud BFF are retired under #828 after the cutover. `cloud.elsaworkflows.io` is not a product hostname.

## Alternatives Considered

### Keep ADR-0008: the Control console as the customer frontend

Rejected. It doesn't match what shipped, it would expose operator concepts to customers, and it conflicts with the roadmap rule that Cloud never proxies the console.

### Keep elsacloud.app as the customer product beside the Hub

Rejected on 2026-10-20. Two customer front ends double the BFF surface, the auth paths and the live-proof work.

## Consequences

- Hub sign-in, Hub BFF endpoints, Hub Stripe entitlements and account linking are on the launch critical path.
- #508's customer-journey rows are re-proven on the Hub. Rows that test Control or provider behaviour carry over.
- PRD sections that name `cloud.elsaworkflows.io` (`elsa-commercial-platform-prd.md`) and elsacloud.app return URLs (`hosted-subscription-management.md`) are updated when the cutover tasks (#826–#828) land.
- The console keeps its own CI and modularity requirements from ADR-0008 for operator and self-hosted use.
