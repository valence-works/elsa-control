import { apiRequest } from "@/lib/api/httpClient";

export type AdminOrganizationCreateRequest = {
  name: string;
  ownerAccountId?: string;
};

export type AdminOrganizationCreateResponse = {
  organizationId: string;
  workspaceId: string;
  ownerAccountId: string;
};

export function createAdminOrganization(request: AdminOrganizationCreateRequest) {
  return apiRequest<AdminOrganizationCreateResponse>("/api/admin/organizations", {
    method: "POST",
    body: JSON.stringify(request)
  });
}
