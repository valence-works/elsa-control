import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import { EditRuntimeBuilderPage, NewRuntimeBuilderPage, RuntimeBuilderPage } from "@/features/runtime-builder/RuntimeBuilderPage";
import type { BuilderCatalog, BuilderPlanResponse, RuntimeBuilderIntent, RuntimeConfiguration } from "@/features/runtime-builder/runtimeBuilderModels";
import { AuthProvider } from "@/lib/auth/AuthProvider";

const organizationId = "00000000-0000-0000-0000-000000000001";
const workspaceId = "00000000-0000-0000-0000-000000000010";

const catalogFixture: BuilderCatalog = {
  images: [
    {
      slug: "elsa-runtime",
      displayName: "Elsa Runtime",
      description: "Runs Elsa workflows with catalog-selected packages.",
      image: "elsa/runtime",
      availableTags: ["4.0.0", "4.0.1"],
      defaultTag: "4.0.1",
      defaultPort: 8080,
      hostPort: 13000,
      containerName: "elsa-runtime",
      licenseTier: "Community",
      stability: "Stable",
      capabilities: ["workflows", "http"],
      runtimeKinds: ["elsa.server"],
      envVars: [
        {
          name: "ELSA_CONNECTION_STRING",
          displayName: "Connection string",
          description: "Database connection used by persistence providers.",
          required: true,
          secret: true,
          defaultValue: null,
          group: "Persistence",
          advanced: false
        }
      ],
      deploymentHints: {
        supportsDockerCompose: true,
        supportsKubernetes: true,
        requiresCompanionServer: false,
        needsSharedNetwork: false,
        companionImageSlug: null
      },
      docs: {
        dockerHubUrl: null,
        containerPaths: ["/app"],
        showPerShellAdmin: false,
        showNuplane: false
      }
    }
  ],
  packages: [
    {
      packageId: "Elsa.Persistence.PostgreSql",
      displayName: "PostgreSQL Persistence",
      source: {
        id: "00000000-0000-0000-0000-000000000001",
        name: "Elsa Official",
        url: "https://api.nuget.org/v3/index.json"
      },
      runtimeKinds: ["elsa.server"],
      latestVersion: "1.0.2",
      versions: [
        {
          packageId: "Elsa.Persistence.PostgreSql",
          version: "1.0.2",
          source: {
            id: "00000000-0000-0000-0000-000000000001",
            name: "Elsa Official",
            url: "https://api.nuget.org/v3/index.json"
          },
          schemaVersion: "1.0",
          runtimeKinds: ["elsa.server"],
          publishedAt: "2026-05-15T08:00:00Z",
          features: [
            {
              featureId: "postgresql",
              typeName: "Elsa.Persistence.PostgreSql.PostgreSqlFeature",
              displayName: "PostgreSQL Persistence",
              description: "Stores workflow state in PostgreSQL.",
              category: "Persistence",
              requiredCapabilities: ["persistence"],
              runtimeKinds: ["elsa.server"],
              infrastructure: [
                {
                  id: "postgresql",
                  kind: "Database",
                  optional: false,
                  reason: "Stores workflow state.",
                  capabilities: ["relational"],
                  providers: ["PostgreSQL"],
                  configurationKeys: ["connectionString"]
                }
              ],
              advanced: false,
              experimental: false,
              settings: [
                {
                  name: "ConnectionString",
                  jsonType: "string",
                  required: true,
                  defaultValue: "",
                  displayName: "Connection string",
                  description: "PostgreSQL connection string for workflow persistence.",
                  category: "Persistence",
                  secret: true,
                  restartRequired: true,
                  environmentVariable: "ELSA_PERSISTENCE_POSTGRESQL_CONNECTIONSTRING"
                }
              ]
            }
          ]
        }
      ]
    }
  ],
  infrastructureProviders: [
    {
      id: "postgresql",
      displayName: "PostgreSQL",
      kind: "Database",
      strategy: "Managed",
      provider: "PostgreSQL",
      capabilities: ["relational"],
      outputs: ["connectionString"]
    }
  ]
};

describe("RuntimeBuilderPage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("builds a runtime plan and previews generated bundle files", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      planResponse: (intent) => ({
        resolved: {
          ...intent,
          infrastructure: [postgresqlInfrastructure]
        },
        autoAdded: {
          packages: [],
          features: [],
          infrastructure: [postgresqlInfrastructure]
        },
        findings: [{ level: "info", code: "planner.infrastructure", message: "PostgreSQL provider added.", scope: null }]
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);
    expect(screen.getByRole("heading", { name: "Configure runtime image" })).toBeInTheDocument();

    await clickWizardFooterButton("Features");
    await userEvent.click(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i }));
    expect(screen.getByText("Package: Elsa.Persistence.PostgreSql 1.0.2")).toBeInTheDocument();

    await clickWizardFooterButton("Settings");
    expect(screen.getByRole("heading", { name: "Configure feature settings" })).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText(/Connection string/), "Host=postgres;Database=elsa;");
    await clickWizardFooterButton("Infrastructure");
    await clickWizardFooterButton("Review");

    await userEvent.click(screen.getByRole("button", { name: "Plan build" }));
    await screen.findByText("Planner additions");
    expect(screen.getByText("PostgreSQL provider added.")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Generate bundle" }));
    expect(await screen.findByText("Bundle bundle-1")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "docker-compose.yml" })).toBeInTheDocument();
    expect(screen.getByText(/image: elsa\/runtime:4\.0\.1/)).toBeInTheDocument();

    await waitFor(() => {
      const planCall = findPlanCall(fetchMock);
      expect(planCall).toBeDefined();
      const planBody = readBody<{ intent: RuntimeBuilderIntent }>(planCall?.[1]);
      expect(planBody.intent.packages).toHaveLength(1);
      expect(planBody.intent.packages[0].selectedFeatures).toEqual(["postgresql"]);
      expect(planBody.intent.packages[0].settings).toEqual({
        postgresql: {
          ConnectionString: "Host=postgres;Database=elsa;"
        }
      });
    });
  });

  it("hides planner additions when no resources were auto-added", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);
    await advanceFromRuntimeToReview();
    await userEvent.click(screen.getByRole("button", { name: "Plan build" }));

    await waitFor(() => expect(findPlanCall(fetchMock)).toBeDefined());
    expect(screen.queryByText("Planner additions")).not.toBeInTheDocument();
  });

  it("finds features by technical identifiers used in findings", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);
    await clickWizardFooterButton("Features");

    await userEvent.type(screen.getByLabelText("Search runtime features"), "Elsa.Persistence.PostgreSql.PostgreSqlFeature");

    expect(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i })).toBeInTheDocument();
    expect(screen.queryByText("No features match the current search.")).not.toBeInTheDocument();
  });

  it("filters runtime features by multiple categories, including uncategorized features", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      catalog: catalogWithFeatureCategories(),
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);
    await clickWizardFooterButton("Features");

    expect(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /HTTP Activities/i })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Workflow Signals/i })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /Data/ }));
    expect(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /HTTP Activities/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /Workflow Signals/i })).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /Uncategorized/ }));
    expect(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /HTTP Activities/i })).not.toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Workflow Signals/i })).toBeInTheDocument();

    await userEvent.type(screen.getByLabelText("Search runtime features"), "signals");
    expect(screen.queryByRole("checkbox", { name: /PostgreSQL Persistence/i })).not.toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Workflow Signals/i })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Clear" }));
    expect(screen.getByRole("checkbox", { name: /Workflow Signals/i })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /HTTP Activities/i })).not.toBeInTheDocument();

    await userEvent.clear(screen.getByLabelText("Search runtime features"));
    expect(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /HTTP Activities/i })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Workflow Signals/i })).toBeInTheDocument();
  });

  it("filters features by the selected runtime image kind", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      catalog: catalogWithRuntimeKindFeatures(),
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect(await screen.findByRole("heading", { name: "Configure runtime image" })).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Image"), "elsa-studio");
    await clickWizardFooterButton("Features");

    expect(screen.getByRole("checkbox", { name: /Studio Feature/i })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /Legacy Feature/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /Server Feature/i })).not.toBeInTheDocument();
  });

  it("shows both server and studio features for combined runtime images", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      catalog: catalogWithRuntimeKindFeatures(),
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect(await screen.findByRole("heading", { name: "Configure runtime image" })).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Image"), "elsa-combined");
    await clickWizardFooterButton("Features");

    expect(screen.getByRole("checkbox", { name: /Server Feature/i })).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Studio Feature/i })).toBeInTheDocument();
    expect(screen.queryByRole("checkbox", { name: /Legacy Feature/i })).not.toBeInTheDocument();
  });

  it("removes incompatible selected features when the runtime image changes", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      catalog: catalogWithRuntimeKindFeatures(),
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect(await screen.findByRole("heading", { name: "Configure runtime image" })).toBeInTheDocument();
    await userEvent.selectOptions(screen.getByLabelText("Image"), "elsa-combined");
    await clickWizardFooterButton("Features");
    await userEvent.click(screen.getByRole("checkbox", { name: /Server Feature/i }));
    await userEvent.click(screen.getByRole("checkbox", { name: /Studio Feature/i }));

    await clickWizardFooterButton("Runtime");
    await userEvent.selectOptions(screen.getByLabelText("Image"), "elsa-server");
    await clickWizardFooterButton("Features");

    expect(await screen.findByText("1 incompatible feature was removed for Elsa Server.")).toBeInTheDocument();
    await userEvent.click(screen.getByText("Removed features"));
    expect(screen.getByText("Studio Feature")).toBeInTheDocument();
    expect(screen.getByText("Elsa.RuntimeKinds 1.0.0")).toBeInTheDocument();
    expect(screen.getByRole("checkbox", { name: /Server Feature/i })).toBeChecked();
    expect(screen.queryByRole("checkbox", { name: /Studio Feature/i })).not.toBeInTheDocument();
  });

  it("lists saved build configurations without rendering the builder wizard", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      configurations: [
        savedConfigurationFixture()
      ],
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock, { page: "list" });

    expect(await screen.findByRole("heading", { name: "Saved build configurations" })).toBeInTheDocument();
    const configurationName = await screen.findByRole("button", { name: "Claims runtime" });
    expect(screen.getByText("Runtime build for claims processing.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "New configuration" })).toHaveAttribute("href", "/admin/runtime-builder/new");
    expect(screen.getByRole("button", { name: "Edit" })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Choose features" })).not.toBeInTheDocument();

    await userEvent.click(configurationName);

    expect(await screen.findByRole("heading", { name: "Edit build configuration" })).toBeInTheDocument();
  });

  it("loads an existing configuration into the edit builder and updates it", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      configurations: [savedConfigurationFixture()],
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock, { page: "edit", route: "/admin/runtime-builder/configuration-1/edit" });

    expect(await screen.findByRole("heading", { name: "Edit build configuration" })).toBeInTheDocument();
    expect(await screen.findByText("Claims runtime")).toBeInTheDocument();
    await clickWizardFooterButton("Features");
    expect(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i })).toBeChecked();

    await advanceFromFeaturesToReview();

    expect(screen.getByDisplayValue("Claims runtime")).toBeInTheDocument();
    expect(screen.getByDisplayValue("Runtime build for claims processing.")).toBeInTheDocument();
    expect(screen.getByText("PostgreSQL Persistence")).toBeInTheDocument();
    expect(screen.getByText("Elsa.Persistence.PostgreSql 1.0.2")).toBeInTheDocument();

    await userEvent.clear(screen.getByDisplayValue("Claims runtime"));
    await userEvent.type(screen.getByLabelText("Name"), "Claims runtime updated");
    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));

    await waitFor(() => {
      const updateCall = findUpdateConfigurationCall(fetchMock);
      expect(updateCall).toBeDefined();
      const body = readBody<{ name: string; intent: RuntimeBuilderIntent }>(updateCall?.[1]);
      expect(body.name).toBe("Claims runtime updated");
      expect(body.intent.packages[0].selectedFeatures).toEqual(["postgresql"]);
    });
  });

  it("shows edit quick actions in the summary rail", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      configurations: [savedConfigurationFixture()],
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock, { page: "edit", route: "/admin/runtime-builder/configuration-1/edit" });

    expect(await screen.findByRole("heading", { name: "Edit build configuration" })).toBeInTheDocument();
    expect(await screen.findByRole("heading", { name: "Quick actions" })).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Plan build" }));
    await waitFor(() => expect(findPlanCall(fetchMock)).toBeDefined());

    await userEvent.click(screen.getByRole("button", { name: "Generate bundle" }));
    expect(await screen.findByText("Bundle bundle-1")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: "Save changes" }));
    await waitFor(() => expect(findUpdateConfigurationCall(fetchMock)).toBeDefined());

    await advanceFromRuntimeToReview();
    expect(screen.queryByRole("heading", { name: "Quick actions" })).not.toBeInTheDocument();
  });

  it("hands the saved editor configuration to the shared provisioning route", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      configurations: [savedConfigurationFixture()],
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock, { page: "edit" });

    expect(await screen.findByRole("heading", { name: "Edit build configuration" })).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: "Provision engine" }));

    const location = await screen.findByTestId("location-probe");
    expect(location).toHaveTextContent("/admin/engines/provision");
    expect(location).toHaveTextContent("runtimeConfigurationId");
    expect(location).toHaveTextContent("builderHandoffToken");
    expect(location).not.toHaveTextContent("Connection string");
  });

  it.each([
    ["without the provisioning module", [], ["deployments.setup.manage"]],
    ["without setup permission", [{ id: "azure", displayName: "Azure" }], []]
  ] as const)("hides Provision engine %s", async (_reason, providers, permissions) => {
    const fetchMock = createRuntimeBuilderFetchMock({
      configurations: [savedConfigurationFixture()],
      provisioningProviders: providers,
      permissions,
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock, { page: "edit" });

    expect(await screen.findByRole("heading", { name: "Edit build configuration" })).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Provision engine" })).not.toBeInTheDocument());
  });

  it("surfaces bundle request diagnostics when generation fails", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      bundleFailure: {
        error: "Local package path ./packages was not found."
      },
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);
    await clickWizardFooterButton("Features");
    await userEvent.click(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i }));
    await advanceFromFeaturesToReview();

    await userEvent.click(screen.getByLabelText("Include local packages directory"));
    await userEvent.click(screen.getByRole("button", { name: "Generate bundle" }));

    expect(await screen.findByRole("heading", { name: "Bundle generation failed" })).toBeInTheDocument();
    expect(screen.getAllByText("Local package path ./packages was not found.").length).toBeGreaterThan(0);
    expect(screen.getAllByText("bundle.request").length).toBeGreaterThan(0);
    expect(screen.getAllByText("HTTP 400 · Validation").length).toBeGreaterThan(0);
  });

  it("selects required feature dependencies when a feature is selected", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      catalog: catalogWithFeatureDependencies(),
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);

    await clickWizardFooterButton("Features");
    await userEvent.click(screen.getByRole("checkbox", { name: /PostgreSQL Persistence/i }));

    expect(screen.getByRole("checkbox", { name: /Persistence Core/i })).toBeChecked();

    await advanceFromFeaturesToReview();
    await userEvent.click(screen.getByRole("button", { name: "Plan build" }));

    await waitFor(() => {
      const planCall = findPlanCall(fetchMock);
      expect(planCall).toBeDefined();
      const planBody = readBody<{ intent: RuntimeBuilderIntent }>(planCall?.[1]);
      expect(planBody.intent.packages[0].selectedFeatures).toEqual(["persistence-core", "postgresql"]);
    });
  });

  it("selects required feature dependencies resolved by shell feature alias", async () => {
    const fetchMock = createRuntimeBuilderFetchMock({
      catalog: catalogWithAliasFeatureDependencies(),
      planResponse: (intent) => ({
        resolved: intent,
        autoAdded: { packages: [], features: [], infrastructure: [] },
        findings: []
      })
    });
    renderRuntimeBuilder(fetchMock);

    expect((await screen.findAllByText("Elsa Runtime")).length).toBeGreaterThan(0);

    await clickWizardFooterButton("Features");
    await userEvent.click(screen.getByRole("checkbox", { name: /Elsa Core/i }));

    expect(screen.getByRole("checkbox", { name: /Workflow Management/i })).toBeChecked();

    await advanceFromFeaturesToReview();
    await userEvent.click(screen.getByRole("button", { name: "Plan build" }));

    await waitFor(() => {
      const planCall = findPlanCall(fetchMock);
      expect(planCall).toBeDefined();
      const packages = readBody<{ intent: RuntimeBuilderIntent }>(planCall?.[1]).intent.packages;
      expect(packages.find((item) => item.packageId === "Elsa")?.selectedFeatures).toEqual(["Elsa.Elsa"]);
      expect(packages.find((item) => item.packageId === "Elsa.Workflows.Management")?.selectedFeatures).toEqual(["Elsa.Workflows.Management.WorkflowManagement"]);
    });
  });

  it("shows a generic error state for non-auth workspace failures", async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : input.toString();
      if (url.endsWith("/api/auth/session"))
        return jsonResponse({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" });
      return url.endsWith("/api/me/organizations") ? jsonResponse({ title: "API unavailable" }, 500) : jsonResponse({ title: "Not found" }, 404);
    });
    renderRuntimeBuilder(fetchMock);

    expect(await screen.findByText("Workspace context could not load")).toBeInTheDocument();
    expect(screen.getByText("The console could not complete the request.")).toBeInTheDocument();
    expect(screen.queryByText("Your console session is missing or no longer valid.")).not.toBeInTheDocument();
  });
});

const postgresqlInfrastructure = { kind: "Database", providerId: "postgresql", strategy: "Managed", settings: null };

function createRuntimeBuilderFetchMock({
  catalog = catalogFixture,
  configurations = [],
  provisioningProviders = [{ id: "azure", displayName: "Azure" }],
  permissions = ["deployments.setup.manage"],
  bundleFailure,
  planResponse
}: {
  catalog?: BuilderCatalog;
  configurations?: RuntimeConfiguration[];
  provisioningProviders?: ReadonlyArray<{ id: string; displayName: string }>;
  permissions?: readonly string[];
  bundleFailure?: unknown;
  planResponse: (intent: RuntimeBuilderIntent) => BuilderPlanResponse;
}) {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    if (url.endsWith("/api/auth/session"))
      return jsonResponse({ loginEnabled: true, authenticated: true, displayName: "Test User", email: "test@example.com", loginPath: "/api/auth/login", logoutPath: "/api/auth/logout" });
    if (url.endsWith("/api/me/organizations"))
      return jsonResponse(workspaceContextFixture());
    if (url.endsWith(`/api/workspaces/${workspaceId}/engine-provisioning/providers`))
      return jsonResponse({ providers: provisioningProviders });
    if (url.endsWith(`/api/workspaces/${workspaceId}/deployments/permissions`))
      return jsonResponse({ permissions });
    if (url.endsWith(`/api/workspaces/${workspaceId}/builder/catalog`)) {
      return jsonResponse(catalog);
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/runtime-configurations`) && (!init?.method || init.method === "GET")) {
      return jsonResponse(configurations);
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/runtime-configurations`) && init?.method === "POST") {
      const body = readBody<{ name: string; description?: string | null; intent: RuntimeBuilderIntent }>(init);
      return jsonResponse({
        id: "configuration-created",
        workspaceId,
        name: body.name,
        description: body.description ?? null,
        intent: body.intent,
        createdAt: "2026-06-01T10:00:00Z",
        updatedAt: "2026-06-01T10:00:00Z"
      });
    }
    const configurationMatch = url.match(new RegExp(`/api/workspaces/${workspaceId}/runtime-configurations/([^/]+)$`));
    if (configurationMatch && (!init?.method || init.method === "GET")) {
      const configuration = configurations.find((item) => item.id === decodeURIComponent(configurationMatch[1]));
      return configuration ? jsonResponse(configuration) : jsonResponse({ title: "Not found" }, 404);
    }
    if (configurationMatch && init?.method === "PUT") {
      const source = configurations.find((item) => item.id === decodeURIComponent(configurationMatch[1]));
      const body = readBody<{ name: string; description?: string | null; intent: RuntimeBuilderIntent }>(init);
      return source
        ? jsonResponse({ ...source, name: body.name, description: body.description ?? null, intent: body.intent, updatedAt: "2026-06-01T10:00:00Z" })
        : jsonResponse({ title: "Not found" }, 404);
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/builder/plan`)) {
      const body = readBody<{ intent: RuntimeBuilderIntent }>(init);
      return jsonResponse(planResponse(body.intent));
    }
    if (url.endsWith(`/api/workspaces/${workspaceId}/builder/bundle`)) {
      if (bundleFailure) {
        return jsonResponse(bundleFailure, 400);
      }
      return jsonResponse({
        bundleId: "bundle-1",
        files: [
          {
            path: "docker-compose.yml",
            language: "yaml",
            contentType: "text/yaml",
            required: true,
            contents: "services:\n  elsa-runtime:\n    image: elsa/runtime:4.0.1\n"
          }
        ],
        findings: []
      });
    }

    return jsonResponse({ title: "Not found" }, 404);
  });
}

function catalogWithFeatureDependencies(): BuilderCatalog {
  return {
    ...catalogFixture,
    packages: catalogFixture.packages.map((packageItem) => ({
      ...packageItem,
      versions: packageItem.versions.map((version) => ({
        ...version,
        features: [
          {
            featureId: "persistence-core",
            typeName: "Elsa.Persistence.PersistenceFeature",
            displayName: "Persistence Core",
            description: "Base persistence services.",
            category: "Persistence",
            requiredCapabilities: ["persistence"],
            runtimeKinds: ["elsa.server"],
            dependencies: [],
            infrastructure: [],
            advanced: false,
            experimental: false,
            settings: []
          },
          {
            ...version.features[0],
            dependencies: [
              {
                packageId: "Elsa.Persistence.PostgreSql",
                versionRange: null,
                featureId: "persistence-core",
                optional: false,
                reason: "PostgreSQL persistence requires base persistence services."
              }
            ]
          }
        ]
      }))
    }))
  };
}

function catalogWithFeatureCategories(): BuilderCatalog {
  return {
    ...catalogFixture,
    packages: catalogFixture.packages.map((packageItem) => ({
      ...packageItem,
      versions: packageItem.versions.map((version) => ({
        ...version,
        features: [
          {
            ...version.features[0],
            category: "Legacy Persistence",
            categories: ["Persistence", "Data"]
          },
          {
            ...version.features[0],
            featureId: "http-activities",
            typeName: "Elsa.Http.HttpActivitiesFeature",
            displayName: "HTTP Activities",
            description: "Adds HTTP activities.",
            category: "HTTP",
            categories: ["HTTP"],
            settings: []
          },
          {
            ...version.features[0],
            featureId: "workflow-signals",
            typeName: "Elsa.Workflows.Signals.SignalFeature",
            displayName: "Workflow Signals",
            description: "Adds signal dispatching for workflow triggers.",
            category: null,
            categories: [],
            infrastructure: [],
            settings: []
          }
        ]
      }))
    }))
  };
}

function catalogWithAliasFeatureDependencies(): BuilderCatalog {
  const source = catalogFixture.packages[0].source;
  const baseFeature = catalogFixture.packages[0].versions[0].features[0];
  return {
    ...catalogFixture,
    packages: [
      {
        packageId: "Elsa",
        displayName: "Elsa",
        source,
        runtimeKinds: ["elsa.server"],
        latestVersion: "1.0.0",
        versions: [
          {
            packageId: "Elsa",
            version: "1.0.0",
            source,
            schemaVersion: "1.0",
            runtimeKinds: ["elsa.server"],
            publishedAt: "2026-06-06T08:00:00Z",
            features: [
              {
                ...baseFeature,
                featureId: "Elsa.Elsa",
                typeName: "Elsa.ShellFeatures.ElsaFeature",
                displayName: "Elsa Core",
                description: "Core Elsa workflow system functionality",
                runtimeKinds: ["elsa.server"],
                dependencies: [
                  {
                    packageId: null,
                    versionRange: null,
                    featureId: "Elsa.WorkflowManagement",
                    optional: false,
                    reason: null
                  }
                ],
                infrastructure: [],
                settings: [],
                extensions: { cshellsFeatureName: "Elsa" }
              }
            ]
          }
        ]
      },
      {
        packageId: "Elsa.Workflows.Management",
        displayName: "Workflows.Management",
        source,
        runtimeKinds: ["elsa.server"],
        latestVersion: "1.0.0",
        versions: [
          {
            packageId: "Elsa.Workflows.Management",
            version: "1.0.0",
            source,
            schemaVersion: "1.0",
            runtimeKinds: ["elsa.server"],
            publishedAt: "2026-06-06T08:00:00Z",
            features: [
              {
                ...baseFeature,
                featureId: "Elsa.Workflows.Management.WorkflowManagement",
                typeName: "Elsa.Workflows.Management.ShellFeatures.WorkflowManagementFeature",
                displayName: "Workflow Management",
                description: "Provides workflow management services",
                runtimeKinds: ["elsa.server"],
                dependencies: [],
                infrastructure: [],
                settings: [],
                extensions: { cshellsFeatureName: "WorkflowManagement" }
              }
            ]
          }
        ]
      }
    ]
  };
}

function catalogWithRuntimeKindFeatures(): BuilderCatalog {
  const source = catalogFixture.packages[0].source;
  const baseFeature = catalogFixture.packages[0].versions[0].features[0];
  return {
    ...catalogFixture,
    images: [
      {
        ...catalogFixture.images[0],
        slug: "elsa-server",
        displayName: "Elsa Server",
        runtimeKinds: ["elsa.server"]
      },
      {
        ...catalogFixture.images[0],
        slug: "elsa-studio",
        displayName: "Elsa Studio",
        runtimeKinds: ["elsa.studio"]
      },
      {
        ...catalogFixture.images[0],
        slug: "elsa-combined",
        displayName: "Elsa Combined",
        runtimeKinds: ["elsa.server", "elsa.studio"]
      }
    ],
    packages: [
      {
        packageId: "Elsa.RuntimeKinds",
        displayName: "Runtime Kind Features",
        source,
        runtimeKinds: ["elsa.server", "elsa.studio"],
        latestVersion: "1.0.0",
        versions: [
          {
            packageId: "Elsa.RuntimeKinds",
            version: "1.0.0",
            source,
            schemaVersion: "1.0",
            runtimeKinds: ["elsa.server", "elsa.studio"],
            publishedAt: "2026-06-06T08:00:00Z",
            features: [
              {
                ...baseFeature,
                featureId: "server-feature",
                displayName: "Server Feature",
                typeName: "Elsa.Server.ServerFeature",
                runtimeKinds: ["elsa.server"],
                settings: []
              },
              {
                ...baseFeature,
                featureId: "studio-feature",
                displayName: "Studio Feature",
                typeName: "Elsa.Studio.StudioFeature",
                runtimeKinds: ["elsa.studio"],
                settings: []
              },
              {
                ...baseFeature,
                featureId: "legacy-feature",
                displayName: "Legacy Feature",
                typeName: "Elsa.Legacy.LegacyFeature",
                runtimeKinds: [],
                settings: []
              }
            ]
          }
        ]
      }
    ]
  };
}

function findPlanCall(fetchMock: ReturnType<typeof createRuntimeBuilderFetchMock>) {
  return fetchMock.mock.calls.find((call) => call[0].toString().endsWith(`/api/workspaces/${workspaceId}/builder/plan`));
}

function findUpdateConfigurationCall(fetchMock: ReturnType<typeof createRuntimeBuilderFetchMock>) {
  return fetchMock.mock.calls.find((call) => {
    const url = call[0].toString();
    return call[1]?.method === "PUT" && url.endsWith(`/api/workspaces/${workspaceId}/runtime-configurations/configuration-1`);
  });
}

function savedConfigurationFixture(): RuntimeConfiguration {
  return {
    id: "configuration-1",
    workspaceId,
    name: "Claims runtime",
    description: "Runtime build for claims processing.",
    intent: {
      image: { slug: "elsa-runtime", tag: "4.0.0", hostPort: 14000, envOverrides: null },
      packages: [
        {
          sourceId: "00000000-0000-0000-0000-000000000001",
          packageId: "Elsa.Persistence.PostgreSql",
          version: "1.0.2",
          selectedFeatures: ["postgresql"]
        }
      ],
      packageSources: [],
      infrastructure: [postgresqlInfrastructure],
      localPackages: { enabled: false, directoryPath: null },
      target: "docker-compose"
    },
    createdAt: "2026-06-01T08:00:00Z",
    updatedAt: "2026-06-01T09:00:00Z"
  };
}

function renderRuntimeBuilder(
  fetchMock: unknown,
  { page = "new", route }: { page?: "list" | "new" | "edit"; route?: string } = {}
) {
  vi.stubGlobal("fetch", fetchMock);
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  const routeConfig = runtimeBuilderRouteConfig(page, route);

  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[routeConfig.route]}>
        <AuthProvider>
          <WorkspaceContextProvider>
            <Routes>
              <Route path="/admin/runtime-builder" element={<RuntimeBuilderPage />} />
              <Route path="/admin/runtime-builder/new" element={<NewRuntimeBuilderPage />} />
              <Route path="/admin/runtime-builder/:configurationId/edit" element={<EditRuntimeBuilderPage />} />
              <Route path="/admin/engines/provision" element={<LocationProbe />} />
            </Routes>
          </WorkspaceContextProvider>
        </AuthProvider>
      </MemoryRouter>
    </QueryClientProvider>
  );
}

function LocationProbe() {
  const location = useLocation();
  return <output data-testid="location-probe">{location.pathname} {JSON.stringify(location.state)}</output>;
}

function runtimeBuilderRouteConfig(page: "list" | "new" | "edit", route?: string) {
  switch (page) {
    case "list":
      return { route: route ?? "/admin/runtime-builder" };
    case "edit":
      return {
        route: route ?? "/admin/runtime-builder/configuration-1/edit"
      };
    default:
      return { route: route ?? "/admin/runtime-builder/new" };
  }
}

async function clickWizardFooterButton(name: string) {
  const buttons = screen.getAllByRole("button").filter((button) => button.textContent?.trim() === name);
  await userEvent.click(buttons[buttons.length - 1]);
}

async function advanceFromFeaturesToReview() {
  await clickWizardFooterButton("Settings");
  await clickWizardFooterButton("Infrastructure");
  await clickWizardFooterButton("Review");
}

async function advanceFromRuntimeToReview() {
  await clickWizardFooterButton("Features");
  await advanceFromFeaturesToReview();
}

function workspaceContextFixture() {
  return {
    account: { id: "account-1", displayName: "Test User", email: "test@example.com" },
    organizations: [{ id: organizationId, name: "Acme Corp", role: "Owner" }],
    workspaces: [
      { id: workspaceId, name: "Default workspace", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme Corp", organizationRole: "Owner" }
    ]
  };
}

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function readBody<T>(init?: RequestInit) {
  return JSON.parse(String(init?.body ?? "{}")) as T;
}
