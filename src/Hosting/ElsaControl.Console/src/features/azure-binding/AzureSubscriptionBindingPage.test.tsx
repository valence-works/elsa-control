import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AzureSubscriptionBindingPage, AzureSubscriptionBindingSurface } from "@/features/azure-binding/AzureSubscriptionBindingPage";
import {
  createAzureSubscriptionBind,
  getAzureSubscriptionBinding,
  verifyAzureSubscriptionBind
} from "@/features/azure-binding/azureBindingApi";
import type { AzureSubscriptionBindingView, AzureSubscriptionBind } from "@/features/azure-binding/azureBindingModels";

const workspaceState = vi.hoisted(() => ({
  organization: {
    id: "org-1",
    name: "Northstar",
    role: "Owner"
  } as { id: string; name: string; role: string } | null
}));

vi.mock("@/app/WorkspaceContextProvider", () => ({
  useWorkspaceContext: () => ({
    selectedOrganization: workspaceState.organization,
    isLoading: false,
    isError: false
  })
}));

vi.mock("@/features/azure-binding/azureBindingApi", () => ({
  createAzureSubscriptionBind: vi.fn(),
  getAzureSubscriptionBinding: vi.fn(),
  verifyAzureSubscriptionBind: vi.fn()
}));

const offer = {
  version: "v1",
  artifactUrl: "https://github.com/valence-works/elsa-control/tree/immutable-head/infra/azure-lighthouse/v1",
  artifactLabel: "Azure Lighthouse ARM artifact v1"
};

function binding(state: AzureSubscriptionBind["state"], code: string | null = null): AzureSubscriptionBind {
  return {
    id: "bind-1",
    organizationId: "org-1",
    customerTenantId: "22222222-2222-4222-8222-222222222222",
    subscriptionId: "11111111-1111-4111-8111-111111111111",
    managingTenantId: "managing-tenant-1",
    managingPrincipalIds: ["principal-1"],
    registrationDefinitionId: "definition-1",
    registrationDefinitionFingerprint: "sha256:definition",
    state,
    verifiedAt: state === "Active" ? "2026-09-14T08:00:00Z" : null,
    lastPreflightCode: code,
    createdByAccountId: "account-1",
    unbindReason: null
  };
}

function view(state: AzureSubscriptionBind["state"], code: string | null = null): AzureSubscriptionBindingView {
  return {
    bind: binding(state, code),
    offer,
    readiness: {
      entitlement: "Not evaluated",
      targeting: "Not available in this onboarding step"
    }
  };
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <AzureSubscriptionBindingPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

function withoutBind(): AzureSubscriptionBindingView {
  return { ...view("PendingConsent"), bind: null };
}

describe("AzureSubscriptionBindingPage", () => {
  beforeEach(() => {
    workspaceState.organization = { id: "org-1", name: "Northstar", role: "Owner" };
    vi.mocked(getAzureSubscriptionBinding).mockResolvedValue(withoutBind());
    vi.mocked(createAzureSubscriptionBind).mockResolvedValue(view("Verifying"));
    vi.mocked(verifyAzureSubscriptionBind).mockResolvedValue(view("Active"));
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it("keeps the guided Review step honest and requires consent before starting", async () => {
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByRole("heading", { name: "Bind a customer Azure subscription" })).toBeInTheDocument();
    expect(screen.getByText(/guided onboarding for design partners/i)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Azure Lighthouse ARM artifact v1/i })).toHaveAttribute("href", offer.artifactUrl);
    expect(screen.getAllByText(/Contributor/i).length).toBeGreaterThan(0);
    expect(screen.getByText(/Role Based Access Control Administrator/i)).toBeInTheDocument();
    expect(screen.getByText(/User Access Administrator/i)).toBeInTheDocument();
    expect(screen.getByText(/Azure bill is separate from any Elsa fee/i)).toBeInTheDocument();

    const continueButton = screen.getByRole("button", { name: "Continue to verification" });
    expect(continueButton).toBeDisabled();

    await user.type(screen.getByRole("textbox", { name: "Azure subscription ID" }), "11111111-1111-4111-8111-111111111111");
    await user.type(screen.getByRole("textbox", { name: "Customer tenant ID" }), "22222222-2222-4222-8222-222222222222");
    await user.click(screen.getByRole("checkbox", { name: /I understand Valence will operate/i }));
    expect(continueButton).toBeEnabled();

    await user.click(continueButton);
    await waitFor(() => expect(createAzureSubscriptionBind).toHaveBeenCalledWith("org-1", {
      subscriptionId: "11111111-1111-4111-8111-111111111111",
      customerTenantId: "22222222-2222-4222-8222-222222222222",
      consentConfirmed: true
    }));
    expect(await screen.findByRole("heading", { name: "Verify the Lighthouse delegation" })).toBeInTheDocument();
  });

  it("shows a non-Active preflight code when required roles are missing", async () => {
    vi.mocked(getAzureSubscriptionBinding).mockResolvedValue(view("Verifying"));
    vi.mocked(verifyAzureSubscriptionBind).mockResolvedValue(view("Degraded", "AZURE_RBAC_ROLE_MISSING"));
    renderPage();

    expect(await screen.findByRole("heading", { name: "Verify the Lighthouse delegation" })).toBeInTheDocument();
    await userEvent.setup().click(screen.getByRole("button", { name: "Verify delegation" }));

    await waitFor(() => expect(verifyAzureSubscriptionBind).toHaveBeenCalledWith("org-1"));
    expect(await screen.findByText(/Preflight code: AZURE_RBAC_ROLE_MISSING/i)).toBeInTheDocument();
    expect(screen.getByText(/does not have Active status/i)).toBeInTheDocument();
    expect(screen.queryByText(/subscription is bound/i)).not.toBeInTheDocument();
  });

  it("resumes a persisted PendingConsent bind at verification after refresh", async () => {
    vi.mocked(getAzureSubscriptionBinding).mockResolvedValue(view("PendingConsent"));
    renderPage();

    expect(await screen.findByRole("heading", { name: "Verify the Lighthouse delegation" })).toBeInTheDocument();
    expect(screen.getByText("v1")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Verify delegation" })).toBeEnabled();
  });

  it("shows the empty organization state without waiting on a disabled query", () => {
    workspaceState.organization = null;
    renderPage();

    expect(screen.getByText("No organization selected")).toBeInTheDocument();
  });

  it("clears draft identifiers and consent when the selected organization has no bind", async () => {
    const queryClient = new QueryClient();
    const { rerender } = render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <AzureSubscriptionBindingSurface organizationId="org-1" organizationName="Northstar" initialView={view("Degraded")} />
        </MemoryRouter>
      </QueryClientProvider>
    );

    rerender(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter>
          <AzureSubscriptionBindingSurface organizationId="org-2" organizationName="Fabrikam" initialView={withoutBind()} />
        </MemoryRouter>
      </QueryClientProvider>
    );

    expect(await screen.findByRole("textbox", { name: "Azure subscription ID" })).toHaveValue("");
    expect(screen.getByRole("textbox", { name: "Customer tenant ID" })).toHaveValue("");
    expect(screen.getByRole("checkbox", { name: /I understand Valence will operate/i })).not.toBeChecked();
  });

  it("lands on Active without implying entitlement or create-instance readiness", async () => {
    vi.mocked(getAzureSubscriptionBinding).mockResolvedValue(view("Active"));
    renderPage();

    expect(await screen.findByRole("heading", { name: "Subscription bound" })).toBeInTheDocument();
    expect(screen.getByText("11111111-1111-4111-8111-111111111111")).toBeInTheDocument();
    expect(screen.getByText(/Bind Active is only a prerequisite signal/i)).toBeInTheDocument();
    expect(screen.getByText(/Azure-bound entitlement and customer-subscription targeting are separate follow-on steps/i)).toBeInTheDocument();
    expect(screen.queryByText(/Ready to create an instance/i)).not.toBeInTheDocument();
  });
});
