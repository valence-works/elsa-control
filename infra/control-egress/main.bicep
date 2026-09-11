// One static egress address for the Control API (#310).
//
// The managed-instance provider runner opens a temporary Azure SQL firewall rule for exactly one
// IPv4 address (Deployment:AzureProvider:Runner:SqlBootstrapIp) while it bootstraps an instance's
// database. App Service egresses from a shared, non-static address pool, so the API is joined to
// a delegated subnet whose outbound traffic leaves through a NAT gateway with a single static
// public IP. Nothing here is customer compute; the customer workload subscription is untouched.
targetScope = 'resourceGroup'

@description('Region of the Control API site. Regional VNet integration requires the VNet in the same region as the app, so this follows the Control resource group rather than the customer workload profile.')
param location string = resourceGroup().location

@minLength(2)
@maxLength(64)
param virtualNetworkName string

@minLength(1)
@maxLength(80)
param subnetName string = 'snet-api-egress'

@minLength(1)
@maxLength(80)
param natGatewayName string

@minLength(1)
@maxLength(80)
param publicIpName string

@description('Private address space reserved for Control egress. Nothing inbound is exposed.')
param virtualNetworkAddressPrefix string = '10.60.0.0/24'

@description('Delegated App Service integration subnet; /26 leaves room for scale-out workers.')
param subnetAddressPrefix string = '10.60.0.0/26'

@minValue(4)
@maxValue(120)
param natIdleTimeoutMinutes int = 4

param tags object = {}

resource egressIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: publicIpName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Regional'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
    idleTimeoutInMinutes: natIdleTimeoutMinutes
  }
}

resource natGateway 'Microsoft.Network/natGateways@2024-05-01' = {
  name: natGatewayName
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    idleTimeoutInMinutes: natIdleTimeoutMinutes
    publicIpAddresses: [
      {
        id: egressIp.id
      }
    ]
  }
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: virtualNetworkName
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [
        virtualNetworkAddressPrefix
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          addressPrefix: subnetAddressPrefix
          natGateway: {
            id: natGateway.id
          }
          delegations: [
            {
              name: 'app-service'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
          privateEndpointNetworkPolicies: 'Enabled'
          privateLinkServiceNetworkPolicies: 'Enabled'
        }
      }
    ]
  }
}

@description('Subnet the API site joins; becomes AZURE_API_EGRESS_SUBNET_ID and the site virtualNetworkSubnetId.')
output integrationSubnetResourceId string = virtualNetwork.properties.subnets[0].id

@description('The single static egress address; becomes Deployment__AzureProvider__Runner__SqlBootstrapIp.')
output egressIpAddress string = egressIp.properties.ipAddress
