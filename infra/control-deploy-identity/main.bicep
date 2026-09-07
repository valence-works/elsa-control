targetScope = 'resourceGroup'

// The GitHub Actions deploy identity for the Control API. Aspire originally created this identity next to
// its dashboard; the dashboard is retired (#302, #307) and this template is now the source of truth for
// the identity, its GitHub federated credential and its exact deploy roles. Deploying it against the
// live resource group must be a no-op: names, location, subject and role-assignment names are inputs.
// NEVER delete this identity: it is the production environment's AZURE_CLIENT_ID.

@description('Existing region of the identity; changing it would recreate the identity and its client id.')
param location string

@minLength(3)
@maxLength(128)
param identityName string

@description('Name of the GitHub OIDC federated credential on the identity.')
@minLength(3)
@maxLength(120)
param federatedCredentialName string = 'github-elsa-control-production'

@description('Exact GitHub OIDC subject the credential trusts, e.g. repo:<owner>/<repo>:environment:production (GitHub may emit owner@id/repo@id).')
@minLength(10)
param githubSubject string

@description('Control API web app that the deploy identity may update.')
param apiSiteName string

@description('Container registry the deploy identity pushes candidate images to.')
param registryName string

@description('Existing role-assignment names to adopt; defaults derive deterministic names for new environments.')
param readerRoleAssignmentName string = guid(resourceGroup().id, identityName, 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
param websiteContributorRoleAssignmentName string = guid(resourceGroup().id, apiSiteName, identityName, 'de139f84-1756-47ae-9be6-808fbbe84772')
param acrPushRoleAssignmentName string = guid(resourceGroup().id, registryName, identityName, '8311e382-0749-4cb8-b61a-304f252e45ec')

param tags object = {}

var readerRoleDefinitionId = 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
var websiteContributorRoleDefinitionId = 'de139f84-1756-47ae-9be6-808fbbe84772'
var acrPushRoleDefinitionId = '8311e382-0749-4cb8-b61a-304f252e45ec'
var githubIssuer = 'https://token.actions.githubusercontent.com'

resource apiSite 'Microsoft.Web/sites@2024-04-01' existing = {
  name: apiSiteName
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

resource deployIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: identityName
  location: location
  tags: tags
}

resource githubCredential 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2024-11-30' = {
  parent: deployIdentity
  name: federatedCredentialName
  properties: {
    issuer: githubIssuer
    subject: githubSubject
    audiences: [
      'api://AzureADTokenExchange'
    ]
  }
}

// Reader on the resource group: the workflow reads the web app, registry and deployment state.
resource readerAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: readerRoleAssignmentName
  properties: {
    principalId: deployIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', readerRoleDefinitionId)
    principalType: 'ServicePrincipal'
  }
}

// Website Contributor on the exact API site only: image promotion, app settings, restart.
resource websiteContributorAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: websiteContributorRoleAssignmentName
  scope: apiSite
  properties: {
    principalId: deployIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleDefinitionId)
    principalType: 'ServicePrincipal'
  }
}

// AcrPush on the exact registry only: candidate image publication.
resource acrPushAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: acrPushRoleAssignmentName
  scope: registry
  properties: {
    principalId: deployIdentity.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPushRoleDefinitionId)
    principalType: 'ServicePrincipal'
  }
}

output clientId string = deployIdentity.properties.clientId
output principalId string = deployIdentity.properties.principalId
