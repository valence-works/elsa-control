import { apiRequest } from "@/lib/api/httpClient";
import type {
  AzureSubscriptionBindingView,
  CreateAzureSubscriptionBindRequest
} from "@/features/azure-binding/azureBindingModels";

const azureSubscriptionBindPath = (organizationId: string) =>
  `/api/organizations/${encodeURIComponent(organizationId)}/azure-subscription-bind`;

/** Reads the current bind and the server-selected Lighthouse artifact. */
export function getAzureSubscriptionBinding(organizationId: string) {
  return apiRequest<AzureSubscriptionBindingView>(azureSubscriptionBindPath(organizationId));
}

/** Creates the next in-flight bind after the customer explicitly consents. */
export function createAzureSubscriptionBind(organizationId: string, request: CreateAzureSubscriptionBindRequest) {
  return apiRequest<AzureSubscriptionBindingView>(azureSubscriptionBindPath(organizationId), {
    method: "POST",
    body: JSON.stringify(request)
  });
}

/** Requests an operator-triggered Lighthouse and RBAC preflight. */
export function verifyAzureSubscriptionBind(organizationId: string) {
  return apiRequest<AzureSubscriptionBindingView>(`${azureSubscriptionBindPath(organizationId)}/verify`, {
    method: "POST",
    body: JSON.stringify({})
  });
}
