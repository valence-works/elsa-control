import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { AdminOrganizationsPage } from "@/features/organizations/AdminOrganizationsPage";
import { AuthProvider } from "@/lib/auth/AuthProvider";

describe("AdminOrganizationsPage", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("lets an administrator create an organization and shows the returned identifiers", async () => {
    const fetchMock = vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = input instanceof Request ? input.url : input.toString();
      if (url.endsWith("/api/auth/session"))
        return Promise.resolve(Response.json({ loginEnabled: true, authenticated: true, isAdmin: true, displayName: "Operator", email: "ops@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" }));
      expect(init?.method).toBe("POST");
      return Promise.resolve(Response.json({ organizationId: "org-1", workspaceId: "workspace-1", ownerAccountId: "account-1" }));
    });
    vi.stubGlobal("fetch", fetchMock);

    renderPage();
    await userEvent.type(await screen.findByRole("textbox", { name: "Organization name" }), "Stripe dry-run");
    await userEvent.click(screen.getByRole("button", { name: "Create organization" }));

    expect(await screen.findByRole("heading", { name: "Organization created" })).toBeInTheDocument();
    expect(screen.getByText("org-1")).toBeInTheDocument();
    expect(screen.getByText("workspace-1")).toBeInTheDocument();
    expect(fetchMock).toHaveBeenCalledWith("/api/admin/organizations", expect.objectContaining({ method: "POST" }));
  });

  it("fails closed and hides the form for a non-administrator", async () => {
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : input.toString();
      return Promise.resolve(url.endsWith("/api/auth/session")
        ? Response.json({ loginEnabled: true, authenticated: true, isAdmin: false, displayName: "Customer", email: "customer@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" })
        : Response.json({ title: "Forbidden" }, { status: 403 }));
    }));

    renderPage();

    expect(await screen.findByRole("alert")).toHaveTextContent("Operator access required");
    expect(screen.queryByRole("textbox", { name: "Organization name" })).not.toBeInTheDocument();
  });
});

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  render(<QueryClientProvider client={queryClient}><MemoryRouter><AuthProvider><AdminOrganizationsPage /></AuthProvider></MemoryRouter></QueryClientProvider>);
}
