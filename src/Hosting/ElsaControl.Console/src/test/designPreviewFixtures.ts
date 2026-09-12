import type { ApplicationInfo } from "@/app/applicationApi";
import type { OrganizationWorkspaceContextResponse } from "@/app/workspaceContextModels";
import type { CustomerAuthSession } from "@/lib/auth/authModels";
import type { WorkspaceArtifact, WorkspaceArtifactListResponse } from "@/features/artifacts/artifactModels";
import type { CatalogPackage } from "@/features/packages/packageModels";
import type { PackageSource } from "@/features/sources/sourceModels";
import type {
  CreatedDeploymentApplication,
  CreatedDeploymentEnvironment,
  DeploymentCockpit,
  EnvironmentSummary,
  RegisterDeploymentEngineRequest,
  WorkflowEngineRegistration,
  WorkspaceDeploymentCredentialReference,
  WorkspaceDeploymentCredentialReferencesResponse,
  WorkspaceDeploymentPermissionsResponse,
  WorkspaceDeploymentSecretStore,
  WorkspaceDeploymentSecretStoresResponse,
  WorkspaceDeploymentTier,
  WorkspaceDeploymentTiersResponse
} from "@/features/deployments/deploymentModels";

const workspaceId = "workspace-preview";
const organizationId = "organization-preview";
const previewSecretStores: WorkspaceDeploymentSecretStore[] = [];

export const designPreviewFixtures = {
  authSession: {
    loginEnabled: true,
    authenticated: true,
    displayName: "Preview Operator",
    email: "preview@elsa-control.example",
    loginPath: "/admin/design-preview.html",
    logoutPath: "/admin/design-preview.html"
  } satisfies CustomerAuthSession,
  applicationInfo: {
    name: "Elsa Control",
    buildNumber: "design-preview"
  } satisfies ApplicationInfo,
  workspaceContext: {
    account: {
      id: "account-preview",
      displayName: "Preview Operator",
      email: "preview@elsa-control.example"
    },
    organizations: [{ id: organizationId, name: "Northstar Systems", role: "Administrator" }],
    workspaces: [{
      id: workspaceId,
      name: "Operations workspace",
      kind: "Organization",
      role: "Owner",
      organizationId,
      organizationName: "Northstar Systems",
      organizationRole: "Administrator"
    }]
  } satisfies OrganizationWorkspaceContextResponse,
  packages: [
    {
      packageId: "Elsa.Workflows.Core",
      approved: true,
      listed: true,
      latestVersion: "3.7.2",
      approvalStatus: "Approved",
      validationStatus: "Valid",
      featuresCount: 18,
      sourceId: "source-nuget",
      source: { id: "source-nuget", name: "Elsa NuGet feed", url: "https://api.nuget.org/v3/index.json", enabled: true, status: "Healthy" },
      versions: [{ version: "3.7.2", validationStatus: "Valid", approvalStatus: "Approved", isListed: true, suspiciousChangeDetected: false, schemaVersion: "1" }],
      createdAt: "2026-08-14T08:22:00Z",
      updatedAt: "2026-09-10T16:04:00Z"
    },
    {
      packageId: "Elsa.Workflows.Api",
      approved: false,
      listed: true,
      latestVersion: "3.7.2",
      approvalStatus: "Pending",
      validationStatus: "NotValidated",
      featuresCount: 7,
      sourceId: "source-nuget",
      source: { id: "source-nuget", name: "Elsa NuGet feed", url: "https://api.nuget.org/v3/index.json", enabled: true, status: "Healthy" },
      versions: [{ version: "3.7.2", validationStatus: "NotValidated", approvalStatus: "Pending", isListed: true, suspiciousChangeDetected: false, schemaVersion: "1" }],
      createdAt: "2026-08-14T08:22:00Z",
      updatedAt: "2026-09-12T06:40:00Z"
    },
    {
      packageId: "Elsa.Workflows.Http",
      approved: true,
      listed: true,
      latestVersion: "3.7.1",
      approvalStatus: "Approved",
      validationStatus: "Invalid",
      featuresCount: 11,
      sourceId: "source-nuget",
      source: { id: "source-nuget", name: "Elsa NuGet feed", url: "https://api.nuget.org/v3/index.json", enabled: true, status: "Healthy" },
      versions: [{ version: "3.7.1", validationStatus: "Invalid", approvalStatus: "Approved", isListed: true, suspiciousChangeDetected: false, schemaVersion: "1" }],
      createdAt: "2026-07-21T12:11:00Z",
      updatedAt: "2026-09-09T14:26:00Z"
    },
    {
      packageId: "Northstar.Extensions",
      approved: false,
      listed: false,
      latestVersion: "0.9.0-preview.3",
      approvalStatus: "Rejected",
      validationStatus: "Suspicious",
      featuresCount: 4,
      sourceId: "source-internal",
      source: { id: "source-internal", name: "Northstar internal feed", url: "https://packages.northstar.example/v3/index.json", enabled: true, status: "Warning" },
      versions: [{ version: "0.9.0-preview.3", validationStatus: "Suspicious", approvalStatus: "Rejected", isListed: false, suspiciousChangeDetected: true, schemaVersion: "1" }],
      createdAt: "2026-09-04T10:00:00Z",
      updatedAt: "2026-09-11T11:18:00Z"
    }
  ] satisfies CatalogPackage[],
  sources: [
    {
      id: "source-nuget",
      name: "Elsa NuGet feed",
      type: "NuGetFeed",
      url: "https://api.nuget.org/v3/index.json",
      enabled: true,
      includePatterns: ["Elsa."],
      excludePatterns: [],
      approvalPolicy: "Manual",
      versionDiscoveryPolicy: "LatestStable",
      status: "Healthy",
      isSyncing: false,
      lastSyncedAt: "2026-09-12T06:18:00Z",
      lastSuccessfulSyncAt: "2026-09-12T06:18:00Z",
      lastSyncError: null,
      packageCount: 148,
      pollingInterval: "PT30M",
      createdAt: "2026-06-10T09:00:00Z",
      updatedAt: "2026-09-12T06:18:00Z"
    },
    {
      id: "source-internal",
      name: "Northstar internal feed",
      type: "NuGetFeed",
      url: "https://packages.northstar.example/v3/index.json",
      enabled: true,
      includePatterns: ["Northstar."],
      excludePatterns: ["*.symbols"],
      approvalPolicy: "Manual",
      versionDiscoveryPolicy: "LatestIncludingPrerelease",
      status: "Warning",
      isSyncing: false,
      lastSyncedAt: "2026-09-11T20:04:00Z",
      lastSuccessfulSyncAt: "2026-09-11T20:04:00Z",
      lastSyncError: "One package manifest needs review.",
      packageCount: 23,
      pollingInterval: "PT1H",
      createdAt: "2026-07-02T12:30:00Z",
      updatedAt: "2026-09-11T20:04:00Z"
    }
  ] satisfies PackageSource[],
  artifacts: {
    items: [
      createArtifact({
        id: "artifact-checkout-372",
        artifactId: "checkout-service",
        name: "Checkout service",
        version: "3.7.2",
        environment: "production",
        digest: "54b9b2a1ef03",
        checksumStatus: "Verified",
        inspectionStatus: "Valid",
        diagnosticCount: 0
      }),
      createArtifact({
        id: "artifact-workflow-371",
        artifactId: "workflow-runtime",
        name: "Workflow runtime",
        version: "3.7.1",
        environment: "staging",
        digest: "f72a80d819c4",
        checksumStatus: "Verified",
        inspectionStatus: "Valid",
        diagnosticCount: 1
      }),
      createArtifact({
        id: "artifact-northstar-090",
        artifactId: "northstar-extensions",
        name: "Northstar extensions",
        version: "0.9.0-preview.3",
        environment: "development",
        digest: "8e91d43c10aa",
        checksumStatus: "Unverified",
        inspectionStatus: "NeverInspected",
        diagnosticCount: 2
      })
    ]
  } satisfies WorkspaceArtifactListResponse,
  permissions: {
    permissions: [
      "deployments.read",
      "deployments.setup.manage",
      "deployments.desired-state.manage",
      "deployments.promotion.preview",
      "deployments.run.execute",
      "deployments.rollback.execute",
      "deployments.controls.execute",
      "deployments.observability.manage"
    ]
  } satisfies WorkspaceDeploymentPermissionsResponse,
  tiers: {
    tiers: [
      createTier("tier-development", "Development", "Fast iteration with validation warnings allowed.", 0, ["deployment.promotion.source"]),
      createTier("tier-production", "Production", "Protected production environment with observability and rollback.", 1, ["deployment.promotion.target", "deployment.confirmation.required", "deployment.rollback.enabled", "deployment.production-like", "deployment.observability.required"])
    ]
  } satisfies WorkspaceDeploymentTiersResponse,
  credentials: {
    items: [
      createCredential("credential-prod", "Production API", "Verified", "Active", "azure-key-vault", "prod/api"),
      createCredential("credential-staging", "Staging API", "Unverified", "Active", "local-protected", "staging/api")
    ]
  } satisfies WorkspaceDeploymentCredentialReferencesResponse,
  secretStores: {
    items: previewSecretStores
  } satisfies WorkspaceDeploymentSecretStoresResponse,
  cockpit: createCockpit()
};

const previewSequences = {
  application: 0,
  environment: 0,
  engine: 0,
  secretStore: 0,
  credential: 0
};

export function installDesignPreviewFixtures() {
  if (!import.meta.env.DEV) {
    throw new Error("Design preview fixtures are available only in a development build.");
  }

  window.fetch = async (input, init) => {
    const request = new Request(input, init);
    const url = new URL(request.url, window.location.origin);
    const method = request.method.toUpperCase();
    const response = await previewResponse(method, url.pathname, request);
    if (!response) {
      // The preview must never quietly contact a real API. An uncovered endpoint is a fixture bug.
      throw new Error(`[Design preview] No sample response for ${method} ${url.pathname}${url.search}`);
    }
    return response;
  };
}

async function previewResponse(method: string, path: string, request: Request) {
  if (method === "GET" && path === "/api/auth/session") return jsonResponse(designPreviewFixtures.authSession);
  if (method === "GET" && path === "/api/me/organizations") return jsonResponse(designPreviewFixtures.workspaceContext);
  if (method === "GET" && path === "/api/admin/application") return jsonResponse(designPreviewFixtures.applicationInfo);
  if (method === "GET" && path === "/api/admin/packages") return jsonResponse(designPreviewFixtures.packages);
  if (method === "GET" && path === "/api/admin/sources") return jsonResponse(designPreviewFixtures.sources);
  if (method === "GET" && path.endsWith("/artifacts")) return jsonResponse(designPreviewFixtures.artifacts);
  if (method === "GET" && path.endsWith("/deployments/cockpit")) return jsonResponse(designPreviewFixtures.cockpit);
  if (method === "GET" && path.endsWith("/deployments/permissions")) return jsonResponse(designPreviewFixtures.permissions);
  if (method === "GET" && path.endsWith("/deployments/tiers")) return jsonResponse(designPreviewFixtures.tiers);
  if (method === "GET" && path.endsWith("/deployments/credential-references")) return jsonResponse(designPreviewFixtures.credentials);
  if (method === "GET" && path.endsWith("/deployments/tier-capabilities")) return jsonResponse({ capabilities: [] });
  if (method === "GET" && path.endsWith("/deployments/secret-stores")) return jsonResponse(designPreviewFixtures.secretStores);

  const engineRegistration = method === "POST" ? path.match(/\/deployments\/environments\/([^/]+)\/engines$/) : null;
  if (engineRegistration) {
    const body = await readJson<Partial<RegisterDeploymentEngineRequest>>(request);
    const environmentId = decodeURIComponent(engineRegistration[1]);
    const environment = findEnvironment(environmentId);
    if (!environment) return jsonResponse({ title: "Preview environment not found" }, 404);

    const template = designPreviewFixtures.cockpit.engines[0];
    const engine: WorkflowEngineRegistration = {
      ...template,
      id: nextPreviewId("engine"),
      name: safeText(body?.name, "Preview engine"),
      environmentId,
      endpoint: {
        ...template.endpoint,
        baseUrl: safeText(body?.baseUrl, template.endpoint.baseUrl),
        region: body?.region ?? "preview",
        version: "preview"
      },
      credentialReference: {
        ...template.credentialReference,
        provider: body?.credentialProvider ?? (body?.credentialReferenceId ? "Preview credential store" : "Deferred"),
        reference: body?.credentialReference ?? body?.credentialReferenceId ?? "Not assigned",
        verificationStatus: body?.credentialReferenceId ? "Unverified" : "NotVerifiable",
        lastVerifiedAt: null
      },
      credentialAssignmentStatus: body?.credentialAssignmentStatus ?? (body?.credentialReferenceId ? "Assigned" : "Deferred"),
      health: "Healthy",
      lastHeartbeatAt: "2026-09-12T06:45:00Z",
      lastVerificationAt: null,
      verificationMessage: "Sample registration accepted in memory; no engine was contacted.",
      capabilities: body?.capabilities ?? template.capabilities,
      controls: body?.controls ?? template.controls,
      hostingProvider: body?.hostingProvider ?? template.hostingProvider
    };
    designPreviewFixtures.cockpit.engines.push(engine);
    if (!environment.engineIds.includes(engine.id)) environment.engineIds.push(engine.id);
    return jsonResponse(engine);
  }
  if (method === "POST" && path.endsWith("/deployments/secret-stores")) {
    const body = await readJson<{ name?: string; provider?: string | null; type?: WorkspaceDeploymentSecretStore["type"]; description?: string | null }>(request);
    const now = "2026-09-12T06:45:00Z";
    const secretStore: WorkspaceDeploymentSecretStore = {
      id: nextPreviewId("secretStore"),
      workspaceId,
      name: safeText(body?.name, "Workspace encrypted credentials"),
      provider: body?.provider ?? "Local encrypted database",
      type: body?.type ?? "LocalEncryptedDatabase",
      description: body?.description ?? null,
      status: "Active",
      createdAt: now,
      updatedAt: now,
      createdByAccountId: "account-preview",
      updatedByAccountId: "account-preview",
      archivedAt: null,
      archivedByAccountId: null
    };
    designPreviewFixtures.secretStores.items.push(secretStore);
    return jsonResponse({
      ...secretStore
    });
  }
  if (method === "POST" && /\/deployments\/secret-stores\/[^/]+\/credential-references$/.test(path)) {
    const storeId = decodeURIComponent(path.match(/\/deployments\/secret-stores\/([^/]+)\/credential-references$/)?.[1] ?? "");
    const store = designPreviewFixtures.secretStores.items.find((item) => item.id === storeId);
    if (!store) return jsonResponse({ title: "Preview credential store not found" }, 404);
    const body = await readJson<{ name?: string; reference?: string; description?: string | null }>(request);
    const credential: WorkspaceDeploymentCredentialReference = {
      id: nextPreviewId("credential"),
      workspaceId,
      secretStoreId: store.id,
      secretStoreName: store.name,
      secretStoreProvider: store.provider,
      secretStoreType: store.type,
      name: safeText(body?.name, "Preview API key"),
      reference: safeText(body?.reference, "local://engine-credentials/preview-api-key"),
      description: body?.description ?? null,
      status: "Active",
      verificationStatus: "Unverified",
      lastVerifiedAt: null,
      createdAt: "2026-09-12T06:45:00Z",
      updatedAt: "2026-09-12T06:45:00Z",
      createdByAccountId: "account-preview",
      updatedByAccountId: "account-preview",
      archivedAt: null,
      archivedByAccountId: null,
      hasProtectedSecret: true,
      usageCount: 0
    };
    designPreviewFixtures.credentials.items.push(credential);
    return jsonResponse(credential);
  }
  if (method === "POST" && path.endsWith("/deployments/applications")) {
    const body = await readJson<{ name?: string }>(request);
    const application: CreatedDeploymentApplication = { id: nextPreviewId("application"), workspaceId, name: safeText(body?.name, "Preview application") };
    designPreviewFixtures.cockpit.applications.push({ ...application, workspaceName: "Operations workspace", environments: [] });
    return jsonResponse(application);
  }
  if (method === "POST" && /\/deployments\/applications\/[^/]+\/environments$/.test(path)) {
    const applicationId = decodeURIComponent(path.match(/\/deployments\/applications\/([^/]+)\/environments$/)?.[1] ?? "");
    const application = designPreviewFixtures.cockpit.applications.find((item) => item.id === applicationId);
    if (!application) return jsonResponse({ title: "Preview application not found" }, 404);
    const body = await readJson<{ name?: string; tier?: EnvironmentSummary["tier"]; tierId?: string | null }>(request);
    const environmentId = nextPreviewId("environment");
    const environment: EnvironmentSummary = {
      id: environmentId,
      name: safeText(body?.name, "Preview environment"),
      tier: body?.tier ?? "Dev",
      tierId: body?.tierId ?? null,
      tierName: body?.tierId ? designPreviewFixtures.tiers.tiers.find((tier) => tier.id === body.tierId)?.name : "Development",
      tierStatus: "Active",
      tierCapabilities: body?.tierId ? designPreviewFixtures.tiers.tiers.find((tier) => tier.id === body.tierId)?.capabilities ?? [] : [],
      health: "Healthy",
      desiredRevision: { id: `revision-${environmentId}`, revision: 0, commit: "preview", label: "No revision yet", authoredAt: "2026-09-12T06:45:00Z" },
      deployedRevision: null,
      deploymentStatus: "Succeeded",
      driftStatus: "Unknown",
      engineIds: []
    };
    application.environments.push(environment);
    const created: CreatedDeploymentEnvironment = { id: environment.id, workspaceId, applicationId, name: environment.name, tierId: environment.tierId ?? null };
    return jsonResponse(created);
  }
  if (method !== "GET") return jsonResponse({ title: "Preview limitation", detail: `The design preview does not simulate ${method} ${path}.` }, 501);

  return null;
}

function findEnvironment(environmentId: string) {
  return designPreviewFixtures.cockpit.applications.flatMap((application) => application.environments).find((environment) => environment.id === environmentId);
}

function nextPreviewId(kind: keyof typeof previewSequences) {
  previewSequences[kind] += 1;
  return `${kind}-preview-${String(previewSequences[kind]).padStart(2, "0")}`;
}

function safeText(value: string | null | undefined, fallback: string) {
  const normalized = value?.trim();
  return normalized || fallback;
}

function createArtifact({
  id,
  artifactId,
  name,
  version,
  environment,
  digest,
  checksumStatus,
  inspectionStatus,
  diagnosticCount
}: {
  id: string;
  artifactId: string;
  name: string;
  version: string;
  environment: string;
  digest: string;
  checksumStatus: WorkspaceArtifact["checksumStatus"];
  inspectionStatus: WorkspaceArtifact["inspectionStatus"];
  diagnosticCount: number;
}): WorkspaceArtifact {
  return {
    id,
    workspaceId,
    artifactId,
    layoutVersion: "1",
    contentDigest: { algorithm: "sha256", value: digest },
    format: "Zip",
    referenceProvider: "workspace-artifact-store",
    reference: `artifacts/${id}`,
    manifest: { name, version, environment },
    resources: [{ type: "Workflow", logicalId: `${artifactId}.definition`, scope: environment, version, desiredStateHash: { algorithm: "sha256", value: digest } }],
    checksumStatus,
    inspectionStatus,
    diagnostics: diagnosticCount === 0 ? [] : Array.from({ length: diagnosticCount }, (_, index) => ({ code: `ART-${index + 1}`, severity: index === 0 ? "Warning" as const : "Info" as const, message: index === 0 ? "Artifact has not been promoted to production." : "Producer metadata is incomplete." })),
    registeredAt: "2026-09-11T09:30:00Z",
    registeredByAccountId: "account-preview",
    lastInspectedAt: inspectionStatus === "NeverInspected" ? null : "2026-09-11T09:45:00Z",
    createdAt: "2026-09-11T09:30:00Z",
    updatedAt: "2026-09-12T06:20:00Z",
    envelopeVersion: "1",
    artifactTypeId: "elsa.workflow-bundle",
    artifactSchemaVersion: "1.0",
    manifestDigest: { algorithm: "sha256", value: digest },
    payloadReference: { provider: "workspace-artifact-store", uri: `artifacts/${id}/payload`, mediaType: "application/zip", sizeBytes: 184320 },
    producer: { producerType: "pipeline", producerName: "Northstar build", producerVersion: "2026.09.12", sourceReference: "build/1842" },
    displayMetadata: { name, version, description: `${name} deployment bundle`, labels: { environment }, annotations: {}, source: "Northstar CI" },
    compatibilityHints: [{ requiredArtifactType: "elsa.workflow-bundle", runtimeFamily: "Elsa", runtimeVersionRange: ">=3.7 <3.8", requiredCapabilities: ["workflow-runtime"], environmentConstraints: { environment } }],
    status: "Active",
    archivedAt: null,
    archivedByAccountId: null
  };
}

function createTier(id: string, name: string, description: string, sortOrder: number, capabilities: string[]): WorkspaceDeploymentTier {
  return { id, workspaceId, name, description, sortOrder, isDefault: sortOrder === 0, status: "Active", capabilities, environmentCount: sortOrder === 0 ? 1 : 1, createdAt: "2026-08-01T08:00:00Z", updatedAt: "2026-09-01T08:00:00Z", createdByAccountId: "account-preview", updatedByAccountId: "account-preview", archivedAt: null, archivedByAccountId: null };
}

function createCredential(id: string, name: string, verificationStatus: WorkspaceDeploymentCredentialReference["verificationStatus"], status: WorkspaceDeploymentCredentialReference["status"], secretStoreProvider: string, reference: string): WorkspaceDeploymentCredentialReference {
  return { id, workspaceId, secretStoreId: `store-${id}`, secretStoreName: secretStoreProvider === "azure-key-vault" ? "Production Key Vault" : "Local protected store", secretStoreProvider, secretStoreType: secretStoreProvider === "azure-key-vault" ? "AzureKeyVault" : "LocalEncryptedDatabase", name, reference, description: `${name} used by preview engine registrations.`, status, verificationStatus, lastVerifiedAt: verificationStatus === "Verified" ? "2026-09-12T05:54:00Z" : null, createdAt: "2026-08-10T10:00:00Z", updatedAt: "2026-09-12T05:54:00Z", createdByAccountId: "account-preview", updatedByAccountId: "account-preview", archivedAt: null, archivedByAccountId: null, hasProtectedSecret: true, usageCount: 1 };
}

function createEngine({ id, name, environmentId, health, credentialAssignmentStatus, verificationStatus, region, version, certificateStatus }: {
  id: string;
  name: string;
  environmentId: string;
  health: WorkflowEngineRegistration["health"];
  credentialAssignmentStatus: WorkflowEngineRegistration["credentialAssignmentStatus"];
  verificationStatus: WorkflowEngineRegistration["credentialReference"]["verificationStatus"];
  region: string;
  version: string;
  certificateStatus: WorkflowEngineRegistration["endpoint"]["certificateStatus"];
}): WorkflowEngineRegistration {
  return {
    id,
    name,
    environmentId,
    endpoint: { baseUrl: `https://${name.toLowerCase().replaceAll(" ", "-")}.northstar.example`, region, version, certificateStatus },
    credentialReference: { provider: credentialAssignmentStatus === "Assigned" ? "Azure Key Vault" : "Deferred", reference: credentialAssignmentStatus === "Assigned" ? "prod/api" : "Not assigned", verificationStatus, lastVerifiedAt: verificationStatus === "Verified" ? "2026-09-12T05:54:00Z" : null },
    credentialAssignmentStatus,
    health,
    lastHeartbeatAt: health === "Unreachable" ? null : "2026-09-12T06:34:00Z",
    lastVerificationAt: verificationStatus === "Verified" ? "2026-09-12T05:54:00Z" : null,
    verificationMessage: health === "Healthy" ? "Engine responded within the expected health window." : "Heartbeat is present, but certificate and credential state need review.",
    capabilities: [{ id: "workflow-runtime", label: "Workflow runtime", boundary: "Workflow" }, { id: "engine-api", label: "Engine API", boundary: "EngineApi" }],
    controls: [{ id: "runtime-restart", label: "Restart runtime", boundary: "Hosting", capabilityId: "engine-api", description: "Restart the managed workflow runtime." }],
    hostingProvider: "Northstar Kubernetes"
  };
}

function createCockpit(): DeploymentCockpit {
  const devRevision = { id: "revision-dev-18", revision: 18, commit: "a1c93d7", label: "Checkout 3.7.2", authoredAt: "2026-09-11T18:40:00Z" };
  const productionRevision = { id: "revision-prod-16", revision: 16, commit: "cf7f43e", label: "Checkout 3.7.1", authoredAt: "2026-09-10T11:20:00Z" };
  const applications = [
    {
      id: "app-checkout",
      name: "Checkout platform",
      workspaceName: "Operations workspace",
      environments: [
        { id: "env-northstar-dev", name: "Development", tier: "Dev" as const, tierId: "tier-development", tierName: "Development", tierStatus: "Active" as const, tierCapabilities: ["deployment.promotion.source"], health: "Healthy" as const, desiredRevision: devRevision, deployedRevision: 18, deploymentStatus: "Succeeded" as const, driftStatus: "InSync" as const, engineIds: ["engine-northstar-dev"] },
        { id: "env-northstar-prod", name: "Production", tier: "Production" as const, tierId: "tier-production", tierName: "Production", tierStatus: "Active" as const, tierCapabilities: ["deployment.promotion.target", "deployment.confirmation.required", "deployment.rollback.enabled", "deployment.production-like", "deployment.observability.required"], health: "Degraded" as const, desiredRevision: devRevision, deployedRevision: 16, deploymentStatus: "Blocked" as const, driftStatus: "DriftDetected" as const, engineIds: ["engine-northstar-prod"] }
      ]
    },
    {
      id: "app-automations",
      name: "Operations automations",
      workspaceName: "Operations workspace",
      environments: [{ id: "env-automations-stage", name: "Staging", tier: "Stage" as const, tierId: "tier-development", tierName: "Development", tierStatus: "Active" as const, tierCapabilities: ["deployment.promotion.source"], health: "Healthy" as const, desiredRevision: productionRevision, deployedRevision: 12, deploymentStatus: "Succeeded" as const, driftStatus: "InSync" as const, engineIds: ["engine-automations-stage"] }]
    }
  ];
  const engines = [
    createEngine({ id: "engine-northstar-dev", name: "checkout-dev-eu1", environmentId: "env-northstar-dev", health: "Healthy", credentialAssignmentStatus: "Assigned", verificationStatus: "Verified", region: "eu-west-1", version: "3.7.2", certificateStatus: "Trusted" }),
    createEngine({ id: "engine-northstar-prod", name: "checkout-prod-eu1", environmentId: "env-northstar-prod", health: "Degraded", credentialAssignmentStatus: "Deferred", verificationStatus: "Unverified", region: "eu-west-1", version: "3.7.1", certificateStatus: "Expiring" }),
    createEngine({ id: "engine-automations-stage", name: "automations-stage-us1", environmentId: "env-automations-stage", health: "Healthy", credentialAssignmentStatus: "Assigned", verificationStatus: "Verified", region: "us-east-1", version: "3.7.2", certificateStatus: "Trusted" })
  ];
  return {
    applications,
    engines,
    comparisons: [],
    observabilityBindings: [
      { id: "obs-logs", kind: "Logs", provider: "OpenTelemetry", status: "Connected", scope: "Checkout platform / Production", correlatedRevision: 16, sample: "logs.checkout-prod" },
      { id: "obs-traces", kind: "Traces", provider: "OpenTelemetry", status: "Degraded", scope: "Checkout platform / Production", correlatedRevision: 16, sample: "trace export delayed" }
    ],
    history: [{ id: "history-1842", status: "Succeeded", revision: 18, actor: "Preview Operator", environmentId: "env-northstar-dev", engineId: "engine-northstar-dev", validationOutcome: "Passed", occurredAt: "2026-09-12T05:55:00Z", rollbackSourceRevision: null }],
    driftReport: [{ id: "drift-prod", environmentId: "env-northstar-prod", engineId: "engine-northstar-prod", area: "Workflow definition", desired: "Checkout 3.7.2", observed: "Checkout 3.7.1", action: "Redeploy" }],
    assistantPlans: []
  };
}

function jsonResponse(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } });
}

async function readJson<T>(request: Request) {
  try {
    return (await request.json()) as T;
  } catch {
    return undefined;
  }
}
