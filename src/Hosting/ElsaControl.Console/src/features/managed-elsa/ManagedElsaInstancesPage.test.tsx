import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
    let scrubbedUrlAtListFetch = "";
    installFetch({
      instances: [instanceFixture()],
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

  it("creates from arbitrary governed release data with an idempotency key", async () => {
    const fetchMock = installFetch({ instances: [] });
    const user = userEvent.setup();
    renderPage();

    await user.type(await screen.findByLabelText("Instance name"), "Future Elsa");
    await user.selectOptions(screen.getByLabelText("Elsa release and topology"), "1");
    await user.click(screen.getByRole("button", { name: "Create instance" }));

    expect(await screen.findByText("Provisioning status: Succeeded")).toBeInTheDocument();
    const createCall = fetchMock.mock.calls.find(([input, init]) =>
      String(input).endsWith(`/api/workspaces/${workspaceId}/instances`) && init?.method === "POST");
    const idempotencyKey = new Headers(createCall?.[1]?.headers).get("Idempotency-Key");
    expect(idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
    expect(window.sessionStorage.getItem(`managed-elsa-idempotency:${workspaceId}`)).not.toBe(idempotencyKey);
    const body = JSON.parse(String(createCall?.[1]?.body));
    expect(body).toMatchObject({
      name: "Future Elsa",
      slug: "future-elsa",
      intent: {
        release: { distributionId: "future-runtime", releaseLine: "5.0", requestedVersion: "5.0.1" },
        application: { topologyId: "combined" },
        placement: { regionCode: "westeurope", isolationProfile: "dedicated" }
      }
    });
    expect(body.intent.release).not.toHaveProperty("previewManifestDigest");
  });

  it("requires explicit Preview consent and sends only the selected immutable digest", async () => {
    const preview = { ...releaseFixture("future-runtime", "5.0", "5.0.0-preview.1"), manifestDigest: `sha256:${"a".repeat(64)}` };
    const fetchMock = installFetch({ instances: [], releaseOptions: [], previewReleases: [preview] });
    const user = userEvent.setup();
    renderPage();

    await user.type(await screen.findByLabelText("Instance name"), "Preview Elsa");
    expect(screen.getByRole("option", { name: /Preview/ })).toBeInTheDocument();
    const consent = screen.getByRole("checkbox", { name: /I agree to use this Preview release/ });
    expect(consent).not.toBeChecked();
    expect(screen.getByRole("button", { name: "Create instance" })).toBeDisabled();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === "POST")).toBe(false);

    await user.click(consent);
    await user.click(screen.getByRole("button", { name: "Create instance" }));
    expect(await screen.findByText("Provisioning status: Succeeded")).toBeInTheDocument();
    const call = fetchMock.mock.calls.find(([, init]) => init?.method === "POST");
    expect(JSON.parse(String(call?.[1]?.body)).intent.release.previewManifestDigest).toBe(preview.manifestDigest);
  });

  it("clears Preview consent when release selection changes", async () => {
    installFetch({ previewReleases: [{ ...releaseFixture("future-runtime", "5.0", "5.0.0-preview.1"), manifestDigest: `sha256:${"b".repeat(64)}` }] });
    const user = userEvent.setup();
    renderPage();
    await user.type(await screen.findByLabelText("Instance name"), "Preview Elsa");
    const selection = screen.getByLabelText("Elsa release and topology");
    await user.selectOptions(selection, "2");
    await user.click(screen.getByRole("checkbox"));
    expect(screen.getByRole("button", { name: "Create instance" })).toBeEnabled();
    await user.selectOptions(selection, "0");
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    await user.selectOptions(selection, "2");
    expect(screen.getByRole("checkbox")).not.toBeChecked();
    expect(screen.getByRole("button", { name: "Create instance" })).toBeDisabled();
  });

  it("maps create validation codes to accurate safe messages", async () => {
    installFetch({
      instances: [],
      createResponse: Response.json({
        title: "Managed hosting is not enabled for this organization.",
        status: 422,
        code: "instance.entitlement-required"
      }, { status: 422 })
    });
    const user = userEvent.setup();
    renderPage();

    await user.type(await screen.findByLabelText("Instance name"), "Unavailable Elsa");
    await user.click(screen.getByRole("button", { name: "Create instance" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Managed hosting is not enabled for this organization.");
  });

  it("resumes durable provisioning status after a browser refresh", async () => {
    const completedIdempotencyKey = "00000000-0000-0000-0000-000000000301";
    window.sessionStorage.setItem(`managed-elsa-idempotency:${workspaceId}`, completedIdempotencyKey);
    window.sessionStorage.setItem(`managed-elsa-operation:${workspaceId}`, JSON.stringify({
      instanceId: healthyInstanceId,
      operationId: "00000000-0000-0000-0000-000000000201"
    }));
    installFetch({ instances: [] });

    renderPage();

    expect(await screen.findByText("Provisioning status: Succeeded")).toBeInTheDocument();
    expect(window.sessionStorage.getItem(`managed-elsa-operation:${workspaceId}`)).toBeNull();
    expect(window.sessionStorage.getItem(`managed-elsa-idempotency:${workspaceId}`)).not.toBe(completedIdempotencyKey);
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

  it("keeps durable progress state and retries a failed status refresh", async () => {
    window.sessionStorage.setItem(`managed-elsa-operation:${workspaceId}`, JSON.stringify({
      instanceId: healthyInstanceId,
      operationId: "00000000-0000-0000-0000-000000000201"
    }));
    installFetch({
      instances: [],
      operationResponses: [Response.json({ title: "Unavailable" }, { status: 503 })]
    });
    const user = userEvent.setup();

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent("Provisioning status could not be refreshed.");
    expect(window.sessionStorage.getItem(`managed-elsa-operation:${workspaceId}`)).not.toBeNull();
    await user.click(screen.getByRole("button", { name: "Retry status" }));
    expect(await screen.findByText("Provisioning status: Succeeded")).toBeInTheDocument();
  });

  it("does not complete a durable operation from a mismatched response", async () => {
    window.sessionStorage.setItem(`managed-elsa-operation:${workspaceId}`, JSON.stringify({
      instanceId: healthyInstanceId,
      operationId: "00000000-0000-0000-0000-000000000201"
    }));
    installFetch({
      instances: [],
      operationResponses: [Response.json({
        id: "00000000-0000-0000-0000-000000000999",
        instanceId: healthyInstanceId,
        action: "Create",
        state: "Succeeded",
        attemptNumber: 1,
        failureCode: null,
        links: {}
      })]
    });

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent("Provisioning status could not be refreshed.");
    expect(window.sessionStorage.getItem(`managed-elsa-operation:${workspaceId}`)).not.toBeNull();
    expect(screen.queryByText("Provisioning status: Succeeded")).not.toBeInTheDocument();
  });

  it("distinguishes managed-hosting entitlement denial", async () => {
    installFetch({
      instances: [],
      onboardingResponse: Response.json({ title: "Not enabled" }, { status: 422 })
    });

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent("Managed hosting is not enabled for this organization.");
  });

  it("renders an empty governed release catalog explicitly", async () => {
    installFetch({ instances: [], releaseOptions: [] });

    renderPage();

    expect(await screen.findByText("No managed Elsa releases are currently available.")).toHaveAttribute("role", "status");
  });

  it("keeps distinct governed release lines and channels selectable", async () => {
    installFetch({
      instances: [],
      releaseOptions: [
        releaseFixture("valence-runtime", "4.0", "4.0.0", "stable"),
        releaseFixture("valence-runtime", "4.1", "4.0.0", "preview")
      ]
    });

    renderPage();

    expect(await screen.findByRole("option", { name: "Elsa 4.0.0 · 4.0 · stable · combined" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Elsa 4.0.0 · 4.1 · preview · combined" })).toBeInTheDocument();
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
  onboardingResponse,
  releaseOptions,
  previewReleases,
  operationResponses = [],
  createResponse
}: {
  instances?: ManagedElsaInstance[];
  instancePages?: ManagedElsaInstance[][];
  instancePageResponses?: Response[];
  issue?: Record<string, string>;
  issueResponses?: Response[];
  onInstancesRequest?: () => void;
  onboardingResponse?: Response;
  releaseOptions?: ReturnType<typeof releaseFixture>[];
  previewReleases?: Array<ReturnType<typeof releaseFixture> & { manifestDigest: string }>;
  operationResponses?: Response[];
  createResponse?: Response;
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
    if (url.endsWith(`/api/workspaces/${workspaceId}/instances/onboarding-options`)) {
      if (onboardingResponse)
        return onboardingResponse;
      return Response.json({
        releases: releaseOptions ?? [releaseFixture("valence-runtime", "3.8", "3.8.4"), releaseFixture("future-runtime", "5.0", "5.0.1")],
        previewReleases,
        launchProfile: { name: "West Europe Dedicated", description: "Managed hosting.", targetMode: "managed", regionCode: "westeurope", isolationProfile: "dedicated", capacityProfile: "standard-small", networkOutcome: "public", domainOutcome: "managed" }
      });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/instances`) && init?.method === "POST") {
      if (createResponse)
        return createResponse;
      return Response.json({
        instance: instanceFixture({ canOpen: false, observedLifecycle: "Pending", health: "Unknown", audience: null, redirectUri: null }),
        operation: { id: "00000000-0000-0000-0000-000000000201", instanceId: healthyInstanceId, action: "Create", state: "Accepted", attemptNumber: 1, failureCode: null, links: {} },
        links: {}
      }, { status: 202 });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/instances/${healthyInstanceId}/operations/00000000-0000-0000-0000-000000000201`)) {
      const response = operationResponses.shift();
      if (response)
        return response;
      return Response.json({ id: "00000000-0000-0000-0000-000000000201", instanceId: healthyInstanceId, action: "Create", state: "Succeeded", attemptNumber: 1, failureCode: null, links: {} });
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

function releaseFixture(distributionId: string, releaseLine: string, releaseVersion: string, channel = "stable") {
  return {
    distributionId, releaseLine, version: releaseVersion, channel, topologyId: "combined"
  };
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
