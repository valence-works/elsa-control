@description('Deterministic globally unique Azure SQL logical server name.')
@minLength(3)
@maxLength(63)
param serverName string

@description('Azure SQL database name.')
@minLength(1)
@maxLength(128)
param databaseName string

@description('Azure region for the server and database.')
param location string

@description('Microsoft Entra object ID for the governed SQL administrator.')
param bootstrapObjectId string

@description('Microsoft Entra login/display name for the governed SQL administrator.')
@minLength(1)
@maxLength(128)
param bootstrapLogin string

@description('Tags applied to SQL resources.')
param tags object = {}

@description('Create the managed database with the dedicated-lite default. False preserves the existing database SKU during reconciliation.')
param provisionDatabase bool = true

@description('Point-in-time restore retention for the managed workload database.')
@minValue(1)
@maxValue(35)
param shortTermRetentionDays int = 35

@description('Differential backup interval for the managed workload database.')
@allowed([
  12
  24
])
param differentialBackupIntervalHours int = 12

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: serverName
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      login: bootstrapLogin
      sid: bootstrapObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    version: '12.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01' = if (provisionDatabase) {
  parent: server
  name: databaseName
  location: location
  tags: tags
  sku: {
    name: 'S0'
    tier: 'Standard'
    capacity: 10
  }
  properties: {
    requestedBackupStorageRedundancy: 'Local'
    zoneRedundant: false
  }
}

resource existingDatabase 'Microsoft.Sql/servers/databases@2023-08-01' existing = if (!provisionDatabase) {
  parent: server
  name: databaseName
}

resource newDatabaseShortTermRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = if (provisionDatabase) {
  parent: database
  name: 'default'
  properties: {
    retentionDays: shortTermRetentionDays
    diffBackupIntervalInHours: differentialBackupIntervalHours
  }
}

resource existingDatabaseShortTermRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01' = if (!provisionDatabase) {
  parent: existingDatabase
  name: 'default'
  properties: {
    retentionDays: shortTermRetentionDays
    diffBackupIntervalInHours: differentialBackupIntervalHours
  }
}

// ACA has public egress in this no-VNet provider profile. The rule is removed
// with the workload resource group.
resource azureServicesFirewallRule 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: server
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output id string = server.id
output name string = server.name
output fullyQualifiedDomainName string = server.properties.fullyQualifiedDomainName
output databaseName string = provisionDatabase ? database!.name : existingDatabase!.name
output shortTermRetentionDays int = provisionDatabase
  ? newDatabaseShortTermRetention!.properties.retentionDays
  : existingDatabaseShortTermRetention!.properties.retentionDays
