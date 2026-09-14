import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createAzureSubscriptionBind,
  getAzureSubscriptionBinding,
  verifyAzureSubscriptionBind
} from "@/features/azure-binding/azureBindingApi";

afterEach(() => vi.unstubAllGlobals());

describe("Azure Lighthouse binding API", () => {
  it("keeps organization paths encoded and sends explicit consent", async () => {
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => Response.json({ bind: null, offer: { version: "2026.09.1", artifactUrl: "https://example.test/arm" } }));
    vi.stubGlobal("fetch", fetchMock);

    await getAzureSubscriptionBinding("org/1");
    await createAzureSubscriptionBind("org/1", {
      subscriptionId: "11111111-1111-4111-8111-111111111111",
      customerTenantId: "22222222-2222-4222-8222-222222222222",
      consentConfirmed: true
    });
    await verifyAzureSubscriptionBind("org/1");

    expect(fetchMock).toHaveBeenNthCalledWith(1, "/api/organizations/org%2F1/azure-subscription-bind", expect.objectContaining({ credentials: "same-origin" }));
    expect(fetchMock).toHaveBeenNthCalledWith(2, "/api/organizations/org%2F1/azure-subscription-bind", expect.objectContaining({ method: "POST" }));
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toEqual({
      subscriptionId: "11111111-1111-4111-8111-111111111111",
      customerTenantId: "22222222-2222-4222-8222-222222222222",
      consentConfirmed: true
    });
    expect(fetchMock).toHaveBeenNthCalledWith(3, "/api/organizations/org%2F1/azure-subscription-bind/verify", expect.objectContaining({ method: "POST" }));
  });
});
