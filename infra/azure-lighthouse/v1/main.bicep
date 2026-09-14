// Elsa Control guided BYO Azure Lighthouse offer, version 1.
//
// This is a direct subscription-scoped ARM delegation for guided design-partner
// onboarding. It is not an Azure Marketplace listing or a self-serve product.
targetScope = 'subscription'

@description('Microsoft Entra tenant ID that operates the delegated customer subscription.')
@minLength(36)
@maxLength(36)
param managingTenantId string

@description('Object ID of the single managing-tenant service principal, managed identity, or security group authorized by v1.')
@minLength(36)
@maxLength(36)
param managingPrincipalObjectId string

@description('Display name shown for each managing principal authorization.')
@minLength(1)
@maxLength(128)
param managingPrincipalDisplayName string = 'Elsa Control guided operator'

// These IDs intentionally mirror AzureProviderAuthorityPreflight and the
// provider's checked-in Key Vault role assignments.
// Contributor: b24988ac-6180-42a0-ab88-20f7382dd24c
// Owner (direct customer-side alternative only): 8e3af657-a8ff-443c-a75c-2fe8c4bcb635
// User Access Administrator: 18d7d88d-d35e-4fb5-a5c3-7773c20a72d9
// Role Based Access Control Administrator (direct customer-side alternative only):
// f58310d9-a9f6-439a-9e8d-f62e7b41a168
var contributorRoleDefinitionId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'
var userAccessAdministratorRoleDefinitionId = '18d7d88d-d35e-4fb5-a5c3-7773c20a72d9'

// UAA is limited by Azure Lighthouse to assigning this role to a managed
// identity in the customer tenant. The workload identity receives it in the
// current provider's Key Vault module. The bootstrap/provisioner identity is
// in the managing tenant, so its Secrets Officer grant is not delegated here.
// Key Vault Secrets User: 4633458b-17de-408a-b874-0445c86b69e6
var keyVaultSecretsUserRoleDefinitionId = '4633458b-17de-408a-b874-0445c86b69e6'

// Reviewed v1 resource names. Keep these constants stable so the bind verifier
// can reproduce the expected ARM IDs from the target subscription and version;
// never accept a browser-supplied registration definition ID.
var registrationDefinitionName = '9f8cf4c0-1f7a-4c7b-9c7b-e5f26a2d8bd9'
var registrationAssignmentName = '50f0f9d1-8c11-47a3-8af5-9a87fa5c7af9'
var delegatedManagedIdentityRoleDefinitionIds = [
  keyVaultSecretsUserRoleDefinitionId
]
var authorizations = [
  {
    principalId: managingPrincipalObjectId
    principalIdDisplayName: managingPrincipalDisplayName
    roleDefinitionId: contributorRoleDefinitionId
  }
  {
    principalId: managingPrincipalObjectId
    principalIdDisplayName: managingPrincipalDisplayName
    roleDefinitionId: userAccessAdministratorRoleDefinitionId
    delegatedRoleDefinitionIds: delegatedManagedIdentityRoleDefinitionIds
  }
]

resource registrationDefinition 'Microsoft.ManagedServices/registrationDefinitions@2022-10-01' = {
  name: registrationDefinitionName
  properties: {
    registrationDefinitionName: 'Elsa Control guided BYO v1'
    description: 'Guided design-partner delegation for Elsa Control Preview. Grants ARM Contributor and limited managed-identity role assignment authority; it is not a Marketplace offer.'
    managedByTenantId: managingTenantId
    authorizations: authorizations
  }
}

resource registrationAssignment 'Microsoft.ManagedServices/registrationAssignments@2022-10-01' = {
  name: registrationAssignmentName
  properties: {
    registrationDefinitionId: registrationDefinition.id
  }
}

output registrationDefinitionId string = registrationDefinition.id
output registrationAssignmentId string = registrationAssignment.id
output delegatedManagedIdentityRoleDefinitionIds array = delegatedManagedIdentityRoleDefinitionIds
