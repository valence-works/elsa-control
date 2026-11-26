// Staging-only Pulumi provisioning proof (#1034). State backend, dedicated seed
// vault, secrets key, and the three smallest data-plane roles for the existing
// provisioner UAMI. Secret values, account keys, SAS tokens, and connection
// strings never appear here. Production keeps enablePulumiProvisioning false.
targetScope = 'resourceGroup'

@description('Azure region for the state account and seed vault. Follows the Control resource group.')
param location string = resourceGroup().location

@description('Principal ID (object ID) of the existing provisioner user-assigned identity. Role assignments use this, not the client ID.')
@minLength(36)
param provisionerPrincipalId string

@description('API egress subnet. The seed vault firewall allows this subnet, matching the Control API Key Vault pattern.')
@minLength(1)
param apiEgressSubnetId string

@description('IPv4 CIDRs allowed to write seed-vault values (operator write path). Empty omits those firewall rules.')
param operatorWriteIpCidrs array = []

@description('Optional IPv4 CIDRs for the API outbound NAT address. Empty omits those firewall rules.')
param apiOutboundIpCidrs array = []

param tags object = {}

@description('Storage Blob Data Contributor. Assigned on the pulumi-state container only.')
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

@description('Key Vault Crypto User. Assigned on the pulumi-secrets key only.')
var keyVaultCryptoUserRoleId = '12338af0-0e69-4776-bea7-57ae8d297424'

@description('Key Vault Secrets User. Assigned on the dedicated seed vault only, never on the Control API Key Vault.')
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

var uniqueSuffix = uniqueString(resourceGroup().id)
var storageAccountName = take('stpulumi${uniqueSuffix}', 24)
var seedVaultName = take('ecseedkv${uniqueSuffix}', 24)
var containerName = 'pulumi-state'
var keyName = 'pulumi-secrets'

var subnetRules = [
  {
    id: apiEgressSubnetId
  }
]

var ipRules = [for cidr in concat(operatorWriteIpCidrs, apiOutboundIpCidrs): {
  value: cidr
}]

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

resource seedVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: seedVaultName
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      virtualNetworkRules: subnetRules
      ipRules: ipRules
    }
  }
}

resource pulumiSecretsKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' = {
  parent: seedVault
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
  name: guid(seedVault.id, provisionerPrincipalId, keyVaultSecretsUserRoleId, 'pulumi-seed')
  scope: seedVault
  properties: {
    principalId: provisionerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

output storageAccountName string = stateAccount.name
output containerName string = stateContainer.name
output stateBackendLocator string = '${stateAccount.name}/${containerName}'
output vaultName string = seedVault.name
output seedVaultUri string = seedVault.properties.vaultUri
output secretsProviderLocator string = 'azurekeyvault://${seedVaultName}.vault.azure.net/keys/${keyName}'
output keyName string = pulumiSecretsKey.name
