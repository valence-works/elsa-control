// Staging-only Pulumi provisioning proof (#1034). State backend, secrets key,
// and the three smallest data-plane roles for the existing provisioner UAMI.
// Secret values, account keys, SAS tokens, and connection strings never appear
// here. Production keeps enablePulumiProvisioning false.
targetScope = 'resourceGroup'

@description('Azure region for the state account. Follows the Control resource group.')
param location string = resourceGroup().location

@description('Principal ID (object ID) of the existing provisioner user-assigned identity. Role assignments use this, not the client ID.')
@minLength(36)
param provisionerPrincipalId string

param tags object = {}

@description('Storage Blob Data Contributor. Assigned on the pulumi-state container only.')
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

@description('Key Vault Crypto User. Assigned on the pulumi-secrets key only.')
var keyVaultCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'

@description('Key Vault Secrets User. Assigned on the existing Control API Key Vault only.')
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

var uniqueSuffix = uniqueString(resourceGroup().id)
var storageAccountName = take('stpulumi${uniqueSuffix}', 24)
var vaultName = take('ecapikv${uniqueSuffix}', 24)
var containerName = 'pulumi-state'
var keyName = 'pulumi-secrets'

resource stateAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    publicNetworkAccess: 'Enabled'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: stateAccount
  name: 'default'
  properties: {
    isVersioningEnabled: true
    deleteRetentionPolicy: {
      enabled: true
      days: 7
    }
  }
}

resource stateContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource pulumiSecretsKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: vault
  name: keyName
  properties: {
    kty: 'RSA'
    keySize: 2048
    keyOps: [
      'wrapKey'
      'unwrapKey'
      'encrypt'
      'decrypt'
    ]
    attributes: {
      enabled: true
    }
  }
}

resource stateBlobContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(stateContainer.id, provisionerPrincipalId, storageBlobDataContributorRoleId, 'pulumi-state')
  scope: stateContainer
  properties: {
    principalId: provisionerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
  }
}

resource secretsKeyCryptoUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pulumiSecretsKey.id, provisionerPrincipalId, keyVaultCryptoUserRoleId, 'pulumi-secrets')
  scope: pulumiSecretsKey
  properties: {
    principalId: provisionerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultCryptoUserRoleId)
  }
}

resource seedVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, provisionerPrincipalId, keyVaultSecretsUserRoleId, 'pulumi-seed')
  scope: vault
  properties: {
    principalId: provisionerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

output storageAccountName string = stateAccount.name
output containerName string = stateContainer.name
output stateBackendLocator string = '${stateAccount.name}/${containerName}'
output vaultName string = vault.name
output seedVaultUri string = vault.properties.vaultUri
output secretsProviderLocator string = 'azurekeyvault://${vaultName}.vault.azure.net/keys/${keyName}'
output keyName string = pulumiSecretsKey.name
