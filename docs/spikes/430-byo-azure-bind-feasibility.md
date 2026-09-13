## Feasibility evidence pack — #430 BYO Azure bind + Entra customer-tenant org mint

**Author:** Elsa Control CEO staff (design-first spike; no Cursor App claim)  
**Date:** 2026-09-13  
**Depth:** Design-only Pass (no live scratch tenant/sub; no production merge; no Stripe/#384 touch; no Marketplace / #103)  
**Parent lock (#429):** BYO Azure sub; customer Entra + bind; Valence operates; same Control shell; defer Marketplace + #103; do **not** touch Stripe secrets.

---

### Honesty redlines (Launch Ops skim)

- **Preview ≠ Dedicated / GA / SLO.** This pack does not claim production BYO or Dedicated isolation for Azure-bound partners.
- **No Azure Marketplace / Logic Apps replacement** claims. Private-offer assist remains OK per parent; metering / public listing deferred.
- **Azure billing = intended / not live.** Cash north star remains Stripe (#384 / #400).
- **Valence-operated Entra remains dogfood-only** for the BYO commercial path.
- **No fake “Ready”** without bind Pass + azure-bound entitlement Pass.

---

### AC1–AC9 Pass/Fail

| AC | Result | Evidence summary |
|---|---|---|
| **AC1** Entra → org mint model | **Pass** | Model below: multi-tenant Entra app; uniqueness `tid` (+ optional verified domain); ExternalIdentity key = issuer(`…/{tid}/v2.0`) + `oid`; dogfood isolation stated; gaps vs single-authority OIDC listed. |
| **AC2** Subscription binding model | **Pass** | **Winner: Azure Lighthouse** (see pick + sequence). Stored bind record fields/states + unbind/relink at design level. Alternatives lose for stated reasons. |
| **AC3** Permissions / roles | **Pass** | Concrete minimum mapped to current preflight (`Contributor`/`Owner` + `Owner`/`UAA`/`RBAC Administrator` at sub scope; registry Narrow/BuiltIn path). Owner/UAA called out with narrowing path. |
| **AC4** Automate vs concierge | **Pass** | Full step table; concierge runbook outline for first N partners. |
| **AC5** Entitlement mint | **Pass** | Provider name **`azure-bound`**; gate interaction; refuse dual active commercial providers; never reuse `internal` for customers. |
| **AC6** ADR-0016 vs BYO gap | **Pass** | Explicit gap list vs pinned `SubscriptionId` / runner composition / preflight identity-in-sub assumption; follow-on leaf split only (no drive-by rewrite). |
| **AC7** Evidence pack | **Pass** | This comment (optional `docs/spikes/` PR if push succeeds). Honesty redlines above. |
| **AC8** Non-goals respected | **Pass** | No Marketplace metering, no #103, no Stripe secret/#384 wiring changes in this leaf (read-only citations only). |
| **AC9** Next leaves + risks | **Pass** | Four implementation leaves with rough size + top risks. |

**AC summary: 9/9 Pass (design-only).**

---

### P1 — Entra customer-tenant → org/workspace mint

**Today (repo):**
- `CustomerOidcOptionsConfigurator` configures a **single** `Authority` / `ValidIssuer` (`ControlIdentityOptions.Authority` / `.Issuer`). Audience = `ClientId`. No multi-tenant issuer validation.
- `ControlIdentityOptions` already has `Provider = MicrosoftEntra` enum value, but configuration remains one Authority string — not “any customer tenant.”
- `AccountWorkspaceService.GetOrCreateAsync` mints Account + Organization + default Workspace on first `TrustedWorkspaceIdentity`, keyed by **`ExternalIdentity(Issuer, Subject)`**. First binder becomes `OrganizationRole.Owner` / `WorkspaceRole.Owner`. Concurrent mint is conflict-retried on issuer+subject.
- Studio / managed runtime handoff (#127) stays **Control-authoritative** — BYO must not invent a second customer IdP for the runtime.

**Proposed mint model (BYO):**
1. Register Control customer login as a **multi-tenant Entra app** (orgs + optionally personal Microsoft accounts **out of scope for MVP** — work/school only).
2. Token claims used:
   - **`tid`** — customer Entra tenant id (org uniqueness primary key)
   - **`oid`** — user object id (account subject within tenant)
   - **`preferred_username` / `email` / `name`** — display only (not uniqueness)
   - **`iss`** — must be `https://login.microsoftonline.com/{tid}/v2.0` (or common/organizations issuer with `tid` validated)
3. **Mint trigger:** first successful Control OIDC login where `tid` is not on the Valence dogfood allowlist and no Organization exists for that `tid`.
4. **Uniqueness key:** Organization keyed by Entra **`tid`** (new `OrganizationIdentityBinding` / `entra_tenant_id` unique). Account ExternalIdentity: `Issuer = https://login.microsoftonline.com/{tid}/v2.0`, `Subject = oid`.
5. **Optional verified domain:** store primary verified domain for UX/support; **not** required for uniqueness (domains move; `tid` does not).
6. **Admin membership of binder:** first successful login creates org + default workspace and grants binder Owner (same as today’s personal mint path). Subsequent users from same `tid` join existing org (default: Member; promote via existing org membership APIs / concierge).
7. **Rejection / merge rules:**
   - Same `tid` → always same Organization (no second org from login alone).
   - Same company, **multiple tenants** → multiple Organizations (honest; merge is manual/concierge with audited transfer).
   - One tenant wants **multiple orgs** → out of MVP self-serve; concierge creates additional org only with explicit operator path (do not invent multi-org-per-tid auto).
   - Collision with existing Stripe-created org for same human: do **not** auto-merge across providers; support-mediated link only.
   - Tokens with missing/empty `tid` → reject BYO mint.
8. **Dogfood isolation:** Valence-operated Entra tenant id(s) remain on an allowlist that continues to use **`internal`** entitlement / existing dogfood path. BYO mint + `azure-bound` entitlement **must not** apply to dogfood tenant ids. Explicit statement: **Valence Entra = dogfood-only** for commercial BYO.

**Gaps vs today:**
- Single-issuer OIDC validation must become multi-tenant (`ValidateIssuer` via tenant allow-any / issuer validator that checks `tid` + Entra issuer pattern).
- No `tid`→Organization store today.
- Personal-workspace-first mint may need a BYO “organization workspace” default name derived from tenant display name (Graph optional; concierge OK for MVP).

---

### P2 — Azure subscription binding (recommended pick)

#### Winner: **Azure Lighthouse (delegated resource management)**

**Why this wins for current runner (az CLI + user-assigned MI):**
- Control worker already authenticates as a **Valence-tenant user-assigned MI** (`AzureProviderRunnerOptions.AzureCliClientId`, `az login --identity`).
- Lighthouse lets that **managing-tenant principal** operate in the **customer subscription** after the customer deploys a subscription-scoped offer/ARM — **no customer secrets**, no attaching a foreign UAMI to Valence App Service, no rewriting the MI login model for MVP.
- Matches parent lock: **Valence operates** in customer-owned sub (payer inverted; operator unchanged).

**Sequence (design-level):**
```text
Customer Entra user (Owner on target sub)
  → signs into Control (P1) → org+workspace exist
  → Control shows bind consent / “deploy Lighthouse offer” (UX mocks reference-only)
  → Customer deploys ARM/Lighthouse registration on chosen subscriptionId
       grants Valence managing tenant principal(s) the RBAC set in P3
  → Control (or concierge) verifies:
       tenantId + subscriptionId visible to Valence MI; preflight roles present
  → Bind record → Active
  → azure-bound entitlement mint (P4) may proceed
  → create-instance uses existing commercial gate + per-org target overlay (P5/AC6)
```

**Stored bind record (design):**
| Field | Notes |
|---|---|
| `organization_id` | FK |
| `customer_tenant_id` | Entra `tid` |
| `subscription_id` | GUID, canonical lowercase |
| `managing_tenant_id` | Valence |
| `managing_principal_object_id` / `client_id` | Provisioner MI |
| `registration_definition_id` / fingerprint | Lighthouse offer version |
| `state` | `PendingConsent` → `Verifying` → `Active` → `Degraded` / `Unbound` |
| `verified_at`, `last_preflight_code`, `created_by_account_id` | Audit |
| `unbind_reason` | Optional |

**Unbind / relink:**
- **Unbind:** customer or operator sets `Unbound`; revoke entitlement managed-hosting (or set lifecycle Constrained); refuse new create-instance; existing instances → separate drain leaf (not this spike).
- **Relink:** only to a new `subscription_id` after Unbound; never silently retarget retained provider assignment fingerprints (ADR-0016 consequence). Relink is a **new** bind verification, not an edit-in-place of historical operations.

#### Alternatives (lost)
| Approach | Why not MVP winner |
|---|---|
| **Customer-created SP + client secret to Valence** | Secrets on wire/ops; mismatches MI runner; high leak risk. |
| **Customer UAMI attached to Valence host** | Cross-tenant attach to App Service is awkward; host identity matrix explodes; still needs role grants. |
| **ARM deploy-as-customer with customer creds in Control** | Control must not hold customer user creds; violates operate-as-Valence model. |
| **Pure custom RBAC without Lighthouse** | Possible later; Lighthouse gives auditable managing-tenant delegation customers already understand for ISVs. |

---

### P3 — RBAC minimum for operate-in-customer-sub

**Mapped from current code** (`AzureProviderAuthorityPreflight` + `AzureProviderRegistryAuthority`):

At **customer subscription** scope, provisioner principal must satisfy **both**:
1. **Mutation role:** built-in **Contributor** (`b24988ac-…`) **or** **Owner** (`8e3af657-…`)
2. **Role-assignment role:** **Owner** **or** **User Access Administrator** (`18d7d88d-…`) **or** **Role Based Access Control Administrator** (`f58310d9-…`)

Because per-instance sibling RGs are created under the subscription, **anchor-RG-only** Contributor is **insufficient** (preflight comment: authority on configured anchor group alone cannot create sibling groups).

**Registry (Valence-owned ACR, `RegistrySubscriptionId` today):**
- Prefer existing **Narrow** registry authority: custom deployment-metadata role on registry RG + conditional **RBAC Administrator** on registry resource limited to **AcrPull** assignment to workload MI (already in `infra/azure-customer-subscription/registry-authority.bicep`).
- BYO workloads still need **AcrPull** on the instance MI toward Valence ACR → **cross-tenant AcrPull** grant (see open Q7 answer below). BuiltIn path requiring Contributor on Valence registry RG for the **customer-sub provisioner** is worse; keep Narrow + explicit AcrPull.

**What forces Owner / UAA today:** role assignments for workload MI (AcrPull, KV secrets user, SQL-related). Preflight explicitly requires a role-assignment-capable built-in.

**Narrowing path (follow-on, not MVP blocker for concierge):**
- Custom role at subscription: RG write/delete + provider resource actions for ACA/SQL/KV/MI **without** full Contributor.
- Conditional UAA / RBAC Admin limited to assigning a small allowlisted set of role definition IDs (pattern already used for registry AcrPull condition).
- Longer-term: deploy into a **single pre-created customer RG** to drop subscription-wide RG create (product/topology change — separate leaf).

**MVP Lighthouse grant recommendation:** Contributor + Role Based Access Control Administrator at subscription scope (prefer RBAC Admin over Owner/UAA when available), plus documented Narrow registry grants on Valence side for pull.

---

### P4 — Entitlement for Azure-bound orgs

**Today:** `BillingProviderNames` = `stripe` | `internal` only. Commercial gate (`EfCoreElsaInstanceCommercialGate`) requires entitlement snapshot: `ManagedHostingEnabled`, non-expired, `SubscriptionState` not Constrained/Suspended/Retained/Deleted, create respects `MaxInstances`. Stop/Delete always allowed. Gate is provider-neutral on the snapshot — provider distinction lives on `OrganizationSubscriptions.Provider` and mint paths.

**Do not** reuse `internal` for customers (admin internal grant explicitly refuses provider-owned subscriptions and is dogfood-only).

#### New billing provider name proposal
**`azure-bound`** (constant `BillingProviderNames.AzureBound = "azure-bound"`).

Rationale: opaque provider id; not a card processor; not operator dogfood; distinct from Stripe checkout/webhook pipeline (those continue to refuse non-stripe providers the same way they refuse `internal`).

**Schema recommendation:**  
- Keep entitlement **snapshot** shape (managed hosting flags) — minimizes gate churn.  
- Add **`OrganizationAzureSubscriptionBind`** table (P2) as the bind source of truth.  
- Mint path: after bind `Active`, create/advance `OrganizationSubscription` with `Provider = azure-bound`, Active state, project snapshot `ManagedHostingEnabled=true`, `MaxInstances` per Preview policy, optional expiry for design-partner window.  
- **Conflict rules:** if subscription provider is `stripe` or `internal` → refuse azure-bound mint (mirror `CommercialSubscriptionExists`). **Dual active commercial providers: refuse** (default recommendation). Historical cancelled Stripe + new azure-bound may be allowed only when Stripe lifecycle is Deleted/Retained per existing lifecycle rules — eng leaf must encode exact matrix; default spike stance: **one active commercial provider per org**.

**Gate interaction for create-instance:**  
1. Existing commercial gate Pass (entitlement + lifecycle + cap)  
2. **Additional BYO check (new):** bind state `Active` and `subscription_id` present for org when provider is `azure-bound`  
Without (2), no fake Ready / no create.

---

### P5 — Automate vs concierge (MVP)

| Step | Automate / Concierge | Notes |
|---|---|---|
| Customer Entra sign-in (multi-tenant app) | **Automate** (leaf) | After multi-tenant OIDC lands |
| Org + default workspace mint on `tid` | **Automate** (leaf) | P1 |
| Join existing org for same `tid` | **Automate** (basic) / concierge for role upgrades | |
| Choose Azure subscription | **Concierge** assist OK | Customer picks; Control stores intent |
| Deploy Lighthouse / role grant | **Concierge** for first N | Offer ARM + runbook; later self-serve button |
| Verify bind / preflight roles | **Automate** check + **concierge** on fail | |
| Mint `azure-bound` entitlement | **Concierge** for first N (operator/admin API patterned on internal grant but **new provider**) | Self-serve mint after bind verified = follow-on |
| First managed instance create | **Automate** via existing create flow **once** targeting overlay exists | Else concierge operates Valence path only — do not lie |
| ACR cross-tenant pull grant | **Concierge** (Valence ops) | |
| Support / drain / unbind | **Concierge** | |

**Concierge runbook outline (first N design partners):**
1. Confirm scratch/partner Entra `tid` + subscription Owner contact.  
2. Ensure partner can sign into Control preview (multi-tenant app consent).  
3. Confirm org mint; record `organization_id` / `tid`.  
4. Deliver Lighthouse ARM + exact role list; partner deploys on target sub.  
5. From Valence MI: `account set` + role observation (same checks as preflight).  
6. Record bind Active; mint `azure-bound` entitlement (max instances, expiry).  
7. Apply per-org subscription targeting overlay; create first Preview instance.  
8. Honesty script: Preview, customer pays Azure for infra, Valence operate fee intended/not Marketplace-metered.

**Self-serve claim boundary:** until Lighthouse deploy + entitlement mint are productized, UX must say **guided onboarding / design partner**, not “self-serve Azure Marketplace.”

---

### Open questions — answers

1. **Bind pick:** Azure Lighthouse (above).  
2. **Multi-tenant Entra:** **One multi-tenant app registration** accepting work/school tenants is enough for MVP. Per-tenant apps / B2B guest-only are unnecessary overhead; admin consent may be required for org-wide use — document in runbook.  
3. **Org uniqueness:** **`tid` sufficient** as org key for MVP. Multi-tenant companies → multi-org; multi-org-per-tenant → concierge only.  
4. **Provider name / schema:** **`azure-bound`** + separate bind table (minimizes Stripe/`internal` collision).  
5. **Dual entitlement:** **Refuse dual active commercial providers.**  
6. **Per-org targeting:** Smallest change = **assignment / operation target overlay** (per-org or per-assignment `subscriptionId` in provider resource assignment) **without** rewriting ADR-0016 Valence default path — new leaf; not “just change config.” Host-level `AzureProviderTargetScope.SubscriptionId` stays Valence default for dogfood.  
7. **Registry pull across tenants:** Valence ACR must grant **AcrPull** to the **workload MI in the customer tenant/sub** (and Narrow registry admin remains on Valence provisioner). Customer-sub provisioner does **not** get Valence registry Contributor.  
8. **Concierge cut:** P2 deploy + P3 grant verification failures + entitlement mint stay manual for first N without claiming self-serve.  
9. **Live proof:** **Design-only Pass** this spike. No scratch customer Entra/sub used; no `needs:live-proof` yet.

---

### AC6 — ADR-0016 vs BYO gap list

ADR-0016 isolates **Valence-hosted** customer workloads in a dedicated Valence subscription (payer = Valence). BYO **inverts payer** to the customer subscription while Valence still operates.

| Gap | What breaks / why |
|---|---|
| Pinned `AzureElsaInstanceProviderOptions.SubscriptionId` | Single configured sub; Enabled provider validates one GUID. |
| `AzureProviderTargetScope.SubscriptionId` in runner composition | Worker process binds one target scope fingerprint at startup (`AzureProviderRunnerComposition`). |
| Preflight `identity list --subscription {target}` | Assumes provisioner UAMI **lives in** the workload subscription; Lighthouse-managed identity may live in Valence sub — observation query must accept managing-tenant identity visible via Lighthouse (implementation leaf). |
| Fingerprinted operations | Historical assignments must not be silently retargeted (ADR text). BYO needs new assignments for customer sub, not edits. |
| `infra/azure-customer-subscription` | Bootstraps **Valence** workload sub anchor + provisioner MI — contrast only; not customer BYO template (Lighthouse offer is new artifact). |
| Registry split already exists | `RegistrySubscriptionId` ≠ workload sub helps BYO, but AcrPull cross-tenant still required. |
| Commercial account-ready | Workspace + entitlement exist; BYO also needs bind Active before create. |

**Not acceptable:** “just change config” to a customer sub globally (would break dogfood/Valence path and starve ADR-0016).

---

### Recommended bind mechanism (one winner)

**Azure Lighthouse** — customer deploys Valence managing-tenant delegation on their subscription; Control’s existing MI operates with P3 roles; bind record stores tenant/sub/state.

---

### RBAC minimum list (MVP)

- **Customer subscription:** Contributor **and** (Role Based Access Control Administrator **or** User Access Administrator **or** Owner). Prefer Contributor + RBAC Administrator.  
- **Valence ACR (cross-tenant):** AcrPull on workload instance MI; Narrow registry metadata/admin on Valence provisioner (existing).  
- **Call-out:** Owner/UAA forced today by role-assignment needs; narrow with conditional RBAC Admin + custom mutation role in a hardening leaf.

---

### New billing provider name

**`azure-bound`** (`BillingProviderNames.AzureBound`).

---

### Next implementation leaf split

| Leaf | Scope | Rough size |
|---|---|---|
| **A — Identity mint** | Multi-tenant Entra OIDC issuer validation; `tid`→org binding; dogfood allowlist isolation; join-same-tenant | **M** |
| **B — Bind + RBAC** | Lighthouse offer ARM; bind record + states; verify roles; unbind/relink; concierge runbook | **L** (split verify UI later) |
| **C — Entitlement provider** | `azure-bound` constant; mint/revoke API (not Stripe; not `internal`); gate+bind conjunction; dual-provider refuse | **M** |
| **D — Multi-sub targeting** | Per-org/assignment customer `subscriptionId` overlay; preflight identity observation fix for Lighthouse; keep ADR-0016 Valence default | **L** |

Do **not** mark these `ready-for-agent` from this spike; parent/CEO dispatches when Stripe #384 capacity allows.

---

### Risks

1. **RBAC breadth** — Contributor+UAA/Owner at customer sub is a hard sell; mitigate with honesty + narrowing leaf.  
2. **Multi-tenant OIDC** — issuer validation bugs → tenant mix-up; require `tid`-scoped issuer checks and tests.  
3. **Provider single-sub assumption** — largest eng risk; leaf D is mandatory before promising create-instance on BYO.  
4. **Preflight identity-in-sub assumption** — Lighthouse breaks naive `identity list` in customer sub.  
5. **Registry cross-tenant AcrPull** — easy to under-specify; blocks first instance.  
6. **Billing confusion** — Azure infra bill ≠ Elsa Control fee; no Marketplace metering; avoid “included in Azure” copy.  
7. **Stripe starvation** — mis-sequencing eng onto BYO provisioner before #384 cash path.  
8. **Security** — over-broad Lighthouse grants; unbind must stop new mutate quickly.

---

### AC8 attestation

This leaf produced **written evidence only** (plus optional docs spike file). No Marketplace work, no #103, no changes under `src/Billing/ElsaControl.Billing.Stripe/`, no Stripe secrets, no #384 checkout wiring.

---

### Definition of done checklist

- [x] Feasibility note on issue (this comment) covering AC1–AC9 / P1–P5  
- [x] Next leaves with sizes  
- [x] Risks called out  
- [x] Design-only Pass (no live PoC)  
- [ ] Launch Ops honesty skim (requested below)

Refs #429 #384 #406 #103 #108 #127
