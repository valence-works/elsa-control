@description('Deterministic Container App name.')
@minLength(2)
@maxLength(32)
param name string

@description('Azure region for the app.')
param location string

@description('Container Apps environment resource ID.')
param managedEnvironmentId string

@description('Name of the existing runtime ACR.')
@minLength(5)
@maxLength(50)
param registryName string = 'valenceruntimeimages'

@description('Subscription ID containing the existing ACR.')
param registrySubscriptionId string = subscription().subscriptionId

@description('Resource group containing the existing ACR.')
@minLength(1)
param registryResourceGroupName string

@description('Immutable container repository, without a tag or digest.')
param imageRepository string

@description('Lower-case SHA-256 digest without the sha256: prefix.')
@minLength(64)
@maxLength(64)
param imageDigest string

@description('User-assigned identity resource ID used for ACR pull and Key Vault reads.')
param workloadIdentityId string

@description('Key Vault URI for the SQL connection secret.')
param sqlConnectionSecretUri string

@description('Key Vault URI for the Elsa identity signing secret.')
param signingKeySecretUri string

@description('Key Vault URI for the runtime administrator credential.')
param adminPasswordSecretUri string

@description('SQL Key Vault reference name used inside Container Apps.')
@minLength(1)
@maxLength(63)
param sqlRef string = 'sql-connection'

@description('Signing Key Vault reference name used inside Container Apps.')
@minLength(1)
@maxLength(63)
param signingRef string = 'identity-signing-key'

@description('Administrator credential Key Vault reference name used inside Container Apps.')
@minLength(1)
@maxLength(63)
param adminCredentialRef string = 'admin-password'

@description('Runtime administrator username supplied by the release/workspace owner.')
@minLength(1)
@maxLength(128)
param adminUsername string

@description('Elsa runtime version represented by the immutable image.')
@minLength(1)
param elsaVersion string

@description('Exact Nuplane feed version for SQL Server workflow/identity persistence packages.')
@minLength(1)
param sqlWorkflowPackageVersion string

@description('Exact Nuplane feed version for SQL Server scheduling package.')
@minLength(1)
param sqlQuartzPackageVersion string

@description('Release line carried by the immutable image and release manifest.')
@minLength(1)
param releaseLine string

@description('Release version carried by the immutable image and release manifest.')
@minLength(1)
param releaseVersion string

@description('Nuplane service index for the release package feed.')
param releaseFeedServiceIndex string

@description('Name of the Nuplane release package feed.')
@minLength(1)
@maxLength(63)
param releaseFeedName string

@description('Elsa topology represented by the immutable image.')
@allowed([
  'combined'
])
param topology string = 'combined'

@description('Deterministic revision suffix derived from the plan fingerprint.')
@minLength(8)
@maxLength(63)
param revisionSuffix string

@description('Existing healthy revision kept at 100% while the candidate revision warms. Empty uses latest revision for the first deployment.')
@maxLength(64)
param stableTrafficRevisionName string = ''

@description('Minimum replicas from the resolved plan capacity.')
param minReplicas int

@description('Maximum replicas from the resolved plan capacity.')
param maxReplicas int

@description('Consumption vCPU for the workload container.')
param cpu string

@description('Consumption memory for the workload container.')
param memory string

@description('Enable the runtime managed Elsa handoff (managed-elsa-handoff-v1).')
param managedHandoffEnabled bool

@description('Lowercase canonical Elsa instance ID the handoff is bound to.')
param managedHandoffInstanceId string = ''

@description('Exact handoff audience of the instance.')
param managedHandoffAudience string = ''

@description('Elsa Control origin that redeems handoff codes.')
param managedHandoffControlBaseUrl string = ''

@description('Elsa Control console route the runtime returns the browser to.')
param managedHandoffControlContinuationUrl string = ''

@description('Exact public handoff callback on this app\'s own origin.')
param managedHandoffCallbackUri string = ''

@description('Upper bound of a runtime session (hh:mm:ss).')
param managedHandoffRuntimeMaximumLifetime string = ''

@description('Runtime permissions granted to a handed-off Control operator.')
param managedHandoffRuntimePermissions array = []

@description('Tags applied to the app.')
param tags object = {}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
  scope: resourceGroup(registrySubscriptionId, registryResourceGroupName)
}
var immutableImage = '${imageRepository}@sha256:${toLower(imageDigest)}'
// Consumption accepts only these CPU/memory pairs. Indexing by the requested pair fails the
// deployment for any other combination rather than letting a default or rounding decide.
var consumptionResources = {
  '0.25/0.5Gi': {
    cpu: json('0.25')
    memory: '0.5Gi'
  }
  '0.5/1Gi': {
    cpu: json('0.5')
    memory: '1Gi'
  }
  '0.75/1.5Gi': {
    cpu: json('0.75')
    memory: '1.5Gi'
  }
  '1/2Gi': {
    cpu: json('1')
    memory: '2Gi'
  }
  '1.25/2.5Gi': {
    cpu: json('1.25')
    memory: '2.5Gi'
  }
  '1.5/3Gi': {
    cpu: json('1.5')
    memory: '3Gi'
  }
  '1.75/3.5Gi': {
    cpu: json('1.75')
    memory: '3.5Gi'
  }
  '2/4Gi': {
    cpu: json('2')
    memory: '4Gi'
  }
}
var nuplaneFeedEnvironment = [
  {
    name: 'Nuplane__Setup__Feeds__0__Name'
    value: 'local-packages'
  }
  {
    name: 'Nuplane__Setup__Feeds__0__DirectoryPath'
    value: 'packages'
  }
  {
    name: 'Nuplane__Setup__Feeds__0__IncludePatterns__0'
    value: '*'
  }
  {
    name: 'Nuplane__Setup__Feeds__0__Directory__Watch'
    value: 'true'
  }
  {
    name: 'Nuplane__Setup__Feeds__0__Directory__DebounceWindow'
    value: '00:00:01'
  }
  {
    name: 'Nuplane__Setup__Feeds__1__Name'
    value: 'nuget.org'
  }
  {
    name: 'Nuplane__Setup__Feeds__1__ServiceIndex'
    value: 'https://api.nuget.org/v3/index.json'
  }
  {
    name: 'Nuplane__Setup__Feeds__2__Name'
    value: releaseFeedName
  }
  {
    name: 'Nuplane__Setup__Feeds__2__ServiceIndex'
    value: releaseFeedServiceIndex
  }
  {
    name: 'Nuplane__Setup__Feeds__2__IncludePatterns__0'
    value: 'Elsa.Persistence.EFCore.SqlServer [${sqlWorkflowPackageVersion}]'
  }
  {
    name: 'Nuplane__Setup__Feeds__2__IncludePatterns__1'
    value: 'Elsa.Scheduling.Quartz.EFCore.SqlServer [${sqlQuartzPackageVersion}]'
  }
]
var featureEnvironment = [
  {
    name: 'CShells__Shells__Default__Features__SqliteWorkflowPersistence'
    value: 'false'
  }
  {
    name: 'CShells__Shells__Default__Features__SqliteIdentityPersistence'
    value: 'false'
  }
  {
    name: 'CShells__Shells__Default__Features__QuartzSqlite'
    value: 'false'
  }
  {
    name: 'CShells__Shells__Default__Features__SqlServerWorkflowPersistence__ConnectionString'
    secretRef: sqlRef
  }
  {
    name: 'CShells__Shells__Default__Features__SqlServerIdentityPersistence__ConnectionString'
    secretRef: sqlRef
  }
  {
    name: 'CShells__Shells__Default__Features__QuartzSqlServer__ConnectionString'
    secretRef: sqlRef
  }
  {
    name: 'CShells__Shells__Default__Features__Identity__SigningKey'
    secretRef: signingRef
  }
  {
    name: 'CShells__Shells__Default__Features__DefaultAdminUser__AdminUsername'
    value: adminUsername
  }
  {
    name: 'CShells__Shells__Default__Features__DefaultAdminUser__AdminPassword'
    secretRef: adminCredentialRef
  }
]
// The runtime maps its handoff endpoints only from a complete, valid set, and its startup validation
// rejects a partial one. It also keeps handoff state and sessions in process, so the handoff holds only on a
// single replica. Enabling it with a missing input or more replicas therefore fails the deployment here (no
// 'invalid' entry below) instead of producing a revision that cannot start or rejects its own callbacks. A
// disabled handoff is stated explicitly so an image default can never switch it on.
var managedHandoffInputsComplete = !empty(managedHandoffInstanceId) && !empty(managedHandoffAudience) && !empty(managedHandoffControlBaseUrl) && !empty(managedHandoffControlContinuationUrl) && !empty(managedHandoffCallbackUri) && !empty(managedHandoffRuntimeMaximumLifetime) && !empty(managedHandoffRuntimePermissions)
var managedHandoffMode = !managedHandoffEnabled ? 'disabled' : managedHandoffInputsComplete && maxReplicas == 1 ? 'enabled' : 'invalid'
var managedHandoffPermissionEnvironment = [for (permission, index) in managedHandoffRuntimePermissions: {
  name: 'ManagedElsa__Handoff__RuntimePermissions__${index}'
  value: permission
}]
var managedHandoffEnvironment = {
  disabled: [
    {
      name: 'ManagedElsa__Handoff__Enabled'
      value: 'false'
    }
  ]
  enabled: concat([
    {
      name: 'ManagedElsa__Handoff__Enabled'
      value: 'true'
    }
    {
      name: 'ManagedElsa__Handoff__ControlBaseUrl'
      value: managedHandoffControlBaseUrl
    }
    {
      name: 'ManagedElsa__Handoff__ControlContinuationUrl'
      value: managedHandoffControlContinuationUrl
    }
    {
      name: 'ManagedElsa__Handoff__InstanceId'
      value: managedHandoffInstanceId
    }
    {
      name: 'ManagedElsa__Handoff__Audience'
      value: managedHandoffAudience
    }
    {
      name: 'ManagedElsa__Handoff__CallbackUri'
      value: managedHandoffCallbackUri
    }
    {
      name: 'ManagedElsa__Handoff__UpstreamAuthenticationScheme'
      value: 'Jwt-or-ApiKey'
    }
    {
      name: 'ManagedElsa__Handoff__SuccessPath'
      value: '/'
    }
    {
      name: 'ManagedElsa__Handoff__StateLifetime'
      value: '00:05:00'
    }
    {
      name: 'ManagedElsa__Handoff__RuntimeMaximumLifetime'
      value: managedHandoffRuntimeMaximumLifetime
    }
    {
      // Container Apps ingress terminates TLS; the handoff cookies and host authentication must observe
      // the external HTTPS scheme. Ingress is the only route into this dedicated environment.
      name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
      value: 'true'
    }
  ], managedHandoffPermissionEnvironment)
}[managedHandoffMode]

resource app 'Microsoft.App/containerApps@2023-05-01' = {
  name: name
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${workloadIdentityId}': {}
    }
  }
  properties: {
    managedEnvironmentId: managedEnvironmentId
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Multiple'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        traffic: empty(stableTrafficRevisionName) ? [
            {
              latestRevision: true
              weight: 100
            }
          ] : [
            {
              revisionName: stableTrafficRevisionName
              weight: 100
            }
          ]
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: workloadIdentityId
        }
      ]
      secrets: [
        {
          name: sqlRef
          keyVaultUrl: sqlConnectionSecretUri
          identity: workloadIdentityId
        }
        {
          name: signingRef
          keyVaultUrl: signingKeySecretUri
          identity: workloadIdentityId
        }
        {
          name: adminCredentialRef
          keyVaultUrl: adminPasswordSecretUri
          identity: workloadIdentityId
        }
      ]
    }
    template: {
      revisionSuffix: revisionSuffix
      containers: [
        {
          name: topology
          image: immutableImage
          resources: consumptionResources['${cpu}/${memory}']
          env: concat([
            {
              name: 'ASPNETCORE_ENVIRONMENT'
              value: 'Production'
            }
            {
              name: 'ASPNETCORE_HTTP_PORTS'
              value: '8080'
            }
            {
              name: 'ELSA_VERSION'
              value: elsaVersion
            }
            {
              name: 'ELSA_RELEASE_LINE'
              value: releaseLine
            }
            {
              name: 'ELSA_RELEASE_VERSION'
              value: releaseVersion
            }
            {
              name: 'ELSA_TOPOLOGY'
              value: topology
            }
          ], concat(nuplaneFeedEnvironment, featureEnvironment, managedHandoffEnvironment))
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: '/alive'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 30
              timeoutSeconds: 5
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 10
              failureThreshold: 6
              timeoutSeconds: 5
            }
            {
              type: 'Liveness'
              httpGet: {
                path: '/alive'
                port: 8080
                scheme: 'HTTP'
              }
              initialDelaySeconds: 30
              periodSeconds: 30
              failureThreshold: 3
              timeoutSeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output id string = app.id
output name string = app.name
output fqdn string = app.properties.configuration.ingress.fqdn
output endpoint string = 'https://${app.properties.configuration.ingress.fqdn}'
output immutableImage string = immutableImage
