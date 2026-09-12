import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { designPreviewFixtures, installDesignPreviewFixtures } from "./designPreviewFixtures";

const originalFetch = window.fetch;
const originalCockpit = structuredClone(designPreviewFixtures.cockpit);
const workspacePath = "/api/workspaces/workspace-preview";
const billingPath = "/api/organizations/organization-preview/billing/";
const previewUrl = (path: string) => new URL(path, window.location.origin).href;

describe("design preview fixtures", () => {
  beforeEach(() => installDesignPreviewFixtures());
  afterEach(() => {
    window.fetch = originalFetch;
    designPreviewFixtures.cockpit = structuredClone(originalCockpit);
  });

  it.each([
    [billingPath, { organizationId: "organization-preview", subscription: null }],
    [`${workspacePath}/instances?page=1&pageSize=100`, { items: [], hasMore: false, totalCount: 0 }],
    [`${workspacePath}/instances/onboarding-options`, { releases: [], previewReleases: [] }]
  ])("supports the initial request for %s", async (path, expected) => {
    const response = await window.fetch(previewUrl(path));
    expect(response.ok).toBe(true);
    expect(await response.json()).toMatchObject(expected);
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
});
