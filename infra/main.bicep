targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Name of the environment that can be used as part of naming resource convention, the name of the resource group for your application will use this name, prefixed with rg-')
param environmentName string

@minLength(1)
@description('The location used for all deployed resources')
param location string

@description('Id of the user or app to assign application roles')
param principalId string = ''

@secure()
param adminApiKey string
@secure()
param builderClientApiKey string
param entraClientId string
@secure()
param entraClientSecret string
param entraTenantId string

var tags = {
  'azd-env-name': environmentName
}

resource rg 'Microsoft.Resources/resourceGroups@2022-09-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module api_identity 'api-identity/api-identity.module.bicep' = {
  name: 'api-identity'
  scope: rg
  params: {
    location: location
  }
}
// NOTE: the Aspire-generated api-roles-control-sql module is removed by dev/regenerate-infra.sh.
// Its deployment script is broken upstream (SqlServer PowerShell 22.3.0 on Az PowerShell 14.0),
// so the API identity's contained SQL user is created out of band instead.
module control_sql 'control-sql/control-sql.module.bicep' = {
  name: 'control-sql'
  scope: rg
  params: {
    location: location
  }
}
module elsa_control 'elsa-control/elsa-control.module.bicep' = {
  name: 'elsa-control'
  scope: rg
  params: {
    elsa_control_acr_outputs_name: elsa_control_acr.outputs.name
    location: location
    userPrincipalId: principalId
  }
}
module elsa_control_acr 'elsa-control-acr/elsa-control-acr.module.bicep' = {
  name: 'elsa-control-acr'
  scope: rg
  params: {
    location: location
  }
}
output API_IDENTITY_CLIENTID string = api_identity.outputs.clientId
output API_IDENTITY_ID string = api_identity.outputs.id
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = elsa_control.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output CONTROL_SQL_SQLSERVERFQDN string = control_sql.outputs.sqlServerFqdn
output ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_ENDPOINT string = elsa_control.outputs.AZURE_CONTAINER_REGISTRY_ENDPOINT
output ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID string = elsa_control.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID
output ELSA_CONTROL_AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID string = elsa_control.outputs.AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID
output ELSA_CONTROL_PLANID string = elsa_control.outputs.planId
