import { apiRequest } from "@/lib/api/httpClient";
import type {
  ManagedElsaAccepted,
  ManagedElsaHandoffIssueRequest,
  ManagedElsaHandoffIssueResponse,
  ManagedElsaInstance,
  ManagedElsaInstanceIntent,
  ManagedElsaInstanceList,
  ManagedElsaOnboardingOptions,
  ManagedElsaOperation,
  ManagedElsaInstanceAudit,
  ManagedElsaInstanceHealth,
} from "@/features/managed-elsa/managedElsaModels";

export async function listManagedElsaInstances(workspaceId: string) {
  const maximumPages = 1000;
  const items: ManagedElsaInstanceList["items"] = [];
  let page = 1;
  let expectedPages = 1;
  let response: ManagedElsaInstanceList;

  do {
    response = await apiRequest<ManagedElsaInstanceList>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/instances?page=${page}&pageSize=100`
    );
    if (response.page !== page ||
        !Number.isSafeInteger(response.pageSize) || response.pageSize < 1 || response.pageSize > 100 ||
        !Number.isSafeInteger(response.totalCount) || response.totalCount < 0 ||
        (response.hasMore && response.items.length === 0))
      throw new Error("managed-instance-pagination-invalid");
    items.push(...response.items);
    expectedPages = Math.max(expectedPages, Math.ceil(response.totalCount / response.pageSize));
    if (expectedPages > maximumPages || (response.hasMore && page >= expectedPages))
      throw new Error("managed-instance-pagination-invalid");
    page += 1;
  } while (response.hasMore);

  return {
    ...response,
    items,
    page: 1,
    hasMore: false
  };
}

export function getManagedElsaOnboardingOptions(workspaceId: string) {
  return apiRequest<ManagedElsaOnboardingOptions>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/instances/onboarding-options`
  );
}

export function createManagedElsaInstance(
  workspaceId: string,
  request: { name: string; slug: string; intent: ManagedElsaInstanceIntent },
  idempotencyKey: string
) {
  return apiRequest<ManagedElsaAccepted>(`/api/workspaces/${encodeURIComponent(workspaceId)}/instances`, {
    method: "POST",
    headers: { "Idempotency-Key": idempotencyKey },
    body: JSON.stringify(request)
  });
}

export function getManagedElsaInstance(workspaceId: string, instanceId: string) {
  return apiRequest<ManagedElsaInstance>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/instances/${encodeURIComponent(instanceId)}`
  );
}

export function updateManagedElsaInstanceIntent(
  workspaceId: string,
  instanceId: string,
  request: { intent: ManagedElsaInstanceIntent; reason?: string },
  eTag: string,
  idempotencyKey: string
) {
  return apiRequest<ManagedElsaAccepted>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/instances/${encodeURIComponent(instanceId)}`,
    {
      method: "PATCH",
      headers: {
        "If-Match": eTag,
        "Idempotency-Key": idempotencyKey
      },
      body: JSON.stringify(request)
    }
  );
}

export function getManagedElsaOperation(workspaceId: string, instanceId: string, operationId: string) {
  return apiRequest<ManagedElsaOperation>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/instances/${encodeURIComponent(instanceId)}/operations/${encodeURIComponent(operationId)}`
  );
}

export function getManagedElsaInstanceHealth(workspaceId: string, instanceId: string) {
  return apiRequest<ManagedElsaInstanceHealth>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/instances/${encodeURIComponent(instanceId)}/health`
  );
}

export function getManagedElsaInstanceAudit(workspaceId: string, instanceId: string) {
  return apiRequest<ManagedElsaInstanceAudit>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/instances/${encodeURIComponent(instanceId)}/audit`
  );
}

export function issueManagedElsaHandoff(request: ManagedElsaHandoffIssueRequest) {
  return apiRequest<ManagedElsaHandoffIssueResponse>("/api/managed-elsa/handoff/issue", {
    method: "POST",
    body: JSON.stringify(request)
  });
}
