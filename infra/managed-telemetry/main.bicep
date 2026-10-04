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

@description('Operator mailbox for RecoveryRequired entry alerts. The pipeline passes STAGING_RECOVERY_REQUIRED_ALERT_RECIPIENT when environment is staging and PRODUCTION_RECOVERY_REQUIRED_ALERT_RECIPIENT when environment is production. Never commit an address or reuse the production mailbox for staging.')
@minLength(3)
@maxLength(320)
param recoveryRequiredAlertEmail string

@description('Sink environment. Staging and production use separate action groups, rules, and event filters so Incremental deploys cannot overwrite each other.')
@allowed([
  'staging'
  'production'
])
param environment string

@description('Create the Monitoring Metrics Publisher assignment on the Insights component. Contributor cannot write role assignments. The deploy script never sets this true; a read-only preflight either skips the assignment or fails closed naming the exact human grant.')
param assignMonitoringMetricsPublisher bool = false

var recoveryRequiredEventName = 'managed_lifecycle.recovery_required.entered'
var recoveryRequiredActionGroupName = 'ag-recovery-required-${environment}'
var recoveryRequiredAlertRuleName = 'qr-recovery-required-entered-${environment}'
var recoveryRequiredActionGroupShortName = environment == 'production' ? 'rr-prod' : 'rr-staging'
// recovery-required-entered.kql is the source of truth. Tests load the same
// file. One email per RecoveryRequired entry. A resend of the same entry more
// than an hour after its first ingestion can email again.
var recoveryRequiredAlertQuery = replace(loadTextContent('recovery-required-entered.kql'), '{{environment}}', environment)

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
// Contributor excludes Microsoft.Authorization/*/write, so this resource is
// created only when a privileged human already granted the assignment and the
// deploy script's read-only preflight confirmed it. The script never requests
// roleAssignments/write or subscription-scope rights.
var MonitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'
resource publisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (assignMonitoringMetricsPublisher) {
  name: guid(applicationInsights.id, apiIdentity.id, MonitoringMetricsPublisherRoleId)
  scope: applicationInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', MonitoringMetricsPublisherRoleId)
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource recoveryRequiredActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: recoveryRequiredActionGroupName
  location: 'global'
  tags: tags
  properties: {
    groupShortName: recoveryRequiredActionGroupShortName
    enabled: true
    emailReceivers: [
      {
        name: 'recovery-required-operator'
        emailAddress: recoveryRequiredAlertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource recoveryRequiredAlertRule 'Microsoft.Insights/scheduledQueryRules@2023-03-15-preview' = {
  name: recoveryRequiredAlertRuleName
  location: location
  tags: tags
  properties: {
    displayName: 'RecoveryRequired entered (${environment})'
    description: 'Fires when Control writes ${recoveryRequiredEventName} for ${environment}. One email per RecoveryRequired entry. A resend of the same entry more than an hour after its first ingestion can email again. Staging and production use separate recipients, names, and environment filters.'
    severity: 1
    enabled: true
    evaluationFrequency: 'PT5M'
    windowSize: 'PT1H'
    autoMitigate: false
    scopes: [
      workspace.id
    ]
    targetResourceTypes: [
      'Microsoft.OperationalInsights/workspaces'
    ]
    criteria: {
      allOf: [
        {
          query: recoveryRequiredAlertQuery
          timeAggregation: 'Count'
          operator: 'GreaterThanOrEqual'
          threshold: 1
          dimensions: [
            {
              name: 'identity'
              operator: 'Include'
              values: [
                '*'
              ]
            }
          ]
          failingPeriods: {
            numberOfEvaluationPeriods: 1
            minFailingPeriodsToAlert: 1
          }
        }
      ]
    }
    actions: {
      actionGroups: [
        recoveryRequiredActionGroup.id
      ]
    }
  }
}

output applicationInsightsResourceId string = applicationInsights.id
output workspaceResourceId string = workspace.id
output publisherIdentityClientId string = apiIdentity.properties.clientId
