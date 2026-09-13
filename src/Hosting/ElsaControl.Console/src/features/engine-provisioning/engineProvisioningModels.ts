import type { ManagedElsaAccepted, ManagedElsaInstanceIntent } from "@/features/managed-elsa/managedElsaModels";
import type { RuntimeBuilderIntent } from "@/features/runtime-builder/runtimeBuilderModels";

export type EngineProvisioningProvider = {
  id: string;
  displayName: string;
};

export type EngineProvisioningProvidersResponse = {
  providers: EngineProvisioningProvider[];
};

export type EngineProvisioningTarget = {
  applicationId: string;
  environmentId: string;
};

export type EngineProvisioningTargetsResponse = {
  targets: EngineProvisioningTarget[];
};

export type EngineProvisioningFinding = {
  severity: string;
  code: string;
  message: string;
  scope?: string | null;
};

export type EngineProvisioningRequest = {
  name: string;
  slug: string;
  applicationId: string;
  environmentId: string;
  intent: ManagedElsaInstanceIntent;
  runtimeConfigurationId?: string;
  builderIntent?: RuntimeBuilderIntent;
  previewDigest?: string | null;
};

export type EngineProvisioningPreviewResponse = {
  canProvision: boolean;
  findings: EngineProvisioningFinding[];
  configurationName: string;
  configurationDigest: string | null;
  previewDigest: string | null;
  builderIntent: RuntimeBuilderIntent | null;
};

export type EngineProvisioningAccepted = ManagedElsaAccepted;

type BuilderProvisioningHandoff = {
  intent: RuntimeBuilderIntent;
  createdAt: number;
};

const builderProvisioningHandoffs = new Map<string, BuilderProvisioningHandoff>();
const handoffLifetimeMs = 15 * 60 * 1000;

/**
 * Keep edited Builder state in the running console only. Router state carries
 * the opaque token, while the server receives the complete intent and can
 * report any incompatible or unsafe values instead of silently dropping them.
 */
export function createBuilderProvisioningHandoff(intent: RuntimeBuilderIntent): string {
  pruneBuilderProvisioningHandoffs();
  const token = globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
  builderProvisioningHandoffs.set(token, { intent, createdAt: Date.now() });
  return token;
}

export function consumeBuilderProvisioningHandoff(token: string | undefined): RuntimeBuilderIntent | undefined {
  if (!token) return undefined;
  pruneBuilderProvisioningHandoffs();
  const handoff = builderProvisioningHandoffs.get(token);
  if (!handoff) return undefined;
  builderProvisioningHandoffs.delete(token);
  return handoff.intent;
}

function pruneBuilderProvisioningHandoffs() {
  const threshold = Date.now() - handoffLifetimeMs;
  for (const [token, handoff] of builderProvisioningHandoffs) {
    if (handoff.createdAt < threshold) builderProvisioningHandoffs.delete(token);
  }
}
