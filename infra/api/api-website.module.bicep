@description('The location for the resource(s) to be deployed.')
param location string = resourceGroup().location

param elsa_control_outputs_azure_container_registry_endpoint string

param elsa_control_outputs_planid string

param elsa_control_outputs_azure_container_registry_managed_identity_id string

param elsa_control_outputs_azure_container_registry_managed_identity_client_id string

param api_containerimage string

param api_containerport string

@description('Vault URI from infra/api-key-vault. Secret app settings are versionless Key Vault references only. This module never takes a secret value.')
param api_key_vault_uri string

param control_sql_outputs_sqlserverfqdn string

param entratenantid_value string

param entraclientid_value string

@description('Optional Elsa Cloud Supabase OIDC issuer. An empty value keeps Cloud account JWT admission disabled. Set only after confirming this issuer publishes asymmetric signing keys.')
param cloudaccountissuer_value string = ''

param api_identity_outputs_id string

param api_identity_outputs_clientid string

@description('Optional full resource ID of the dedicated Azure provider provisioner identity. The identity must be in the same Microsoft Entra tenant as this app; it may be hosted in another subscription. Empty preserves the existing API and ACR identity set.')
param provisioner_identity_outputs_id string = ''

@description('Optional resource ID of the delegated App Service integration subnet from infra/control-egress that carries all API egress through one static NAT address. Empty keeps the platform outbound address pool.')
param api_egress_subnet_id string = ''

@description('Comma-separated organization GUIDs allowed to create or repair external-engine pairings. Empty refuses every organization. Staging only; production must stay empty.')
param pairingallowedorganizationids_value string = ''

var pairingAllowedOrganizationIds = empty(pairingallowedorganizationids_value)
  ? []
  : filter(map(split(pairingallowedorganizationids_value, ','), id => trim(id)), id => !empty(id))

var pairingAllowlistSettings = [for (organizationId, i) in pairingAllowedOrganizationIds: {
  name: 'ElsaControl__ExternalEngines__PairingAllowedOrganizationIds__${i}'
  value: organizationId
}]

@description('When true, emit Billing:StagingLifecycleLever:Enabled. Staging only; production must stay false.')
param stagingbillingleverenabled_value bool = false

@description('Comma-separated organization GUIDs the staging billing lifecycle lever may target. Empty refuses every organization. Staging only; production must stay empty.')
param stagingbillingleverallowedorganizationids_value string = ''

var stagingBillingLeverAllowedOrganizationIds = empty(stagingbillingleverallowedorganizationids_value)
  ? []
  : filter(map(split(stagingbillingleverallowedorganizationids_value, ','), id => trim(id)), id => !empty(id))

var stagingBillingLeverEnabledSettings = stagingbillingleverenabled_value ? [
  {
    name: 'Billing__StagingLifecycleLever__Enabled'
    value: 'true'
  }
] : []

var stagingBillingLeverAllowlistSettings = [for (organizationId, i) in stagingBillingLeverAllowedOrganizationIds: {
  name: 'Billing__StagingLifecycleLever__AllowedOrganizationIds__${i}'
  value: organizationId
}]

var stagingBillingLeverSettings = concat(stagingBillingLeverEnabledSettings, stagingBillingLeverAllowlistSettings)

@description('When true, emit Staging:RecoveryLifecycleLever:Enabled. Staging only; production must stay false.')
param stagingrecoveryleverenabled_value bool = false

@description('Comma-separated instance GUIDs the staging recovery lifecycle lever may target. Empty refuses every instance. Staging only; production must stay empty.')
param stagingrecoveryleverallowedinstanceids_value string = ''

@description('Hosted smoke-owner instance GUID the staging recovery lever must refuse. Staging only; production must stay empty.')
param stagingsmokeownerinstanceid_value string = ''

var stagingRecoveryLeverAllowedInstanceIds = empty(stagingrecoveryleverallowedinstanceids_value)
  ? []
  : filter(map(split(stagingrecoveryleverallowedinstanceids_value, ','), id => trim(id)), id => !empty(id))

var stagingRecoveryLeverEnabledSettings = stagingrecoveryleverenabled_value ? [
  {
    name: 'Staging__RecoveryLifecycleLever__Enabled'
    value: 'true'
  }
] : []

var stagingRecoveryLeverSmokeOwnerSettings = empty(stagingsmokeownerinstanceid_value) ? [] : [
  {
    name: 'Staging__RecoveryLifecycleLever__SmokeOwnerInstanceId'
    value: stagingsmokeownerinstanceid_value
  }
]

var stagingRecoveryLeverAllowlistSettings = [for (instanceId, i) in stagingRecoveryLeverAllowedInstanceIds: {
  name: 'Staging__RecoveryLifecycleLever__AllowedInstanceIds__${i}'
  value: instanceId
}]

var stagingRecoveryLeverSettings = concat(stagingRecoveryLeverEnabledSettings, stagingRecoveryLeverSmokeOwnerSettings, stagingRecoveryLeverAllowlistSettings)

var vaultUriPrefix = endsWith(api_key_vault_uri, '/') ? api_key_vault_uri : '${api_key_vault_uri}/'
var kv = {
  authenticationApiKey: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Authentication-ApiKey)'
  authenticationControlIdentityClientSecret: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Authentication-ControlIdentity-ClientSecret)'
  authenticationBuilderClientApiKey: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Authentication-BuilderClientApiKey)'
  billingStripeSecretKey: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Billing-Stripe-SecretKey)'
  billingStripeWebhookSigningSecret: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Billing-Stripe-WebhookSigningSecret)'
  billingStripeWebhookSourcesControlSigningSecret: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Billing-Stripe-WebhookSources-control-SigningSecret)'
  billingStripeWebhookSourcesHubSigningSecret: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/Billing-Stripe-WebhookSources-hub-SigningSecret)'
  managedElsaHandoffActivePrivateKeyPem: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/ManagedElsa-Handoff-ActivePrivateKeyPem)'
  rateLimitingBffCredentialsCloudSecret: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/RateLimiting-BffCredentials-cloud-Secret)'
  rateLimitingBffCredentialsHubSecret: '@Microsoft.KeyVault(SecretUri=${vaultUriPrefix}secrets/RateLimiting-BffCredentials-hub-Secret)'
}

@description('Comma-separated BFF credential sources, each cloud or hub. Empty emits no RateLimiting:BffCredentials settings, so the section stays off. Each listed source gets its Name and a versionless Key Vault reference for its Secret. PreviousSecret is a rotation-only setting the operator sets.')
param ratelimitbffcredentialsources_value string = ''

var rateLimitBffCredentialSources = filter(map(split(ratelimitbffcredentialsources_value, ','), entry => toLower(trim(entry))), entry => !empty(entry))

var rateLimitBffCredentialEnabledSettings = empty(rateLimitBffCredentialSources) ? [] : [
  {
    name: 'RateLimiting__BffCredentials__Enabled'
    value: 'true'
  }
]

// Fixed indices (cloud 0, hub 1) keep each source's setting names stable for
// scripts/lib/key_vault_app_settings.py and scripts/check-key-vault-references.sh.
var rateLimitBffCredentialCloudSettings = contains(rateLimitBffCredentialSources, 'cloud') ? [
  {
    name: 'RateLimiting__BffCredentials__Sources__0__Name'
    value: 'cloud'
  }
  {
    name: 'RateLimiting__BffCredentials__Sources__0__Secret'
    value: kv.rateLimitingBffCredentialsCloudSecret
  }
] : []

var rateLimitBffCredentialHubSettings = contains(rateLimitBffCredentialSources, 'hub') ? [
  {
    name: 'RateLimiting__BffCredentials__Sources__1__Name'
    value: 'hub'
  }
  {
    name: 'RateLimiting__BffCredentials__Sources__1__Secret'
    value: kv.rateLimitingBffCredentialsHubSecret
  }
] : []

var rateLimitBffCredentialSettings = concat(rateLimitBffCredentialEnabledSettings, rateLimitBffCredentialCloudSettings, rateLimitBffCredentialHubSettings)

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
    httpsOnly: true
    serverFarmId: elsa_control_outputs_planid
    keyVaultReferenceIdentity: api_identity_outputs_id
    // Regional VNet integration for one static egress (#310); empty keeps the platform pool.
    virtualNetworkSubnetId: empty(api_egress_subnet_id) ? null : api_egress_subnet_id
    siteConfig: {
      numberOfWorkers: 1
      vnetRouteAllEnabled: !empty(api_egress_subnet_id)
      linuxFxVersion: 'SITECONTAINERS'
      acrUseManagedIdentityCreds: true
      acrUserManagedIdentityID: elsa_control_outputs_azure_container_registry_managed_identity_client_id
      appSettings: concat(
        [
        {
          name: 'WEBSITES_PORT'
          value: api_containerport
        }
        {
          name: 'OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY'
          value: 'in_memory'
        }
        {
          name: 'HTTP_PORTS'
          value: api_containerport
        }
        {
          name: 'Authentication__ApiKey'
          value: kv.authenticationApiKey
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
          value: kv.authenticationControlIdentityClientSecret
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
          name: 'Authentication__CloudAccount__Enabled'
          value: empty(cloudaccountissuer_value) ? 'false' : 'true'
        }
        {
          name: 'Authentication__CloudAccount__Issuer'
          value: cloudaccountissuer_value
        }
        {
          name: 'Authentication__CloudAccount__Audience'
          value: 'authenticated'
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
          value: 'false'
        }
        {
          name: 'Authentication__BuilderClientApiKey'
          value: kv.authenticationBuilderClientApiKey
        }
        {
          name: 'Billing__Stripe__SecretKey'
          value: kv.billingStripeSecretKey
        }
        {
          name: 'Billing__Stripe__WebhookSigningSecret'
          value: kv.billingStripeWebhookSigningSecret
        }
        {
          name: 'Billing__Stripe__WebhookSources__0__Name'
          value: 'control'
        }
        {
          name: 'Billing__Stripe__WebhookSources__0__SigningSecret'
          value: kv.billingStripeWebhookSourcesControlSigningSecret
        }
        {
          name: 'Billing__Stripe__WebhookSources__1__Name'
          value: 'hub'
        }
        {
          name: 'Billing__Stripe__WebhookSources__1__SigningSecret'
          value: kv.billingStripeWebhookSourcesHubSigningSecret
        }
        {
          name: 'ManagedElsa__Handoff__ActivePrivateKeyPem'
          value: kv.managedElsaHandoffActivePrivateKeyPem
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
        ],
        pairingAllowlistSettings,
        stagingBillingLeverSettings,
        stagingRecoveryLeverSettings,
        rateLimitBffCredentialSettings)
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

resource scmBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: webapp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource ftpBasicAuth 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2025-03-01' = {
  parent: webapp
  name: 'ftp'
  properties: {
    allow: false
  }
}
