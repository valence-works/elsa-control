export type ReleaseCatalogWriteStatus = "Stored" | "Unchanged" | "Conflict";

export type ReleaseCatalogSource = {
  repository: string;
  commit: string;
  runId: string;
};

export type ReleaseCatalogDistribution = {
  id: string;
  generation: string;
  releaseLine: string;
  releaseVersion: string;
  channel: string;
  producerLifecycle: string;
  catalogLifecycle: string;
  edition: string | null;
  source: ReleaseCatalogSource;
};

export type ReleaseCatalogTopology = {
  id: string;
  packageManifestSchema: string;
  runtimeKinds: string[];
  capabilities: string[];
};

export type ReleaseCatalogEntry = {
  schemaVersion: string;
  manifestReference: string;
  manifestDigest: string;
  payloadDigest: string;
  signatureEvidenceReference: string;
  signatureEvidenceDigest: string;
  registryClass: string;
  distribution: ReleaseCatalogDistribution;
  topology: ReleaseCatalogTopology;
  admittedAt: string;
};

export type ReleaseCatalogAdmissionResponse = {
  status: ReleaseCatalogWriteStatus;
  entries: ReleaseCatalogEntry[];
};

export type ReleaseCatalogFinding = {
  code: string;
  scope?: string;
  message: string;
};

export type ReleaseCatalogProblem = {
  title?: string;
  detail?: string;
  code?: string;
  findings?: ReleaseCatalogFinding[];
  existing?: ReleaseCatalogEntry[];
  incoming?: ReleaseCatalogEntry[];
};

export type ReleaseCatalogIdentityFacts = {
  distributionId: string;
  generation: string;
  releaseLine: string;
  releaseVersion: string;
  channel: string;
  producerLifecycle: string;
  catalogLifecycle: string | null;
  registryClass: string;
  manifestDigest: string;
  sourceRunId: string;
  topologies: string[];
  capabilities: string[];
  build: string | null;
};

export const releaseCatalogIdentityConflictCode = "releaseCatalog.identity.conflict";

export const releaseCatalogCopy = {
  admitBlockedUntilAdmitted: "You cannot Apply this build to an instance until it is admitted.",
  happyPath:
    "Happy path: admit a unique preview releaseVersion that includes build.N, then Apply, then Open.",
  conflictTitle: "Release catalog identity conflict",
  conflictBody:
    "This release is not admitted. A different immutable release already owns this catalog identity. This drawer is recovery UX — not the happy path.",
  primaryRecovery:
    "Mint a unique preview releaseVersion that includes build.N, then Admit again. Identity collisions are recovered this way; they are not a live Open blocker.",
  idempotentRetry:
    "Disabled — fingerprints differ. Only works when the projection matches the existing identity (Unchanged).",
  openEdge:
    "Open may still fail until the instance is Healthy/Ready and handoff/auth is configured. A 404 is missing handoff (#383-class); a 500 with handoff present is runtime wiring (#397-class). Admit + Apply does not mean Open succeeded.",
  admitRequiresAdmin: "Admit requires a control_admin session or admin API key. Ordinary customer sessions cannot admit releases.",
  applyNotAdmitted:
    "This release is not in the admitted catalog. Admit the unique preview identity (include build.N) from Releases first, then return here. The conflict drawer is recovery UX if Admit returns 409 — do not overwrite the existing identity."
} as const;
