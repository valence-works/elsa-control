import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import { AuthProvider } from "@/lib/auth/AuthProvider";
import { claimExpiredHandoffRetry, ManagedElsaInstancesPage } from "@/features/managed-elsa/ManagedElsaInstancesPage";
import { managedElsaHandoffTokenType, type ManagedElsaInstance } from "@/features/managed-elsa/managedElsaModels";

const organizationId = "00000000-0000-0000-0000-000000000001";
const workspaceId = "00000000-0000-0000-0000-000000000010";
const healthyInstanceId = "00000000-0000-0000-0000-000000000101";
const unavailableInstanceId = "00000000-0000-0000-0000-000000000102";
const unboundInstanceId = "00000000-0000-0000-0000-000000000103";
const callbackUri = "https://managed.example.test/managed-elsa/handoff/callback";
const state = "state-value-that-is-long-enough";
const codeChallenge = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";
const azureProvisioningProvider = { id: "azure", displayName: "Azure" };

describe("ManagedElsaInstancesPage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    window.sessionStorage.clear();
  });

  it("shows Open only for a healthy instance with a current binding", async () => {
    installFetch({
      instances: [
        instanceFixture(),
        instanceFixture({
          instanceId: unavailableInstanceId,
          name: "Deleting runtime",
          slug: "deleting-runtime",
          desiredLifecycle: "Deleting",
          observedLifecycle: "Deleting",
          health: "Unknown",
          canOpen: false,
          audience: null,
          redirectUri: null,
          unavailableReason: "This instance is not currently available."
        }),
        instanceFixture({
          instanceId: unboundInstanceId,
          name: "Unbound runtime",
          slug: "unbound-runtime",
          canOpen: false,
          audience: null,
          redirectUri: null,
          unavailableReason: "The current instance binding is unavailable."
        })
      ]
    });

    renderPage();

    expect(await screen.findByRole("button", { name: "Open" })).toBeInTheDocument();
    expect(screen.getAllByText("Unavailable")).toHaveLength(2);
    expect(screen.getAllByRole("button", { name: "Open" })).toHaveLength(1);
    expect(screen.queryByText(/urn:elsa:instance/)).not.toBeInTheDocument();
  });

  it("issues for the runtime challenge and posts only code and state to the exact callback", async () => {
    const issue = {
      token: "signed-handoff-token",
      tokenType: managedElsaHandoffTokenType,
      audience: "urn:elsa:instance:00000000-0000-0000-0000-000000000101",
      redirectUri: callbackUri,
      issuedAt: "2026-08-31T12:00:00Z",
      expiresAt: "2026-08-31T12:01:00Z"
    };
    const fetchMock = installFetch({ instances: [instanceFixture()], issue });
    const submit = vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(() => undefined);

    renderPage(`?instance_id=${healthyInstanceId}&state=${state}&code_challenge=${codeChallenge}`);

    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    const issueCall = fetchMock.mock.calls.find(([input]) => String(input).endsWith("/api/managed-elsa/handoff/issue"));
    const issueBody = JSON.parse(String(issueCall?.[1]?.body));
    expect(issueBody).toEqual({
      organizationId,
      instanceId: healthyInstanceId,
      audience: instanceFixture().audience,
      redirectUri: callbackUri,
      codeChallenge
    });

    const form = document.querySelector(`form[action="${callbackUri}"]`);
    expect(form).toHaveAttribute("method", "post");
    expect(form).toHaveAttribute("action", callbackUri);
    expect(form).not.toHaveAttribute("action", expect.stringContaining(issue.token));
    expect(form?.querySelector("[name=code]")).toHaveValue(issue.token);
    expect(form?.querySelector("[name=state]")).toHaveValue(state);
    expect(form?.querySelector("[name=code_verifier]")).toBeNull();
  });

  it("issues from camelCase runtime continuation and never renders Sign in or bounces the route", async () => {
    const issue = {
      token: "signed-handoff-token",
      tokenType: managedElsaHandoffTokenType,
      audience: "urn:elsa:instance:00000000-0000-0000-0000-000000000101",
      redirectUri: callbackUri,
      issuedAt: "2026-08-31T12:00:00Z",
      expiresAt: "2026-08-31T12:01:00Z"
    };
    installFetch({ instances: [instanceFixture()], issue });
    const submit = vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(() => undefined);

    renderPage(`?instanceId=${healthyInstanceId}&state=${state}&codeChallenge=${codeChallenge}`);

    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    const form = document.querySelector(`form[action="${callbackUri}"]`);
    expect(form).toHaveAttribute("method", "post");
    expect(form?.querySelector("[name=code]")).toHaveValue(issue.token);
    expect(form?.querySelector("[name=state]")).toHaveValue(state);
    expect(screen.queryByRole("heading", { name: /sign in/i })).not.toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Managed Elsa" })).toBeInTheDocument();
    expect(window.location.pathname).not.toBe("/login");
  });

  it("does not blindly retry an issue request after a 401", async () => {
    const fetchMock = installFetch({
      instances: [instanceFixture()],
      issueResponses: [Response.json({ title: "expired Control session" }, { status: 401 })]
    });

    renderPage(`?instance_id=${healthyInstanceId}&state=${state}&code_challenge=${codeChallenge}`);

    expect(await screen.findByRole("alert")).toHaveTextContent("Your Control session could not authorize this handoff. Sign in again and retry.");
    expect(fetchMock.mock.calls.filter(([input]) => String(input).endsWith("/api/managed-elsa/handoff/issue"))).toHaveLength(1);
  });

  it("claims the expired handoff restart only once per browser session", () => {
    expect(claimExpiredHandoffRetry(healthyInstanceId)).toBe(true);
    expect(claimExpiredHandoffRetry(healthyInstanceId)).toBe(false);
    expect(window.sessionStorage.getItem(`managed-elsa-handoff-retry:${healthyInstanceId}`)).toBe("1");
  });

  it("scrubs continuation parameters before fetching the authenticated instance list", async () => {
    const replaceState = vi.spyOn(window.history, "replaceState");
    let listFetchedBeforeScrub = false;
    let providersFetchedBeforeScrub = false;
    let scrubbedUrlAtListFetch = "";
    installFetch({
      instances: [instanceFixture()],
      onProvisioningProvidersRequest: () => {
        providersFetchedBeforeScrub = replaceState.mock.calls.length === 0;
      },
      onInstancesRequest: () => {
        listFetchedBeforeScrub = replaceState.mock.calls.length === 0;
        const lastReplace = replaceState.mock.calls[replaceState.mock.calls.length - 1];
        scrubbedUrlAtListFetch = String(lastReplace?.[2] ?? "");
      }
    });

    renderPage(`?instance_id=${healthyInstanceId}&state=${state}&code_challenge=${codeChallenge}`);

    expect(await screen.findByRole("button", { name: "Open" })).toBeInTheDocument();
    expect(replaceState).toHaveBeenCalled();
    expect(listFetchedBeforeScrub).toBe(false);
    expect(providersFetchedBeforeScrub).toBe(false);
    expect(scrubbedUrlAtListFetch).not.toContain("state=");
    expect(scrubbedUrlAtListFetch).not.toContain("code_challenge=");
  });

  it("ignores unrelated state parameters without an instance identifier", async () => {
    const replaceState = vi.spyOn(window.history, "replaceState");
    installFetch({ instances: [instanceFixture()] });

    renderPage(`?state=${state}`);

    expect(await screen.findByRole("button", { name: "Open" })).toBeInTheDocument();
    expect(replaceState).not.toHaveBeenCalled();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("fails closed when the issue response binding differs from the selected instance", async () => {
    const submit = vi.spyOn(HTMLFormElement.prototype, "submit").mockImplementation(() => undefined);
    installFetch({
      instances: [instanceFixture()],
      issue: {
        token: "signed-handoff-token",
        tokenType: managedElsaHandoffTokenType,
        audience: "urn:elsa:instance:wrong",
        redirectUri: callbackUri,
        issuedAt: "2026-08-31T12:00:00Z",
        expiresAt: "2026-08-31T12:01:00Z"
      }
    });

    renderPage(`?instance_id=${healthyInstanceId}&state=${state}&code_challenge=${codeChallenge}`);

    expect(await screen.findByRole("alert")).toHaveTextContent("This managed instance is no longer available. Refresh the page and try again.");
    expect(submit).not.toHaveBeenCalled();
  });

  it("maps a runtime 403 continuation to safe actionable copy", async () => {
    installFetch({ instances: [instanceFixture()] });

    renderPage(`?instance_id=${healthyInstanceId}&handoff_status=403`);

    expect(await screen.findByRole("alert")).toHaveTextContent("This managed instance is no longer available to your account.");
    expect(screen.queryByText(/signed-handoff-token|code_verifier|state-value/)).not.toBeInTheDocument();
  });

  it("renders the shared provisioning link after Azure provider and setup permission checks succeed", async () => {
    installFetch({ instances: [], provisioningProviders: [azureProvisioningProvider] });

    renderPage();

    expect(await screen.findByRole("link", { name: "Provision engine" })).toHaveAttribute("href", "/admin/engines/provision");
  });

  it("hides provisioning when discovery has no recognized provider", async () => {
    installFetch({ instances: [], provisioningProviders: [{ id: "unknown-provider", displayName: "Unknown" }] });

    renderPage();

    expect(await screen.findByText("No managed Elsa instances")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Provision engine" })).not.toBeInTheDocument();
  });

  it("fails closed when provider discovery errors", async () => {
    installFetch({
      instances: [],
      provisioningProvidersResponse: Response.json({ title: "Unavailable" }, { status: 503 })
    });

    renderPage();

    expect(await screen.findByText("No managed Elsa instances")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Provision engine" })).not.toBeInTheDocument();
  });

  it("treats an older discovery endpoint as no available providers", async () => {
    installFetch({ instances: [] });

    renderPage();

    expect(await screen.findByText("No managed Elsa instances")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Provision engine" })).not.toBeInTheDocument();
  });

  it("hides provisioning when setup permission is missing", async () => {
    installFetch({ instances: [], provisioningProviders: [azureProvisioningProvider], deploymentPermissions: [] });

    renderPage();

    expect(await screen.findByText("No managed Elsa instances")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Provision engine" })).not.toBeInTheDocument();
  });

  it("loads every canonical instance page", async () => {
    const secondInstance = instanceFixture({
      instanceId: unavailableInstanceId,
      name: "Second page runtime",
      slug: "second-page-runtime"
    });
    const fetchMock = installFetch({
      instancePages: [[instanceFixture()], [secondInstance]]
    });

    renderPage();

    expect(await screen.findByText("Claims runtime")).toBeInTheDocument();
    expect(screen.getByText("Second page runtime")).toBeInTheDocument();
    expect(fetchMock.mock.calls.some(([input]) => String(input).includes("instances?page=2&pageSize=100"))).toBe(true);
  });

  it("fails safely when canonical pagination cannot terminate", async () => {
    installFetch({
      instancePageResponses: [Response.json({
        items: [instanceFixture()], page: 1, pageSize: 100, totalCount: 100, hasMore: true
      })]
    });

    renderPage();

    expect(await screen.findByText("Managed instances could not load")).toBeInTheDocument();
  });

});

function renderPage(search = "") {
  render(
    <TestQueryProvider>
      <MemoryRouter initialEntries={[`/admin/runtimes${search}`]}>
        <AuthProvider>
          <WorkspaceContextProvider>
            <ManagedElsaInstancesPage />
          </WorkspaceContextProvider>
        </AuthProvider>
      </MemoryRouter>
    </TestQueryProvider>
  );
}

function installFetch({
  instances = [],
  instancePages,
  instancePageResponses = [],
  issue,
  issueResponses = [],
  onInstancesRequest,
  onProvisioningProvidersRequest,
  provisioningProvidersResponse,
  provisioningProviders,
  deploymentPermissions,
}: {
  instances?: ManagedElsaInstance[];
  instancePages?: ManagedElsaInstance[][];
  instancePageResponses?: Response[];
  issue?: Record<string, string>;
  issueResponses?: Response[];
  onInstancesRequest?: () => void;
  onProvisioningProvidersRequest?: () => void;
  provisioningProvidersResponse?: Response;
  provisioningProviders?: Array<{ id: string; displayName: string }>;
  deploymentPermissions?: string[];
}) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    if (url.endsWith("/api/auth/session"))
      return Response.json({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com" });
    if (url.endsWith("/api/me/organizations"))
      return Response.json({
        account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
        organizations: [{ id: organizationId, name: "Acme Corp", role: "Owner" }],
        workspaces: [{ id: workspaceId, name: "Acme Insurance", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" }]
      });
    if (url.endsWith(`/api/workspaces/${workspaceId}/engine-provisioning/providers`)) {
      onProvisioningProvidersRequest?.();
      if (provisioningProvidersResponse)
        return provisioningProvidersResponse;
      if (provisioningProviders)
        return Response.json({ providers: provisioningProviders });
      return Response.json({ title: "Not found" }, { status: 404 });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/permissions`))
      return Response.json({ permissions: deploymentPermissions ?? ["deployments.setup.manage"] });
    if (url.includes(`/api/workspaces/${workspaceId}/instances?`)) {
      onInstancesRequest?.();
      const response = instancePageResponses.shift();
      if (response)
        return response;
      const page = Number(new URL(url, "https://console.test").searchParams.get("page") ?? "1");
      const pages = instancePages ?? [instances];
      const items = pages[page - 1] ?? [];
      return Response.json({
        items,
        page,
        pageSize: 100,
        totalCount: pages.length > 1
          ? ((pages.length - 1) * 100) + (pages.at(-1)?.length ?? 0)
          : items.length,
        hasMore: page < pages.length
      });
    }
    if (url.endsWith("/api/managed-elsa/handoff/issue")) {
      const response = issueResponses.shift();
      if (response)
        return response;
      return Response.json(issue ?? {
        token: "signed-handoff-token",
        tokenType: managedElsaHandoffTokenType,
        audience: instanceFixture().audience,
        redirectUri: callbackUri,
        issuedAt: "2026-08-31T12:00:00Z",
        expiresAt: "2026-08-31T12:01:00Z"
      });
    }
    return Response.json({ title: "Not found" }, { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}


function instanceFixture(overrides: Partial<ManagedElsaInstance> = {}): ManagedElsaInstance {
  return {
    organizationId,
    instanceId: healthyInstanceId,
    name: "Claims runtime",
    slug: "claims-runtime",
    desiredLifecycle: "Running" as const,
    observedLifecycle: "Ready" as const,
    health: "Healthy" as const,
    canOpen: true,
    audience: "urn:elsa:instance:00000000-0000-0000-0000-000000000101",
    redirectUri: callbackUri,
    unavailableReason: null,
    ...overrides
  };
}

function TestQueryProvider({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false },
      mutations: { retry: false }
    }
  });

  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
