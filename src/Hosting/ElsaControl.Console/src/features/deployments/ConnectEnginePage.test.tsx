import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ConnectEnginePage } from "@/features/deployments/ConnectEnginePage";
import type { DeploymentCockpit, WorkflowEngineRegistration, WorkspaceDeploymentCredentialReference } from "@/features/deployments/deploymentModels";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";
import {
  createDeploymentApplication,
  createDeploymentCredentialReference,
  createDeploymentEnvironment,
  createDeploymentSecretStore,
  getDeploymentCockpit,
  getDeploymentCredentialReferences,
  getDeploymentPermissions,
  getDeploymentSecretStores,
  getDeploymentTiers,
  registerDeploymentEngine
} from "@/features/deployments/deploymentApi";

const workspaceState = vi.hoisted(() => ({ selectedWorkspaceId: "workspace-1" }));

vi.mock("@/app/WorkspaceContextProvider", () => ({
  useWorkspaceContext: () => ({
    selectedWorkspaceId: workspaceState.selectedWorkspaceId,
    isLoading: false,
    isError: false
  })
}));

vi.mock("@/features/deployments/deploymentApi", () => ({
  createDeploymentApplication: vi.fn(),
  createDeploymentCredentialReference: vi.fn(),
  createDeploymentEnvironment: vi.fn(),
  createDeploymentSecretStore: vi.fn(),
  getDeploymentCockpit: vi.fn(),
  getDeploymentCredentialReferences: vi.fn(),
  getDeploymentPermissions: vi.fn(),
  getDeploymentSecretStores: vi.fn(),
  getDeploymentTiers: vi.fn(),
  registerDeploymentEngine: vi.fn()
}));

const cockpitFixture = {
  applications: [
    {
      id: "app-orders",
      name: "Orders",
      workspaceName: "Northstar",
      environments: [{
        id: "env-development",
        name: "Development",
        tier: "Dev",
        health: "Healthy",
        desiredRevision: { id: "revision-1", revision: 1, commit: "", label: "Initial", authoredAt: "2026-09-12T08:00:00Z" },
        deployedRevision: null,
        deploymentStatus: "Succeeded",
        driftStatus: "InSync",
        engineIds: []
      }]
    }
  ],
  engines: [],
  comparisons: [],
  observabilityBindings: [],
  history: [],
  driftReport: [],
  assistantPlans: []
} as DeploymentCockpit;

const credentialFixture = {
  id: "credential-orders",
  workspaceId: "workspace-1",
  secretStoreId: "store-local",
  secretStoreName: "Workspace encrypted store",
  secretStoreProvider: "Elsa Control",
  secretStoreType: "LocalEncryptedDatabase",
  name: "Orders API key",
  reference: "local://orders",
  description: null,
  status: "Active",
  verificationStatus: "Verified",
  lastVerifiedAt: "2026-09-12T08:00:00Z",
  createdAt: "2026-09-12T08:00:00Z",
  updatedAt: "2026-09-12T08:00:00Z",
  createdByAccountId: null,
  updatedByAccountId: null,
  archivedAt: null,
  archivedByAccountId: null,
  hasProtectedSecret: true,
  usageCount: 0
} as const;

const secretStoreFixture = {
  id: "store-local",
  workspaceId: "workspace-1",
  name: "Workspace encrypted store",
  provider: "Elsa Control",
  type: "LocalEncryptedDatabase",
  description: null,
  status: "Active",
  createdAt: "2026-09-12T08:00:00Z",
  updatedAt: "2026-09-12T08:00:00Z",
  createdByAccountId: null,
  updatedByAccountId: null,
  archivedAt: null,
  archivedByAccountId: null
} as const;

const engineFixture: WorkflowEngineRegistration = {
  id: "engine-orders",
  name: "orders-dev",
  environmentId: "env-development",
  endpoint: { baseUrl: "https://orders.example.com", region: "", version: "4.0.0", certificateStatus: "Trusted" },
  credentialReference: { provider: "Elsa Control", reference: "local://orders", verificationStatus: "Verified", lastVerifiedAt: "2026-09-12T08:00:00Z" },
  credentialAssignmentStatus: "Assigned",
  health: "Healthy",
  lastHeartbeatAt: "2026-09-12T08:00:00Z",
  lastVerificationAt: "2026-09-12T08:00:00Z",
  verificationMessage: "Endpoint responded successfully.",
  capabilities: [],
  controls: [],
  hostingProvider: null
};

describe("ConnectEnginePage", () => {
  beforeEach(() => {
    vi.mocked(getDeploymentCockpit).mockResolvedValue(cockpitFixture);
    vi.mocked(getDeploymentPermissions).mockResolvedValue({ permissions: ["deployments.read", "deployments.setup.manage"] });
    vi.mocked(getDeploymentCredentialReferences).mockResolvedValue({ items: [credentialFixture] });
    vi.mocked(getDeploymentSecretStores).mockResolvedValue({ items: [secretStoreFixture] });
    vi.mocked(getDeploymentTiers).mockResolvedValue({ tiers: [] });
    vi.mocked(createDeploymentApplication).mockResolvedValue({ id: "app-new", workspaceId: "workspace-1", name: "My application" });
    vi.mocked(createDeploymentCredentialReference).mockResolvedValue({ ...credentialFixture, id: "credential-new", name: "new-engine API key", reference: "local://engine-credentials/new-engine-api-key" });
    vi.mocked(createDeploymentSecretStore).mockResolvedValue(secretStoreFixture);
    vi.mocked(createDeploymentEnvironment).mockResolvedValue({ id: "env-new", workspaceId: "workspace-1", applicationId: "app-new", name: "Development", tierId: null });
    vi.mocked(registerDeploymentEngine).mockResolvedValue(engineFixture);
  });

  afterEach(() => {
    cleanup();
    workspaceState.selectedWorkspaceId = "workspace-1";
    vi.clearAllMocks();
  });

  it("registers an existing placement with the selected saved credential", async () => {
    renderPage();

    expect(await screen.findByRole("heading", { name: "Connect an engine" })).toBeInTheDocument();
    expect(screen.getByText("Orders / Development")).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Engine name"), "orders-dev");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://orders.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    await waitFor(() => expect(registerDeploymentEngine).toHaveBeenCalledTimes(1));
    expect(registerDeploymentEngine).toHaveBeenCalledWith(
      "workspace-1",
      "env-development",
      expect.objectContaining({
        name: "orders-dev",
        baseUrl: "https://orders.example.com",
        credentialReferenceId: "credential-orders",
        credentialAssignmentStatus: "Assigned"
      })
    );
    expect(await screen.findByRole("heading", { name: "Your engine is connected." })).toBeInTheDocument();
  });

  it("selects a saved credential after delayed credentials load", async () => {
    const delayedCredentials = createDelayedCredentials();
    vi.mocked(getDeploymentCredentialReferences).mockReturnValueOnce(delayedCredentials.promise);

    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "delayed-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://delayed.example.com");
    expect(screen.getByRole("button", { name: "Assign later" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Connect engine →" })).toBeDisabled();

    delayedCredentials.resolve({ items: [credentialFixture] });

    await waitFor(() => expect(screen.getByRole("button", { name: "Saved credential" })).toHaveAttribute("aria-pressed", "true"));
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));
    await waitFor(() => expect(registerDeploymentEngine).toHaveBeenCalledWith("workspace-1", "env-development", expect.objectContaining({ credentialReferenceId: "credential-orders", credentialAssignmentStatus: "Assigned" })));
  });

  it("retains an explicit deferred choice when credentials arrive late", async () => {
    const delayedCredentials = createDelayedCredentials();
    vi.mocked(getDeploymentCredentialReferences).mockReturnValueOnce(delayedCredentials.promise);

    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.click(screen.getByRole("button", { name: "Assign later" }));
    delayedCredentials.resolve({ items: [credentialFixture] });

    await waitFor(() => expect(screen.getByRole("button", { name: "Saved credential" })).not.toBeDisabled());
    expect(screen.getByRole("button", { name: "Assign later" })).toHaveAttribute("aria-pressed", "true");
    expect(screen.getByRole("button", { name: "Saved credential" })).toHaveAttribute("aria-pressed", "false");
  });

  it("explains when the caller cannot manage engine setup", async () => {
    vi.mocked(getDeploymentPermissions).mockResolvedValue({ permissions: ["deployments.read"] });
    renderPage();

    expect(await screen.findByText("Deployment setup permission is required to register a workflow engine.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Engine URL")).not.toBeInTheDocument();
    expect(registerDeploymentEngine).not.toHaveBeenCalled();
  });

  it("does not fall back to another environment for an invalid deep link", async () => {
    renderPage("/admin/engines/connect?environmentId=missing-environment");

    expect(await screen.findByText("The requested engine environment is no longer available in this workspace.")).toBeInTheDocument();
    expect(screen.queryByLabelText("Engine URL")).not.toBeInTheDocument();
  });

  it("keeps local placement validation recoverable after deployment data changes", async () => {
    const { queryClient } = renderPageWithQueryClient();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "stale-placement-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://stale-placement.example.com");
    queryClient.setQueryData(queryKeys.deploymentCockpit("workspace-1"), { ...cockpitFixture, applications: [], engines: [] });

    await waitFor(() => expect(screen.getByText("Choose an application and environment")).toBeInTheDocument());
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("Choose an application and environment before connecting the engine.");
    expect(alert).toHaveTextContent("Your entries are still here. Correct them and try again.");
    expect(alert).not.toHaveTextContent("The request may have reached the server.");
    expect(registerDeploymentEngine).not.toHaveBeenCalled();
  });

  it("creates a local API-key reference when the new credential mode is selected", async () => {
    vi.mocked(getDeploymentSecretStores).mockResolvedValue({ items: [] });
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "new-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://new.example.com");
    const newCredentialButton = screen.getByRole("button", { name: "New API key" });
    await waitFor(() => expect(newCredentialButton).not.toBeDisabled());
    await userEvent.click(newCredentialButton);
    await userEvent.type(screen.getByLabelText("Engine API key"), "secret-value");
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    await waitFor(() => expect(createDeploymentCredentialReference).toHaveBeenCalledTimes(1));
    expect(createDeploymentSecretStore).toHaveBeenCalledWith("workspace-1", expect.objectContaining({
      name: "Workspace encrypted credentials",
      type: "LocalEncryptedDatabase"
    }));
    expect(createDeploymentCredentialReference).toHaveBeenCalledWith("workspace-1", "store-local", expect.objectContaining({
      name: "new-engine API key",
      reference: "local://engine-credentials/new-engine-api-key",
      secretValue: "secret-value"
    }));
    expect(registerDeploymentEngine).toHaveBeenCalledWith("workspace-1", "env-development", expect.objectContaining({
      credentialReferenceId: "credential-new",
      credentialAssignmentStatus: "Assigned"
    }));
  });

  it("redacts the trimmed API key when a credential request echoes it", async () => {
    vi.mocked(createDeploymentCredentialReference).mockRejectedValueOnce(new ApiError("Validation", "The API key secret-value was rejected.", 400));
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "new-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://new.example.com");
    await userEvent.click(screen.getByRole("button", { name: "New API key" }));
    await userEvent.type(screen.getByLabelText("Engine API key"), "  secret-value  ");
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    const alert = await screen.findByRole("alert");
    expect(alert).toHaveTextContent("[redacted]");
    expect(alert).not.toHaveTextContent("secret-value");
    expect(createDeploymentCredentialReference).toHaveBeenCalledWith("workspace-1", "store-local", expect.objectContaining({ secretValue: "secret-value" }));
  });

  it("creates a new application when its name matches an existing application", async () => {
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "new-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://new.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.selectOptions(screen.getByLabelText("Placement mode"), "new");
    const applicationName = screen.getByLabelText("Application name");
    await userEvent.clear(applicationName);
    await userEvent.type(applicationName, "Orders");
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    await waitFor(() => expect(createDeploymentApplication).toHaveBeenCalledWith("workspace-1", { name: "Orders", description: null }));
    expect(createDeploymentEnvironment).toHaveBeenCalledWith("workspace-1", "app-new", expect.objectContaining({ name: "Development" }));
  });

  it("uses the production legacy tier when no active custom tier is available", async () => {
    vi.mocked(getDeploymentCockpit).mockResolvedValue({ ...cockpitFixture, applications: [], engines: [] });
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "new-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://new.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    await waitFor(() => expect(createDeploymentEnvironment).toHaveBeenCalledTimes(1));
    expect(createDeploymentEnvironment).toHaveBeenCalledWith("workspace-1", "app-new", {
      name: "Development",
      tier: "Production"
    });
  });

  it.each([
    ["endpoint credentials", "https://user:secret@example.com"],
    ["query-string URLs", "https://engine.example.com?api_key=hidden"]
  ])("rejects %s before saving", async (_caseName, invalidUrl) => {
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "unsafe-engine");
    const endpoint = screen.getByLabelText("Engine URL");
    await userEvent.type(endpoint, invalidUrl);

    const error = "Use a base URL without credentials, a query, or a fragment.";
    expect(screen.getByText(error)).toBeInTheDocument();
    expect(endpoint).toHaveAttribute("aria-invalid", "true");
    expect(endpoint).toHaveAccessibleDescription(error);
    expect(screen.getByRole("button", { name: "Connect engine →" })).toBeDisabled();
    expect(registerDeploymentEngine).not.toHaveBeenCalled();

    await userEvent.clear(endpoint);
    await userEvent.type(endpoint, "https://engine.example.com");
    expect(endpoint).not.toHaveAttribute("aria-invalid");
    expect(endpoint).toHaveAccessibleDescription("Use the base HTTP or HTTPS URL for the engine.");
  });

  it("keeps a created application across an environment failure so retry does not duplicate it", async () => {
    vi.mocked(getDeploymentCockpit).mockResolvedValue({ ...cockpitFixture, applications: [], engines: [] });
    vi.mocked(getDeploymentCredentialReferences).mockResolvedValue({ items: [] });
    vi.mocked(createDeploymentEnvironment)
      .mockRejectedValueOnce(new ApiError("Validation", "Environment creation failed", 400))
      .mockResolvedValueOnce({ id: "env-new", workspaceId: "workspace-1", applicationId: "app-new", name: "Development", tierId: null });
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "new-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://new.example.com");
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Environment creation failed");
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));
    await screen.findByRole("heading", { name: "Your engine is connected." });

    expect(createDeploymentApplication).toHaveBeenCalledTimes(1);
    expect(createDeploymentEnvironment).toHaveBeenCalledTimes(2);
    expect(registerDeploymentEngine).toHaveBeenCalledWith("workspace-1", "env-new", expect.objectContaining({ name: "new-engine" }));
  });

  it("keeps confirmed placement and credential writes across an engine retry", async () => {
    vi.mocked(getDeploymentCockpit).mockResolvedValue({ ...cockpitFixture, applications: [], engines: [] });
    vi.mocked(getDeploymentCredentialReferences).mockResolvedValue({ items: [] });
    vi.mocked(registerDeploymentEngine)
      .mockRejectedValueOnce(new ApiError("Validation", "Engine registration failed", 400))
      .mockResolvedValueOnce(engineFixture);
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "new-engine");
    await userEvent.type(screen.getByLabelText("Engine URL"), "https://new.example.com");
    await userEvent.click(screen.getByRole("button", { name: "New API key" }));
    await userEvent.type(screen.getByLabelText("Engine API key"), "secret-value");
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Engine registration failed");
    expect(screen.queryByLabelText("Engine API key")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Saved credential" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Connect engine →" })).not.toBeDisabled();

    await userEvent.click(screen.getByRole("button", { name: "Connect engine →" }));
    expect(await screen.findByRole("heading", { name: "Your engine is connected." })).toBeInTheDocument();
    expect(createDeploymentApplication).toHaveBeenCalledTimes(1);
    expect(createDeploymentEnvironment).toHaveBeenCalledTimes(1);
    expect(createDeploymentCredentialReference).toHaveBeenCalledTimes(1);
    expect(registerDeploymentEngine).toHaveBeenCalledTimes(2);
  });

  it("blocks new placement while workspace tiers are unavailable", async () => {
    vi.mocked(getDeploymentTiers).mockRejectedValueOnce(new Error("tier service unavailable"));
    renderPage();

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.click(screen.getByRole("button", { name: "Change" }));
    await userEvent.selectOptions(screen.getByLabelText("Placement mode"), "new");

    expect(await screen.findByText(/Workspace tiers could not load/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Connect engine →" })).toBeDisabled();
    expect(createDeploymentEnvironment).not.toHaveBeenCalled();
  });

  it("shows a permissions loading error instead of treating it as a denial", async () => {
    vi.mocked(getDeploymentPermissions).mockRejectedValueOnce(new Error("permission service unavailable"));
    renderPage();

    expect(await screen.findByText("Deployment permissions could not load")).toBeInTheDocument();
    expect(screen.queryByLabelText("Engine URL")).not.toBeInTheDocument();
  });

  it("resets form state when the selected workspace changes", async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const view = render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/admin/engines/connect"]}>
          <ConnectEnginePage />
        </MemoryRouter>
      </QueryClientProvider>
    );

    await screen.findByRole("heading", { name: "Connect an engine" });
    await userEvent.type(screen.getByLabelText("Engine name"), "workspace-one-engine");
    workspaceState.selectedWorkspaceId = "workspace-2";
    view.rerender(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={["/admin/engines/connect"]}>
          <ConnectEnginePage />
        </MemoryRouter>
      </QueryClientProvider>
    );

    await waitFor(() => expect(screen.getByLabelText("Engine name")).toHaveValue(""));
  });
});

function renderPage(initialEntry = "/admin/engines/connect") {
  return renderPageWithQueryClient(initialEntry).view;
}

function renderPageWithQueryClient(initialEntry = "/admin/engines/connect") {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <ConnectEnginePage />
      </MemoryRouter>
    </QueryClientProvider>
  );
  return { queryClient, view };
}

function createDelayedCredentials() {
  let resolve!: (value: { items: WorkspaceDeploymentCredentialReference[] }) => void;
  const promise = new Promise<{ items: WorkspaceDeploymentCredentialReference[] }>((promiseResolve) => {
    resolve = promiseResolve;
  });
  return { promise, resolve };
}
