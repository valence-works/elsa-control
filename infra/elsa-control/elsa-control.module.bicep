@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param userPrincipalId string = ''

param tags object = { }

param elsa_control_acr_outputs_name string

resource elsa_control_mi 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('elsa_control_mi-${uniqueString(resourceGroup().id)}', 128)
  location: location
  tags: tags
}

resource elsa_control_acr 'Microsoft.ContainerRegistry/registries@2025-04-01' existing = {
  name: elsa_control_acr_outputs_name
}

resource elsa_control_acr_elsa_control_mi_AcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(elsa_control_acr.id, elsa_control_mi.id, subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d'))
  properties: {
    principalId: elsa_control_mi.properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
    principalType: 'ServicePrincipal'
  }
  scope: elsa_control_acr
}

resource elsa_control_asplan 'Microsoft.Web/serverfarms@2025-03-01' = {
  name: take('elsacontrolasplan-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    perSiteScaling: true
    reserved: true
  }
  kind: 'Linux'
  sku: {
    name: 'B2'
    tier: 'Basic'
    capacity: 1
  }
}

output name string = elsa_control_asplan.name

output planId string = elsa_control_asplan.id

output webSiteSuffix string = uniqueString(resourceGroup().id)

output AZURE_CONTAINER_REGISTRY_NAME string = elsa_control_acr.name

output AZURE_CONTAINER_REGISTRY_ENDPOINT string = elsa_control_acr.properties.loginServer

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = elsa_control_mi.id

output AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = elsa_control_mi.properties.clientId