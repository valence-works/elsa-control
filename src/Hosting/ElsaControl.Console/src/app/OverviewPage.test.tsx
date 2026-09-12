import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OverviewPage } from "@/app/OverviewPage";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import { AuthProvider } from "@/lib/auth/AuthProvider";
import type { DeploymentCockpit, DeploymentHealth, WorkflowEngineRegistration } from "@/features/deployments/deploymentModels";

describe("OverviewPage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage?.clear();
  });

  it("renders the live inventory with a selected engine inspector and real detail links", async () => {
    renderOverview();

    expect(await screen.findByRole("heading", { name: "Acme Insurance" })).toBeInTheDocument();
    expect(screen.getByText("Engine inventory")).toBeInTheDocument();
    expect(screen.getByText("03 / 03 ENGINES")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Claims Dev" })).toBeInTheDocument();
    expect(screen.getAllByText("Healthy", { exact: true })).not.toHaveLength(0);
    expect(screen.getByRole("link", { name: "View engine" })).toHaveAttribute(
      "href",
      "/admin/deployments/applications/claims/environments/claims-dev/engines/claims-dev-engine"
    );
    expect(screen.getByRole("link", { name: "View application" })).toHaveAttribute(
      "href",
      "/admin/deployments/applications/claims"
    );
    expect(screen.getByRole("link", { name: /Connect engine/ })).toHaveAttribute("href", "/admin/engines/connect");
  });

  it("keeps the inspector on a visible engine while searching and filtering the inventory", async () => {
    const user = userEvent.setup();
    renderOverview();

    await screen.findByRole("heading", { name: "Acme Insurance" });
    await user.click(screen.getByRole("button", { name: "Inspect Claims Production" }));
    expect(screen.getByRole("heading", { name: "Claims Production" })).toBeInTheDocument();
    expect(screen.getAllByText("Unreachable", { exact: true })).not.toHaveLength(0);

    const search = screen.getByRole("searchbox", { name: "Search engines" });
    await user.type(search, "policies");
    expect(screen.getByRole("heading", { name: "Policies Dev" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Claims Production" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Inspect Policies Dev" })).toHaveAttribute("aria-pressed", "true");

    await user.clear(search);
    await user.selectOptions(screen.getByRole("combobox", { name: "Filter engines" }), "attention");
    expect(screen.getByRole("button", { name: "Inspect Claims Production" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Inspect Claims Dev" })).not.toBeInTheDocument();
  });

  it("shows Unknown when the API does not report engine health", async () => {
    const cockpit = deploymentCockpitFixture();
    const unknownEngine = { ...cockpit.engines[0], health: undefined } as unknown as WorkflowEngineRegistration;
    renderOverview({ cockpit: { ...cockpit, engines: [unknownEngine] } });

    expect(await screen.findByRole("heading", { name: "Claims Dev" })).toBeInTheDocument();
    expect(screen.getAllByText("Unknown", { exact: true })).toHaveLength(2);
    expect(screen.getByText("Certificate", { exact: false })).toBeInTheDocument();
  });

  it("handles a forbidden cockpit without rendering fabricated overview data", async () => {
    renderOverview({ cockpitResponse: Response.json({ title: "Forbidden" }, { status: 403 }) });

    expect(await screen.findByRole("heading", { name: "Workspace access required" })).toBeInTheDocument();
    expect(screen.queryByText("Engine inventory")).not.toBeInTheDocument();
    expect(screen.queryByText("Healthy", { exact: true })).not.toBeInTheDocument();
  });

  it("keeps an empty fleet focused on connecting the first engine", async () => {
    renderOverview({ cockpit: { ...deploymentCockpitFixture(), applications: [], engines: [] } });

    expect(await screen.findByRole("heading", { name: "Connect your first engine" })).toBeInTheDocument();
    expect(screen.getAllByRole("link", { name: /Connect engine/ })).toHaveLength(1);
    expect(screen.getByRole("link", { name: /Connect engine/ })).toHaveAttribute("href", "/admin/engines/connect");
    expect(screen.queryByText("Engine inventory")).not.toBeInTheDocument();
    expect(screen.queryByText("Healthy", { exact: true })).not.toBeInTheDocument();
  });
});

type RenderOptions = {
  cockpit?: DeploymentCockpit;
  cockpitResponse?: Response;
};

function renderOverview(options: RenderOptions = {}) {
  installLocalStorageStub();
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = input instanceof Request ? input.url : input.toString();
    if (url.endsWith("/api/auth/session")) {
      return Response.json({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" });
    }
    if (url.endsWith("/api/me/organizations")) return Response.json(workspaceContextFixture());
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/cockpit`)) return options.cockpitResponse ?? Response.json(options.cockpit ?? deploymentCockpitFixture());
    return Response.json({ title: "Not found" }, { status: 404 });
  }));

  render(
    <TestQueryProvider>
      <MemoryRouter>
        <AuthProvider>
          <WorkspaceContextProvider>
            <OverviewPage />
          </WorkspaceContextProvider>
        </AuthProvider>
      </MemoryRouter>
    </TestQueryProvider>
  );
}

function workspaceContextFixture() {
  return {
    account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
    organizations: [{ id: organizationId, name: "Acme Corp", role: "Owner" }],
    workspaces: [{ id: workspaceId, name: "Acme Insurance", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" }]
  };
}

function deploymentCockpitFixture(): DeploymentCockpit {
  return {
    applications: [
      {
        id: "claims",
        name: "Claims",
        workspaceName: "Acme Insurance",
        environments: [
          {
            id: "claims-dev",
            name: "Dev",
            tier: "Dev",
            tierName: "Development",
            health: "Healthy",
            desiredRevision: { id: "dev-revision", revision: 4, commit: "abc1234", label: "Dev baseline", authoredAt: "2026-09-12T08:30:00Z" },
            deployedRevision: 4,
            deploymentStatus: "Succeeded",
            driftStatus: "InSync",
            engineIds: ["claims-dev-engine"]
          },
          {
            id: "claims-prod",
            name: "Prod",
            tier: "Production",
            tierName: "Production",
            health: "Unreachable",
            desiredRevision: { id: "prod-revision", revision: 5, commit: "def5678", label: "Production baseline", authoredAt: "2026-09-12T08:45:00Z" },
            deployedRevision: 4,
            deploymentStatus: "Blocked",
            driftStatus: "Unknown",
            engineIds: ["claims-prod-engine"]
          }
        ]
      },
      {
        id: "policies",
        name: "Policies",
        workspaceName: "Acme Insurance",
        environments: [{ id: "policies-dev", name: "Dev", tier: "Dev", tierName: "Development", health: "Healthy", desiredRevision: { id: "policies-revision", revision: 2, commit: "fedcba9", label: "Policy baseline", authoredAt: "2026-09-12T08:50:00Z" }, deployedRevision: 2, deploymentStatus: "Succeeded", driftStatus: "InSync", engineIds: ["policies-dev-engine"] }]
      }
    ],
    engines: [
      engine("claims-dev-engine", "claims-dev", "Claims Dev", "Healthy"),
      engine("claims-prod-engine", "claims-prod", "Claims Production", "Unreachable"),
      engine("policies-dev-engine", "policies-dev", "Policies Dev", "Healthy")
    ],
    comparisons: [],
    observabilityBindings: [],
    history: [],
    driftReport: [],
    assistantPlans: []
  };
}

function engine(id: string, environmentId: string, name: string, health: DeploymentHealth): WorkflowEngineRegistration {
  return {
    id,
    name,
    environmentId,
    endpoint: { baseUrl: `https://${id}.example.test`, region: "eu-west-1", version: "3.7.0", certificateStatus: "Trusted" },
    credentialReference: { provider: "Local", reference: `credential://${id}`, verificationStatus: "Verified", lastVerifiedAt: "2026-09-12T08:00:00Z" },
    credentialAssignmentStatus: "Assigned",
    health,
    lastHeartbeatAt: "2026-09-12T08:58:00Z",
    lastVerificationAt: "2026-09-12T08:58:00Z",
    verificationMessage: health === "Healthy" ? "Health verification passed." : "Endpoint did not respond before verification timed out.",
    capabilities: [],
    controls: [],
    hostingProvider: "Kubernetes"
  };
}

function installLocalStorageStub() {
  const storage = new Map<string, string>();
  Object.defineProperty(window, "localStorage", { configurable: true, value: { getItem: (key: string) => storage.get(key) ?? null, setItem: (key: string, value: string) => storage.set(key, value), removeItem: (key: string) => storage.delete(key), clear: () => storage.clear() } });
}

function TestQueryProvider({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

const organizationId = "00000000-0000-0000-0000-000000000001";
const workspaceId = "00000000-0000-0000-0000-000000000010";
