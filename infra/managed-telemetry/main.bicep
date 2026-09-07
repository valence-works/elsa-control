targetScope = 'resourceGroup'

@description('Explicit reviewed Azure region for the Control telemetry sink.')
param location string

@minLength(4)
@maxLength(63)
param workspaceName string

@minLength(1)
@maxLength(255)
param applicationInsightsName string

@description('Existing Control API identity, not the customer workload provisioner. No identity is created or attached by this template.')
param apiIdentityName string

param apiIdentityResourceGroupName string

@description('Ingestion safety brake in GB/day, not a guaranteed billing cap or proof that a telemetry window is complete.')
@minValue(1)
@maxValue(5)
param dailyQuotaGb int = 1

param tags object = {}

resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: apiIdentityName
  scope: resourceGroup(apiIdentityResourceGroupName)
}

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
    features: {
      disableLocalAuth: true
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    workspaceCapping: {
      dailyQuotaGb: dailyQuotaGb
    }
    // Azure service endpoints remain Entra/RBAC protected. This is not an AMPLS
    // private-link deployment; there is no public dashboard (the Aspire dashboard is not provisioned).
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    DisableIpMasking: false
    RetentionInDays: 30
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

// The built-in role authorizes telemetry publication, not query or administration.
var MonitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'
resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, apiIdentity.id, MonitoringMetricsPublisherRoleId)
  scope: applicationInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', MonitoringMetricsPublisherRoleId)
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output applicationInsightsResourceId string = applicationInsights.id
output workspaceResourceId string = workspace.id
output publisherIdentityClientId string = apiIdentity.properties.clientId
