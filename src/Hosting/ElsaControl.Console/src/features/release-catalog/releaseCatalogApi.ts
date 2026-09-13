import { apiRequest, ApiError } from "@/lib/api/httpClient";
import type {
  ReleaseCatalogAdmissionResponse,
  ReleaseCatalogEntry,
  ReleaseCatalogProblem
} from "@/features/release-catalog/releaseCatalogModels";
import { releaseCatalogIdentityConflictCode } from "@/features/release-catalog/releaseCatalogModels";

export type AdmitReleaseManifestRequest = {
  reference: string;
  digest: string;
  payload: string;
};

export type ReleaseCatalogQuery = {
  distributionId?: string;
  releaseLine?: string;
  releaseVersion?: string;
  channel?: string;
  lifecycle?: string;
  topologyId?: string;
};

export function listWorkspaceReleaseCatalog(workspaceId: string, query: ReleaseCatalogQuery = {}) {
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value?.trim()) params.set(key, value.trim());
  }
  const suffix = params.size > 0 ? `?${params.toString()}` : "";
  return apiRequest<ReleaseCatalogEntry[]>(
    `/api/workspaces/${encodeURIComponent(workspaceId)}/release-catalog${suffix}`
  );
}

export function admitReleaseManifest(request: AdmitReleaseManifestRequest) {
  return apiRequest<ReleaseCatalogAdmissionResponse>("/api/admin/release-catalog/manifests", {
    method: "POST",
    body: JSON.stringify(request)
  });
}

export function isReleaseCatalogIdentityConflict(error: unknown): error is ApiError {
  return error instanceof ApiError && error.status === 409 && problemCode(error) === releaseCatalogIdentityConflictCode;
}

export function problemCode(error: unknown): string | null {
  const problem = releaseCatalogProblem(error);
  return problem?.code ?? null;
}

export function releaseCatalogProblem(error: unknown): ReleaseCatalogProblem | null {
  if (!(error instanceof ApiError) || !error.details || typeof error.details !== "object")
    return null;
  return error.details as ReleaseCatalogProblem;
}
