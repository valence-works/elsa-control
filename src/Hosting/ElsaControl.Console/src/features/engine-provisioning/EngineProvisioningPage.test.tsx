import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { EngineProvisioningPage } from "@/features/engine-provisioning/EngineProvisioningPage";
import type { DeploymentCockpit } from "@/features/deployments/deploymentModels";
import type { ManagedElsaAccepted, ManagedElsaOnboardingOptions, ManagedElsaOperation } from "@/features/managed-elsa/managedElsaModels";
import type { RuntimeConfiguration } from "@/features/runtime-builder/runtimeBuilderModels";
import type { RuntimeBuilderIntent } from "@/features/runtime-builder/runtimeBuilderModels";
import { createBuilderProvisioningHandoff, type EngineProvisioningPreviewResponse } from "@/features/engine-provisioning/engineProvisioningModels";
import {
  createEngineProvisioning,
  getEngineProvisioningTargets,
  previewEngineProvisioning
} from "@/features/engine-provisioning/engineProvisioningApi";
import { getDeploymentCockpit, getDeploymentPermissions } from "@/features/deployments/deploymentApi";
import { getManagedElsaOnboardingOptions, getManagedElsaOperation } from "@/features/managed-elsa/managedElsaApi";
import { getBuilderCatalog, listRuntimeConfigurations } from "@/features/runtime-builder/runtimeBuilderApi";
import { ApiError } from "@/lib/api/httpClient";

const workspaceState = vi.hoisted(() => ({ selectedWorkspaceId: "workspace-1" }));
const provisioningState = vi.hoisted(() => ({ available: true, pending: false, error: null as Error | null }));

vi.mock("@/app/WorkspaceContextProvider", () => ({
  useWorkspaceContext: () => ({ selectedWorkspaceId: workspaceState.selectedWorkspaceId, isLoading: false, isError: false })
}));

vi.mock("@/features/engine-provisioning/useEngineProvisioningProviders", () => ({
  useEngineProvisioningProviders: () => ({
    isPending: provisioningState.pending,
    isError: Boolean(provisioningState.error),
    error: provisioningState.error,
    hasProvider: () => provisioningState.available
  })
}));

vi.mock("@/features/engine-provisioning/engineProvisioningApi", () => ({
  createEngineProvisioning: vi.fn(),
  getEngineProvisioningTargets: vi.fn(),
  previewEngineProvisioning: vi.fn()
}));

vi.mock("@/features/deployments/deploymentApi", () => ({
  getDeploymentCockpit: vi.fn(),
  getDeploymentPermissions: vi.fn()
}));

vi.mock("@/features/managed-elsa/managedElsaApi", () => ({
  getManagedElsaOnboardingOptions: vi.fn(),
  getManagedElsaOperation: vi.fn(),
  onboardingChoices: (options: ManagedElsaOnboardingOptions | undefined) => options?.releases.map((release) => ({ ...release, previewManifestDigest: null })) ?? [],
  slugify: (value: string) => value.toLowerCase().trim().replace(/[^a-z0-9]+/g, "-")
}));

vi.mock("@/features/runtime-builder/runtimeBuilderApi", () => ({
  listRuntimeConfigurations: vi.fn(),
  getBuilderCatalog: vi.fn()
}));

const optionsFixture: ManagedElsaOnboardingOptions = {
  releases: [{ distributionId: "elsa", releaseLine: "4.x", version: "4.0.0", channel: "stable", topologyId: "managed" }],
  launchProfile: {
    name: "West Europe Dedicated",
    description: "Managed hosting in West Europe.",
    targetMode: "managed",
    regionCode: "westeurope",
    isolationProfile: "dedicated",
    capacityProfile: "standard-small",
    networkOutcome: "public",
    domainOutcome: "managed"
  }
};

const cockpitFixture = {
  applications: [{
    id: "app-1",
    name: "Orders",
    workspaceName: "Northstar",
    environments: [
      { id: "env-empty", name: "Development", tier: "Dev", health: "Unknown", desiredRevision: { id: "", revision: 0, commit: "", label: "No desired revision", authoredAt: "2026-09-12T08:00:00Z" }, deployedRevision: null, deploymentStatus: "Succeeded", driftStatus: "InSync", engineIds: [] },
      { id: "env-occupied", name: "Production", tier: "Production", health: "Healthy", desiredRevision: { id: "revision-2", revision: 1, commit: "", label: "Initial", authoredAt: "2026-09-12T08:00:00Z" }, deployedRevision: null, deploymentStatus: "Succeeded", driftStatus: "InSync", engineIds: ["engine-1"] }
    ]
  }],
  engines: [], comparisons: [], observabilityBindings: [], history: [], driftReport: [], assistantPlans: []
} as unknown as DeploymentCockpit;

const configurationFixture = {
  id: "configuration-1",
  workspaceId: "workspace-1",
  name: "Events runtime",
  description: "Messaging runtime",
  intent: { image: { slug: "elsa-instance", tag: "4.0.0", hostPort: 14000, envOverrides: { ADMIN_PASSWORD: "secret" } }, packages: [], packageSources: [], infrastructure: [], localPackages: null, target: "docker-compose" },
  createdAt: "2026-09-12T08:00:00Z",
  updatedAt: "2026-09-12T08:00:00Z"
} as unknown as RuntimeConfiguration;

const acceptedFixture = {
  instance: { instanceId: "instance-1", name: "orders-engine", slug: "orders-engine", organizationId: "org-1", desiredLifecycle: "Running", observedLifecycle: "Accepted", health: "Unknown", canOpen: false, audience: null, redirectUri: null, unavailableReason: null },
  operation: { id: "operation-1", instanceId: "instance-1", action: "Create", state: "Accepted", attemptNumber: 1, failureCode: null, links: {} },
  links: { workspace: "/admin/overview" }
} as unknown as ManagedElsaAccepted;

const succeededOperation: ManagedElsaOperation = { id: "operation-1", instanceId: "instance-1", action: "Create", state: "Succeeded", attemptNumber: 1, failureCode: null, links: { engine: "/admin/deployments/applications/app-1/environments/env-empty/engines/engine-1" } };

const projectedBuilderIntent: RuntimeBuilderIntent = {
  image: { slug: "elsa-instance", tag: "4.0.0" },
  packages: [{ sourceId: "source-1", packageId: "Elsa.Messaging", version: "4.0.0", selectedFeatures: ["Messaging"] }],
  packageSources: [{ sourceId: "source-1" }],
  infrastructure: [],
  localPackages: null,
  target: "managed"
};

describe("EngineProvisioningPage", () => {
  beforeEach(() => {
    workspaceState.selectedWorkspaceId = "workspace-1";
    provisioningState.available = true;
    provisioningState.pending = false;
    provisioningState.error = null;
    vi.mocked(getDeploymentPermissions).mockResolvedValue({ permissions: ["deployments.setup.manage"] });
    vi.mocked(getManagedElsaOnboardingOptions).mockResolvedValue(optionsFixture);
    vi.mocked(getDeploymentCockpit).mockResolvedValue(cockpitFixture);
    vi.mocked(getEngineProvisioningTargets).mockResolvedValue([{ applicationId: "app-1", environmentId: "env-empty" }]);
    vi.mocked(listRuntimeConfigurations).mockResolvedValue([configurationFixture]);
    vi.mocked(previewEngineProvisioning).mockResolvedValue({ canProvision: true, findings: [], configurationName: "Recommended runtime", configurationDigest: "sha256:config", previewDigest: "sha256:preview", builderIntent: null });
    vi.mocked(createEngineProvisioning).mockResolvedValue(acceptedFixture);
    vi.mocked(getManagedElsaOperation).mockResolvedValue(succeededOperation);
  });

  afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
    window.sessionStorage.clear();
    vi.clearAllMocks();
  });

  it("previews, accepts, polls, and links the registered engine", async () => {
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByRole("heading", { name: "Provision an engine" })).toBeInTheDocument();
    const environment = screen.getByRole("combobox", { name: "Environment" });
    expect(within(environment).queryByRole("option", { name: "Production" })).not.toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Events runtime" })).toBeInTheDocument();

    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await waitFor(() => expect(previewEngineProvisioning).toHaveBeenCalledWith("workspace-1", expect.objectContaining({
      name: "Orders engine",
      slug: "orders-engine",
      applicationId: "app-1",
      environmentId: "env-empty",
      intent: expect.objectContaining({ release: expect.objectContaining({ requestedVersion: "4.0.0" }) })
    })));
    expect(await screen.findByRole("heading", { name: "Ready to provision" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Provision engine" }));
    await waitFor(() => expect(createEngineProvisioning).toHaveBeenCalledWith("workspace-1", expect.objectContaining({ previewDigest: "sha256:preview" }), expect.any(String)));
    expect(await screen.findByRole("heading", { name: "Your engine is registered" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "View engine detail" })).toHaveAttribute("href", "/admin/deployments/applications/app-1/environments/env-empty/engines/engine-1");
    await waitFor(() => expect(getEngineProvisioningTargets).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(getDeploymentCockpit).toHaveBeenCalledTimes(2));
  });

  it("joins the cockpit by server-authoritative eligible target ids", async () => {
    vi.mocked(getEngineProvisioningTargets).mockResolvedValue([{ applicationId: "app-1", environmentId: "env-occupied" }]);
    renderPage();

    const environment = await screen.findByRole("combobox", { name: "Environment" });
    expect(within(environment).getByRole("option", { name: "Production" })).toBeInTheDocument();
    expect(within(environment).queryByRole("option", { name: "Development" })).not.toBeInTheDocument();
  });

  it("requires explicit Preview consent before server preview", async () => {
    vi.mocked(getManagedElsaOnboardingOptions).mockResolvedValue({
      ...optionsFixture,
      releases: [],
      previewReleases: [{ distributionId: "elsa", releaseLine: "5.x", version: "5.0.0-preview.1", channel: "preview", topologyId: "managed", manifestDigest: `sha256:${"a".repeat(64)}` }]
    });
    const user = userEvent.setup();
    renderPage();

    await screen.findByRole("heading", { name: "Provision an engine" });
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Preview engine");
    const consent = screen.getByRole("checkbox", { name: /Use this Preview release/ });
    expect(consent).not.toBeChecked();
    expect(screen.getByRole("button", { name: "Review deployment" })).toBeDisabled();

    await user.click(consent);
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await waitFor(() => expect(previewEngineProvisioning).toHaveBeenCalledWith("workspace-1", expect.objectContaining({
      intent: expect.objectContaining({ release: expect.objectContaining({ previewManifestDigest: `sha256:${"a".repeat(64)}` }) })
    })));
  });

  it("retries uncertain acceptance with the same key and creates a new key for a revised review", async () => {
    vi.mocked(createEngineProvisioning)
      .mockRejectedValueOnce(new Error("Connection lost"))
      .mockRejectedValueOnce(new Error("Connection lost"));
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole("heading", { name: "Provision an engine" });
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await user.click(await screen.findByRole("button", { name: "Provision engine" }));
    await screen.findByText("Provisioning needs attention");
    await user.click(screen.getByRole("button", { name: "Provision engine" }));
    await waitFor(() => expect(createEngineProvisioning).toHaveBeenCalledTimes(2));
    expect(vi.mocked(createEngineProvisioning).mock.calls[1]).toEqual(vi.mocked(createEngineProvisioning).mock.calls[0]);
    await user.click(screen.getByRole("button", { name: "Edit configuration" }));
    await user.clear(screen.getByRole("textbox", { name: "Engine name" }));
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Revised engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await user.click(await screen.findByRole("button", { name: "Provision engine" }));
    await waitFor(() => expect(createEngineProvisioning).toHaveBeenCalledTimes(3));
    const calls = vi.mocked(createEngineProvisioning).mock.calls;
    expect(calls[2][2]).not.toEqual(calls[0][2]);
    expect(calls[2][1].name).toBe("Revised engine");
  });

  it("replays the exact projected request after a lost response when the target is now occupied", async () => {
    const rawExportOnlyMarker = "raw-export-only-marker";
    const rawBuilderIntent: RuntimeBuilderIntent = {
      image: { slug: "elsa-instance", envOverrides: { ADMIN_PASSWORD: "raw-password" } },
      packages: [{ sourceId: "source-1", packageId: "Elsa.Messaging", version: "4.0.0", selectedFeatures: ["Messaging"], settings: { Messaging: { exportOnlyMarker: rawExportOnlyMarker } } }],
      packageSources: [{ sourceId: "source-1" }],
      infrastructure: [{ kind: "message-bus", providerId: "azure", strategy: "managed", settings: { exportOnlyMarker: rawExportOnlyMarker } }],
      localPackages: { enabled: false, directoryPath: `/exports/${rawExportOnlyMarker}` },
      target: "docker-compose"
    };
    const handoffToken = createBuilderProvisioningHandoff(rawBuilderIntent);
    vi.mocked(previewEngineProvisioning).mockResolvedValue({ canProvision: true, findings: [], configurationName: "Projected runtime", configurationDigest: "sha256:config", previewDigest: "sha256:preview", builderIntent: projectedBuilderIntent });
    vi.mocked(createEngineProvisioning).mockRejectedValueOnce(new Error("Lost response"));
    const user = userEvent.setup();
    renderPage("/admin/engines/provision", { state: { runtimeConfigurationId: "configuration-1", configurationName: "Events runtime", builderHandoffToken: handoffToken } });

    await screen.findByRole("heading", { name: "Provision an engine" });
    await user.clear(screen.getByRole("textbox", { name: "Engine name" }));
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await waitFor(() => expect(previewEngineProvisioning).toHaveBeenCalledWith("workspace-1", expect.objectContaining({
      builderIntent: expect.objectContaining({ packages: expect.arrayContaining([expect.objectContaining({ settings: { Messaging: { exportOnlyMarker: rawExportOnlyMarker } } })]) })
    })));
    await user.click(await screen.findByRole("button", { name: "Provision engine" }));
    await waitFor(() => expect(createEngineProvisioning).toHaveBeenCalledTimes(1));

    const firstCall = vi.mocked(createEngineProvisioning).mock.calls[0];
    const storedAttempt = window.sessionStorage.getItem("engine-provisioning-attempt:workspace-1");
    expect(storedAttempt).toBeTruthy();
    expect(storedAttempt).not.toContain("ADMIN_PASSWORD");
    expect(storedAttempt).not.toContain(rawExportOnlyMarker);
    expect(JSON.parse(storedAttempt!).request).toEqual(firstCall[1]);
    expect(JSON.stringify(firstCall[1])).not.toContain(rawExportOnlyMarker);

    cleanup();
    vi.mocked(createEngineProvisioning).mockResolvedValue(acceptedFixture);
    vi.mocked(getEngineProvisioningTargets).mockResolvedValue([]);
    vi.mocked(getManagedElsaOnboardingOptions).mockRejectedValue(new Error("options unavailable"));
    vi.mocked(getDeploymentCockpit).mockRejectedValue(new Error("cockpit unavailable"));
    vi.mocked(listRuntimeConfigurations).mockRejectedValue(new Error("configurations unavailable"));
    renderPage();

    await waitFor(() => expect(createEngineProvisioning).toHaveBeenCalledTimes(2));
    const replayedCall = vi.mocked(createEngineProvisioning).mock.calls[1];
    expect(replayedCall[1]).toEqual(firstCall[1]);
    expect(replayedCall[2]).toBe(firstCall[2]);
    expect(await screen.findByRole("heading", { name: "Your engine is registered" })).toBeInTheDocument();
    expect(window.sessionStorage.getItem("engine-provisioning-attempt:workspace-1")).toBeNull();
  });

  it("does not POST when the reviewed request cannot be persisted for retry", async () => {
    const restoreStorage = overrideSessionStorage((key) => {
      throw new Error(`storage unavailable for ${key}`);
    });
    try {
      const user = userEvent.setup();
      renderPage();

      await screen.findByRole("heading", { name: "Provision an engine" });
      await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
      await user.click(screen.getByRole("button", { name: "Review deployment" }));
      await user.click(await screen.findByRole("button", { name: "Provision engine" }));

      expect(await screen.findByText("This browser could not preserve the request safely for retry. No provisioning request was sent.")).toBeInTheDocument();
      expect(createEngineProvisioning).not.toHaveBeenCalled();
    } finally {
      restoreStorage();
    }
  });

  it("retains the attempt when the accepted operation cannot be persisted", async () => {
    const restoreStorage = overrideSessionStorage((key, value, originalStorage) => {
      if (key.startsWith("engine-provisioning-operation:")) throw new Error("operation storage unavailable");
      originalStorage.setItem(key, value);
    });
    try {
      const user = userEvent.setup();
      renderPage();

      await screen.findByRole("heading", { name: "Provision an engine" });
      await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
      await user.click(screen.getByRole("button", { name: "Review deployment" }));
      await user.click(await screen.findByRole("button", { name: "Provision engine" }));
      expect(await screen.findByRole("heading", { name: "Your engine is registered" })).toBeInTheDocument();
      expect(window.sessionStorage.getItem("engine-provisioning-operation:workspace-1")).toBeNull();
      expect(window.sessionStorage.getItem("engine-provisioning-attempt:workspace-1")).toBeTruthy();
    } finally {
      restoreStorage();
    }
  });

  it("lets a definitive recovery failure return to configuration", async () => {
    const attempt = {
      key: "recovery-key",
      request: {
        name: "Orders engine",
        slug: "orders-engine",
        applicationId: "app-1",
        environmentId: "env-empty",
        intent: { release: { distributionId: "elsa", requestedVersion: "4.0.0", releaseLine: "4.x", channel: "stable", topologyId: "managed" }, launchProfile: "managed" }
      },
      display: { name: "Orders engine", applicationName: "Orders", environmentName: "Development" }
    };
    window.sessionStorage.setItem("engine-provisioning-attempt:workspace-1", JSON.stringify(attempt));
    vi.mocked(createEngineProvisioning).mockRejectedValueOnce(new ApiError("Conflict", "The preview is stale.", 409));
    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent("The preview is stale");
    await userEvent.click(screen.getByRole("button", { name: "Return to configuration" }));
    expect(await screen.findByRole("heading", { name: "Provision an engine" })).toBeInTheDocument();
    expect(window.sessionStorage.getItem("engine-provisioning-attempt:workspace-1")).toBeNull();
  });

  it("shows the provider unavailable state on a direct route", async () => {
    provisioningState.available = false;
    renderPage();
    expect(await screen.findByRole("heading", { name: "Engine provisioning is unavailable" })).toBeInTheDocument();
    expect(getManagedElsaOnboardingOptions).not.toHaveBeenCalled();
  });

  it("passes the complete edited builder intent through the ephemeral handoff", async () => {
    const handoffToken = createBuilderProvisioningHandoff({
      image: { slug: "elsa-instance", envOverrides: { ADMIN_PASSWORD: "secret" } },
      packages: [{ sourceId: "source-1", packageId: "Elsa.Messaging", version: "4.0.0", selectedFeatures: ["Messaging"], settings: { Messaging: { topic: "orders" } } }],
      packageSources: [{ sourceId: "source-1" }],
      infrastructure: [{ kind: "message-bus", providerId: "azure", strategy: "managed", settings: { region: "westeurope" } }],
      target: "docker-compose",
      localPackages: { enabled: false, directoryPath: "./packages" }
    });
    renderPage("/admin/engines/provision", { state: { runtimeConfigurationId: "configuration-1", configurationName: "Events runtime", builderHandoffToken: handoffToken } });

    await screen.findByRole("heading", { name: "Provision an engine" });
    expect(screen.getByRole("textbox", { name: "Engine name" })).toHaveValue("Events runtime");
    expect(screen.getByRole("textbox", { name: "Engine address" })).toHaveValue("events-runtime");
    await userEvent.click(screen.getByRole("button", { name: "Review deployment" }));
    await waitFor(() => expect(previewEngineProvisioning).toHaveBeenCalledWith("workspace-1", expect.objectContaining({
      runtimeConfigurationId: "configuration-1",
      builderIntent: expect.objectContaining({ image: expect.objectContaining({ envOverrides: { ADMIN_PASSWORD: "secret" } }), packageSources: expect.any(Array) })
    })));
  });

  it("customizes saved runtime features without changing the saved configuration", async () => {
    const source = { id: "source-1", name: "Packages", url: "https://packages.example.test" };
    const saved = { ...configurationFixture, intent: { ...configurationFixture.intent,
      image: { slug: "elsa-instance" },
      packages: [{ sourceId: source.id, packageId: "Elsa.Messaging", version: "4.0.0", selectedFeatures: [], settings: { Messaging: { topic: "orders" } } }]
    } };
    vi.mocked(listRuntimeConfigurations).mockResolvedValue([saved]);
    vi.mocked(getBuilderCatalog).mockResolvedValue({ images: [], infrastructureProviders: [], packages: [{
      packageId: "Elsa.Messaging", displayName: "Messaging", source, runtimeKinds: [], versions: [{
        packageId: "Elsa.Messaging", version: "4.0.0", source, runtimeKinds: [], features: [{
          featureId: "Messaging", typeName: "MessagingFeature", displayName: "Messaging", requiredCapabilities: [],
          runtimeKinds: [], infrastructure: [], advanced: false, experimental: false, settings: []
        }]
      }]
    }] });
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole("heading", { name: "Provision an engine" });
    await user.selectOptions(screen.getByRole("combobox", { name: "Saved configuration" }), saved.id);
    await user.click(screen.getByRole("button", { name: "Customize features" }));
    await user.click(await screen.findByRole("checkbox", { name: /Messaging/ }));
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await waitFor(() => expect(previewEngineProvisioning).toHaveBeenCalledWith("workspace-1", expect.objectContaining({
      runtimeConfigurationId: saved.id,
      builderIntent: expect.objectContaining({ packages: [{ ...saved.intent.packages[0], selectedFeatures: ["Messaging"] }] })
    })));
    expect(saved.intent.packages[0].selectedFeatures).toEqual([]);
  });

  it("separates the included runtime snapshot from server findings during review", async () => {
    vi.mocked(previewEngineProvisioning).mockResolvedValue({
      canProvision: false,
      findings: [{ severity: "error", code: "runtime.env-overrides", message: "Environment overrides are not accepted for managed provisioning.", scope: "builder" }],
      configurationName: "Events runtime",
      configurationDigest: "sha256:config",
      previewDigest: null,
      builderIntent: configurationFixture.intent
    });
    const user = userEvent.setup();
    renderPage();

    await screen.findByRole("heading", { name: "Provision an engine" });
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));

    expect(await screen.findByRole("heading", { name: "Compatibility notes" })).toBeInTheDocument();
    expect(screen.getByText("Rejected", { exact: true })).toBeInTheDocument();
    expect(screen.getByText("Included in this engine", { exact: true })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Resolve preview findings first" })).toBeDisabled();
  });

  it("blocks a stale builder handoff instead of falling back to the saved configuration", async () => {
    renderPage("/admin/engines/provision", { state: { runtimeConfigurationId: "configuration-1", builderHandoffToken: "expired-token", configurationName: "Events runtime" } });

    expect(await screen.findByText("Runtime Builder handoff expired", { exact: true })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Review deployment" })).toBeDisabled();
    expect(screen.getByRole("combobox", { name: "Saved configuration" })).toHaveValue("configuration-1");
    expect(previewEngineProvisioning).not.toHaveBeenCalled();
  });

  it("ignores an in-flight preview when the workspace changes", async () => {
    let resolvePreview!: (result: EngineProvisioningPreviewResponse) => void;
    vi.mocked(previewEngineProvisioning).mockImplementation(() => new Promise((resolve) => {
      resolvePreview = resolve;
    }));
    const view = renderPage();
    const user = userEvent.setup();

    await screen.findByRole("heading", { name: "Provision an engine" });
    await user.type(screen.getByRole("textbox", { name: "Engine name" }), "Orders engine");
    await user.click(screen.getByRole("button", { name: "Review deployment" }));
    await waitFor(() => expect(previewEngineProvisioning).toHaveBeenCalledTimes(1));

    workspaceState.selectedWorkspaceId = "workspace-2";
    view.rerenderPage();
    expect(await screen.findByRole("heading", { name: "Provision an engine" })).toBeInTheDocument();

    resolvePreview({ canProvision: true, findings: [], configurationName: "Stale preview", configurationDigest: null, previewDigest: "stale", builderIntent: null });
    await waitFor(() => expect(screen.queryByRole("heading", { name: "Ready to provision" })).not.toBeInTheDocument());
  });

  it("resumes a stored operation even when configure data is unavailable", async () => {
    window.sessionStorage.setItem("engine-provisioning-operation:workspace-1", JSON.stringify({
      instanceId: "instance-1",
      operationId: "operation-1",
      name: "Orders engine",
      applicationName: "Orders",
      environmentName: "Development",
      engineLink: null
    }));
    vi.mocked(getManagedElsaOperation).mockResolvedValue({ ...succeededOperation, state: "Running", links: {} });
    vi.mocked(getManagedElsaOnboardingOptions).mockRejectedValue(new Error("options-unavailable"));
    vi.mocked(getDeploymentCockpit).mockRejectedValue(new Error("cockpit-unavailable"));
    vi.mocked(listRuntimeConfigurations).mockRejectedValue(new Error("configurations-unavailable"));

    renderPage();

    expect(await screen.findByRole("heading", { name: "Your engine is on its way" })).toBeInTheDocument();
    expect(screen.getByText("Orders engine · Orders / Development")).toBeInTheDocument();
  });
});

function renderPage(path = "/admin/engines/provision", entry?: { state?: unknown }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const renderApp = () => <QueryClientProvider client={queryClient}><MemoryRouter initialEntries={[entry ? { pathname: path, ...entry } : path]}><EngineProvisioningPage /></MemoryRouter></QueryClientProvider>;
  const view = render(renderApp());
  return { ...view, rerenderPage: () => view.rerender(renderApp()) };
}

function overrideSessionStorage(setItem: (key: string, value: string, originalStorage: Storage) => void) {
  const originalStorage = window.sessionStorage;
  const descriptor = Object.getOwnPropertyDescriptor(window, "sessionStorage");
  Object.defineProperty(window, "sessionStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => originalStorage.getItem(key),
      setItem: (key: string, value: string) => setItem(key, value, originalStorage),
      removeItem: (key: string) => originalStorage.removeItem(key),
      clear: () => originalStorage.clear(),
      key: (index: number) => originalStorage.key(index),
      get length() { return originalStorage.length; }
    }
  });
  return () => {
    if (descriptor) Object.defineProperty(window, "sessionStorage", descriptor);
  };
}
