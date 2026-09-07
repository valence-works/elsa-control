@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param elsa_control_outputs_azure_container_registry_endpoint string

param elsa_control_outputs_planid string

param elsa_control_outputs_azure_container_registry_managed_identity_id string

param elsa_control_outputs_azure_container_registry_managed_identity_client_id string

param api_containerimage string

param api_containerport string

@secure()
param adminapikey_value string

param control_sql_outputs_sqlserverfqdn string

param entratenantid_value string

param entraclientid_value string

@secure()
param entraclientsecret_value string

@secure()
param builderclientapikey_value string

param api_identity_outputs_id string

param api_identity_outputs_clientid string

@description('Optional full resource ID of the dedicated Azure provider provisioner identity. The identity must be in the same Microsoft Entra tenant as this app; it may be hosted in another subscription. Empty preserves the existing API and ACR identity set.')
param provisioner_identity_outputs_id string = ''

resource mainContainer 'Microsoft.Web/sites/sitecontainers@2025-03-01' = {
  name: 'main'
  properties: {
    authType: 'UserAssigned'
    image: api_containerimage
    isMain: true
    targetPort: api_containerport
    userManagedIdentityClientId: elsa_control_outputs_azure_container_registry_managed_identity_client_id
  }
  parent: webapp
}

resource webapp 'Microsoft.Web/sites@2025-03-01' = {
  name: take('${toLower('api')}-${uniqueString(resourceGroup().id)}', 60)
  location: location
  properties: {
    serverFarmId: elsa_control_outputs_planid
    keyVaultReferenceIdentity: api_identity_outputs_id
    siteConfig: {
      numberOfWorkers: 1
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: elsa_control_outputs_azure_container_registry_managed_identity_client_id
      appSettings: [
        {
          name: 'WEBSITES_PORT'
          value: api_containerport
        }
        {
          name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
          value: 'in_memory'
        }
        {
          name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
          value: 'true'
        }
        {
          name: 'HTTP_PORTS'
          value: api_containerport
        }
        {
          name: 'Authentication__ApiKey'
          value: adminapikey_value
        }
        {
          name: 'ConnectionStrings__Catalog'
          value: 'Server=tcp:${control_sql_outputs_sqlserverfqdn},1433;Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Managed Identity;User Id=${api_identity_outputs_clientid};Database=Catalog'
        }
        {
          name: 'CATALOG_HOST'
          value: control_sql_outputs_sqlserverfqdn
        }
        {
          name: 'CATALOG_PORT'
          value: '1433'
        }
        {
          name: 'CATALOG_URI'
          value: 'mssql://${control_sql_outputs_sqlserverfqdn}:1433/Catalog'
        }
        {
          name: 'CATALOG_JDBCCONNECTIONSTRING'
          value: 'jdbc:sqlserver://${control_sql_outputs_sqlserverfqdn}:1433;database=Catalog;encrypt=true;trustServerCertificate=false'
        }
        {
          name: 'CATALOG_DATABASENAME'
          value: 'Catalog'
        }
        {
          name: 'Database__Provider'
          value: 'SqlServer'
        }
        {
          name: 'DataProtection__KeysPath'
          value: '/home/data-protection-keys'
        }
        {
          name: 'WEBSITES_ENABLE_APP_SERVICE_STORAGE'
          value: 'true'
        }
        {
          name: 'Authentication__ControlIdentity__Provider'
          value: 'MicrosoftEntra'
        }
        {
          name: 'Authentication__ControlIdentity__Authority'
          value: 'https://login.microsoftonline.com/${entratenantid_value}/v2.0'
        }
        {
          name: 'Authentication__ControlIdentity__Issuer'
          value: 'https://login.microsoftonline.com/${entratenantid_value}/v2.0'
        }
        {
          name: 'Authentication__ControlIdentity__Audience'
          value: entraclientid_value
        }
        {
          name: 'Authentication__ControlIdentity__ClientId'
          value: entraclientid_value
        }
        {
          name: 'Authentication__ControlIdentity__ClientSecret'
          value: entraclientsecret_value
        }
        {
          name: 'Authentication__ControlIdentity__RedirectUri'
          value: '/api/auth/callback'
        }
        {
          name: 'Authentication__ControlIdentity__PostLogoutRedirectUri'
          value: '/admin'
        }
        {
          name: 'Authentication__ControlIdentity__RequireHttpsMetadata'
          value: 'true'
        }
        {
          name: 'Authentication__ControlIdentity__Claims__DisplayName__0'
          value: 'name'
        }
        {
          name: 'Authentication__ControlIdentity__Claims__DisplayName__1'
          value: 'preferred_username'
        }
        {
          name: 'Authentication__ControlIdentity__Claims__Email__0'
          value: 'email'
        }
        {
          name: 'Authentication__ControlIdentity__Claims__Email__1'
          value: 'preferred_username'
        }
        {
          name: 'Authentication__Admin__AllowAuthenticatedCustomerSession'
          value: 'true'
        }
        {
          name: 'Authentication__BuilderClientApiKey'
          value: builderclientapikey_value
        }
        {
          name: 'Cors__BuilderClientOrigins__0'
          value: 'https://www.elsaworkflows.io'
        }
        {
          name: 'Cors__BuilderClientOrigins__1'
          value: 'https://elsaworkflows.io'
        }
        {
          name: 'AZURE_CLIENT_ID'
          value: api_identity_outputs_clientid
        }
        {
          name: 'AZURE_TOKEN_CREDENTIALS'
          value: 'ManagedIdentityCredential'
        }
        {
          name: 'ASPIRE_ENVIRONMENT_NAME'
          value: 'elsa-control'
        }
      ]
    }
  }
  identity: {
    type: 'UserAssigned'
    // A user-assigned identity is a standalone resource and App Service supports multiple
    // user-assigned identities. Keep the existing API/ACR identities as the default; the
    // optional provisioner identity is only attached when explicitly supplied by the host.
    // Same-tenant/cross-subscription use follows Microsoft's App Service managed-identity
    // contract: https://learn.microsoft.com/en-us/azure/app-service/overview-managed-identity
    userAssignedIdentities: union(
      {
        '${elsa_control_outputs_azure_container_registry_managed_identity_id}': { }
        '${api_identity_outputs_id}': { }
      },
      empty(provisioner_identity_outputs_id)
        ? { }
        : {
            '${provisioner_identity_outputs_id}': { }
          })
  }
}