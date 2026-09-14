# Azure Lighthouse BYO concierge runbook (v1)

**Status:** guided design-partner Preview. This runbook accompanies the direct
ARM artifact at [`infra/azure-lighthouse/v1/main.bicep`](../../infra/azure-lighthouse/v1/main.bicep).
It is not a Marketplace/self-serve flow, and it does not announce Dedicated,
GA, SLO, or production-support commitments.

## Contract to say out loud

Use this wording with the partner:

> You will deploy a versioned Azure Lighthouse delegation to the exact Azure
> subscription you select. Elsa Control will operate Azure Resource Manager
> resources in that subscription through the roles you approve. This is a
> guided design-partner Preview, not an Azure Marketplace or Logic Apps
> replacement. You pay Azure for Azure infrastructure; an Azure bill is not an
> Elsa fee. A successful bind is not by itself commercial Ready or permission
> to create a managed instance.

Do not shorten the role explanation to “Contributor access.” The effective
Lighthouse v1 grant is:

- Contributor — `b24988ac-6180-42a0-ab88-20f7382dd24c`;
- User Access Administrator — `18d7d88d-d35e-4fb5-a5c3-7773c20a72d9`, with
  delegated managed-identity role ID Key Vault Secrets User
  (`4633458b-17de-408a-b874-0445c86b69e6`).

The local `AzureProviderAuthorityPreflight` role matrix also names Owner
(`8e3af657-a8ff-443c-a75c-2fe8c4bcb635`) and Role Based Access Control
Administrator (`f58310d9-a9f6-439a-9e8d-f62e7b41a168`). Those are acceptable
role-assignment alternatives only for a direct customer-side grant or a later
non-Lighthouse path. Owner is unsupported by Lighthouse, and RBAC Administrator
contains unsupported broad authorization actions; do not put either into the
v1 Lighthouse authorizations.

## Before sending the artifact

1. Confirm the partner's Entra tenant ID (`tid`), the exact target subscription
   ID, subscription display name, and an Owner contact. Do not infer any of
   these from the CLI default context.
2. Confirm the single managing-tenant principal object ID from the Valence private
   operational record. An object ID is not a client ID; no credential, secret,
   token, or private key belongs in the template or parameter file.
3. Record the artifact version (`azure-lighthouse/v1`) and reviewed template
   fingerprint/commit in the concierge record.
4. Explain the role bar and the data-plane boundary before the partner gives
   consent. The partner should understand that Key Vault secret list/set calls
   used by the current runner are not covered by Azure Lighthouse.

## Partner deployment

Send the partner the repository artifact or an approved immutable copy. Have
the partner run a what-if from the selected subscription:

```sh
az account show --subscription <customer-subscription-id> \
  --query '{id:id,name:name,tenantId:tenantId}'

az deployment sub what-if \
  --subscription <customer-subscription-id> \
  --location westeurope \
  --name elsa-control-lighthouse-v1 \
  --template-file infra/azure-lighthouse/v1/main.bicep \
  --parameters managingTenantId=<managing-tenant-id> \
               managingPrincipalObjectId=<managing-principal-object-id>
```

The expected what-if contains only one
`Microsoft.ManagedServices/registrationDefinitions` and one
`Microsoft.ManagedServices/registrationAssignments` resource at subscription
scope. It must not create an App Service, Container App, database, Key Vault,
role assignment resource, secret, or customer workload.

The v1 definition and assignment names are fixed reviewed constants:

- registration definition: `9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9`;
- registration assignment: `50f0f9d1-8c11-47a3-8af5-9a87fa5c7af9`.

The bind verifier derives the full subscription-scoped ARM IDs from these
constants. Do not accept a registration definition/assignment ID from the
browser as authority.

After the partner's Owner approves the what-if, the partner runs the matching
`az deployment sub create` command from the artifact README. Capture only the
deployment name, registration definition/assignment IDs, target subscription,
tenant, version, and timestamps.

## Valence verification and bind record

1. Refresh the managing-tenant session and confirm the delegated subscription
   is visible to the Valence operator principal. Inspect the registration
   definition and assignment through the Azure Lighthouse/ARM management
   surface; ordinary customer-tenant `az role assignment list` output is not
   sufficient evidence for a Lighthouse delegation.

   These read-only ARM observations can be captured without storing raw
   provider output:

   ```sh
   az rest --method get \
     --url "https://management.azure.com/subscriptions/<customer-subscription-id>/providers/Microsoft.ManagedServices/registrationDefinitions?api-version=2022-10-01" \
     --query "value[?properties.managedByTenantId=='<managing-tenant-id>'].{id:id,name:properties.registrationDefinitionName,tenant:properties.managedByTenantId,authorizations:properties.authorizations}"

   az rest --method get \
     --url "https://management.azure.com/subscriptions/<customer-subscription-id>/providers/Microsoft.ManagedServices/registrationAssignments?api-version=2022-10-01" \
     --query "value[].{id:id,definition:properties.registrationDefinitionId}"
   ```

   Confirm the definition ID, managing tenant, expected principal object IDs,
   Contributor authorization, limited UAA authorization, and the single
   subscription-level assignment. Do not use a role-list result that omits
   Lighthouse-projected assignments as a substitute.
2. Run the approved provider authority verification for this customer binding
   once the bind-aware verifier exists. Record only its stable result code,
   timestamp, subscription/tenant IDs, principal IDs, and artifact fingerprint.
   If verification fails, leave the bind non-Active and record
   `last_preflight_code`; do not simulate Ready.
3. The verification must demonstrate the required Contributor mutation role and
   the limited UAA/delegated managed-identity role path. Contributor alone is
   a failure. The current provider's bootstrap Key Vault Secrets Officer role
   (`b86a8fe4-44ce-4948-aee5-eccb2c155cd7`) targets the managing-tenant
   provisioner identity and remains a downstream cross-tenant data-plane
   blocker; it is not a v1 Lighthouse delegated ID.
4. Record the bind as `PendingConsent` before partner deployment,
   `Verifying` while observation is in progress, and `Active` only after the
   approved verification passes. Preserve `Degraded`/`Unbound` history and
   never silently retarget a historical assignment to a new subscription.

## Explicit capability gates

Even after an ARM delegation verifies, stop before claiming managed-instance
readiness:

- Lighthouse is an ARM control-plane delegation; it does not authorize
  instance-specific Key Vault data-plane operations. The current runner uses
  `az keyvault secret list`/`set`, so a later runner/targeting leaf must solve
  that boundary.
- The Valence ACR `AcrPull` grant (`7f951dda-4ed3-4680-a7ca-43fe172d538d`) is a
  separate concierge task on the Valence-owned registry.
- `azure-bound` entitlement mint (#435) and per-organization customer-sub
  targeting (#436) are prerequisites for any future create-instance claim.
- `Active` bind status alone is not commercial `Ready`. Preview is not
  Dedicated, GA, or an SLO. Azure infrastructure billing remains separate
  from any Elsa fee.

If any gate is missing, tell the partner: “The delegation is recorded, but
managed provisioning is not enabled for this subscription yet.” Do not offer a
Marketplace link or a one-click deployment claim.

## Unbind / relink

Unbind is an explicit operation with an operator/customer reason. Mark the
current bind `Unbound`, stop new mutate/create actions for that bind, and retain
its registration fingerprint and audit record. A relink requires a new
subscription ID and a new v1 deployment/verification after the old bind is
Unbound. Never edit a retained operation or assignment to point at the new
subscription.

## Evidence checklist

The concierge record should contain:

- partner organization and Entra tenant ID;
- exact customer subscription ID/display name and Owner contact;
- managing principal object ID, artifact version, and template fingerprint;
- what-if reviewed by and timestamp;
- registration definition/assignment IDs and deployment timestamp;
- verification code and timestamp, or the stable failure code;
- separate ACR pull and data-plane/targeting blockers;
- bind state and explicit next action.

Do not store credentials, access tokens, secret values, Key Vault payloads, or
raw provider command output in the runbook record.
