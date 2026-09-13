import type { WorkspaceDeploymentRunStatus } from "@/features/deployments/deploymentModels";

export type ManagedElsaDesiredLifecycle = "Running" | "Stopped" | "Deleting";
export type ManagedElsaObservedLifecycle =
  | "Pending"
  | "Provisioning"
  | "Ready"
  | "Updating"
  | "Degraded"
  | "Stopping"
  | "Stopped"
  | "Deleting"
  | "Failed"
  | "Unknown"
  | "Deleted";
export type ManagedElsaHealth = "Healthy" | "Degraded" | "Unreachable" | "Unknown";

export const managedElsaHandoffTokenType = "elsa-handoff+jwt" as const;

export type ManagedElsaCurrentResolvedRelease = {
  distributionId: string;
  releaseLine: string;
  version: string;
  manifestDigest: string;
};

export type ManagedElsaInstance = {
  organizationId: string;
  instanceId: string;
  name: string;
  slug: string;
  desiredLifecycle: ManagedElsaDesiredLifecycle;
  observedLifecycle: ManagedElsaObservedLifecycle;
  health: ManagedElsaHealth;
  canOpen: boolean;
  audience: string | null;
  redirectUri: string | null;
  unavailableReason: string | null;
  version?: number;
  eTag?: string;
  intent?: ManagedElsaInstanceIntent | null;
  currentResolvedRelease?: ManagedElsaCurrentResolvedRelease | null;
  identityBindingState?: string | null;
};

export type ManagedElsaInstanceList = {
  items: ManagedElsaInstance[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
};

export type ManagedElsaLaunchProfile = {
  name: string;
  description: string;
  targetMode: string;
  regionCode: string;
  isolationProfile: string;
  capacityProfile: string;
  networkOutcome: string;
  domainOutcome: string;
};

export type ManagedElsaReleaseOption = {
  distributionId: string;
  releaseLine: string;
  version: string;
  channel: string;
  topologyId: string;
};

export type ManagedElsaOnboardingOptions = {
  releases: ManagedElsaReleaseOption[];
  previewReleases?: Array<ManagedElsaReleaseOption & { manifestDigest: string }> | null;
  launchProfile: ManagedElsaLaunchProfile;
};

export type ManagedElsaInstanceIntent = {
  release: {
    distributionId: string;
    releaseLine: string;
    requestedVersion: string;
    channel: string;
    patchUpdates: string;
    minorUpdates: string;
    majorMigrations: string;
    previewManifestDigest?: string;
  };
  application: {
    topologyId: string;
    featurePresetId: string | null;
    featureOverrides: Record<string, ManagedElsaFeatureOverride>;
    packagePolicy: string | null;
    configurationShapeRevisionId: string | null;
  };
  placement: Omit<ManagedElsaLaunchProfile, "name" | "description">;
  desiredLifecycle: "Running";
};

export type ManagedElsaFeatureOverride = {
  kind: "Boolean" | "Number" | "Catalog";
  value: string;
};

export type ManagedElsaOperation = {
  id: string;
  instanceId: string;
  action: string;
  state: "Accepted" | "WaitingForPriorOperation" | "Queued" | "Running" | "Succeeded" | "Failed" | "RecoveryRequired" | "Cancelled";
  attemptNumber: number;
  failureCode: string | null;
  links: Record<string, string>;
};

export type ManagedElsaOperationalHealthStatus =
  | "Healthy"
  | "Degraded"
  | "Failed"
  | "Unknown"
  | "Stale"
  | "RecoveryRequired";

export type ManagedElsaOperationalAlertSeverity = "Warning" | "Critical";

export type ManagedElsaOperationalOperation = {
  id: string;
  state: ManagedElsaOperation["state"];
  attemptNumber: number;
  acceptedAt: string;
  startedAt: string | null;
  heartbeatAt: string | null;
  lastProgressAt: string | null;
  diagnosticCode: string | null;
};

export type ManagedElsaOperationalRun = {
  id: string;
  status: WorkspaceDeploymentRunStatus;
  attemptNumber: number;
  queuedAt: string;
  startedAt: string | null;
  heartbeatAt: string | null;
  lastProgressAt: string | null;
  diagnosticCode: string | null;
};

export type ManagedElsaOperationalAlert = {
  code: string;
  severity: ManagedElsaOperationalAlertSeverity;
  // The API includes this safe correlation value for support workflows. Keep it
  // in the typed contract but never render it in the customer-facing cockpit.
  dedupeIdentity: string;
};

export type ManagedElsaInstanceHealth = {
  status: ManagedElsaOperationalHealthStatus;
  diagnosticCode: string;
  evaluatedAt: string;
  reconciledAt: string | null;
  operation: ManagedElsaOperationalOperation | null;
  run: ManagedElsaOperationalRun | null;
  alerts: ManagedElsaOperationalAlert[];
};

/** Customer-safe audit fields used by the operations cockpit. */
export type ManagedElsaAuditEvent = {
  id: string;
  sequence: number;
  eventType: string;
  operationId: string | null;
  migrationId: string | null;
  deploymentRunId: string | null;
  priorState: string | null;
  newState: string | null;
  diagnosticCode: string | null;
  occurredAt: string;
};

export type ManagedElsaInstanceAudit = {
  items: ManagedElsaAuditEvent[];
};

export const operationalHealthGuidance: Record<ManagedElsaOperationalHealthStatus, string> = {
  Healthy: "The instance is running, ready, and healthy with no active lifecycle work.",
  Degraded: "The instance is known but not healthy. Inspect the endpoint and active work projections.",
  Failed: "The provider projection is failed or unreachable. Inspect the operation and audit history before acting.",
  Unknown: "No trustworthy runtime health is available. Reconcile the instance before making another lifecycle request.",
  Stale: "Active lifecycle work exceeded its deadline. Inspect worker progress and provider state before retrying.",
  RecoveryRequired: "Durable recovery is required. Reconcile provider state before making another lifecycle request."
};

export const operationalCodeGuidance: Record<string, string> = {
  "managed.lifecycle.healthy": operationalHealthGuidance.Healthy,
  "managed.lifecycle.degraded": operationalHealthGuidance.Degraded,
  "managed.lifecycle.failed": operationalHealthGuidance.Failed,
  "managed.lifecycle.unknown": operationalHealthGuidance.Unknown,
  "managed.lifecycle.provider-unknown": operationalHealthGuidance.Unknown,
  "managed.lifecycle.stale": operationalHealthGuidance.Stale,
  "managed.lifecycle.stale-work": "A blocking operation or active run has no recent progress. Inspect worker progress and provider state.",
  "managed.lifecycle.reconciliation-stale": "Unknown provider reconciliation is older than the allowed boundary. Request reconciliation before acting.",
  "managed.lifecycle.reconciliation-unknown": "Provider observation is unknown or ambiguous. Request reconciliation before acting.",
  "managed.lifecycle.recovery-required": operationalHealthGuidance.RecoveryRequired,
  "managed.lifecycle.operation-failed": "The lifecycle operation failed. Inspect its safe code and audit history before retrying.",
  "managed.lifecycle.run-failed": "The correlated deployment run failed. Inspect its safe code and audit history before retrying.",
  "managed.lifecycle.work-active": "Existing lifecycle work is still active. Allow the reservation to finish before acting.",
  "managed.lifecycle.unhealthy-endpoint": "The endpoint or lifecycle projection is unhealthy. Confirm the safe runtime health diagnostics before reconciling.",
  "managed.lifecycle.retry-exhausted": "The configured retry limit has been reached. Stop automatic replay and inspect provider correlation."
};

export type ManagedElsaAccepted = {
  instance: ManagedElsaInstance;
  operation: ManagedElsaOperation;
  links: Record<string, string>;
};

export type ManagedElsaHandoffIssueRequest = {
  organizationId: string;
  instanceId: string;
  audience: string;
  redirectUri: string;
  codeChallenge: string;
};

export type ManagedElsaHandoffIssueResponse = {
  token: string;
  tokenType: string;
  audience: string;
  redirectUri: string;
  issuedAt: string;
  expiresAt: string;
};
