@description('Staging-only SQL logical server and at most three Standard S0 databases for valence-works/elsa-control#939. Do not deploy to production.')
targetScope = 'resourceGroup'

@description('Azure region. Must be the staging resource group region.')
param location string = resourceGroup().location

@description('SQL admin login. Operator-supplied; never a production credential.')
param administratorLogin string

@secure()
@description('SQL admin password. Operator-supplied; never commit.')
param administratorLoginPassword string

var tags = {
  purpose: 'shared-engine-spike'
  issue: '939'
}

resource server 'Microsoft.Sql/servers@2023-08-01' = {
  name: take('spike939sql-${uniqueString(resourceGroup().id)}', 63)
  location: location
  tags: tags
  properties: {
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource allowAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  name: 'AllowAllAzureIps'
  parent: server
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

var databaseNames = [
  'spike_939_tenant_a'
  'spike_939_tenant_b'
  'spike_939_fallback'
]

resource databases 'Microsoft.Sql/servers/databases@2023-08-01' = [for name in databaseNames: {
  name: name
  parent: server
  location: location
  tags: tags
  sku: {
    name: 'S0'
    tier: 'Standard'
    capacity: 10
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    zoneRedundant: false
  }
}]

output sqlServerId string = server.id
output sqlServerName string = server.name
output databaseIds array = [for (name, i) in databaseNames: databases[i].id]
output databaseNames array = databaseNames
