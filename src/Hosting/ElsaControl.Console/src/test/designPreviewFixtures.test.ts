import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { designPreviewFixtures, installDesignPreviewFixtures, resetDesignPreviewFixtures } from "./designPreviewFixtures";

const originalFetch = window.fetch;
const workspacePath = "/api/workspaces/workspace-preview";
const billingPath = "/api/organizations/organization-preview/billing/";
const previewUrl = (path: string) => new URL(path, window.location.origin).href;

describe("design preview fixtures", () => {
  beforeEach(() => {
    resetDesignPreviewFixtures();
    installDesignPreviewFixtures();
  });
  afterEach(() => {
    window.fetch = originalFetch;
    resetDesignPreviewFixtures();
  });

  it.each([
    [billingPath, { organizationId: "organization-preview", subscription: null }],
    [`${workspacePath}/instances?page=1&pageSize=100`, { items: [], hasMore: false, totalCount: 0 }],
    [`${workspacePath}/instances/onboarding-options`, { releases: [], previewReleases: [] }],
    ["/api/admin/sync-runs", []],
    [`${workspacePath}/runtime-configurations`, []],
    ["/api/admin/console-logs/recent?limit=150", { items: [], dropped: [] }],
    ["/api/admin/console-logs/sources", []]
  ])("supports the initial request for %s", async (path, expected) => {
    const response = await window.fetch(previewUrl(path));
    expect(response.ok).toBe(true);
    expect(await response.json()).toMatchObject(expected);
  });

  it("keeps live console log negotiation inside the preview", async () => {
    const response = await window.fetch(previewUrl("/api/admin/console-logs/hub/negotiate?negotiateVersion=1"), { method: "POST" });
    expect(response.status).toBe(501);
    expect(await response.json()).toEqual({ title: "Live console streaming is unavailable in the sample-data preview." });
  });

  it.each(["checkout", "portal"])("keeps billing %s inside the preview", async (action) => {
    const response = await window.fetch(previewUrl(`${billingPath}${action}`), { method: "POST" });
    expect(response.status).toBe(503);
    expect(await response.json()).toEqual({ title: "Billing actions are unavailable in the sample-data preview." });
  });

  it("registers a sample engine without claiming verified health", async () => {
    const environment = designPreviewFixtures.cockpit.applications[0].environments[0];
    const response = await window.fetch(previewUrl(`${workspacePath}/deployments/environments/${environment.id}/engines`), {
      method: "POST",
      body: JSON.stringify({ name: "Unverified sample", baseUrl: "https://sample.example.test" })
    });
    expect(response.ok).toBe(true);
    expect(await response.json()).toMatchObject({ health: "Unreachable", lastHeartbeatAt: null, lastVerificationAt: null });
  });

  it("keeps sample credential references consistent with their stores", async () => {
    const stores = await (await window.fetch(previewUrl(`${workspacePath}/deployments/secret-stores`))).json();
    const credentials = await (await window.fetch(previewUrl(`${workspacePath}/deployments/credential-references`))).json();
    expect(stores.items).toHaveLength(2);
    for (const credential of credentials.items) {
      expect(stores.items).toContainEqual(expect.objectContaining({ id: credential.secretStoreId, type: credential.secretStoreType, status: "Active" }));
      expect(credential.hasProtectedSecret).toBe(credential.secretStoreType === "LocalEncryptedDatabase");
    }
  });

  it("returns typed usage details for preview credential references", async () => {
    const response = await window.fetch(previewUrl(`${workspacePath}/deployments/credential-references/credential-prod/usage`));
    expect(response.ok).toBe(true);
    expect(await response.json()).toEqual({
      items: [{
        engineId: "engine-northstar-dev",
        engineName: "checkout-dev-eu1",
        applicationId: "app-checkout",
        applicationName: "Checkout platform",
        environmentId: "env-northstar-dev",
        environmentName: "Development"
      }]
    });

    const stagingResponse = await window.fetch(previewUrl(`${workspacePath}/deployments/credential-references/credential-staging/usage`));
    expect(stagingResponse.ok).toBe(true);
    expect(await stagingResponse.json()).toMatchObject({
      items: [{
        engineId: "engine-automations-stage",
        applicationId: "app-automations",
        environmentId: "env-automations-stage"
      }]
    });
  });

  it("resets saved credential registration state between preview sessions", async () => {
    const environment = designPreviewFixtures.cockpit.applications[0].environments[0];
    const storeResponse = await window.fetch(previewUrl(`${workspacePath}/deployments/secret-stores`), {
      method: "POST",
      body: JSON.stringify({ name: "Preview local store", type: "LocalEncryptedDatabase", provider: "Preview protected store" })
    });
    const store = await storeResponse.json();
    const credentialResponse = await window.fetch(previewUrl(`${workspacePath}/deployments/secret-stores/${store.id}/credential-references`), {
      method: "POST",
      body: JSON.stringify({ name: "Preview saved credential", reference: "local://preview/saved", description: "Fixture credential" })
    });
    const credential = await credentialResponse.json();
    const engineResponse = await window.fetch(previewUrl(`${workspacePath}/deployments/environments/${environment.id}/engines`), {
      method: "POST",
      body: JSON.stringify({ name: "Saved credential engine", baseUrl: "https://saved.example.test", credentialReferenceId: credential.id })
    });
    const engine = await engineResponse.json();

    expect(credential.id).toBe("credential-preview-01");
    expect(credential.usageCount).toBe(0);
    expect(engine.id).toBe("engine-preview-01");
    expect((await (await window.fetch(previewUrl(`${workspacePath}/deployments/credential-references/${credential.id}/usage`))).json()).items).toMatchObject([{ engineId: engine.id }]);

    resetDesignPreviewFixtures();

    const cleanStores = await (await window.fetch(previewUrl(`${workspacePath}/deployments/secret-stores`))).json();
    const cleanCredentials = await (await window.fetch(previewUrl(`${workspacePath}/deployments/credential-references`))).json();
    const cleanCockpit = await (await window.fetch(previewUrl(`${workspacePath}/deployments/cockpit`))).json();
    expect(cleanStores.items).toHaveLength(2);
    expect(cleanCredentials.items).toHaveLength(2);
    expect(cleanCredentials.items.some((item: { id: string }) => item.id === credential.id)).toBe(false);
    expect(cleanCockpit.engines).toHaveLength(3);
    expect((await window.fetch(previewUrl(`${workspacePath}/deployments/credential-references/${credential.id}/usage`))).status).toBe(404);

    const nextStore = await (await window.fetch(previewUrl(`${workspacePath}/deployments/secret-stores`), {
      method: "POST",
      body: JSON.stringify({ name: "Sequence reset store", type: "LocalEncryptedDatabase" })
    })).json();
    expect(nextStore.id).toBe("secretStore-preview-01");
  });

  it("starts a new preview environment as unavailable with no desired revision", async () => {
    const applicationResponse = await window.fetch(previewUrl(`${workspacePath}/deployments/applications`), {
      method: "POST",
      body: JSON.stringify({ name: "Preview application" })
    });
    const application = await applicationResponse.json();
    await window.fetch(previewUrl(`${workspacePath}/deployments/applications/${application.id}/environments`), {
      method: "POST",
      body: JSON.stringify({ name: "Preview environment", tier: "Dev", tierId: "tier-development" })
    });

    const environment = designPreviewFixtures.cockpit.applications.find((item) => item.id === application.id)?.environments[0];
    expect(environment).toMatchObject({
      health: "Unreachable",
      deploymentStatus: "Blocked",
      desiredRevision: { id: "", revision: 0, commit: "", label: "No desired revision" }
    });
  });
});
