# Elsa Control Azure Lighthouse offer v1

This subscription-scoped Bicep template is the versioned **direct ARM
delegation** for guided Elsa Control BYO Azure onboarding. A customer
subscription owner deploys it to the customer subscription so a principal in
the Valence managing tenant can operate Azure Resource Manager resources there.
It is a concierge/design-partner artifact for Preview. It is not a Marketplace
listing, a self-serve onboarding button, or a Logic Apps replacement.

The artifact grants the same principal(s):

| Authorization | Built-in role ID | Scope | Boundary |
| --- | --- | --- | --- |
| Subscription mutation | Contributor — `b24988ac-6180-42a0-ab88-20f7382dd24c` | Customer subscription | ARM control-plane operations permitted by Contributor |
| Managed-identity role assignment | User Access Administrator — `18d7d88d-d35e-4fb5-a5c3-7773c20a72d9` | Customer subscription | Azure Lighthouse's limited UAA behavior, constrained to the delegated IDs below |

The UAA authorization explicitly delegates only the role the BYO workload
identity can receive in the customer tenant:

- Key Vault Secrets User — `4633458b-17de-408a-b874-0445c86b69e6`

These delegated IDs permit later ARM role-assignment templates to grant the
role to a customer-tenant managed identity. They do not grant the managing
principal Key Vault secret data-plane access through Lighthouse. The current
provider also assigns Key Vault Secrets Officer
(`b86a8fe4-44ce-4948-aee5-eccb2c155cd7`) to its bootstrap/provisioner identity;
that identity is in the managing tenant for BYO and this cross-tenant
data-plane/bootstrap path remains a downstream blocker.

The v1 resource names are fixed reviewed constants so a bind verifier can
reproduce the expected ARM IDs without accepting an ID from the browser:

| Resource | v1 name/ID suffix |
| --- | --- |
| Registration definition | `9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9` |
| Registration assignment | `50f0f9d1-8c11-47a3-8af5-9a87fa5c7af9` |

The full IDs are the target subscription resource paths. The same v1 names
are used once per customer subscription; a future role or semantic change
requires a new artifact version and new reviewed constants.

## Why this is not Contributor + RBAC Administrator in the template

`AzureProviderAuthorityPreflight` accepts Contributor (`b24988ac-6180-42a0-ab88-20f7382dd24c`)
or Owner (`8e3af657-a8ff-443c-a75c-2fe8c4bcb635`) as the mutation role, and
Owner, User Access Administrator, or Role Based Access Control Administrator
(`f58310d9-a9f6-439a-9e8d-f62e7b41a168`) as the role-assignment role.

Azure Lighthouse has a narrower contract than that local preflight:

- Owner is not a supported Lighthouse authorization.
- User Access Administrator is supported only for assigning specified roles to
  managed identities, so the `delegatedRoleDefinitionIds` list is required.
- A role containing the broad `Microsoft.Authorization/*` write/delete
  actions is not a supported Lighthouse authorization. Therefore the
  Role Based Access Control Administrator ID is documented as a direct
  customer-side alternative for a future/non-Lighthouse path, not emitted by
  this v1 offer.

If a customer grants the managing principal permissions directly in its own
tenant/subscription rather than through Lighthouse, the preflight-compatible
role bar remains **Contributor plus one of RBAC Administrator, User Access
Administrator, or Owner**. Contributor alone is not sufficient for the
provider's role assignments. Do not add Owner or RBAC Administrator to this
Lighthouse template: Azure may reject that delegation.

## Deploy (customer owner, guided)

Use an exact subscription ID selected with the concierge. Do not rely on the
Azure CLI default subscription. Replace the placeholder IDs with the
managing-tenant values supplied by Valence; IDs are identifiers, not secrets.
Review the complete what-if before creating the deployment.

```sh
az deployment sub what-if \
  --subscription <customer-subscription-id> \
  --location westeurope \
  --name elsa-control-lighthouse-v1 \
  --template-file infra/azure-lighthouse/v1/main.bicep \
  --parameters managingTenantId=<managing-tenant-id> \
               managingPrincipalObjectIds='["<managing-principal-object-id>"]'

az deployment sub create \
  --subscription <customer-subscription-id> \
  --location westeurope \
  --name elsa-control-lighthouse-v1 \
  --template-file infra/azure-lighthouse/v1/main.bicep \
  --parameters managingTenantId=<managing-tenant-id> \
               managingPrincipalObjectIds='["<managing-principal-object-id>"]'
```

For multiple principals, provide a JSON array of object IDs. Prefer a
security group or service principal/managed identity over individual users;
the object ID must come from the managing tenant. Keep the deployment output
and subscription ID in the private operational record, not in source control.

The customer remains the Azure subscription owner and Azure bill payer. **An
Azure bill is not an Elsa fee.** Any Elsa commercial entitlement, fee,
subscription state, or service agreement is a separate Control concern.

## What the deployment proves (and what it does not)

The registration definition and assignment prove that the delegation was
accepted at the customer subscription scope. They do not prove that the
current Elsa provider can complete a managed instance deployment.

Azure Lighthouse covers Azure Resource Manager control-plane requests. It does
not cover instance-specific data-plane requests such as Key Vault secret
operations. The current provider runner uses `az keyvault secret list` and
`az keyvault secret set`; the runner/targeting work must replace or otherwise
solve that cross-tenant data-plane boundary (for example with an approved ARM
secret-resource path) before end-to-end BYO create-instance readiness can be
claimed. A later preflight must also account for Lighthouse's delegated-role
observation behavior rather than treating ordinary customer-tenant CLI role
listing as proof.

The Valence ACR `AcrPull` role (`7f951dda-4ed3-4680-a7ca-43fe172d538d`) is a
separate concierge operation on the Valence-owned registry for a workload
managed identity. It is intentionally not one of this customer-subscription
offer's UAA delegated IDs.

`Active` for a future bind record means only that the approved ARM delegation
and its required verification have passed. It is not commercial `Ready`:
`azure-bound` entitlement work (#435), per-organization customer-subscription
targeting (#436), secret data-plane handling, and the applicable operational
gates remain separate. Preview is not Dedicated, GA, or an SLO commitment.

## Versioning and removal

`v1` is part of the artifact path and deterministic registration identity.
Changes to the role set, delegated IDs, fixed resource names, or semantics
require a new version and an explicit re-review. A redeployment updates the
same v1 registration for the same managing tenant. Unbinding is a separate, explicit customer/operator
action; do not silently retarget retained assignments to another subscription.

The registration definition and assignment are the only resources in this
template. Removing this file does not revoke an existing delegation. Revoke or
replace the registration explicitly after confirming the bind state, retained
operations, and customer approval.

Sources: [Azure Lighthouse role support](https://learn.microsoft.com/en-us/azure/lighthouse/concepts/tenants-users-roles),
[registration definitions](https://learn.microsoft.com/en-us/azure/templates/microsoft.managedservices/registrationdefinitions),
[registration assignments](https://learn.microsoft.com/en-us/azure/templates/microsoft.managedservices/registrationassignments),
and [subscription-scoped Bicep deployments](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-to-subscription).

Local contract gate (requires Azure CLI with Bicep):

```sh
python3 scripts/tests/test_azure_lighthouse_offer.py -v
az bicep build --file infra/azure-lighthouse/v1/main.bicep --stdout >/dev/null
```
