import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import { AuthProvider } from "@/lib/auth/AuthProvider";
import { ApplyReleasePage } from "@/features/managed-elsa/ApplyReleasePage";
import { releaseCatalogCopy } from "@/features/release-catalog/releaseCatalogModels";
import type { ManagedElsaInstance, ManagedElsaOnboardingOptions } from "@/features/managed-elsa/managedElsaModels";

const organizationId = "00000000-0000-0000-0000-000000000001";
const workspaceId = "00000000-0000-0000-0000-000000000010";
const instanceId = "00000000-0000-0000-0000-000000000101";
const digest = `sha256:${"c".repeat(64)}`;

describe("ApplyReleasePage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("limits the picker to admitted catalog entries and requires Preview consent", async () => {
    const fetchMock = installFetch();
    const user = userEvent.setup();
    renderPage();

    const picker = await screen.findByRole("combobox", { name: "Admitted release" });
    expect(within(picker).getByRole("option", { name: /3.8.0-preview.5567-build.160/ })).toBeInTheDocument();
    expect(within(picker).queryByRole("option", { name: /not-admitted/ })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Apply release" })).toBeDisabled();

    await user.click(screen.getByRole("checkbox", { name: /Preview release with no SLO/ }));
    await user.click(screen.getByRole("button", { name: "Apply release" }));

    await waitFor(() => expect(fetchMock.mock.calls.some(([input, init]) =>
      String(input).endsWith(`/api/workspaces/${workspaceId}/instances/${instanceId}`)
      && String(init?.method ?? "GET").toUpperCase() === "PATCH"
    )).toBe(true));
    const applyCall = fetchMock.mock.calls.find(([input, init]) =>
      String(input).endsWith(`/api/workspaces/${workspaceId}/instances/${instanceId}`)
      && String(init?.method ?? "GET").toUpperCase() === "PATCH"
    );
    const headers = new Headers(applyCall?.[1]?.headers);
    expect(headers.get("If-Match")).toBe("\"7\"");
    expect(headers.get("Idempotency-Key")).toMatch(/^[0-9a-f-]{36}$/i);
    const body = JSON.parse(String(applyCall?.[1]?.body));
    expect(body.intent.release.requestedVersion).toBe("3.8.0-preview.5567-build.160");
    expect(body.intent.release.previewManifestDigest).toBe(digest);
  });

  it("refuses Apply when the requested release is not admitted", async () => {
    installFetch();
    renderPage("/admin/runtimes/" + instanceId + "/apply?requestedVersion=3.8.0-preview.5567-build.151");

    expect(await screen.findByRole("alert")).toHaveTextContent(releaseCatalogCopy.applyNotAdmitted);
    expect(screen.getByRole("link", { name: /Releases conflict drawer/ })).toHaveAttribute("href", "/admin/releases");
    expect(screen.getByRole("button", { name: "Apply release" })).toBeDisabled();
    expect(screen.queryByText(/V3 Pass/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/Open Pass/i)).not.toBeInTheDocument();
  });

  it("offers Open after Healthy/Ready without claiming a Studio pass", async () => {
    installFetch({
      instance: instanceFixture({ canOpen: true, health: "Healthy", observedLifecycle: "Ready" }),
      acceptImmediately: true
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole("checkbox", { name: /Preview release with no SLO/ }));
    await user.click(screen.getByRole("button", { name: "Apply release" }));
    expect(await screen.findByRole("button", { name: "Open" })).toBeInTheDocument();
    expect(screen.getByText(/does not claim Studio success/)).toBeInTheDocument();
    expect(screen.queryByText(/V3 Pass/i)).not.toBeInTheDocument();
  });
});

function renderPage(path = `/admin/runtimes/${instanceId}/apply`) {
  render(
    <TestQueryProvider>
      <MemoryRouter initialEntries={[path]}>
        <AuthProvider>
          <WorkspaceContextProvider>
            <Routes>
              <Route path="/admin/runtimes/:instanceId/apply" element={<ApplyReleasePage />} />
            </Routes>
          </WorkspaceContextProvider>
        </AuthProvider>
      </MemoryRouter>
    </TestQueryProvider>
  );
}

function installFetch({
  instance = instanceFixture(),
  acceptImmediately = false
}: {
  instance?: ManagedElsaInstance;
  acceptImmediately?: boolean;
} = {}) {
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    const method = (input instanceof Request ? input.method : init?.method ?? "GET").toUpperCase();
    if (url.endsWith("/api/auth/session"))
      return Response.json({ loginEnabled: true, authenticated: true, displayName: "Ops", email: "ops@example.com" });
    if (url.endsWith("/api/me/organizations"))
      return Response.json({
        account: { id: "account-1", displayName: "Ops", email: "ops@example.com" },
        organizations: [{ id: organizationId, name: "Acme", role: "Owner" }],
        workspaces: [{ id: workspaceId, name: "Dogfood", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme", organizationRole: "Owner" }]
      });
    if (url.endsWith(`/api/workspaces/${workspaceId}/instances/onboarding-options`))
      return Response.json(optionsFixture());
    if (method === "GET" && url.endsWith(`/api/workspaces/${workspaceId}/instances/${instanceId}`))
      return Response.json(instance);
    if (method === "PATCH" && url.endsWith(`/api/workspaces/${workspaceId}/instances/${instanceId}`))
      return Response.json({
        instance: { ...instance, canOpen: acceptImmediately ? instance.canOpen : false },
        operation: {
          id: "00000000-0000-0000-0000-000000000201",
          instanceId,
          action: "UpdateIntent",
          state: acceptImmediately ? "Succeeded" : "Accepted",
          attemptNumber: 1,
          failureCode: null,
          links: {}
        },
        links: {}
      }, { status: 202 });
    if (url.includes("/health"))
      return Response.json({
        status: "Healthy",
        diagnosticCode: "managed.lifecycle.healthy",
        evaluatedAt: "2026-09-13T00:00:00Z",
        reconciledAt: "2026-09-13T00:00:00Z",
        operation: null,
        run: null,
        alerts: []
      });
    if (url.includes("/operations/"))
      return Response.json({
        id: "00000000-0000-0000-0000-000000000201",
        instanceId,
        action: "UpdateIntent",
        state: "Succeeded",
        attemptNumber: 1,
        failureCode: null,
        links: {}
      });
    return Response.json({ title: "Not found" }, { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function instanceFixture(overrides: Partial<ManagedElsaInstance> = {}): ManagedElsaInstance {
  return {
    organizationId,
    instanceId,
    name: "Dogfood2",
    slug: "dogfood2",
    desiredLifecycle: "Running",
    observedLifecycle: "Ready",
    health: "Healthy",
    canOpen: true,
    audience: "urn:elsa:instance:" + instanceId,
    redirectUri: "https://managed.example.test/managed-elsa/handoff/callback",
    unavailableReason: null,
    version: 7,
    eTag: "\"7\"",
    intent: {
      release: {
        distributionId: "valence-runtime",
        releaseLine: "3.8",
        requestedVersion: "3.8.0-preview.5567-build.149",
        channel: "preview",
        patchUpdates: "automatic-within-minor",
        minorUpdates: "explicit-approval",
        majorMigrations: "explicit-migration"
      },
      application: {
        topologyId: "combined",
        featurePresetId: null,
        featureOverrides: {},
        packagePolicy: null,
        configurationShapeRevisionId: null
      },
      placement: {
        targetMode: "managed",
        regionCode: "westeurope",
        isolationProfile: "dedicated",
        capacityProfile: "standard-small",
        networkOutcome: "public",
        domainOutcome: "managed"
      },
      desiredLifecycle: "Running"
    },
    currentResolvedRelease: {
      distributionId: "valence-runtime",
      releaseLine: "3.8",
      version: "3.8.0-preview.5567-build.149",
      manifestDigest: digest
    },
    ...overrides
  };
}

function optionsFixture(): ManagedElsaOnboardingOptions {
  return {
    releases: [],
    previewReleases: [{
      distributionId: "valence-runtime",
      releaseLine: "3.8",
      version: "3.8.0-preview.5567-build.160",
      channel: "preview",
      topologyId: "combined",
      manifestDigest: digest
    }],
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
}

function TestQueryProvider({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
