// Subscription deployments and resource-group modules:
// https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-to-subscription
targetScope = 'subscription'

@description('Region for the anchor group and provisioner identity; no customer compute is created here.')
@allowed(['westeurope'])
param location string = 'westeurope'

@description('Explicit environment tag; production remains the default for existing deployments.')
@allowed(['production', 'staging'])
param environment string = 'production'

param anchorResourceGroupName string = 'rg-elsa-cloud-workloads-platform-prod-weu'
param provisionerIdentityName string = 'mi-elsa-cloud-provisioner-prod-weu'
param budgetName string = 'elsa-cloud-customer-workloads-monthly'

@description('Alert threshold in the subscription billing currency, not a spending limit.')
@minValue(1)
param monthlyBudgetAmount int = 100

@description('First day of the budget month, in UTC. Retain the existing start date on redeployment.')
param budgetStartDate string

@description('Explicit budget end date. Preserve the observed end date when adopting an existing budget.')
param budgetEndDate string

@secure()
@minLength(3)
@description('Existing operational billing contact. Its secure value is masked in deployment history and never emitted as an output; the parameter name remains visible.')
param budgetContactEmail string

var resourceTags = {
  product: 'elsa-cloud'
  component: 'customer-workload-bootstrap'
  environment: environment
  'managed-by': 'elsa-control'
}

resource anchor 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: anchorResourceGroupName
  location: location
  tags: resourceTags
}

module provisioner '../azure-production/modules/identity.bicep' = {
  name: 'elsa-cloud-provisioner-identity'
  scope: anchor
  params: {
    location: location
    name: provisionerIdentityName
    tags: resourceTags
  }
}

// Alerts never disable resources. No action group or shutdown automation is configured.
// https://learn.microsoft.com/en-us/azure/templates/microsoft.consumption/2024-08-01/budgets
resource budget 'Microsoft.Consumption/budgets@2024-08-01' = {
  name: budgetName
  properties: {
    amount: monthlyBudgetAmount
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
      endDate: budgetEndDate
    }
    notifications: {
      actual50: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        locale: 'en-gb'
      }
      actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        locale: 'en-gb'
      }
      actual100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [budgetContactEmail]
        locale: 'en-gb'
      }
      forecast100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [budgetContactEmail]
        locale: 'en-gb'
      }
    }
  }
}

output anchorResourceGroupId string = anchor.id
output provisionerIdentityId string = provisioner.outputs.id
output provisionerClientId string = provisioner.outputs.clientId
output provisionerPrincipalId string = provisioner.outputs.principalId
output budgetId string = budget.id
