import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppShell } from "@/app/AppShell";
import { AdminLoginPage, ConsoleNotFoundPage } from "@/app/routes";
import { AuthProvider } from "@/lib/auth/AuthProvider";

describe("AppShell", () => {
  let restoreDialogStub: (() => void) | undefined;

  beforeEach(() => {
    restoreDialogStub = installDialogStub();
  });

  afterEach(() => {
    cleanup();
    restoreDialogStub?.();
    restoreDialogStub = undefined;
    vi.unstubAllGlobals();
    if (typeof window.localStorage?.clear === "function") {
      window.localStorage.clear();
    }
    document.documentElement.classList.remove("dark");
    document.documentElement.removeAttribute("data-console-theme");
    document.documentElement.removeAttribute("data-theme-accent");
    document.documentElement.removeAttribute("style");
  });

  it("renders the unified Elsa Control navigation with package catalog active links", async () => {
    renderAppShell();

    const navigationText = screen.getAllByRole("navigation", { name: "Primary" })[0].textContent ?? "";
    expect(screen.getAllByRole("link", { name: "Overview" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Sources" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Packages" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Sync Runs" }).length).toBeGreaterThan(0);
    expect(screen.getAllByText("Deliver").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Operate").length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Applications" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Engine credentials" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Tiers" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Artifacts" }).length).toBeGreaterThan(0);
    expect(screen.getAllByText("Runtime Builder").length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Build configurations" }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Console" }).length).toBeGreaterThan(0);
    expect(screen.getAllByText("Managed Runtimes").length).toBeGreaterThan(0);
    expect(screen.getAllByRole("link", { name: "Runtime Operations" }).length).toBeGreaterThan(0);
    expect(screen.queryByRole("link", { name: "Audit" })).not.toBeInTheDocument();
    expect(screen.getByText("Audit").closest("[aria-disabled='true']")).toBeInTheDocument();
    expect(navigationText).toContain("ControlOverview");
    expect(navigationText).toContain("DeliverOverviewApplicationsArtifacts");
    expect(navigationText).toContain("OperateTiersEngine credentialsConsole");
    expect(navigationText).toContain("Runtime BuilderBuild configurations");
    expect(screen.queryByRole("link", { name: "Settings" })).not.toBeInTheDocument();
    expect(await screen.findAllByRole("combobox", { name: "Organization" }, { timeout: 5_000 })).toHaveLength(2);
    expect(screen.getAllByRole("combobox", { name: "Workspace" })).toHaveLength(2);
    expect(await screen.findAllByLabelText("Application build number", {}, { timeout: 5_000 })).toHaveLength(2);
  });

  it("shows the application build number", async () => {
    renderAppShell("2026.05.16.7");

    const buildLabels = await screen.findAllByLabelText("Application build number");
    expect(buildLabels).toHaveLength(2);
    buildLabels.forEach((label) => expect(label).toHaveTextContent("Build 2026.05.16.7"));
  });

  it("changes the selected theme and color mode without disturbing the console content", async () => {
    renderAppShell();

    const overviewLinksBefore = screen.getAllByRole("link", { name: "Overview" });
    expect(document.documentElement).toHaveAttribute("data-console-theme", "classic");
    expect(document.documentElement).not.toHaveClass("dark");

    await userEvent.click(screen.getAllByRole("button", { name: "Appearance" })[0]);

    expect(screen.getByRole("dialog", { name: "Appearance" })).toHaveAttribute("open");
    expect(screen.getByRole("radio", { name: "Classic" })).toBeChecked();
    expect(screen.getByRole("radio", { name: "Light" })).toBeChecked();

    await userEvent.click(screen.getByRole("radio", { name: "Operations Canvas" }));
    expect(document.documentElement).toHaveAttribute("data-console-theme", "operations-canvas");
    expect(screen.getAllByRole("link", { name: "Overview" })).toHaveLength(overviewLinksBefore.length);
    expect(screen.getAllByText("Elsa Control").length).toBeGreaterThan(0);

    await userEvent.click(screen.getByRole("radio", { name: "Dark" }));

    expect(document.documentElement).toHaveAttribute("data-console-theme", "operations-canvas");
    expect(document.documentElement).toHaveClass("dark");
    expect(window.localStorage.getItem("elsa-control-console-theme")).toBe("dark");
    expect(window.localStorage.getItem("elsa-control-console-theme-accent")).toBe("teal");
  });

  it("stores the Classic accent and keeps the selection after closing and reopening Appearance", async () => {
    renderAppShell();

    await userEvent.click(screen.getAllByRole("button", { name: "Appearance" })[0]);

    const dialog = screen.getByRole("dialog", { name: "Appearance" });
    const accentPicker = screen.getByRole("combobox", { name: "Theme accent" });
    expect(accentPicker).toHaveValue("teal");
    await waitFor(() => expect(document.documentElement).toHaveAttribute("data-theme-accent", "teal"));

    await userEvent.selectOptions(accentPicker, "violet");

    expect(accentPicker).toHaveValue("violet");
    expect(document.documentElement).toHaveAttribute("data-theme-accent", "violet");
    expect(window.localStorage.getItem("elsa-control-console-theme-accent")).toBe("violet");

    const persisted = JSON.parse(window.localStorage.getItem("elsa-control-console-appearance") ?? "null") as Record<string, unknown>;
    expect(persisted).toMatchObject({ version: 1, themeId: "classic", mode: "light", accent: "violet" });

    await userEvent.click(screen.getByRole("button", { name: "Close appearance" }));
    expect(dialog).not.toHaveAttribute("open");

    await userEvent.click(screen.getAllByRole("button", { name: "Appearance" })[0]);

    expect(screen.getByRole("dialog", { name: "Appearance" })).toHaveAttribute("open");
    expect(screen.getByRole("radio", { name: "Classic" })).toBeChecked();
    expect(screen.getByRole("radio", { name: "Light" })).toBeChecked();
    expect(screen.getByRole("combobox", { name: "Theme accent" })).toHaveValue("violet");
  });

  it("opens Weaver as a global assistant drawer", async () => {
    renderAppShell();

    await userEvent.click(screen.getAllByRole("button", { name: "Open Weaver assistant" })[0]);

    expect(screen.getByRole("complementary", { name: "Weaver assistant" })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Weaver" })).toBeInTheDocument();
    expect(screen.getByText("/admin")).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText("Mode")).toHaveValue("Plan"));

    await userEvent.click(screen.getByRole("button", { name: "Suggest prompt" }));
    expect(screen.getByLabelText("Message Weaver")).toHaveValue("Summarize the current page and recommended next actions.");

    await userEvent.click(screen.getByRole("button", { name: "Send" }));
    expect(screen.getByText("Summarize the current page and recommended next actions.")).toBeInTheDocument();
    expect(await screen.findByText(/Mode: Plan/i)).toBeInTheDocument();
    expect(screen.getByText("Tool activity")).toBeInTheDocument();
    expect(screen.getByText("get_current_context")).toBeInTheDocument();
    expect(screen.getByText("Draft promotion plan")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Close Weaver assistant" }));
    expect(screen.queryByRole("complementary", { name: "Weaver assistant" })).not.toBeInTheDocument();
  }, 10_000);

  it("shows Weaver unavailable state", async () => {
    renderAppShell("0.0.1", workspaceContextFixture(), disabledWeaverConfigurationFixture());

    await userEvent.click(screen.getAllByRole("button", { name: "Open Weaver assistant" })[0]);

    expect(await screen.findByText("Unavailable")).toBeInTheDocument();
    expect(screen.getByText("Weaver is disabled.")).toBeInTheDocument();
    expect(screen.getByLabelText("Message Weaver")).toBeDisabled();
  });

  it("keeps workspace choices scoped to the selected organization", async () => {
    renderAppShell("0.0.1", multiOrganizationContextFixture());

    const organizationSelect = (await screen.findAllByRole("combobox", { name: "Organization" }, { timeout: 5_000 }))[0];
    const workspaceSelect = screen.getAllByRole("combobox", { name: "Workspace" })[0];

    expect(workspaceSelect).toHaveDisplayValue("Claims");

    await userEvent.selectOptions(organizationSelect, "org-beta");

    expect(workspaceSelect).toHaveDisplayValue("Research");
    expect(screen.queryByRole("option", { name: "Claims" })).not.toBeInTheDocument();
  });

  it("renders a console-owned login recovery page", async () => {
    renderAppShellRoute("/admin/login");

    expect(await screen.findByRole("heading", { name: "You are already signed in" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open overview" })).toHaveAttribute("href", "/admin/overview");
    expect(screen.queryByText("Unexpected Application Error!")).not.toBeInTheDocument();
  });

  it("renders a console-owned not found page for unknown admin routes", async () => {
    renderAppShellRoute("/admin/missing");

    expect(await screen.findByRole("heading", { name: "Console page not found" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Open overview" })).toHaveAttribute("href", "/admin/overview");
    expect(screen.queryByText("Unexpected Application Error!")).not.toBeInTheDocument();
  });
});

function renderAppShell(
  buildNumber = "0.0.1",
  workspaceContext = workspaceContextFixture(),
  weaverConfiguration = weaverConfigurationFixture()
) {
  installLocalStorageStub();
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    const method = input instanceof Request ? input.method : init?.method ?? "GET";
    const path = new URL(url, window.location.origin).pathname;
    if (url.endsWith("/api/auth/session"))
      return Response.json({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" });
    if (url.endsWith("/api/me/organizations"))
      return Response.json(workspaceContext);
    if (path.endsWith("/weaver/configuration"))
      return Response.json(weaverConfiguration);
    if (path.endsWith("/weaver/sessions") && method === "POST")
      return Response.json({ id: "session-1", status: "Active", mode: "Plan", createdAt: "2026-06-07T12:00:00Z" }, { status: 201 });
    if (path.endsWith("/weaver/sessions/session-1/messages") && method === "POST")
      return Response.json({ messageId: "message-1", assistantMessageId: "message-2", sessionStatus: "Active" });
    if (path.endsWith("/weaver/sessions/session-1"))
      return Response.json(weaverSessionDetailFixture());
    return Response.json({ name: "ElsaControl.Api", buildNumber });
  }));
  const router = createMemoryRouter([{ path: "/admin", element: <AppShell /> }], {
    initialEntries: ["/admin"]
  });

  render(
    <TestQueryProvider>
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>
    </TestQueryProvider>
  );
}

function renderAppShellRoute(route: string) {
  installLocalStorageStub();
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    const url = input instanceof Request ? input.url : input.toString();
    if (url.endsWith("/api/auth/session"))
      return Response.json({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" });
    if (url.endsWith("/api/me/organizations"))
      return Response.json(workspaceContextFixture());
    return Response.json({ name: "ElsaControl.Api", buildNumber: "0.0.1" });
  }));
  const router = createMemoryRouter([
    {
      path: "/admin",
      element: <AppShell />,
      children: [
        { path: "login", element: <AdminLoginPage /> },
        { path: "*", element: <ConsoleNotFoundPage /> }
      ]
    }
  ], {
    initialEntries: [route]
  });

  render(
    <TestQueryProvider>
      <AuthProvider>
        <RouterProvider router={router} />
      </AuthProvider>
    </TestQueryProvider>
  );
}

function workspaceContextFixture() {
  const organizationId = "00000000-0000-0000-0000-000000000001";
  return {
    account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
    organizations: [{ id: organizationId, name: "Acme Corp", role: "Owner" }],
    workspaces: [
      { id: "00000000-0000-0000-0000-000000000010", name: "Acme Insurance", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" }
    ]
  };
}

function multiOrganizationContextFixture() {
  return {
    account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
    organizations: [
      { id: "org-alpha", name: "Alpha Corp", role: "Owner" },
      { id: "org-beta", name: "Beta Labs", role: "Administrator" }
    ],
    workspaces: [
      { id: "workspace-claims", name: "Claims", kind: "Shared", role: "Owner", organizationId: "org-alpha", organizationName: "Alpha Corp", organizationRole: "Owner" },
      { id: "workspace-billing", name: "Billing", kind: "Shared", role: "Reader", organizationId: "org-alpha", organizationName: "Alpha Corp", organizationRole: "Owner" },
      { id: "workspace-research", name: "Research", kind: "Shared", role: "Owner", organizationId: "org-beta", organizationName: "Beta Labs", organizationRole: "Administrator" }
    ]
  };
}

type WeaverConfigurationFixture = {
  enabled: boolean;
  providerMode: string;
  model: string;
  reasoningEffort: string;
  streamingEnabled: boolean;
  modes: string[];
  disabledReason: string | null;
};

function weaverConfigurationFixture(): WeaverConfigurationFixture {
  return {
    enabled: true,
    providerMode: "Fake",
    model: "gpt-5",
    reasoningEffort: "medium",
    streamingEnabled: true,
    modes: ["Inspect", "Plan"],
    disabledReason: null
  };
}

function disabledWeaverConfigurationFixture(): WeaverConfigurationFixture {
  return {
    ...weaverConfigurationFixture(),
    enabled: false,
    providerMode: "Disabled",
    modes: [],
    disabledReason: "Weaver is disabled."
  };
}

function weaverSessionDetailFixture() {
  return {
    session: { id: "session-1", status: "Active", mode: "Plan", createdAt: "2026-06-07T12:00:00Z" },
    messages: [
      {
        id: "message-1",
        role: "User",
        content: "Summarize the current page and recommended next actions.",
        redactionState: "None",
        sequence: 1,
        createdAt: "2026-06-07T12:00:01Z"
      },
      {
        id: "message-2",
        role: "Assistant",
        content: "Mode: Plan. I can inspect this workspace from /admin.",
        redactionState: "None",
        sequence: 2,
        createdAt: "2026-06-07T12:00:02Z"
      }
    ],
    toolCalls: [
      {
        id: "tool-1",
        toolName: "get_current_context",
        resultSummaryJson: "{\"summary\":\"routePath=/admin\"}",
        authorizationResult: "Allowed",
        status: "Succeeded",
        durationMilliseconds: 1,
        createdAt: "2026-06-07T12:00:01Z",
        completedAt: "2026-06-07T12:00:02Z"
      }
    ],
    plans: [
      {
        id: "plan-1",
        version: 1,
        planType: "Promotion",
        title: "Draft promotion plan",
        summary: "Prepare a promotion plan for Production.",
        targetJson: "{\"environment\":\"Production\"}",
        impactJson: "{\"changes\":\"No mutation until approval\"}",
        validationJson: "{\"status\":\"Requires review\"}",
        rollbackJson: "{\"path\":\"Previous revision\"}",
        risk: "Medium",
        status: "ReadyForApproval",
        createdAt: "2026-06-07T12:00:03Z",
        updatedAt: "2026-06-07T12:00:03Z"
      }
    ]
  };
}

function installLocalStorageStub() {
  const storage = new Map<string, string>();

  Object.defineProperty(window, "localStorage", {
    configurable: true,
    value: {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value),
      removeItem: (key: string) => storage.delete(key),
      clear: () => storage.clear()
    }
  });
}

function installDialogStub() {
  const dialogPrototype = HTMLDialogElement.prototype as unknown as Record<string, unknown>;
  const previousShowModal = dialogPrototype.showModal;
  const previousClose = dialogPrototype.close;

  Object.defineProperty(dialogPrototype, "showModal", {
    configurable: true,
    value: vi.fn(function (this: HTMLDialogElement) {
      this.setAttribute("open", "");
    })
  });
  Object.defineProperty(dialogPrototype, "close", {
    configurable: true,
    value: vi.fn(function (this: HTMLDialogElement) {
      this.removeAttribute("open");
      this.dispatchEvent(new Event("close"));
    })
  });

  return () => {
    if (previousShowModal === undefined) {
      delete dialogPrototype.showModal;
    } else {
      Object.defineProperty(dialogPrototype, "showModal", { configurable: true, value: previousShowModal });
    }
    if (previousClose === undefined) {
      delete dialogPrototype.close;
    } else {
      Object.defineProperty(dialogPrototype, "close", { configurable: true, value: previousClose });
    }
  };
}

function TestQueryProvider({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({
    defaultOptions: {
      queries: { retry: false }
    }
  });

  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
