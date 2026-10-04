@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

@description('Azure SQL Catalog service objective (SKU name). Production default is S0.')
param sqlDatabaseSkuName string = 'S0'

@description('Azure SQL Catalog edition (SKU tier). Production default is Standard.')
param sqlDatabaseSkuTier string = 'Standard'

@description('Azure SQL Catalog SKU family. Leave empty for DTU objectives such as S0.')
param sqlDatabaseSkuFamily string = ''

@description('Azure SQL Catalog SKU capacity (vCores or DTUs).')
param sqlDatabaseSkuCapacity int = 10

@description('Azure SQL Catalog max size in bytes. Empty or 0 omits the property. Use a string so 250 GiB (268435456000) is not an ARM 32-bit int.')
param sqlDatabaseMaxSizeBytes string = '268435456000'

var serverlessSku = startsWith(sqlDatabaseSkuName, 'GP_S_')
var catalogMaxSizeBytes = json(empty(sqlDatabaseMaxSizeBytes) ? '0' : sqlDatabaseMaxSizeBytes)
var catalogSku = empty(sqlDatabaseSkuFamily) ? {
  name: sqlDatabaseSkuName
  tier: sqlDatabaseSkuTier
  capacity: sqlDatabaseSkuCapacity
} : {
  name: sqlDatabaseSkuName
  tier: sqlDatabaseSkuTier
  family: sqlDatabaseSkuFamily
  capacity: sqlDatabaseSkuCapacity
}
var catalogProperties = union(
  {
    zoneRedundant: false
    requestedBackupStorageRedundancy: 'Zone'
    useFreeLimit: false
  },
  catalogMaxSizeBytes > 0 ? {
    maxSizeBytes: catalogMaxSizeBytes
  } : {},
  serverlessSku ? {
    autoPauseDelay: 60
    minCapacity: json('0.5')
  } : {}
)

resource sqlServerAdminManagedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: take('control_sql-admin-${uniqueString(resourceGroup().id)}', 63)
  location: location
}

resource control_sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: take('controlsql-${uniqueString(resourceGroup().id)}', 63)
  location: location
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      login: sqlServerAdminManagedIdentity.name
      sid: sqlServerAdminManagedIdentity.properties.principalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    version: '12.0'
  }
  tags: {
    'aspire-resource-name': 'control-sql'
  }
}

resource sqlFirewallRule_AllowAllAzureIps 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  name: 'AllowAllAzureIps'
  properties: {
    endIpAddress: '0.0.0.0'
    startIpAddress: '0.0.0.0'
  }
  parent: control_sql
}

resource Catalog 'Microsoft.Sql/servers/databases@2023-08-01' = {
  name: 'Catalog'
  location: location
  properties: catalogProperties
  sku: catalogSku
  parent: control_sql
}

output sqlServerFqdn string = control_sql.properties.fullyQualifiedDomainName

output name string = control_sql.name

output id string = control_sql.id

output sqlServerAdminName string = control_sql.properties.administrators.login
