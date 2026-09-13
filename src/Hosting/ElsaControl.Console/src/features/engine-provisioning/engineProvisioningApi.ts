import { ApiError, apiRequest } from "@/lib/api/httpClient";
import type {
  EngineProvisioningAccepted,
  EngineProvisioningPreviewResponse,
  EngineProvisioningProvider,
  EngineProvisioningProvidersResponse,
  EngineProvisioningTarget,
  EngineProvisioningTargetsResponse,
  EngineProvisioningRequest
} from "@/features/engine-provisioning/engineProvisioningModels";

export async function listEngineProvisioningProviders(workspaceId: string): Promise<EngineProvisioningProvider[]> {
  try {
    const response = await apiRequest<EngineProvisioningProvidersResponse>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/engine-provisioning/providers`
    );
    if (!response || !Array.isArray(response.providers) || response.providers.some((provider) => !isProvider(provider)))
      throw new Error("engine-provisioning-providers-invalid");
    return response.providers;
  } catch (error) {
    // Older control hosts do not expose discovery yet. Treat that absence as
    // no available providers while preserving all other failures for the gate.
    if (error instanceof ApiError && error.status === 404)
      return [];
    throw error;
  }
}

export async function getEngineProvisioningTargets(workspaceId: string): Promise<EngineProvisioningTarget[]> {
  const response = await apiRequest<EngineProvisioningTargetsResponse>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/engine-provisioning/targets`
  );
  if (!response || !Array.isArray(response.targets) || response.targets.some((target) => !isTarget(target)))
    throw new Error("engine-provisioning-targets-invalid");
  return response.targets;
}

export function previewEngineProvisioning(workspaceId: string, request: EngineProvisioningRequest) {
  return apiRequest<EngineProvisioningPreviewResponse>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/engine-provisioning/preview`,
    { method: "POST", body: JSON.stringify(request) }
  );
}

export function createEngineProvisioning(workspaceId: string, request: EngineProvisioningRequest, idempotencyKey: string) {
  return apiRequest<EngineProvisioningAccepted>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/engine-provisioning`,
    {
      method: "POST",
      headers: { "Idempotency-Key": idempotencyKey },
      body: JSON.stringify(request)
    }
  );
}

function isProvider(value: unknown): value is EngineProvisioningProvider {
  return !!value && typeof value === "object" &&
    "id" in value && typeof value.id === "string" && value.id.trim().length > 0 &&
    "displayName" in value && typeof value.displayName === "string" && value.displayName.trim().length > 0;
}

function isTarget(value: unknown): value is EngineProvisioningTarget {
  return !!value && typeof value === "object" &&
    "applicationId" in value && typeof value.applicationId === "string" && value.applicationId.trim().length > 0 &&
    "environmentId" in value && typeof value.environmentId === "string" && value.environmentId.trim().length > 0;
}
