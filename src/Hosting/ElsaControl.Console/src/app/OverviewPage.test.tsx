import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { OverviewPage } from "@/app/OverviewPage";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import { AuthProvider } from "@/lib/auth/AuthProvider";

describe("OverviewPage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage?.clear();
  });

  it("surfaces live engine topology and links engine rows to their detail route", async () => {
    renderOverview();

    expect(await screen.findByRole("heading", { name: "Acme Insurance" })).toBeInTheDocument();
    expect(screen.getByText("Engine fleet")).toBeInTheDocument();
    expect(screen.getByText("2/3")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Artifacts 2 registered/i })).toBeInTheDocument();
    expect(screen.queryByText("Package review")).not.toBeInTheDocument();
    expect(screen.getByText("Claims")).toBeInTheDocument();
    expect(screen.getByText("Claims Production")).toBeInTheDocument();

    expect(screen.getByRole("link", { name: "Open Claims Dev" })).toHaveAttribute(
      "href",
      "/admin/deployments/applications/claims/environments/claims-dev/engines/claims-dev-engine"
    );
    expect(screen.getAllByRole("link", { name: /Connect engine/i }).at(-1)).toHaveAttribute("href", "/admin/engines/connect");
    expect(screen.getByText(/Claims Production: Unreachable/i)).toBeInTheDocument();
  });

  it("filters the real fleet by search and health state", async () => {
    const user = userEvent.setup();
    renderOverview();

    const search = await screen.findByRole("textbox", { name: "Search engines" });
    await user.type(search, "prod");

    expect(screen.getByText("Claims Production")).toBeInTheDocument();
    expect(screen.queryByText("Claims Dev")).not.toBeInTheDocument();

    await user.clear(search);
    await user.click(screen.getByRole("button", { name: /Attention 1/i }));
    expect(screen.getByText("Claims Production")).toBeInTheDocument();
    expect(screen.queryByText("Claims Dev")).not.toBeInTheDocument();
  });

  it("handles a forbidden cockpit without rendering fabricated overview data", async () => {
    renderOverview({ cockpitResponse: Response.json({ title: "Forbidden" }, { status: 403 }) });

    expect(await screen.findByRole("heading", { name: "Workspace access required" })).toBeInTheDocument();
    expect(screen.queryByText("Engine fleet")).not.toBeInTheDocument();
    expect(screen.queryByText("0/0")).not.toBeInTheDocument();
  });

  it("shows the registered empty state when the workspace has no applications", async () => {
    renderOverview({ cockpit: { ...deploymentCockpitFixture(), applications: [], engines: [] } });

    expect(await screen.findByRole("heading", { name: "Connect your first engine" })).toBeInTheDocument();
    expect(screen.getByText("0/0")).toBeInTheDocument();
    expect(screen.getAllByRole("link", { name: /Connect engine/i }).at(-1)).toHaveAttribute("href", "/admin/engines/connect");
    expect(screen.queryByRole("heading", { name: "Attention" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Applications and environments" })).not.toBeInTheDocument();
  });

  it("keeps an application without environments in a neutral state", async () => {
    const cockpit = deploymentCockpitFixture();
    renderOverview({
      cockpit: {
        ...cockpit,
        applications: [...cockpit.applications, { id: "billing", name: "Billing", workspaceName: "Acme Insurance", environments: [] }]
      }
    });

    const billing = await screen.findByRole("link", { name: /Billing/ });
    expect(within(billing).getByText("No environments")).toBeInTheDocument();
    expect(within(billing).queryByLabelText("Healthy")).not.toBeInTheDocument();
  });
});

type RenderOptions = {
  cockpit?: ReturnType<typeof deploymentCockpitFixture>;
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
    if (url.endsWith(`/api/workspaces/${workspaceId}/artifacts`)) return Response.json({ items: [{ id: "artifact-1" }, { id: "artifact-2" }] });
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/cockpit`)) return options.cockpitResponse ?? Response.json(options.cockpit ?? deploymentCockpitFixture());
    if (url.endsWith("/api/admin/packages")) return Response.json([packageItem("Elsa.Workflows", "Pending"), packageItem("Elsa.Http", "Approved"), packageItem("Elsa.Timers", "Pending")]);
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

function packageItem(packageId: string, approvalStatus: "Pending" | "Approved") {
  return {
    packageId,
    approved: approvalStatus === "Approved",
    listed: true,
    latestVersion: "1.0.0",
    approvalStatus,
    validationStatus: "Valid",
    versions: [{ version: "1.0.0", approvalStatus, validationStatus: "Valid", isListed: true, suspiciousChangeDetected: false }]
  };
}

function workspaceContextFixture() {
  return {
    account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
    organizations: [{ id: organizationId, name: "Acme Corp", role: "Owner" }],
    workspaces: [{ id: workspaceId, name: "Acme Insurance", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" }]
  };
}

function deploymentCockpitFixture() {
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

function engine(id: string, environmentId: string, name: string, health: "Healthy" | "Unreachable") {
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
