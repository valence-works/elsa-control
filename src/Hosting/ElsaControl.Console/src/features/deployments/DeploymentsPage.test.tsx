import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import {
  DeploymentApplicationEditPage,
  DeploymentApplicationsPage,
  DeploymentApplicationPage,
  DeploymentApplicationRevisionsPage,
  DeploymentCredentialReferenceCreatePage,
  DeploymentCredentialReferenceEditPage,
  DeploymentCredentialReferencePage,
  DeploymentCredentialsPage,
  DeploymentCredentialStoreCreatePage,
  DeploymentCredentialStoreEditPage,
  DeploymentEngineEditPage,
  DeploymentEnginePage,
  DeploymentEngineRegisterPage,
  DeploymentEnvironmentCreatePage,
  DeploymentEnvironmentEditPage,
  DeploymentEnvironmentPage,
  DeploymentRevisionDetailPage,
  DeploymentRevisionCreatePage,
  DeploymentsPage,
  NewDeploymentSetupPage
} from "@/features/deployments/DeploymentsPage";
import { DeploymentSetupPanel } from "@/features/deployments/DeploymentSetupPanel";
import type {
  DeploymentCockpit,
  WorkspaceDeploymentCredentialReference,
  WorkspaceDeploymentSecretStore,
  WorkspaceDeploymentTier,
  WorkspaceDesiredStateRevisionDetail,
  WorkspaceDesiredStateRevisionSummary
} from "@/features/deployments/deploymentModels";
import { AuthProvider } from "@/lib/auth/AuthProvider";

describe("DeploymentsPage", () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("renders a workspace deployment overview without the application list", async () => {
    renderDeployments();

    expect(await screen.findByRole("heading", { name: /^Deployments$/ }, { timeout: 15000 })).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/new")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/tiers")).toBeInTheDocument();
    expect(screen.getAllByText("4").length).toBeGreaterThan(0);
    expect(screen.getByText("Deployment posture")).toBeInTheDocument();
    expect(screen.getByText("Operational shortcuts")).toBeInTheDocument();
    expect(screen.queryByText("Workflow applications")).not.toBeInTheDocument();
    expect(screen.queryByText("Claims Operations")).not.toBeInTheDocument();
    expect(screen.getByText("Healthy engines")).toBeInTheDocument();
    expect(screen.queryByText(/password|token|secret value/i)).not.toBeInTheDocument();
  });

  it("renders application list on a dedicated route", async () => {
    renderDeployments(multipleApplicationsCockpit, "/admin/deployments/applications");

    expect(await screen.findByRole("heading", { name: "Applications" })).toBeInTheDocument();
    expect(screen.getByRole("region", { name: "Workflow applications" })).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/policy-app")).toBeInTheDocument();
    expect(screen.getByText("Claims Operations")).toBeInTheDocument();
    expect(screen.getByLabelText("Sort applications")).toHaveValue("name");
    expect(screen.getByRole("link", { name: "Claims Operations" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Claims Operations" })).toHaveAccessibleDescription(/environment.*engine/);
    expect(screen.getByRole("link", { name: "Policy" })).toBeInTheDocument();

    await userEvent.type(screen.getByPlaceholderText("Search applications"), "Policy");

    expect(screen.getByRole("link", { name: "Policy" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Claims Operations" })).not.toBeInTheDocument();
    expect(screen.queryByText("Deployment posture")).not.toBeInTheDocument();
  });

  it.each([
    { missingEngine: true, blocked: false, expectedHealth: "Needs setup" },
    { missingEngine: false, blocked: false, expectedHealth: "Healthy" },
    { missingEngine: false, blocked: true, expectedHealth: "Needs review" }
  ])("reports $expectedHealth for missingEngine=$missingEngine and blocked=$blocked", async ({ missingEngine, blocked, expectedHealth }) => {
    const application = deploymentCockpitFixture.applications[0];
    const environments = application.environments.slice(0, 2).map(environment => ({ ...environment, deploymentStatus: blocked ? "Blocked" as const : environment.deploymentStatus }));
    const connectedIds = environments.slice(0, missingEngine ? 1 : 2).map(environment => environment.id);
    renderDeployments({
      ...deploymentCockpitFixture,
      applications: [{ ...application, environments }],
      engines: deploymentCockpitFixture.engines.filter(engine => connectedIds.includes(engine.environmentId))
    }, "/admin/deployments/applications");

    const card = await screen.findByRole("link", { name: application.name });
    expect(within(card).getByText(expectedHealth)).toBeInTheDocument();
    if (missingEngine) expect(within(card).queryByText("Healthy")).not.toBeInTheDocument();
  });

  it("reports Needs review when an application environment has unknown drift", async () => {
    const application = deploymentCockpitFixture.applications[0];
    renderDeployments({
      ...deploymentCockpitFixture,
      applications: [{
        ...application,
        environments: application.environments.map((environment, index) => ({
          ...environment,
          driftStatus: index === 0 ? "Unknown" as const : "InSync" as const,
          deploymentStatus: "Succeeded" as const
        }))
      }],
      engines: deploymentCockpitFixture.engines.map((engine) => ({ ...engine, health: "Healthy" as const }))
    }, "/admin/deployments/applications");

    const card = await screen.findByRole("link", { name: application.name });
    expect(within(card).getByText("Needs review")).toBeInTheDocument();
  });

  it.each([
    { path: "/admin/deployments/applications", heading: "Applications", action: "New application setup" },
    { path: "/admin/deployments/applications/claims-ops/environments/claims-dev", heading: "Dev", action: "Connect engine" }
  ])("prevents keyboard activation of $action without setup permission", async ({ path, heading, action }) => {
    renderDeployments(undefined, path, { permissions: ["deployments.read"] });
    await screen.findByRole("heading", { name: heading });
    const link = screen.getByRole("link", { name: action });
    expect(link).toHaveAttribute("aria-disabled", "true");
    expect(link).toHaveAttribute("tabindex", "-1");
    link.focus();
    await userEvent.keyboard("{Enter}");
    expect(screen.getByRole("heading", { name: heading })).toBeInTheDocument();
  });

  it("shows an empty application list state when no deployment setup exists", async () => {
    renderDeployments({
      applications: [],
      engines: [],
      comparisons: [],
      observabilityBindings: [],
      history: [],
      driftReport: [],
      assistantPlans: []
    }, "/admin/deployments/applications");

    expect(await screen.findByText("No deployment setup")).toBeInTheDocument();
    expect(screen.getByText("Connect an engine to create your first application and environment.")).toBeInTheDocument();
    expect(linkByHref("/admin/engines/connect")).toBeInTheDocument();
  });

  it("keeps the empty-workspace Connect engine action read-only without setup permission", async () => {
    renderDeployments({
      applications: [],
      engines: [],
      comparisons: [],
      observabilityBindings: [],
      history: [],
      driftReport: [],
      assistantPlans: []
    }, "/admin/deployments/applications", { permissions: ["deployments.read"] });

    expect(await screen.findByText("No deployment setup")).toBeInTheDocument();
    const link = screen.getByRole("link", { name: "Connect engine" });
    expect(link).toHaveAttribute("aria-disabled", "true");
    expect(link).toHaveAttribute("tabindex", "-1");
    link.focus();
    await userEvent.keyboard("{Enter}");
    expect(screen.getByText("No deployment setup")).toBeInTheDocument();
  });

  it("keeps an empty environment engine action read-only without setup permission", async () => {
    const cockpit: DeploymentCockpit = {
      ...deploymentCockpitFixture,
      applications: deploymentCockpitFixture.applications.map((application) => application.id === "claims-ops"
        ? {
            ...application,
            environments: application.environments.map((environment) => environment.id === "claims-dev" ? { ...environment, engineIds: [] } : environment)
          }
        : application),
      engines: deploymentCockpitFixture.engines.filter((engine) => engine.id !== "dev-engine")
    };
    renderDeployments(cockpit, "/admin/deployments/applications/claims-ops/environments/claims-dev", { permissions: ["deployments.read"] });

    expect(await screen.findByRole("heading", { name: "Dev" })).toBeInTheDocument();
    const links = screen.getAllByRole("link", { name: "Connect engine" });
    expect(links).toHaveLength(2);
    links.forEach((link) => {
      expect(link).toHaveAttribute("aria-disabled", "true");
      expect(link).toHaveAttribute("tabindex", "-1");
    });
    links[1].focus();
    await userEvent.keyboard("{Enter}");
    expect(screen.getByRole("heading", { name: "Dev" })).toBeInTheDocument();
  });

  it("creates application setup with environments and engines from the guided setup route", async () => {
    const fetchMock = renderDeployments({
      applications: [],
      engines: [],
      comparisons: [],
      observabilityBindings: [],
      history: [],
      driftReport: [],
      assistantPlans: []
    }, "/admin/deployments/new");

    expect(await screen.findByRole("heading", { name: "New application setup" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Application"), "Claims Operations");
    expect(screen.queryByLabelText("Engine name")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create application" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications`),
        expect.objectContaining({ method: "POST" })
      )
    );
    // The environments step is a multi-row editor pre-filled with a Dev/Test/Production ladder.
    await waitFor(() => expect(screen.getAllByLabelText("Tier").length).toBeGreaterThan(1));
    expect(screen.getAllByText("Claims Operations").length).toBeGreaterThan(0);
    // Reduce the ladder to a single Production environment to keep the engine target unambiguous.
    const removeButtons = screen.getAllByRole("button", { name: /Remove .* row/ });
    await userEvent.click(removeButtons[0]);
    await userEvent.click(screen.getAllByRole("button", { name: /Remove .* row/ })[0]);
    const nameInputs = screen.getAllByLabelText("Name");
    await userEvent.clear(nameInputs[0]);
    await userEvent.type(nameInputs[0], "Prod");
    await userEvent.click(screen.getByRole("button", { name: /Create 1 environment/ }));

    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/applications/app-created/environments")).toMatchObject({
        name: "Prod",
        tier: "Production",
        tierId: "tier-production"
      })
    );
    expect(await screen.findByText("Prod - 0 engines")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Continue to engines" }));
    await userEvent.type(screen.getByLabelText("Engine name"), "claims-prod");
    await userEvent.type(screen.getByLabelText("Engine base URL"), "https://claims-prod.example/elsa");
    await userEvent.type(screen.getByLabelText("Key Vault reference"), "kv://acme-elsa-control/prod/claims-engine-api");
    await userEvent.click(screen.getByRole("button", { name: "Register engine" }));

    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/credential-references")).toMatchObject({
        name: "claims-prod engine credential",
        reference: "kv://acme-elsa-control/prod/claims-engine-api",
        secretValue: null
      })
    );
    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/environments/env-created/engines")).toMatchObject({
        name: "claims-prod",
        baseUrl: "https://claims-prod.example/elsa",
        credentialReferenceId: "credential-reference-4"
      })
    );
    expect(await screen.findByText("Prod - 1 engines")).toBeInTheDocument();
    // The wizard now continues to an artifact step so it can deliver the first version.
    await userEvent.click(screen.getByRole("button", { name: "Continue to artifact" }));
    expect(await screen.findByRole("heading", { name: "Pick an artifact" })).toBeInTheDocument();
  }, 15000);

  it("renders application detail with an environment table", async () => {
    renderDeployments(multipleApplicationsCockpit, "/admin/deployments/applications/claims-ops");

    expect(await screen.findByRole("heading", { name: "Claims Operations" })).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/revisions")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/new")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-prod")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Policy/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Engine credential stores" })).not.toBeInTheDocument();
    expect(screen.queryByText("Register control-plane-to-engine credentials. Runtime secrets remain managed inside runtimes.")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New credential store" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New credential reference" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Register engine credential store" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Register credential reference" })).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Secret store")).not.toBeInTheDocument();
  });

  it("renders standalone engine credential management for the selected workspace", async () => {
    renderDeployments(undefined, "/admin/deployments/credentials");

    expect(await screen.findByRole("heading", { name: "Engine credentials", level: 1 })).toBeInTheDocument();
    expect(screen.getByText("Manage workspace control-plane-to-engine credential stores. Runtime secrets remain managed inside runtimes.")).toBeInTheDocument();
    expect(screen.getByText("Engine credentials let Elsa Control interact with registered workflow engines. Runtime secrets and artifact secret references stay in the runtimes.")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/credentials/stores/new")).toBeInTheDocument();
    expect(screen.getAllByText("Control Key Vault").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Local engine credentials").length).toBeGreaterThan(0);
    expect(screen.queryByRole("link", { name: "New credential reference" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Credential references" })).not.toBeInTheDocument();
    expect(screen.queryByText("Dev engine API")).not.toBeInTheDocument();
    expect(screen.queryByText("Protected local credential")).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Credential actions" })).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Register credential reference" })).not.toBeInTheDocument();
    expect(linkByHref("/admin/deployments/credentials/stores/secret-store-azure/edit")).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText("Status"), "Archived");

    expect(screen.getByText("No archived engine credential stores.")).toBeInTheDocument();
    expect(screen.queryByText("No archived credential references.")).not.toBeInTheDocument();
  });

  it("lists credential references from the selected credential store", async () => {
    renderDeployments(undefined, "/admin/deployments/credentials/stores/secret-store-azure/edit");

    expect(await screen.findByRole("heading", { name: "Edit Control Key Vault", level: 1 })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "Credentials" })).toBeInTheDocument();
    expect(screen.getByText("Dev engine API")).toBeInTheDocument();
    expect(screen.getByText("Prod engine API")).toBeInTheDocument();
    expect(screen.queryByText("Local dev engine API")).not.toBeInTheDocument();
    expect(screen.queryByText("Protected local credential")).not.toBeInTheDocument();
    expect(linkByHref("/admin/deployments/credentials/references/new?storeId=secret-store-azure")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/credentials/references/credential-reference-dev")).toBeInTheDocument();
  });

  it("creates engine credential stores from a dedicated create page", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/credentials/stores/new");

    expect(await screen.findByRole("heading", { name: "New credential store", level: 1 })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Store name"), "Local registration credentials");
    await userEvent.selectOptions(screen.getByLabelText("Store type"), "LocalEncryptedDatabase");
    await userEvent.click(screen.getByRole("button", { name: "Create store" }));

    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/deployments/secret-stores")).toMatchObject({
        name: "Local registration credentials",
        provider: null,
        type: "LocalEncryptedDatabase"
      })
    );
    expect(await screen.findByRole("heading", { name: "Engine credentials", level: 1 })).toBeInTheDocument();
  });

  it("creates write-only local references from a dedicated create page", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/credentials/references/new?storeId=secret-store-local");

    expect(await screen.findByRole("heading", { name: "New credential reference", level: 1 })).toBeInTheDocument();
    expect(screen.getByLabelText("Engine credential store")).toHaveValue("secret-store-local");
    await userEvent.type(screen.getByLabelText("Reference name"), "Local staging engine API");
    await userEvent.type(screen.getByLabelText("Secret value"), "local-staging-secret");
    await userEvent.click(screen.getByRole("button", { name: "Create reference" }));

    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/credential-references")).toMatchObject({
        name: "Local staging engine API",
        reference: "local://engine-credentials/local-staging-engine-api",
        secretValue: "local-staging-secret"
      })
    );
    expect(screen.queryByText("local-staging-secret")).not.toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Edit Local engine credentials", level: 1 })).toBeInTheDocument();
  }, 15000);

  it("renders standalone engine credential management as read-only without setup permission", async () => {
    renderDeployments(undefined, "/admin/deployments/credentials", { permissions: ["deployments.read"] });

    expect(await screen.findByRole("heading", { name: "Engine credentials", level: 1 })).toBeInTheDocument();
    expect(screen.getByText("Deployment setup permission is required to change engine credential stores and references.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "New credential store" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New credential store" })).not.toBeInTheDocument();
    expect(linkByHref("/admin/deployments/credentials/stores/secret-store-azure/edit")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Credential references" })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New credential reference" })).not.toBeInTheDocument();
  });

  it("edits credential references from a dedicated edit page", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/credentials/references/credential-reference-dev/edit");

    expect(await screen.findByRole("heading", { name: "Edit Dev engine API", level: 1 })).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("Reference name"));
    await userEvent.type(screen.getByLabelText("Reference name"), "Development engine API");
    await userEvent.click(screen.getByRole("button", { name: "Save reference" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/credential-references/credential-reference-dev`),
        expect.objectContaining({ method: "PUT" })
      )
    );
    expect(requestBody(fetchMock, "PUT", "/credential-references/credential-reference-dev")).toMatchObject({
      name: "Development engine API",
      reference: "kv://acme-elsa-control/dev/elsa-api"
    });
  });

  it("rotates and archives credential references from the reference detail page", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/credentials/references/credential-reference-local");

    expect(await screen.findByRole("heading", { name: "Local dev engine API", level: 1 })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("New credential value"), "new-local-secret");
    await userEvent.click(screen.getByRole("button", { name: "Rotate" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/credential-references/credential-reference-local/rotate`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(requestBody(fetchMock, "POST", "/credential-references/credential-reference-local/rotate")).toMatchObject({
      secretValue: "new-local-secret"
    });
    expect(screen.queryByText("new-local-secret")).not.toBeInTheDocument();
  });

  it("shows usage before archiving a credential reference from the detail page", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/credentials/references/credential-reference-dev");

    expect(await screen.findByRole("heading", { name: "Dev engine API", level: 1 })).toBeInTheDocument();
    expect(await screen.findByText("claims-dev-01")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Archive reference" }));
    expect(screen.getByText("1 engines currently use this reference.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Confirm archive" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/credential-references/credential-reference-dev/archive`),
        expect.objectContaining({ method: "POST" })
      )
    );
  }, 15000);

  it("lets engine setup create credentials when no reusable references exist", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/engines/new", { credentialReferences: [] });

    expect(await screen.findByRole("heading", { name: "Register engine for Dev" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Create for this engine" })).toBeInTheDocument();
    expect(screen.getByLabelText("Reference name")).toBeInTheDocument();
    expect(screen.queryByText(/No active credential references are registered for the selected store/)).not.toBeInTheDocument();
  });

  it("renders application revisions across environments", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/revisions");

    expect(await screen.findByRole("heading", { name: "Claims Operations revisions" })).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev/revisions/new")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/revisions/00000000-0000-0000-0000-000000000142")).toBeInTheDocument();
    expect(screen.getByText("Payment retry workflow")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-stage")).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText("Filter revisions by environment"), "claims-prod");

    expect(screen.getByText("Baseline production")).toBeInTheDocument();
    expect(screen.queryByText("Payment retry workflow")).not.toBeInTheDocument();
  });

  it("renders revision detail records and deployment state", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/revisions/00000000-0000-0000-0000-000000000142");

    expect(await screen.findByRole("heading", { name: "Revision r42" })).toBeInTheDocument();
    expect(screen.getByText("Desired-state records")).toBeInTheDocument();
    expect(screen.getByText("Payment Retry")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deploy revision" })).toBeDisabled();
    expect(screen.getByText("This revision is already deployed in Dev.")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev")).toBeInTheDocument();
  });

  it("renders deployability blockers on revision detail before queueing", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/revisions/00000000-0000-0000-0000-000000000141");

    expect(await screen.findByRole("heading", { name: "Revision r41" })).toBeInTheDocument();
    expect(await screen.findByText("Blocked")).toBeInTheDocument();
    expect(screen.getByText("EngineCapabilities:")).toBeInTheDocument();
    expect(screen.getByText("Payment Retry requires runtime capability artifact.elsa.workflow-definition.apply.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deploy revision" })).toBeDisabled();
    expect(screen.getByText("Resolve deployability blockers before queueing deployment.")).toBeInTheDocument();
  });

  it("renders environment detail with engine cards that link to detail pages", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev");

    expect(await screen.findByRole("heading", { name: "Dev" })).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev/revisions/new")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/revisions/00000000-0000-0000-0000-000000000142")).toBeInTheDocument();
    expect(screen.getByText("Engine registrations")).toBeInTheDocument();
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev/engines/dev-engine")).toBeInTheDocument();
    expect(screen.queryByText("Engine details")).not.toBeInTheDocument();
    expect(screen.getByText("Promotion")).toBeInTheDocument();
    expect(screen.getByText("Observability")).toBeInTheDocument();
    expect(screen.getAllByText("Run history").length).toBeGreaterThan(0);
  });

  it("links promotion blocker to source revision creation when valid artifacts exist", async () => {
    const cockpit = {
      ...deploymentCockpitFixture,
      applications: deploymentCockpitFixture.applications.map((application) => ({
        ...application,
        environments: application.environments.map((environment) =>
          environment.id === "claims-dev"
            ? { ...environment, desiredRevision: { id: "", revision: 0, commit: "", label: "", authoredAt: "" } }
            : environment)
      }))
    };
    renderDeployments(cockpit, "/admin/deployments/applications/claims-ops/environments/claims-dev");

    expect(await screen.findByText("Source revision")).toBeInTheDocument();
    expect(screen.getByText("Dev does not have a desired-state revision yet. Create or choose a source revision before previewing promotion.")).toBeInTheDocument();
    await waitFor(() =>
      expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev/revisions/new")).toBeInTheDocument()
    );
  });

  it("explains production observability validation and links to a revision with a binding", async () => {
    const cockpit = {
      ...deploymentCockpitFixture,
      comparisons: [
        {
          ...deploymentCockpitFixture.comparisons[1],
          targetEnvironmentId: "claims-prod",
          targetRevision: 40,
          validations: [
            {
              id: "deployment.tier.observability-required",
              severity: "Blocker" as const,
              scope: "Observability",
              message: "Production requires at least one observability binding."
            }
          ]
        }
      ]
    };
    renderDeployments(cockpit, "/admin/deployments/applications/claims-ops/environments/claims-prod");

    expect(await screen.findByText("Deployment blockers")).toBeInTheDocument();
    expect((await screen.findAllByText("Production requires at least one observability binding.")).length).toBeGreaterThan(0);
    expect(screen.getAllByText(/Production promotion requires the source revision to declare where runtime telemetry will be sent/).length).toBeGreaterThan(0);
    expect(linkByHref("/admin/deployments/applications/claims-ops/environments/claims-dev/revisions/new?includeRequirement=observability-binding")).toBeInTheDocument();
  });

  it("renders not-found states for unknown hierarchy ids", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/missing-app");

    expect(await screen.findByText("Application not found")).toBeInTheDocument();
  });

  it("creates an environment from the application route without registering an engine", async () => {
    const fetchMock = renderDeployments(applicationWithoutSetupCockpit, "/admin/deployments/applications/policy-app/environments/new");

    expect(await screen.findByRole("heading", { name: "Add environment" })).toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText("Tier")).toHaveValue("tier-production"));
    await userEvent.clear(screen.getByLabelText("Environment"));
    await userEvent.type(screen.getByLabelText("Environment"), "Prod");
    expect(screen.queryByLabelText("Engine name")).not.toBeInTheDocument();
    expect(screen.queryByLabelText("Engine base URL")).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Add environment" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications/policy-app/environments`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(fetchMock.mock.calls.some(([input, init]) => input.toString().includes("/engines") && init?.method === "POST")).toBe(false);
  }, 15000);

  it("edits application metadata through a dedicated route", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/edit");

    expect(await screen.findByRole("heading", { name: "Edit application" })).toBeInTheDocument();
    await userEvent.clear(screen.getByLabelText("Application name"));
    await userEvent.type(screen.getByLabelText("Application name"), "Claims Control");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops`),
        expect.objectContaining({ method: "PUT" })
      )
    );
  }, 15000);

  it("edits environment metadata through a dedicated route", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/edit");

    expect(await screen.findByRole("heading", { name: "Edit environment" })).toBeInTheDocument();
    expect(screen.getByLabelText("Tier")).toHaveValue("tier-dev");
    await userEvent.clear(screen.getByLabelText("Environment"));
    await userEvent.type(screen.getByLabelText("Environment"), "Development");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-dev`),
        expect.objectContaining({ method: "PUT" })
      )
    );
  });

  it("creates a desired-state revision from a registered artifact", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/revisions/new");

    expect(await screen.findByRole("heading", { name: "New revision" })).toBeInTheDocument();
    expect((await screen.findAllByText("Payment Retry 8")).length).toBeGreaterThan(0);
    expect(screen.getByText("No additional desired-state records are required for Dev.")).toBeInTheDocument();
    expect(screen.queryByText("Observability binding")).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "local:///tmp/payment-retry-dev.zip" })).toHaveAttribute(
      "href",
      `/api/workspaces/${workspaceId}/artifacts/11111111-1111-1111-1111-111111111111/download`
    );
    await userEvent.type(screen.getByLabelText("Revision label"), "Payment retry v8");
    await userEvent.type(screen.getByLabelText("Commit"), "8f6a9c1");
    await userEvent.click(screen.getByRole("button", { name: "Create revision" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-dev/revisions`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(requestBody(fetchMock, "POST", "/environments/claims-dev/revisions")).toMatchObject({
      label: "Payment retry v8",
      commit: "8f6a9c1",
      records: [
        {
          kind: "ArtifactReference",
          name: "Payment Retry 8",
          payload: {
            artifactRecordId: "11111111-1111-1111-1111-111111111111",
            artifactId: "sha256:payment-retry-dev",
            artifactTypeId: "elsa.workflow-definition",
            contentDigest: { algorithm: "sha256", value: "dev-digest" }
          }
        }
      ]
    });
  }, 15000);

  it("creates a desired-state revision with a contextual observability binding", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/revisions/new?includeRequirement=observability-binding");

    expect(await screen.findByRole("heading", { name: "New revision" })).toBeInTheDocument();
    expect((await screen.findAllByText("Payment Retry 8")).length).toBeGreaterThan(0);
    expect(screen.getByText("Observability binding")).toBeInTheDocument();
    expect(screen.getByText("Included from a validation action for a target environment.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create revision" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-dev/revisions`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(requestBody(fetchMock, "POST", "/environments/claims-dev/revisions")).toMatchObject({
      records: [
        { kind: "ArtifactReference" },
        {
          kind: "ObservabilityBinding",
          name: "Traces - OpenTelemetry Collector",
          payload: {
            kind: "Traces",
            provider: "OpenTelemetry Collector",
            scope: "Dev / workflow runtime"
          }
        }
      ]
    });
  }, 15000);

  it("shows and submits required observability for production revisions", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-prod/revisions/new");

    expect(await screen.findByRole("heading", { name: "New revision" })).toBeInTheDocument();
    expect(await screen.findByText("Observability binding")).toBeInTheDocument();
    expect(screen.getByText("Required by Production tier.")).toBeInTheDocument();
    expect(screen.getByText("Required")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create revision" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-prod/revisions`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(requestBody(fetchMock, "POST", "/environments/claims-prod/revisions")).toMatchObject({
      records: [
        { kind: "ArtifactReference" },
        {
          kind: "ObservabilityBinding",
          payload: {
            kind: "Traces",
            provider: "OpenTelemetry Collector",
            scope: "Prod / workflow runtime"
          }
        }
      ]
    });
  }, 15000);

  it("edits engine metadata through a dedicated route", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/engines/dev-engine/edit");

    expect(await screen.findByRole("heading", { name: "Edit engine" })).toBeInTheDocument();
    expect(screen.queryByLabelText("Credential provider")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Credential store")).toHaveValue("secret-store-azure");
    expect(screen.getByLabelText("Credential reference")).toHaveValue("credential-reference-dev");
    await userEvent.clear(screen.getByLabelText("Engine base URL"));
    await userEvent.type(screen.getByLabelText("Engine base URL"), "https://dev-workflows-2.acme.example/elsa");
    await userEvent.selectOptions(screen.getByLabelText("Credential reference"), "credential-reference-prod");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/engines/dev-engine`),
        expect.objectContaining({ method: "PUT" })
      )
    );
    expect(requestBody(fetchMock, "PUT", "/deployments/engines/dev-engine")).toMatchObject({
      baseUrl: "https://dev-workflows-2.acme.example/elsa",
      credentialProvider: "Azure Key Vault",
      credentialReference: "kv://acme-elsa-control/prod/elsa-api",
      credentialReferenceId: "credential-reference-prod",
      credentialAssignmentStatus: "Assigned"
    });
  }, 15000);

  it("creates an engine-specific credential reference while editing an engine", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/engines/dev-engine/edit");

    expect(await screen.findByRole("heading", { name: "Edit engine" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Create for this engine" }));
    await userEvent.selectOptions(screen.getByLabelText("Credential store"), "secret-store-local");
    await userEvent.type(screen.getByLabelText("Secret value"), "edited-engine-local-secret");
    await userEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/credential-references")).toMatchObject({
        name: "claims-dev-weu-01 engine credential",
        reference: "local://engine-credentials/claims-dev-weu-01-engine-credential",
        secretValue: "edited-engine-local-secret"
      })
    );
    expect(screen.queryByText("edited-engine-local-secret")).not.toBeInTheDocument();
    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/engines/dev-engine`),
        expect.objectContaining({ method: "PUT" })
      )
    );
    expect(requestBody(fetchMock, "PUT", "/deployments/engines/dev-engine")).toMatchObject({
      credentialProvider: "Local encrypted database",
      credentialReference: "local://engine-credentials/claims-dev-weu-01-engine-credential",
      credentialReferenceId: "credential-reference-4",
      credentialAssignmentStatus: "Assigned"
    });
  }, 15000);

  it("registers another workflow engine for an existing environment", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/engines/new");

    expect(await screen.findByRole("heading", { name: "Register engine" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText("Engine name"), "claims-dev-weu-02");
    await userEvent.type(screen.getByLabelText("Engine base URL"), "https://claims-dev-weu-02.example/elsa");
    await userEvent.selectOptions(screen.getByLabelText("Credential store"), "secret-store-local");
    await userEvent.type(screen.getByLabelText("Secret value"), "engine-local-secret");
    await userEvent.click(screen.getByRole("button", { name: "Register engine" }));

    await waitFor(() =>
      expect(requestBody(fetchMock, "POST", "/credential-references")).toMatchObject({
        name: "claims-dev-weu-02 engine credential",
        reference: "local://engine-credentials/claims-dev-weu-02-engine-credential",
        secretValue: "engine-local-secret"
      })
    );
    expect(screen.queryByText("engine-local-secret")).not.toBeInTheDocument();
    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/environments/claims-dev/engines`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(requestBody(fetchMock, "POST", "/deployments/environments/claims-dev/engines")).toMatchObject({
      name: "claims-dev-weu-02",
      baseUrl: "https://claims-dev-weu-02.example/elsa",
      credentialReferenceId: "credential-reference-4"
    });
  }, 15000);

  it("verifies a selected workflow engine through the live API", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev/engines/dev-engine");

    expect((await screen.findAllByRole("heading", { name: "claims-dev-weu-01" })).length).toBeGreaterThan(0);
    expect(screen.getByText("Engine details")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Verify" }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/engines/dev-engine/verify`),
        expect.objectContaining({ method: "POST" })
      )
    );
    expect(await screen.findByRole("status")).toHaveTextContent("Endpoint responded successfully.");
  });

  it("runs supported engine controls from an environment detail", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-stage/engines/stage-engine");

    expect((await screen.findAllByRole("heading", { name: "claims-stage-weu-01" })).length).toBeGreaterThan(0);
    expect(screen.getByText("Engine details")).toBeInTheDocument();
    expect(screen.getAllByText("claims-stage-weu-01").length).toBeGreaterThan(0);
    expect(screen.getByText("Pause Processing")).toBeInTheDocument();
    expect(screen.getAllByText("Reload Configuration").length).toBeGreaterThan(0);
    expect(screen.queryByText("Restart Shell")).not.toBeInTheDocument();

    await userEvent.click(screen.getAllByRole("button", { name: "Run" })[1]);

    expect(await screen.findByRole("status")).toHaveTextContent("Reload Configuration executed for claims-stage-weu-01.");
  }, 15000);

  it("keeps promotion validation and deployment actions on environment detail", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-prod");

    expect(await screen.findByRole("heading", { name: "Prod" })).toBeInTheDocument();
    expect(screen.getAllByText("Secret references").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Payment API secret reference is missing or not verified in Prod.").length).toBeGreaterThan(0);
    expect(screen.getByText(/Promotion creates a target desired-state revision in/)).toHaveTextContent("Promotion creates a target desired-state revision in Prod from Stage.");
    expect(screen.getByLabelText("Promote from")).toHaveValue("claims-stage");
    expect(screen.queryByLabelText("Promote into")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deploy Target Revision" })).toBeDisabled();
    expect(screen.getByRole("button", { name: /Roll Back to r39/i })).toBeEnabled();
  });

  it("promotes from a source-only environment into an eligible target", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev");

    expect(await screen.findByRole("heading", { name: "Dev" })).toBeInTheDocument();
    expect(screen.getByText(/Promotion creates a target desired-state revision in/)).toHaveTextContent("Promotion creates a target desired-state revision in Test from Dev.");
    expect(screen.queryByLabelText("Promote from")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Promote into")).toHaveValue("claims-test");
    await userEvent.click(screen.getByRole("button", { name: "Preview promotion" }));
    expect(await screen.findByText("Live validation passed for Test.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Promote only" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Target revision r43 created");
    await userEvent.click(screen.getByRole("button", { name: "Deploy Target Revision" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Deployment run queued");

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/promotions/preview`),
      expect.objectContaining({ method: "POST" })
    );
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/promotions`),
      expect.objectContaining({ method: "POST" })
    );
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/runs`),
      expect.objectContaining({ method: "POST" })
    );
    expect(requestBody(fetchMock, "POST", "/deployments/runs")).toMatchObject({
      sourceRevisionId: "00000000-0000-0000-0000-000000000243"
    });
  }, 10000);

  it("promotes and deploys in one step from the promotion gate", async () => {
    const fetchMock = renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-dev");

    expect(await screen.findByRole("heading", { name: "Dev" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Preview promotion" }));
    expect(await screen.findByText("Live validation passed for Test.")).toBeInTheDocument();
    // Promote & deploy chains promotion, confirmation, and the run behind a single primary action.
    await userEvent.click(screen.getByRole("button", { name: "Promote & deploy" }));
    expect(await screen.findByRole("status")).toHaveTextContent("promoted and deployment run");

    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/promotions`),
      expect.objectContaining({ method: "POST" })
    );
    expect(fetchMock).toHaveBeenCalledWith(
      expect.stringContaining(`/api/workspaces/${workspaceId}/deployments/confirmations`),
      expect.objectContaining({ method: "POST" })
    );
    expect(requestBody(fetchMock, "POST", "/deployments/runs")).toMatchObject({
      sourceRevisionId: "00000000-0000-0000-0000-000000000243"
    });
  }, 10000);

  it("scopes promotion selectors to the current application and resets preview when direction changes", async () => {
    renderDeployments(multipleApplicationsCockpit, "/admin/deployments/applications/claims-ops/environments/claims-test");

    expect(await screen.findByRole("heading", { name: "Test" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Promote into this environment" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Promote from this environment" })).toBeInTheDocument();
    const sourceSelector = screen.getByLabelText("Promote from") as HTMLSelectElement;
    expect(sourceSelector).toHaveValue("claims-dev");
    expect(Array.from(sourceSelector.options).map((option) => option.value)).toEqual(["claims-dev", "claims-stage"]);

    await userEvent.click(screen.getByRole("button", { name: "Preview promotion" }));
    expect(await screen.findByText("Live validation passed for Test.")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Promote from this environment" }));

    const targetSelector = screen.getByLabelText("Promote into") as HTMLSelectElement;
    expect(targetSelector).toHaveValue("claims-stage");
    expect(Array.from(targetSelector.options).map((option) => option.value)).toEqual(["claims-stage", "claims-prod"]);
    expect(screen.queryByText("Live validation passed for Test.")).not.toBeInTheDocument();
    expect(screen.getByText("No comparison available")).toBeInTheDocument();
  });

  it("explains why promotion preview is unavailable when the source revision is missing", async () => {
    renderDeployments(cockpitWithMissingSourceRevision(), "/admin/deployments/applications/claims-ops/environments/claims-dev");

    expect(await screen.findByRole("heading", { name: "Dev" })).toBeInTheDocument();
    expect(screen.getByText("Promotion requirements")).toBeInTheDocument();
    expect(screen.getByText("Dev does not have a desired-state revision yet. Create or choose a source revision before previewing promotion.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Preview promotion" })).toBeDisabled();
  });

  it("keeps assistant plans immutable and scoped to environment detail", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-prod");

    expect(await screen.findByRole("heading", { name: "Prod" })).toBeInTheDocument();
    expect(screen.getByText("Assistant plan plan-20260522-001 v3")).toBeInTheDocument();
    expect(screen.getByText("Proposed actions")).toBeInTheDocument();
    expect(screen.getByText("Executed actions")).toBeInTheDocument();
    expect(screen.getByText("No Elsa Control mutations executed.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Approve Plan" })).toBeDisabled();

    await userEvent.click(screen.getByRole("button", { name: "Reject Plan" }));
    expect(screen.getByRole("status")).toHaveTextContent("Plan marked Rejected");
  }, 15000);

  it("shows deployment run history and drift on environment detail", async () => {
    renderDeployments(undefined, "/admin/deployments/applications/claims-ops/environments/claims-prod");

    expect(await screen.findByRole("heading", { name: "Prod" })).toBeInTheDocument();
    expect(screen.getByText("Deployment blockers")).toBeInTheDocument();
    expect(screen.getAllByText("Payment API secret reference is missing or not verified in Prod.").length).toBeGreaterThan(0);
    expect(screen.getAllByText("claims-prod-weu-01 does not advertise engine.reload-configuration.").length).toBeGreaterThan(0);
    expect(screen.getAllByText(/claims-prod-weu-01 is unreachable/i).length).toBeGreaterThan(0);
    expect(screen.getAllByText("Run history").length).toBeGreaterThan(0);
    expect(screen.getByText("Mira Chen")).toBeInTheDocument();
    expect(screen.getByText("Latest Blocked")).toBeInTheDocument();
    expect(screen.getByText("Deploy Running")).toBeInTheDocument();
    expect(screen.getByText("Applying workflow definitions")).toBeInTheDocument();
  });

  it("keeps setup panel tier behavior independent of routed pages", async () => {
    const submit = vi.fn();
    const firstProductionTier = tier("tier-production-old", "Production", 40, ["deployment.tier.production-like"]);
    const currentProductionTier = tier("tier-production-current", "Production", 40, ["deployment.tier.production-like"]);
    const { rerender } = render(
      <DeploymentSetupPanel
        canManageSetup
        tiers={[firstProductionTier]}
        isSubmitting={false}
        onSubmit={submit}
      />
    );

    await waitFor(() => expect(screen.getByLabelText("Tier")).toHaveValue("tier-production-old"));
    rerender(
      <DeploymentSetupPanel
        canManageSetup
        tiers={[currentProductionTier]}
        isSubmitting={false}
        onSubmit={submit}
      />
    );

    await waitFor(() => expect(screen.getByLabelText("Tier")).toHaveValue("tier-production-current"));
  });
});

type DeploymentFetchMockOptions = {
  permissions?: string[];
  secretStores?: WorkspaceDeploymentSecretStore[];
  credentialReferences?: WorkspaceDeploymentCredentialReference[];
};

function renderDeployments(cockpit: DeploymentCockpit = deploymentCockpitFixture, initialEntry = "/admin/deployments", options: DeploymentFetchMockOptions = {}) {
  const fetchMock = createDeploymentFetchMock(cockpit, options);
  vi.stubGlobal("fetch", fetchMock);
  render(
    <TestQueryProvider>
      <MemoryRouter initialEntries={[initialEntry]}>
        <AuthProvider>
          <WorkspaceContextProvider>
            <Routes>
              <Route path="/admin/deployments" element={<DeploymentsPage />} />
              <Route path="/admin/deployments/new" element={<NewDeploymentSetupPage />} />
              <Route path="/admin/deployments/credentials" element={<DeploymentCredentialsPage />} />
              <Route path="/admin/deployments/credentials/stores/new" element={<DeploymentCredentialStoreCreatePage />} />
              <Route path="/admin/deployments/credentials/stores/:secretStoreId/edit" element={<DeploymentCredentialStoreEditPage />} />
              <Route path="/admin/deployments/credentials/references/new" element={<DeploymentCredentialReferenceCreatePage />} />
              <Route path="/admin/deployments/credentials/references/:credentialReferenceId" element={<DeploymentCredentialReferencePage />} />
              <Route path="/admin/deployments/credentials/references/:credentialReferenceId/edit" element={<DeploymentCredentialReferenceEditPage />} />
              <Route path="/admin/deployments/applications" element={<DeploymentApplicationsPage />} />
              <Route path="/admin/deployments/applications/:applicationId" element={<DeploymentApplicationPage />} />
              <Route path="/admin/deployments/applications/:applicationId/edit" element={<DeploymentApplicationEditPage />} />
              <Route path="/admin/deployments/applications/:applicationId/revisions" element={<DeploymentApplicationRevisionsPage />} />
              <Route path="/admin/deployments/applications/:applicationId/revisions/:revisionId" element={<DeploymentRevisionDetailPage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/new" element={<DeploymentEnvironmentCreatePage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/:environmentId" element={<DeploymentEnvironmentPage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/:environmentId/edit" element={<DeploymentEnvironmentEditPage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/:environmentId/revisions/new" element={<DeploymentRevisionCreatePage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/:environmentId/engines/new" element={<DeploymentEngineRegisterPage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/:environmentId/engines/:engineId" element={<DeploymentEnginePage />} />
              <Route path="/admin/deployments/applications/:applicationId/environments/:environmentId/engines/:engineId/edit" element={<DeploymentEngineEditPage />} />
            </Routes>
          </WorkspaceContextProvider>
        </AuthProvider>
      </MemoryRouter>
    </TestQueryProvider>
  );
  return fetchMock;
}

function desiredStateRequirements(environment: DeploymentCockpit["applications"][number]["environments"][number]) {
  const requiresObservability = environment.tierCapabilities?.includes("deployment.observability.required") ?? false;
  return {
    environmentId: environment.id,
    environmentName: environment.name,
    tierName: environment.tierName || environment.tier,
    tierCapabilities: environment.tierCapabilities ?? [],
    requirements: requiresObservability
      ? [
          {
            id: "observability-binding",
            capabilityId: "deployment.observability.required",
            recordKind: "ObservabilityBinding",
            label: "Observability binding",
            description: "Requires at least one logs, metrics, traces, or console telemetry binding.",
            validationId: "deployment.tier.observability-required",
            required: true,
            applicability: "CurrentTier"
          }
        ]
      : []
  };
}

function createDeploymentFetchMock(cockpit: DeploymentCockpit, options: DeploymentFetchMockOptions = {}) {
  let currentCockpit = cockpit;
  let secretStores = [...(options.secretStores ?? deploymentSecretStores)];
  let credentialReferences = [...(options.credentialReferences ?? deploymentCredentialReferences)];
  const permissions = options.permissions ?? [
    "deployments.read",
    "deployments.setup.manage",
    "deployments.desired-state.manage",
    "deployments.promotion.preview",
    "deployments.run.execute",
    "deployments.rollback.execute",
    "deployments.controls.execute"
  ];
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    const method = init?.method ?? (input instanceof Request ? input.method : "GET");
    if (url.endsWith("/api/auth/session"))
      return jsonResponse({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" });
    if (url.endsWith("/api/me/organizations"))
      return jsonResponse(workspaceContextFixture());
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/permissions`)) {
      return jsonResponse({ permissions });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/tier-capabilities`)) {
      return jsonResponse({ capabilities: tierCapabilities });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/tiers`)) {
      return jsonResponse({ tiers: deploymentTiers });
    }
    if (url.includes(`/api/workspaces/${workspaceId}/deployments/environments/`) && url.endsWith("/desired-state-requirements")) {
      const environmentId = decodeURIComponent(url.split("/deployments/environments/")[1]?.split("/desired-state-requirements")[0] ?? "");
      const environment = currentCockpit.applications.flatMap((application) => application.environments).find((item) => item.id === environmentId);
      return environment ? jsonResponse(desiredStateRequirements(environment)) : jsonResponse({ title: "Not found" }, 404);
    }
    if (method === "GET" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/secret-stores`)) {
      return jsonResponse({ items: secretStores });
    }
    if (method === "GET" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/credential-references/[^/]+/usage$`))) {
      const credentialReferenceId = decodeURIComponent(url.split("/credential-references/")[1]?.split("/usage")[0] ?? "");
      return jsonResponse({
        items: credentialReferenceId === "credential-reference-dev"
          ? [{
              engineId: "engine-dev-1",
              engineName: "claims-dev-01",
              applicationId: "claims-ops",
              applicationName: "Claims Operations",
              environmentId: "claims-dev",
              environmentName: "Dev"
            }]
          : []
      });
    }
    if (method === "GET" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/credential-references`)) {
      return jsonResponse({ items: credentialReferences });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/artifacts`)) {
      return jsonResponse({ items: workspaceArtifacts });
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/revisions`)) {
      return jsonResponse({ items: revisionSummaries(currentCockpit, "claims-ops") });
    }
    if (method === "POST" && url.endsWith("/deployability")) {
      const revisionId = decodeURIComponent(url.split("/deployments/revisions/")[1]?.replace("/deployability", "") ?? "");
      const detail = revisionDetail(currentCockpit, revisionId);
      if (!detail)
        return jsonResponse({ title: "Not found" }, 404);
      const isBlocked = revisionId === "00000000-0000-0000-0000-000000000141";
      return jsonResponse({
        workspaceId,
        revisionId,
        environmentId: detail.summary.revision.environmentId,
        targetEngineId: detail.summary.revision.environmentId === "claims-stage" ? "stage-engine" : "dev-engine",
        mode: "Apply",
        status: isBlocked ? "Blocked" : "Deployable",
        evaluatedAt: "2026-05-26T10:00:00Z",
        artifacts: [
          {
            artifactRecordId: "00000000-0000-0000-0000-000000000900",
            recordName: "Payment Retry",
            artifactId: "payment-retry",
            artifactTypeId: "elsa.workflow-definition",
            artifactSchemaVersion: "1.0",
            contentDigest: { algorithm: "sha256", value: "stage-digest" },
            status: isBlocked ? "Blocked" : "Deployable",
            requiredCapabilities: ["artifact.elsa.workflow-definition.apply"],
            missingCapabilities: isBlocked ? ["artifact.elsa.workflow-definition.apply"] : [],
            payloadAvailable: true,
            diagnostics: []
          }
        ],
        blockers: isBlocked
          ? [
              {
                id: "artifact.capability.missing",
                severity: "Blocker",
                scope: "EngineCapabilities",
                message: "Payment Retry requires runtime capability artifact.elsa.workflow-definition.apply.",
                remediation: "Refresh the engine heartbeat or install the runtime applier for the missing capability.",
                artifactRecordId: "00000000-0000-0000-0000-000000000900",
                engineId: "stage-engine"
              }
            ]
          : [],
        canDeploy: !isBlocked
      });
    }
    if (url.includes(`/api/workspaces/${workspaceId}/deployments/revisions/`)) {
      const revisionId = decodeURIComponent(url.split("/deployments/revisions/")[1] ?? "");
      const detail = revisionDetail(currentCockpit, revisionId);
      return detail ? jsonResponse(detail) : jsonResponse({ title: "Not found" }, 404);
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/cockpit`)) {
      return jsonResponse(currentCockpit);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications`)) {
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name?: string };
      const application = {
        id: "app-created",
        name: body.name ?? "Claims Operations",
        workspaceName: "Acme Insurance",
        environments: []
      };
      currentCockpit = {
        ...currentCockpit,
        applications: [...currentCockpit.applications, application]
      };
      return jsonResponse({ id: application.id, workspaceId, name: application.name }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/app-created/environments`)) {
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name?: string; tier?: DeploymentCockpit["applications"][number]["environments"][number]["tier"]; tierId?: string };
      const environment = {
        id: "env-created",
        name: body.name ?? "Prod",
        tier: body.tier ?? "Production",
        tierId: body.tierId ?? "tier-production",
        tierName: body.tier ?? "Production",
        tierStatus: "Active",
        tierCapabilities: [
          "deployment.tier.production-like",
          "deployment.promotion.target",
          "deployment.confirmation.required",
          "deployment.rollback.enabled",
          "deployment.secret-verification.required",
          "deployment.observability.required"
        ],
        health: "Healthy",
        desiredRevision: { id: "", revision: 0, commit: "", label: "No desired revision", authoredAt: "2026-05-26T10:00:00Z" },
        deployedRevision: null,
        deploymentStatus: "Succeeded",
        driftStatus: "Unknown",
        engineIds: []
      } satisfies DeploymentCockpit["applications"][number]["environments"][number];
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) =>
          application.id === "app-created"
            ? { ...application, environments: [...application.environments, environment] }
            : application
        )
      };
      return jsonResponse({ id: environment.id, workspaceId, applicationId: "app-created", name: environment.name }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/secret-stores`)) {
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name: string; provider: string | null; type?: WorkspaceDeploymentSecretStore["type"]; description?: string | null };
      const store = deploymentSecretStore(`secret-store-${secretStores.length + 1}`, body.name, providerLabel(body.type, body.provider), body.description ?? null, body.type);
      secretStores = [...secretStores, store];
      return jsonResponse(store, 201);
    }
    if (method === "PUT" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/secret-stores/[^/]+$`))) {
      const secretStoreId = decodeURIComponent(url.split("/secret-stores/")[1] ?? "");
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name: string; provider: string | null; type?: WorkspaceDeploymentSecretStore["type"]; description?: string | null };
      secretStores = secretStores.map((store) =>
        store.id === secretStoreId
          ? { ...store, name: body.name, provider: providerLabel(body.type ?? store.type, body.provider), type: body.type ?? store.type, description: body.description ?? null }
          : store
      );
      return jsonResponse(secretStores.find((store) => store.id === secretStoreId));
    }
    if (method === "POST" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/secret-stores/[^/]+/credential-references$`))) {
      const secretStoreId = decodeURIComponent(url.split("/secret-stores/")[1]?.split("/credential-references")[0] ?? "");
      const store = secretStores.find((item) => item.id === secretStoreId) ?? secretStores[0];
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name: string; reference: string; description?: string | null; secretValue?: string | null };
      const reference = deploymentCredentialReference(
        `credential-reference-${credentialReferences.length + 1}`,
        store.id,
        store.name,
        store.provider,
        body.name,
        body.reference,
        body.description ?? null,
        store.type,
        Boolean(body.secretValue)
      );
      credentialReferences = [...credentialReferences, reference];
      return jsonResponse(reference, 201);
    }
    if (method === "PUT" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/credential-references/[^/]+$`))) {
      const credentialReferenceId = decodeURIComponent(url.split("/credential-references/")[1] ?? "");
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name: string; reference: string; description?: string | null };
      credentialReferences = credentialReferences.map((reference) =>
        reference.id === credentialReferenceId
          ? { ...reference, name: body.name, reference: body.reference, description: body.description ?? null }
          : reference
      );
      return jsonResponse(credentialReferences.find((reference) => reference.id === credentialReferenceId));
    }
    if (method === "POST" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/credential-references/[^/]+/rotate$`))) {
      const credentialReferenceId = decodeURIComponent(url.split("/credential-references/")[1]?.split("/rotate")[0] ?? "");
      credentialReferences = credentialReferences.map((reference) =>
        reference.id === credentialReferenceId
          ? { ...reference, hasProtectedSecret: true, updatedAt: "2026-05-26T12:00:00Z" }
          : reference
      );
      return jsonResponse(credentialReferences.find((reference) => reference.id === credentialReferenceId));
    }
    if (method === "POST" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/secret-stores/[^/]+/archive$`))) {
      const secretStoreId = decodeURIComponent(url.split("/secret-stores/")[1]?.split("/archive")[0] ?? "");
      secretStores = secretStores.map((store) => store.id === secretStoreId ? { ...store, status: "Archived", archivedAt: "2026-05-26T12:00:00Z" } : store);
      credentialReferences = credentialReferences.map((reference) =>
        reference.secretStoreId === secretStoreId ? { ...reference, status: "Archived", archivedAt: "2026-05-26T12:00:00Z" } : reference
      );
      return jsonResponse(secretStores.find((store) => store.id === secretStoreId));
    }
    if (method === "POST" && url.match(new RegExp(`/api/workspaces/${workspaceId}/deployments/credential-references/[^/]+/archive$`))) {
      const credentialReferenceId = decodeURIComponent(url.split("/credential-references/")[1]?.split("/archive")[0] ?? "");
      credentialReferences = credentialReferences.map((reference) =>
        reference.id === credentialReferenceId ? { ...reference, status: "Archived", archivedAt: "2026-05-26T12:00:00Z" } : reference
      );
      return jsonResponse(credentialReferences.find((reference) => reference.id === credentialReferenceId));
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/environments/env-created/engines`)) {
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { name?: string; credentialReferenceId?: string | null; baseUrl?: string };
      const createdEngine = {
        ...engine("engine-created", body.name ?? "claims-prod", "env-created", "Healthy", "Unverified", [
          capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
        ]),
        endpoint: {
          ...engine("engine-created", body.name ?? "claims-prod", "env-created", "Healthy", "Unverified", []).endpoint,
          baseUrl: body.baseUrl ?? "https://claims-prod.example/elsa"
        }
      };
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) =>
          application.id === "app-created"
            ? {
                ...application,
                environments: application.environments.map((environment) =>
                  environment.id === "env-created"
                    ? { ...environment, engineIds: [...environment.engineIds, createdEngine.id] }
                    : environment
                )
              }
            : application
        ),
        engines: [...currentCockpit.engines, createdEngine]
      };
      return jsonResponse(createdEngine, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/policy-app/environments`)) {
      return jsonResponse({ id: "policy-prod", workspaceId, applicationId: "policy-app", name: "Prod" }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/environments/policy-prod/engines`)) {
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) =>
          application.id === "policy-app"
            ? {
                ...application,
                environments: [
                  ...application.environments,
                  {
                    id: "policy-prod",
                    name: "Prod",
                    tier: "Production",
                    tierId: "tier-production",
                    tierName: "Production",
                    tierStatus: "Active",
                    tierCapabilities: [
                      "deployment.tier.production-like",
                      "deployment.promotion.target",
                      "deployment.confirmation.required",
                      "deployment.rollback.enabled",
                      "deployment.secret-verification.required",
                      "deployment.observability.required"
                    ],
                    health: "Healthy",
                    desiredRevision: { id: "00000000-0000-0000-0000-000000000250", revision: 1, commit: "initial", label: "Initial desired state", authoredAt: "2026-05-26T10:00:00Z" },
                    deployedRevision: null,
                    deploymentStatus: "Succeeded",
                    driftStatus: "Unknown",
                    engineIds: ["policy-prod-engine"]
                  }
                ]
              }
            : application
        ),
        engines: [
          ...currentCockpit.engines,
          engine("policy-prod-engine", "policy-prod-weu-01", "policy-prod", "Healthy", "Verified", [
            capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
          ])
        ]
      };
      return jsonResponse({ id: "policy-prod-engine", name: "policy-prod-weu-01", environmentId: "policy-prod" }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/environments/claims-dev/engines`)) {
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) =>
          application.id === "claims-ops"
            ? {
                ...application,
                environments: application.environments.map((environment) =>
                  environment.id === "claims-dev"
                    ? { ...environment, engineIds: [...environment.engineIds, "dev-engine-secondary"] }
                    : environment
                )
              }
            : application
        ),
        engines: [
          ...currentCockpit.engines,
          engine("dev-engine-secondary", "claims-dev-weu-02", "claims-dev", "Healthy", "Verified", [
            capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
          ])
        ]
      };
      return jsonResponse({ id: "dev-engine-secondary", name: "claims-dev-weu-02", environmentId: "claims-dev" }, 201);
    }
    if (method === "PUT" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops`)) {
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) =>
          application.id === "claims-ops" ? { ...application, name: "Claims Control" } : application
        )
      };
      return jsonResponse({ id: "claims-ops", workspaceId, name: "Claims Control" });
    }
    if (method === "PUT" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-dev`)) {
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) => ({
          ...application,
          environments: application.environments.map((environment) =>
            environment.id === "claims-dev" ? { ...environment, name: "Development" } : environment
          )
        }))
      };
      return jsonResponse({ id: "claims-dev", workspaceId, applicationId: "claims-ops", name: "Development" });
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-dev/revisions`)) {
      currentCockpit = {
        ...currentCockpit,
        applications: currentCockpit.applications.map((application) =>
          application.id === "claims-ops"
            ? {
                ...application,
                environments: application.environments.map((environment) =>
                  environment.id === "claims-dev"
                    ? {
                        ...environment,
                        desiredRevision: {
                          id: "00000000-0000-0000-0000-000000000242",
                          revision: 43,
                          commit: "8f6a9c1",
                          label: "Payment retry v8",
                          authoredAt: "2026-05-26T11:00:00Z"
                        }
                      }
                    : environment
                )
              }
            : application
        )
      };
      return jsonResponse({
        id: "00000000-0000-0000-0000-000000000242",
        workspaceId,
        applicationId: "claims-ops",
        environmentId: "claims-dev",
        revisionNumber: 43,
        label: "Payment retry v8",
        commit: "8f6a9c1",
        contentHash: "hash-v43",
        desiredStateJson: "{}",
        authoredAt: "2026-05-26T11:00:00Z",
        createdAt: "2026-05-26T11:00:00Z",
        createdByAccountId: null
      }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/applications/claims-ops/environments/claims-prod/revisions`)) {
      return jsonResponse({
        id: "00000000-0000-0000-0000-000000000243",
        workspaceId,
        applicationId: "claims-ops",
        environmentId: "claims-prod",
        revisionNumber: 41,
        label: "Payment Retry 8",
        commit: null,
        contentHash: "hash-v41",
        desiredStateJson: "{}",
        authoredAt: "2026-05-26T11:05:00Z",
        createdAt: "2026-05-26T11:05:00Z",
        createdByAccountId: null
      }, 201);
    }
    if (method === "PUT" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/engines/dev-engine`)) {
      currentCockpit = {
        ...currentCockpit,
        engines: currentCockpit.engines.map((item) =>
          item.id === "dev-engine"
            ? { ...item, endpoint: { ...item.endpoint, baseUrl: "https://dev-workflows-2.acme.example/elsa" } }
            : item
        )
      };
      return jsonResponse({ id: "dev-engine", name: "claims-dev-weu-01", environmentId: "claims-dev" });
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/engines/dev-engine/verify`)) {
      currentCockpit = {
        ...currentCockpit,
        engines: currentCockpit.engines.map((item) =>
          item.id === "dev-engine"
            ? {
                ...item,
                health: "Healthy",
                lastHeartbeatAt: "2026-05-26T10:05:00Z",
                lastVerificationAt: "2026-05-26T10:05:00Z",
                verificationMessage: "Endpoint responded successfully.",
                credentialReference: {
                  ...item.credentialReference,
                  verificationStatus: "Verified",
                  lastVerifiedAt: "2026-05-26T10:05:00Z"
                }
              }
            : item
        )
      };
      return jsonResponse({
        engineId: "dev-engine",
        environmentId: "claims-dev",
        health: "Healthy",
        version: "Elsa 4.0.1",
        certificateStatus: "Trusted",
        credentialVerificationStatus: "Verified",
        credentialLastVerifiedAt: "2026-05-26T10:05:00Z",
        lastHeartbeatAt: "2026-05-26T10:05:00Z",
        lastVerificationAt: "2026-05-26T10:05:00Z",
        message: "Endpoint responded successfully."
      });
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/confirmations`)) {
      const body = JSON.parse(init?.body?.toString() ?? "{}") as { actionType?: string; targetId?: string };
      return jsonResponse({ id: `${body.actionType ?? "action"}-confirmation-1`, workspaceId, actionType: body.actionType, targetId: body.targetId }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/promotions/preview`)) {
      return jsonResponse({
        ...deploymentCockpitFixture.comparisons[1],
        validations: [
          { id: "secret-payment-test", severity: "Pass", scope: "Secret references", message: "Live validation passed for Test." },
          { id: "capability-test", severity: "Pass", scope: "Engine capabilities", message: "claims-test-weu-01 supports required engine operations." }
        ]
      });
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/promotions`)) {
      return jsonResponse({
        sourceRevision: {
          id: "00000000-0000-0000-0000-000000000142",
          workspaceId,
          applicationId: "claims-ops",
          environmentId: "claims-dev",
          revisionNumber: 42,
          label: "Payment retry workflow",
          commit: "8f6a9c1",
          contentHash: "source-hash",
          desiredStateJson: "{}"
        },
        targetRevision: {
          id: "00000000-0000-0000-0000-000000000243",
          workspaceId,
          applicationId: "claims-ops",
          environmentId: "claims-test",
          revisionNumber: 43,
          label: "Promoted from Dev r42",
          commit: "8f6a9c1",
          contentHash: "target-hash",
          desiredStateJson: "{}"
        },
        comparison: {
          ...deploymentCockpitFixture.comparisons[1],
          validations: [
            { id: "secret-payment-test", severity: "Pass", scope: "Secret references", message: "Live validation passed for Test." },
            { id: "capability-test", severity: "Pass", scope: "Engine capabilities", message: "claims-test-weu-01 supports required engine operations." }
          ]
        }
      }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/runs`)) {
      currentCockpit = {
        ...currentCockpit,
        history: [
          {
            id: "00000000-0000-0000-0000-000000000777",
            status: "Queued",
            revision: 42,
            actor: "account-1",
            environmentId: "claims-test",
            engineId: "test-engine",
            validationOutcome: "Passed",
            occurredAt: "2026-05-26T10:01:00Z",
            rollbackSourceRevision: null
          },
          ...currentCockpit.history
        ]
      };
      return jsonResponse({
        id: "00000000-0000-0000-0000-000000000777",
        workspaceId,
        applicationId: "claims-ops",
        environmentId: "claims-test",
        engineId: "test-engine",
        sourceRevisionId: "00000000-0000-0000-0000-000000000142",
        previousDeployedRevisionId: null,
        rollbackSourceRunId: null,
        status: "Queued",
        validationOutcome: "Passed",
        confirmationId: "Deploy-confirmation-1",
        actorAccountId: "account-1",
        queuedAt: "2026-05-26T10:01:00Z",
        startedAt: null,
        completedAt: null,
        createdAt: "2026-05-26T10:01:00Z",
        workerId: null,
        workerHeartbeatAt: null,
        attemptNumber: 1,
        recoveryReason: null,
        failureMessage: null
      }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/rollbacks`)) {
      currentCockpit = {
        ...currentCockpit,
        history: [
          {
            id: "00000000-0000-0000-0000-000000000778",
            status: "Queued",
            revision: 39,
            actor: "account-1",
            environmentId: "claims-prod",
            engineId: "prod-engine",
            validationOutcome: "Warnings",
            occurredAt: "2026-05-26T10:02:00Z",
            rollbackSourceRevision: 41
          },
          ...currentCockpit.history
        ]
      };
      return jsonResponse({
        id: "00000000-0000-0000-0000-000000000778",
        workspaceId,
        applicationId: "claims-ops",
        environmentId: "claims-prod",
        engineId: "prod-engine",
        sourceRevisionId: "00000000-0000-0000-0000-000000000139",
        previousDeployedRevisionId: "00000000-0000-0000-0000-000000000141",
        rollbackSourceRunId: "00000000-0000-0000-0000-000000000410",
        status: "Queued",
        validationOutcome: "Warnings",
        confirmationId: "Rollback-confirmation-1",
        actorAccountId: "account-1",
        queuedAt: "2026-05-26T10:02:00Z",
        startedAt: null,
        completedAt: null,
        createdAt: "2026-05-26T10:02:00Z",
        workerId: null,
        workerHeartbeatAt: null,
        attemptNumber: 1,
        recoveryReason: null,
        failureMessage: null
      }, 201);
    }
    if (method === "POST" && url.endsWith(`/api/workspaces/${workspaceId}/deployments/engines/stage-engine/controls/reload-configuration/run`)) {
      return jsonResponse({
        id: "control-execution-1",
        workspaceId,
        engineId: "stage-engine",
        environmentId: "claims-stage",
        controlId: "reload-configuration",
        controlLabel: "Reload Configuration",
        boundary: "EngineApi",
        requiredCapabilityId: "engine.reload-configuration",
        confirmationId: "confirmation-1",
        actorAccountId: "account-1",
        status: "Succeeded",
        createdAt: "2026-05-26T10:00:00Z",
        message: "Reload Configuration executed for claims-stage-weu-01."
      });
    }
    return jsonResponse({ title: "Not found" }, 404);
  });
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function requestBody(fetchMock: ReturnType<typeof createDeploymentFetchMock>, method: string, urlSuffix: string) {
  const call = fetchMock.mock.calls.find(([input, init]) => {
    const url = input instanceof Request ? input.url : input.toString();
    return url.includes(urlSuffix) && init?.method === method;
  });
  expect(call).toBeDefined();
  return JSON.parse(call![1]?.body?.toString() ?? "{}") as Record<string, unknown>;
}

function linkByHref(href: string) {
  const link = screen.getAllByRole("link").find((item) => item.getAttribute("href") === href);
  expect(link).toBeDefined();
  return link!;
}

function workspaceContextFixture() {
  return {
    account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
    organizations: [{ id: organizationId, name: "Acme Corp", role: "Owner" }],
    workspaces: [
      { id: workspaceId, name: "Acme Insurance", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" },
      { id: "00000000-0000-0000-0000-000000000011", name: "Acme Labs", kind: "Shared", role: "Reader", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" }
    ]
  };
}

function revisionSummaries(cockpit: DeploymentCockpit, applicationId: string): WorkspaceDesiredStateRevisionSummary[] {
  const application = cockpit.applications.find((item) => item.id === applicationId);
  if (!application) return [];

  return application.environments.map((environment) => {
    const latestRun = cockpit.history.find((event) => event.environmentId === environment.id && event.revision === environment.desiredRevision.revision);
    return {
      revision: {
        id: environment.desiredRevision.id,
        workspaceId,
        applicationId: application.id,
        environmentId: environment.id,
        revisionNumber: environment.desiredRevision.revision,
        label: environment.desiredRevision.label,
        commit: environment.desiredRevision.commit || null,
        contentHash: `hash-${environment.desiredRevision.revision}`,
        desiredStateJson: JSON.stringify({
          records: [
            {
              kind: "ArtifactReference",
              name: environment.desiredRevision.label,
              payload: {
                artifactId: `artifact-${environment.id}`,
                contentDigest: { algorithm: "sha256", value: `digest-${environment.desiredRevision.revision}` }
              }
            }
          ]
        }),
        authoredAt: environment.desiredRevision.authoredAt,
        createdAt: environment.desiredRevision.authoredAt,
        createdByAccountId: null
      },
      environmentName: environment.name,
      environmentTier: environment.tier,
      environmentTierId: environment.tierId ?? null,
      environmentTierName: environment.tierName ?? null,
      isCurrentDesired: true,
      isCurrentDeployed: environment.deployedRevision === environment.desiredRevision.revision,
      latestRunStatus: latestRun?.status ?? null,
      latestRunQueuedAt: latestRun?.occurredAt ?? null
    };
  });
}

function revisionDetail(cockpit: DeploymentCockpit, revisionId: string): WorkspaceDesiredStateRevisionDetail | null {
  const summary = revisionSummaries(cockpit, "claims-ops").find((item) => item.revision.id === revisionId);
  if (!summary) return null;
  const runs = cockpit.history
    .filter((event) => event.environmentId === summary.revision.environmentId && event.revision === summary.revision.revisionNumber)
    .map((event) => ({
      id: event.id,
      environmentId: event.environmentId,
      engineId: event.engineId,
      status: event.status,
      validationOutcome: event.validationOutcome,
      queuedAt: event.occurredAt,
      completedAt: event.occurredAt,
      failureMessage: null
    }));

  return {
    summary,
    records: [
      {
        id: `${revisionId}:artifact`,
        kind: "ArtifactReference",
        name: "Payment Retry",
        payloadJson: JSON.stringify({
          artifactId: "payment-retry",
          contentDigest: { algorithm: "sha256", value: "stage-digest" }
        }),
        contentHash: "record-hash",
        artifactRecordId: "00000000-0000-0000-0000-000000000900",
        artifactId: "payment-retry",
        artifactTypeId: "elsa.workflow-definition",
        artifactDigest: { algorithm: "sha256", value: "stage-digest" }
      }
    ],
    runs
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

const organizationId = "00000000-0000-0000-0000-000000000001";
const workspaceId = "00000000-0000-0000-0000-000000000010";
const deploymentSecretStores: WorkspaceDeploymentSecretStore[] = [
  deploymentSecretStore("secret-store-azure", "Control Key Vault", "Azure Key Vault", "Shared deployment credential metadata", "AzureKeyVault"),
  deploymentSecretStore("secret-store-local", "Local engine credentials", "Local encrypted database", "Protected local engine credentials", "LocalEncryptedDatabase")
];
const deploymentCredentialReferences: WorkspaceDeploymentCredentialReference[] = [
  {
    ...deploymentCredentialReference(
      "credential-reference-dev",
      "secret-store-azure",
      "Control Key Vault",
      "Azure Key Vault",
      "Dev engine API",
      "kv://acme-elsa-control/dev/elsa-api",
      "Dev workflow engine API credential",
      "AzureKeyVault"
    ),
    usageCount: 1
  },
  deploymentCredentialReference(
    "credential-reference-prod",
    "secret-store-azure",
    "Control Key Vault",
    "Azure Key Vault",
    "Prod engine API",
    "kv://acme-elsa-control/prod/elsa-api",
    "Prod workflow engine API credential",
    "AzureKeyVault"
  ),
  deploymentCredentialReference(
    "credential-reference-local",
    "secret-store-local",
    "Local engine credentials",
    "Local encrypted database",
    "Local dev engine API",
    "local://engine-credentials/local-dev-engine-api",
    "Local protected workflow engine API credential",
    "LocalEncryptedDatabase",
    true
  )
];
const tierCapabilities = [
  capabilityDefinition("deployment.tier.development-like", "Development-like", "Classification"),
  capabilityDefinition("deployment.tier.test-like", "Test-like", "Classification"),
  capabilityDefinition("deployment.tier.preproduction-like", "Pre-production-like", "Classification"),
  capabilityDefinition("deployment.tier.production-like", "Production-like", "Classification"),
  capabilityDefinition("deployment.promotion.source", "Promotion source", "Promotion"),
  capabilityDefinition("deployment.promotion.target", "Promotion target", "Promotion"),
  capabilityDefinition("deployment.confirmation.required", "Confirmation required", "Safeguards"),
  capabilityDefinition("deployment.rollback.enabled", "Rollback enabled", "Rollback"),
  capabilityDefinition("deployment.secret-verification.required", "Secret verification required", "Validation"),
  capabilityDefinition("deployment.observability.required", "Observability required", "Observability")
];
const deploymentTiers = [
  tier("tier-dev", "Dev", 10, ["deployment.tier.development-like", "deployment.promotion.source"]),
  tier("tier-test", "Test", 20, ["deployment.tier.test-like", "deployment.promotion.source", "deployment.promotion.target"]),
  tier("tier-stage", "Stage", 30, ["deployment.tier.preproduction-like", "deployment.promotion.source", "deployment.promotion.target", "deployment.secret-verification.required"]),
  tier("tier-production", "Production", 40, [
    "deployment.tier.production-like",
    "deployment.promotion.target",
    "deployment.confirmation.required",
    "deployment.rollback.enabled",
    "deployment.secret-verification.required",
    "deployment.observability.required"
  ]),
  tier("tier-uat", "UAT", 35, ["deployment.tier.preproduction-like", "deployment.promotion.target"]),
  tier("tier-legacy-prod", "Legacy Production", 50, ["deployment.tier.production-like"], "Archived")
];
const workspaceArtifacts = [
  {
    id: "11111111-1111-1111-1111-111111111111",
    workspaceId,
    artifactId: "sha256:payment-retry-dev",
    layoutVersion: "elsa-control/deployment-artifact/v1alpha1",
    contentDigest: { algorithm: "sha256", value: "dev-digest" },
    format: "Zip",
    referenceProvider: "local",
    reference: "local:///tmp/payment-retry-dev.zip",
    manifest: { name: "Payment Retry", version: "8", environment: "Dev" },
    resources: [],
    checksumStatus: "Verified",
    inspectionStatus: "Valid",
    diagnostics: [],
    registeredAt: "2026-05-26T10:00:00Z",
    registeredByAccountId: "account-1",
    lastInspectedAt: "2026-05-26T10:00:00Z",
    createdAt: "2026-05-26T10:00:00Z",
    updatedAt: "2026-05-26T10:00:00Z",
    envelopeVersion: "elsa-control/artifact-envelope/v1alpha1",
    artifactTypeId: "elsa.workflow-definition",
    artifactSchemaVersion: "1.0",
    manifestDigest: { algorithm: "sha256", value: "manifest-digest" },
    payloadReference: null,
    producer: null,
    displayMetadata: {
      name: "Payment Retry",
      version: "8",
      description: "Payment retry workflow",
      labels: { workflow: "payment-retry" },
      annotations: {},
      source: null
    },
    compatibilityHints: null
  }
] as const;

const deploymentCockpitFixture: DeploymentCockpit = {
  applications: [
    {
      id: "claims-ops",
      name: "Claims Operations",
      workspaceName: "Acme Insurance",
      environments: [
        {
          id: "claims-dev",
          name: "Dev",
          tier: "Dev",
          tierId: "tier-dev",
          tierName: "Dev",
          tierStatus: "Active",
          tierCapabilities: ["deployment.tier.development-like", "deployment.promotion.source"],
          health: "Healthy",
          desiredRevision: { id: "00000000-0000-0000-0000-000000000142", revision: 42, commit: "8f6a9c1", label: "Payment retry workflow", authoredAt: "2026-05-21T08:30:00Z" },
          deployedRevision: 42,
          deploymentStatus: "Succeeded",
          driftStatus: "InSync",
          engineIds: ["dev-engine"]
        },
        {
          id: "claims-test",
          name: "Test",
          tier: "Test",
          tierId: "tier-test",
          tierName: "Test",
          tierStatus: "Active",
          tierCapabilities: ["deployment.tier.test-like", "deployment.promotion.source", "deployment.promotion.target"],
          health: "Healthy",
          desiredRevision: { id: "00000000-0000-0000-0000-000000000139", revision: 39, commit: "79d1b07", label: "Fraud review tuning", authoredAt: "2026-05-20T13:20:00Z" },
          deployedRevision: 39,
          deploymentStatus: "Succeeded",
          driftStatus: "InSync",
          engineIds: ["test-engine"]
        },
        {
          id: "claims-stage",
          name: "Stage",
          tier: "Stage",
          tierId: "tier-stage",
          tierName: "Stage",
          tierStatus: "Active",
          tierCapabilities: ["deployment.tier.preproduction-like", "deployment.promotion.source", "deployment.promotion.target", "deployment.secret-verification.required"],
          health: "Degraded",
          desiredRevision: { id: "00000000-0000-0000-0000-000000000141", revision: 41, commit: "c174f2a", label: "Policy document sync", authoredAt: "2026-05-21T06:10:00Z" },
          deployedRevision: 40,
          deploymentStatus: "Running",
          driftStatus: "DriftDetected",
          engineIds: ["stage-engine"]
        },
        {
          id: "claims-prod",
          name: "Prod",
          tier: "Production",
          tierId: "tier-production",
          tierName: "Production",
          tierStatus: "Active",
          tierCapabilities: [
            "deployment.tier.production-like",
            "deployment.promotion.target",
            "deployment.confirmation.required",
            "deployment.rollback.enabled",
            "deployment.secret-verification.required",
            "deployment.observability.required"
          ],
          health: "Unreachable",
          desiredRevision: { id: "00000000-0000-0000-0000-000000000140", revision: 40, commit: "11ec9d4", label: "Baseline production", authoredAt: "2026-05-19T15:45:00Z" },
          deployedRevision: 40,
          deploymentStatus: "Blocked",
          driftStatus: "Unknown",
          engineIds: ["prod-engine"]
        }
      ]
    }
  ],
  engines: [
    engine("dev-engine", "claims-dev-weu-01", "claims-dev", "Healthy", "Verified", [
      capability("workflow.pause-processing", "Pause workflow processing", "Workflow"),
      capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
    ]),
    engine("test-engine", "claims-test-weu-01", "claims-test", "Healthy", "Verified", [
      capability("workflow.pause-processing", "Pause workflow processing", "Workflow"),
      capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
    ]),
    engine("stage-engine", "claims-stage-weu-01", "claims-stage", "Degraded", "Verified", [
      capability("workflow.pause-processing", "Pause workflow processing", "Workflow"),
      capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
    ]),
    engine("prod-engine", "claims-prod-weu-01", "claims-prod", "Unreachable", "Missing", [
      capability("workflow.pause-processing", "Pause workflow processing", "Workflow")
    ])
  ],
  comparisons: [
    {
      sourceEnvironmentId: "claims-stage",
      targetEnvironmentId: "claims-prod",
      sourceRevisionId: "00000000-0000-0000-0000-000000000141",
      sourceRevision: 41,
      targetRevision: 40,
      rollbackRevisionId: "00000000-0000-0000-0000-000000000139",
      rollbackRevision: 39,
      diff: [
        { id: "workflow-payment-retry", category: "Workflows", name: "Payment Retry", sourceValue: "v7 with idempotent retry", targetValue: "v6", impact: "Changed" },
        { id: "secret-payment-api", category: "SecretReferences", name: "Payment API", sourceValue: "kv://acme-elsa-control/prod/payment-api:v3", targetValue: "Missing reference", impact: "Changed" }
      ],
      validations: [
        { id: "secret-payment-api", severity: "Blocker", scope: "Secret references", message: "Payment API secret reference is missing or not verified in Prod." },
        { id: "capability-reload", severity: "Blocker", scope: "Engine capabilities", message: "claims-prod-weu-01 does not advertise engine.reload-configuration." }
      ],
      artifacts: [
        artifactComparison("Payment Retry", "sha256:payment-retry-stage", "stage-digest", "Changed", {
          displayName: "Payment Retry",
          version: "7"
        })
      ]
    },
    {
      sourceEnvironmentId: "claims-dev",
      targetEnvironmentId: "claims-test",
      sourceRevisionId: "00000000-0000-0000-0000-000000000142",
      sourceRevision: 42,
      targetRevision: 39,
      rollbackRevisionId: "00000000-0000-0000-0000-000000000138",
      rollbackRevision: 38,
      diff: [
        { id: "workflow-payment-retry-test", category: "Workflows", name: "Payment Retry", sourceValue: "v8", targetValue: "v6", impact: "Changed" },
        { id: "secret-payment-test", category: "SecretReferences", name: "Payment API", sourceValue: "kv://acme-elsa-control/test/payment-api:v3", targetValue: "kv://acme-elsa-control/test/payment-api:v2", impact: "Changed" }
      ],
      validations: [
        { id: "secret-payment-test", severity: "Pass", scope: "Secret references", message: "Required secret references are verified for Test." },
        { id: "capability-test", severity: "Pass", scope: "Engine capabilities", message: "claims-test-weu-01 supports required engine operations." }
      ],
      artifacts: [
        artifactComparison("Payment Retry", "sha256:payment-retry-dev", "dev-digest", "Changed", {
          displayName: "Payment Retry",
          version: "8"
        })
      ]
    }
  ],
  observabilityBindings: [
    { id: "prod-logs", kind: "Logs", provider: "Azure Monitor", status: "Connected", scope: "claims-prod / rev 40", correlatedRevision: 40, sample: "143 structured events in the last 30 minutes" }
  ],
  history: [
    {
      id: "00000000-0000-0000-0000-000000000410",
      status: "Blocked",
      revision: 41,
      actor: "Mira Chen",
      environmentId: "claims-prod",
      engineId: "prod-engine",
      validationOutcome: "Blocked",
      occurredAt: "2026-05-22T08:05:00Z",
      rollbackSourceRevision: null,
      commands: [
        {
          id: "command-1",
          workspaceId,
          runId: "00000000-0000-0000-0000-000000000410",
          environmentId: "claims-prod",
          engineId: "prod-engine",
          action: "Deploy",
          status: "Running",
          artifact: commandArtifact("sha256:payment-retry-stage", "stage-digest"),
          workerId: "runtime-worker-1",
          claimedAt: "2026-05-22T08:06:00Z",
          leaseExpiresAt: "2026-05-22T08:11:00Z",
          heartbeatAt: "2026-05-22T08:07:00Z",
          attemptNumber: 1,
          percentComplete: 75,
          progressMessage: "Applying workflow definitions",
          observedArtifactDigest: null,
          runtimeReference: "elsa://workflows/payment-retry",
          diagnostics: [],
          createdAt: "2026-05-22T08:05:00Z",
          updatedAt: "2026-05-22T08:07:00Z",
          completedAt: null
        }
      ]
    }
  ],
  driftReport: [
    { id: "drift-shell", environmentId: "claims-stage", engineId: "stage-engine", area: "Shell concurrency", desired: "16 workers", observed: "12 workers", action: "Review" }
  ],
  assistantPlans: [
    {
      id: "plan-20260522-001",
      version: 3,
      status: "Proposed",
      workspaceName: "Acme Insurance",
      targetEnvironmentId: "claims-prod",
      targetEngineId: "prod-engine",
      summary: "Promote revision 41 from Stage to Prod after validating secrets, reachability, and engine reload capability.",
      proposedActions: [
        "Verify Prod secret references and provider access",
        "Run desired-state diff from Stage revision 41 to Prod revision 40",
        "Apply revision 41 to claims-prod-weu-01 as one deployment",
        "Keep rollback path to revision 39 available"
      ],
      executedActions: [],
      validations: [
        { id: "assistant-scope", severity: "Pass", scope: "Workspace authorization", message: "Plan is scoped to Acme Insurance only." },
        { id: "assistant-secret", severity: "Blocker", scope: "Secret references", message: "Payment API reference must verify before approval can execute." }
      ],
      rollbackPath: "Redeploy revision 39 to claims-prod-weu-01 if revision 41 fails after validation clears.",
      allOrNothing: true,
      createdAt: "2026-05-22T08:02:00Z"
    }
  ]
};

const applicationWithoutSetupCockpit: DeploymentCockpit = {
  applications: [
    {
      id: "policy-app",
      name: "Policy",
      workspaceName: "Acme Insurance",
      environments: []
    }
  ],
  engines: [],
  comparisons: [],
  observabilityBindings: [],
  history: [],
  driftReport: [],
  assistantPlans: []
};

function cockpitWithMissingSourceRevision(): DeploymentCockpit {
  return {
    ...deploymentCockpitFixture,
    applications: deploymentCockpitFixture.applications.map((application) => application.id !== "claims-ops"
      ? application
      : {
        ...application,
        environments: application.environments.map((environment) => environment.id === "claims-dev"
          ? {
            ...environment,
            desiredRevision: {
              ...environment.desiredRevision,
              id: "",
              revision: 0,
              commit: "",
              label: "No desired revision"
            }
          }
          : environment)
      })
  };
}

const multipleApplicationsCockpit: DeploymentCockpit = {
  ...deploymentCockpitFixture,
  applications: [
    ...deploymentCockpitFixture.applications,
    {
      id: "policy-app",
      name: "Policy",
      workspaceName: "Acme Insurance",
      environments: [
        {
          id: "policy-prod",
          name: "Prod",
          tier: "Production",
          tierId: "tier-production",
          tierName: "Production",
          tierStatus: "Active",
          tierCapabilities: ["deployment.tier.production-like", "deployment.promotion.target"],
          health: "Healthy",
          desiredRevision: { id: "00000000-0000-0000-0000-000000000250", revision: 1, commit: "initial", label: "Initial desired state", authoredAt: "2026-05-26T10:00:00Z" },
          deployedRevision: 1,
          deploymentStatus: "Succeeded",
          driftStatus: "InSync",
          engineIds: ["policy-prod-engine"]
        }
      ]
    }
  ],
  engines: [
    ...deploymentCockpitFixture.engines,
    engine("policy-prod-engine", "policy-prod-weu-01", "policy-prod", "Healthy", "Verified", [
      capability("engine.reload-configuration", "Reload engine configuration", "EngineApi")
    ])
  ]
};

function engine(
  id: string,
  name: string,
  environmentId: string,
  health: DeploymentCockpit["engines"][number]["health"],
  verificationStatus: DeploymentCockpit["engines"][number]["credentialReference"]["verificationStatus"],
  capabilities: DeploymentCockpit["engines"][number]["capabilities"]
): DeploymentCockpit["engines"][number] {
  return {
    id,
    name,
    environmentId,
    endpoint: {
      baseUrl: `https://${name}.example/elsa`,
      region: "West Europe",
      version: "Elsa 4.0.1",
      certificateStatus: "Trusted"
    },
    credentialReference: {
      provider: "Azure Key Vault",
      reference: `kv://acme-elsa-control/${environmentId.replace("claims-", "")}/elsa-api`,
      verificationStatus,
      lastVerifiedAt: verificationStatus === "Verified" ? "2026-05-22T07:50:00Z" : null
    },
    credentialAssignmentStatus: "Assigned",
    health,
    lastHeartbeatAt: health === "Unreachable" ? null : "2026-05-22T08:16:30Z",
    lastVerificationAt: health === "Unreachable" ? null : "2026-05-22T08:12:30Z",
    verificationMessage: health === "Unreachable" ? "Engine has not been verified." : "Endpoint responded successfully.",
    capabilities,
    controls: [
      { id: "pause-processing", label: "Pause Processing", boundary: "Workflow", capabilityId: "workflow.pause-processing", description: "Stops new workflow dispatch without touching host infrastructure." },
      { id: "reload-configuration", label: "Reload Configuration", boundary: "EngineApi", capabilityId: "engine.reload-configuration", description: "Reloads engine API configuration from desired state." },
      { id: "restart-shell", label: "Restart Shell", boundary: "Shell", capabilityId: "shell.restart", description: "Hidden until the shell restart capability is advertised." }
    ],
    hostingProvider: null
  };
}

function deploymentSecretStore(
  id: string,
  name: string,
  provider: string,
  description: string | null,
  type: WorkspaceDeploymentSecretStore["type"] = provider === "Azure Key Vault" ? "AzureKeyVault" : "GenericExternalReference"
): WorkspaceDeploymentSecretStore {
  return {
    id,
    workspaceId,
    name,
    provider,
    type,
    description,
    status: "Active",
    createdAt: "2026-05-26T10:00:00Z",
    updatedAt: "2026-05-26T10:00:00Z",
    createdByAccountId: "account-1",
    updatedByAccountId: "account-1",
    archivedAt: null,
    archivedByAccountId: null
  };
}

function deploymentCredentialReference(
  id: string,
  secretStoreId: string,
  secretStoreName: string,
  secretStoreProvider: string,
  name: string,
  reference: string,
  description: string | null,
  secretStoreType: WorkspaceDeploymentSecretStore["type"] = secretStoreProvider === "Azure Key Vault" ? "AzureKeyVault" : "GenericExternalReference",
  hasProtectedSecret = false
): WorkspaceDeploymentCredentialReference {
  return {
    id,
    workspaceId,
    secretStoreId,
    secretStoreName,
    secretStoreProvider,
    secretStoreType,
    name,
    reference,
    description,
    status: "Active",
    verificationStatus: "Unverified",
    lastVerifiedAt: null,
    createdAt: "2026-05-26T10:00:00Z",
    updatedAt: "2026-05-26T10:00:00Z",
    createdByAccountId: "account-1",
    updatedByAccountId: "account-1",
    archivedAt: null,
    archivedByAccountId: null,
    hasProtectedSecret,
    usageCount: 0
  };
}

function providerLabel(type: WorkspaceDeploymentSecretStore["type"] | undefined, provider: string | null | undefined) {
  if (provider) return provider;
  switch (type) {
    case "LocalEncryptedDatabase":
      return "Local encrypted database";
    case "AzureKeyVault":
      return "Azure Key Vault";
    case "KubernetesSecrets":
      return "Kubernetes Secrets";
    case "EnvironmentVariableName":
      return "Environment variable name";
    default:
      return "Generic external reference";
  }
}

function artifactComparison(
  name: string,
  artifactId: string,
  digest: string,
  impact: DeploymentCockpit["comparisons"][number]["artifacts"][number]["impact"],
  metadata: Record<string, string>
): DeploymentCockpit["comparisons"][number]["artifacts"][number] {
  return {
    name,
    source: {
      artifactRecordId: "00000000-0000-0000-0000-000000000900",
      artifactId,
      artifactTypeId: "elsa.workflow-definition",
      contentDigest: { algorithm: "sha256", value: digest },
      metadata,
      configuration: { environment: "stage" },
      compatibilityHints: ["workflow-definition.apply"]
    },
    target: {
      artifactRecordId: "00000000-0000-0000-0000-000000000899",
      artifactId: "sha256:payment-retry-prod",
      artifactTypeId: "elsa.workflow-definition",
      contentDigest: { algorithm: "sha256", value: "prod-digest" },
      metadata: { displayName: "Payment Retry", version: "6" },
      configuration: { environment: "prod" },
      compatibilityHints: ["workflow-definition.apply"]
    },
    impact,
    runtimeCompatibility: [{ id: `${artifactId}-runtime`, severity: "Pass", scope: "Runtime compatibility", message: "Workflow runtime capability is present." }]
  };
}

function commandArtifact(artifactId: string, digest: string): NonNullable<DeploymentCockpit["history"][number]["commands"]>[number]["artifact"] {
  return {
    artifactRecordId: "00000000-0000-0000-0000-000000000900",
    artifactId,
    artifactTypeId: "elsa.workflow-definition",
    contentDigest: { algorithm: "sha256", value: digest }
  };
}

function capability(
  id: string,
  label: string,
  boundary: DeploymentCockpit["engines"][number]["capabilities"][number]["boundary"]
) {
  return { id, label, boundary };
}

function capabilityDefinition(id: string, label: string, category: string) {
  return {
    id,
    label,
    description: label,
    category,
    isDeprecated: false
  };
}

function tier(id: string, name: string, sortOrder: number, capabilities: string[], status: WorkspaceDeploymentTier["status"] = "Active"): WorkspaceDeploymentTier {
  return {
    id,
    workspaceId,
    name,
    description: null,
    sortOrder,
    isDefault: name === "Dev" || name === "Test" || name === "Stage" || name === "Production",
    status,
    capabilities,
    environmentCount: 0,
    createdAt: "2026-05-28T10:00:00Z",
    updatedAt: "2026-05-28T10:00:00Z",
    createdByAccountId: null,
    updatedByAccountId: null,
    archivedAt: status === "Archived" ? "2026-05-28T10:00:00Z" : null,
    archivedByAccountId: null
  };
}
