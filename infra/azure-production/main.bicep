targetScope = 'resourceGroup'

@description('Short, unique name for the managed workload. Use lowercase letters, numbers and hyphens only.')
@minLength(3)
@maxLength(16)
param workloadName string

@description('The production provider is intentionally constrained to governed Azure regions.')
@allowed([
  'westeurope'
  'northeurope'
  'swedencentral'
])
param location string = 'westeurope'

@description('Immutable runtime image repository, without a tag or digest.')
@minLength(1)
param imageRepository string = 'valenceruntimeimages.azurecr.io/runtime-combined'

@description('Lower-case SHA-256 digest for the image, without the sha256: prefix. Tags are not accepted.')
@minLength(64)
@maxLength(64)
param imageDigest string

@description('Existing runtime ACR resource name.')
@minLength(5)
@maxLength(50)
param registryName string = 'valenceruntimeimages'

@description('Subscription ID containing the existing runtime ACR.')
param registrySubscriptionId string = subscription().subscriptionId

@description('Resource group containing the existing runtime ACR.')
@minLength(1)
param registryResourceGroupName string

@description('Microsoft Entra object ID for the SQL administrator.')
param sqlBootstrapObjectId string

@description('Microsoft Entra login/display name for the SQL administrator.')
@minLength(1)
@maxLength(128)
param sqlBootstrapLogin string

@description('Name of the SQL connection secret in the workload Key Vault.')
@minLength(1)
@maxLength(127)
param sqlConnectionSecretName string = 'sql-connection'

@description('Name of the Elsa identity signing secret in the workload Key Vault.')
@minLength(1)
@maxLength(127)
param signingKeySecretName string = 'identity-signing-key'

@description('Name of the runtime administrator credential in Key Vault.')
@minLength(1)
@maxLength(127)
param adminPasswordSecretName string = 'admin-password'

@description('Runtime administrator username supplied by the release/workspace owner.')
@minLength(1)
@maxLength(128)
param adminUsername string

@description('Elsa runtime version represented by the immutable image. Version is data, not an IaC branch.')
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

@description('Optional release version retained as deployment metadata. Empty reuses elsaVersion.')
param releaseVersion string = ''

@description('Nuplane service index for the release package feed. Override for a producer-owned feed.')
param releaseFeedServiceIndex string = 'https://api.nuget.org/v3/index.json'

@description('Name of the Nuplane release package feed.')
@minLength(1)
@maxLength(63)
param releaseFeedName string = 'release'

@description('Owner tag for the managed workload.')
@minLength(1)
param owner string = 'elsa-control'

@description('Create the externally reachable Container App. Set false for the foundation phase while the runbook seeds Key Vault secrets.')
param deployWorkload bool = true

// Workload capacity comes from the resolved plan and has no defaults: a caller that omits it
// fails the deployment instead of silently scaling to zero. Values are the exact Container Apps
// consumption representation; workloadMemory must be the consumption pair of workloadCpu.
@description('Minimum workload replicas from the resolved plan capacity. Zero permits scale-to-zero only when the plan asks for it.')
@minValue(0)
@maxValue(300)
param workloadMinReplicas int

@description('Maximum workload replicas from the resolved plan capacity.')
@minValue(1)
@maxValue(300)
param workloadMaxReplicas int

@description('Container Apps consumption vCPU for the workload container, mapped exactly from the plan millicores.')
@allowed([
  '0.25'
  '0.5'
  '0.75'
  '1'
  '1.25'
  '1.5'
  '1.75'
  '2'
])
param workloadCpu string

@description('Container Apps consumption memory for the workload container, mapped exactly from the plan MiB.')
@allowed([
  '0.5Gi'
  '1Gi'
  '1.5Gi'
  '2Gi'
  '2.5Gi'
  '3Gi'
  '3.5Gi'
  '4Gi'
])
param workloadMemory string

// The runtime's managed Elsa handoff (release capability managed-elsa-handoff-v1). The provider runner enables
// it only for a release that declares the capability and supplies the instance identity and Control's own
// handoff configuration. The callback is never a caller input: it is derived below from the workload origin.
@description('Enable the runtime managed Elsa handoff. The runner sets it only when the admitted release declares managed-elsa-handoff-v1.')
param managedHandoffEnabled bool

@description('Lowercase canonical Elsa instance ID the handoff binds to. Required when the handoff is enabled.')
@maxLength(36)
param managedHandoffInstanceId string = ''

@description('Exact handoff audience of the instance (urn:elsa:instance:<id>). Required when the handoff is enabled.')
@maxLength(64)
param managedHandoffAudience string = ''

@description('Elsa Control origin that redeems handoff codes: absolute HTTPS without a path. Required when the handoff is enabled.')
@maxLength(2048)
param managedHandoffControlBaseUrl string = ''

@description('Elsa Control console route the runtime returns the browser to. Required when the handoff is enabled.')
@maxLength(2048)
param managedHandoffControlContinuationUrl string = ''

@description('Upper bound of a runtime session (hh:mm:ss), no longer than Control\'s runtime session maximum. Required when the handoff is enabled.')
@maxLength(16)
param managedHandoffRuntimeMaximumLifetime string = ''

@description('Runtime permissions granted to a handed-off Control operator. Required when the handoff is enabled.')
param managedHandoffRuntimePermissions array = []

@description('SHA-256 of the compiled main template. The runbook supplies this so IaC changes produce a new plan and revision identity.')
@minLength(64)
@maxLength(64)
param templateFingerprint string

@description('Runbook-selected Container Apps revision suffix. Empty uses the plan fingerprint for direct template consumers.')
@maxLength(63)
param workloadRevisionSuffix string = ''

@description('Existing healthy revision kept at 100% while a candidate warms. Empty is valid only for the first workload deployment.')
@maxLength(64)
param stableTrafficRevisionName string = ''

@description('Additional tags. Required owner, release and fingerprint tags always win.')
param additionalTags object = {}

var effectiveReleaseVersion = empty(releaseVersion) ? elsaVersion : releaseVersion
var managedHandoffInput = managedHandoffEnabled ? 'v1/${managedHandoffInstanceId}/${managedHandoffAudience}/${managedHandoffControlBaseUrl}/${managedHandoffControlContinuationUrl}/${managedHandoffRuntimeMaximumLifetime}/${join(managedHandoffRuntimePermissions, ',')}' : 'disabled'
var planInput = 'template=${toLower(templateFingerprint)}|name=${workloadName}|location=${location}|image=${imageRepository}@sha256:${toLower(imageDigest)}|elsa=${elsaVersion}|release-line=${releaseLine}|release-version=${effectiveReleaseVersion}|release-feed=${releaseFeedName}/${releaseFeedServiceIndex}|sql-workflow=${sqlWorkflowPackageVersion}|sql-quartz=${sqlQuartzPackageVersion}|topology=combined|capacity=${workloadMinReplicas}/${workloadMaxReplicas}/${workloadCpu}/${workloadMemory}|handoff=${managedHandoffInput}|acr=${registrySubscriptionId}/${registryResourceGroupName}/${registryName}|sql-bootstrap=${sqlBootstrapObjectId}/${sqlBootstrapLogin}|admin=${adminUsername}|secrets=${sqlConnectionSecretName}/${signingKeySecretName}/${adminPasswordSecretName}'
// Bicep 0.43 has no SHA-256 function. uniqueString is deterministic for the
// canonical input, including the externally computed compiled-template hash.
var planFingerprint = uniqueString(planInput)
var revisionSuffix = empty(workloadRevisionSuffix) ? take(planFingerprint, 24) : workloadRevisionSuffix
var requiredTags = {
  owner: owner
  'workload-name': workloadName
  'plan-fingerprint': planFingerprint
  'managed-by': 'elsa-control-bicep'
  'release-line': releaseLine
  'release-version': effectiveReleaseVersion
}
var tags = union(additionalTags, requiredTags)

module workloadIdentity 'modules/identity.bicep' = {
  name: 'workload-identity'
  params: {
    name: '${workloadName}-identity'
    location: location
    tags: tags
  }
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    name: '${workloadName}-logs'
    location: location
    tags: tags
  }
}

module database 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    serverName: '${workloadName}-sql'
    databaseName: 'Elsa'
    location: location
    bootstrapObjectId: sqlBootstrapObjectId
    bootstrapLogin: sqlBootstrapLogin
    tags: tags
  }
}

module vault 'modules/key-vault.bicep' = {
  name: 'key-vault'
  params: {
    name: '${workloadName}-kv'
    location: location
    workloadPrincipalId: workloadIdentity.outputs.principalId
    bootstrapObjectId: sqlBootstrapObjectId
    sqlConnectionSecretName: sqlConnectionSecretName
    signingKeySecretName: signingKeySecretName
    adminPasswordSecretName: adminPasswordSecretName
    tags: tags
  }
}

module containerEnvironment 'modules/container-apps-environment.bicep' = {
  name: 'container-apps-environment'
  params: {
    name: '${workloadName}-aca'
    location: location
    logAnalyticsWorkspaceName: observability.outputs.name
    tags: tags
  }
}

// Container Apps serves an external app at <app name>.<environment default domain>, the same origin the provider
// verifies as the workload endpoint and Control binds the instance's handoff callback to. The environment exists
// from the foundation phase, so the origin is known before the app is created.
var workloadAppName = '${workloadName}-app'
var managedHandoffCallbackUri = managedHandoffEnabled ? toLower('https://${workloadAppName}.${containerEnvironment.outputs.defaultDomain}/managed-elsa/handoff/callback') : ''

module workload 'modules/container-app.bicep' = if (deployWorkload) {
  name: 'container-app'
  params: {
    name: workloadAppName
    location: location
    managedEnvironmentId: containerEnvironment.outputs.id
    registryName: registryName
    registrySubscriptionId: registrySubscriptionId
    registryResourceGroupName: registryResourceGroupName
    imageRepository: imageRepository
    imageDigest: imageDigest
    workloadIdentityId: workloadIdentity.outputs.id
    sqlConnectionSecretUri: vault.outputs.sqlConnectionSecretUri
    signingKeySecretUri: vault.outputs.signingKeySecretUri
    adminPasswordSecretUri: vault.outputs.adminCredentialUri
    sqlRef: take(sqlConnectionSecretName, 63)
    signingRef: take(signingKeySecretName, 63)
    adminCredentialRef: take(adminPasswordSecretName, 63)
    adminUsername: adminUsername
    elsaVersion: elsaVersion
    releaseLine: releaseLine
    releaseVersion: effectiveReleaseVersion
    releaseFeedName: releaseFeedName
    releaseFeedServiceIndex: releaseFeedServiceIndex
    sqlWorkflowPackageVersion: sqlWorkflowPackageVersion
    sqlQuartzPackageVersion: sqlQuartzPackageVersion
    revisionSuffix: revisionSuffix
    stableTrafficRevisionName: stableTrafficRevisionName
    minReplicas: workloadMinReplicas
    maxReplicas: workloadMaxReplicas
    cpu: workloadCpu
    memory: workloadMemory
    managedHandoffEnabled: managedHandoffEnabled
    managedHandoffInstanceId: managedHandoffInstanceId
    managedHandoffAudience: managedHandoffAudience
    managedHandoffControlBaseUrl: managedHandoffControlBaseUrl
    managedHandoffControlContinuationUrl: managedHandoffControlContinuationUrl
    managedHandoffCallbackUri: managedHandoffCallbackUri
    managedHandoffRuntimeMaximumLifetime: managedHandoffRuntimeMaximumLifetime
    managedHandoffRuntimePermissions: managedHandoffRuntimePermissions
    tags: tags
  }
}

// Deliberately limited to identifiers, endpoint and fingerprint metadata. Secret values,
// shared keys, tokens and connection strings never cross the output boundary.
output resourceGroupName string = resourceGroup().name
output deploymentName string = take('elsa-${workloadName}-${take(planFingerprint, 12)}', 64)
output planFingerprint string = planFingerprint
output workloadIdentityId string = workloadIdentity.outputs.id
output workloadIdentityClientId string = workloadIdentity.outputs.clientId
output workloadIdentityPrincipalId string = workloadIdentity.outputs.principalId
output keyVaultId string = vault.outputs.id
output keyVaultUri string = vault.outputs.uri
output sqlServerId string = database.outputs.id
output sqlServerName string = database.outputs.name
output sqlDatabaseName string = database.outputs.databaseName
output sqlServerFqdn string = database.outputs.fullyQualifiedDomainName
output sqlShortTermRetentionDays int = database.outputs.shortTermRetentionDays
output containerAppsEnvironmentId string = containerEnvironment.outputs.id
output containerAppId string = deployWorkload ? workload!.outputs.id : ''
output containerAppEndpoint string = deployWorkload ? workload!.outputs.endpoint : ''
output managedHandoffCallbackUri string = deployWorkload ? managedHandoffCallbackUri : ''
output immutableImage string = '${imageRepository}@sha256:${toLower(imageDigest)}'
