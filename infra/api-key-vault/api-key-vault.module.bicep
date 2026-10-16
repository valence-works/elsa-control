// One Control API Key Vault per environment. RBAC authorization, soft delete,
// and purge protection. Network access is the vault firewall: the API egress
// subnet (Microsoft.KeyVault service endpoint) plus the operator write-path
// CIDRs. Trusted-services bypass is off. A private endpoint is #839.
// Secret values are written by an operator, never by this module or by CI.
targetScope = 'resourceGroup'

@description('Azure region for the vault. Follows the Control API resource group.')
param location string = resourceGroup().location

@description('Principal ID of the API user-assigned identity. Receives Key Vault Secrets User on this vault only.')
param apiIdentityPrincipalId string

@description('Principal ID of the deploy identity. Receives Key Vault Secrets User (read only) on this vault only. It never writes secret values.')
param deployIdentityPrincipalId string

@description('Resource ID of the API egress subnet from infra/control-egress. Required so Key Vault references resolve through the subnet Microsoft.KeyVault service endpoint.')
@minLength(1)
param apiEgressSubnetId string

@description('IPv4 CIDRs allowed to write secret values (operator write path). Management-plane updates can add a CIDR later if this starts empty.')
param operatorWriteIpCidrs array = []

@description('Optional IPv4 CIDRs for the API outbound NAT address. Empty omits those firewall rules.')
param apiOutboundIpCidrs array = []

param tags object = {}

var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

var vaultName = take('ecapikv${uniqueString(resourceGroup().id)}', 24)

var subnetRules = [
  {
    id: apiEgressSubnetId
    ignoreMissingVnetServiceEndpoint: false
  }
]

var ipRules = [for cidr in concat(operatorWriteIpCidrs, apiOutboundIpCidrs): {
  value: cidr
}]

@description('Stable secret names. This module does not create the secrets; an operator writes each value.')
var secretNames = {
  authenticationApiKey: 'Authentication-ApiKey'
  authenticationControlIdentityClientSecret: 'Authentication-ControlIdentity-ClientSecret'
  authenticationBuilderClientApiKey: 'Authentication-BuilderClientApiKey'
  billingStripeSecretKey: 'Billing-Stripe-SecretKey'
  billingStripeWebhookSigningSecret: 'Billing-Stripe-WebhookSigningSecret'
  billingStripeWebhookSourcesControlSigningSecret: 'Billing-Stripe-WebhookSources-control-SigningSecret'
  billingStripeWebhookSourcesHubSigningSecret: 'Billing-Stripe-WebhookSources-hub-SigningSecret'
  managedElsaHandoffActivePrivateKeyPem: 'ManagedElsa-Handoff-ActivePrivateKeyPem'
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: vaultName
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
      // Trusted-services bypass does not cover App Service Key Vault reference
      // resolution. The site reaches this vault through VNet integration and
      // the Microsoft.KeyVault service endpoint on snet-api-egress.
      bypass: 'None'
      defaultAction: 'Deny'
      virtualNetworkRules: subnetRules
      ipRules: ipRules
    }
  }
}

resource apiSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, apiIdentityPrincipalId, keyVaultSecretsUserRoleId, 'api')
  scope: vault
  properties: {
    principalId: apiIdentityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

resource deploySecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vault.id, deployIdentityPrincipalId, keyVaultSecretsUserRoleId, 'deploy')
  scope: vault
  properties: {
    principalId: deployIdentityPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
  }
}

output id string = vault.id
output name string = vault.name
output uri string = vault.properties.vaultUri
output secretNames object = secretNames
