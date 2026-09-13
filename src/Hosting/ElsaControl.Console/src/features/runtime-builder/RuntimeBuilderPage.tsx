import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, Check, FileCode2, Pencil, Play, Plus, RefreshCw, Rocket, Save, Search, Settings2 } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import type { Dispatch, ReactNode, SetStateAction } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { Badge, Button, EmptyState, Input, SecondaryButton, Select, buttonClassName } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import {
  createRuntimeConfiguration,
  generateBundle,
  getBuilderCatalog,
  getRuntimeConfiguration,
  listRuntimeConfigurations,
  planRuntime,
  updateRuntimeConfiguration
} from "@/features/runtime-builder/runtimeBuilderApi";
import {
  deploymentTargets,
  latestPackageVersion,
  normalizeFindingLevel,
  packageSelectionKey,
  type BuilderBundleFile,
  type BuilderBundleResponse,
  type BuilderCatalog,
  type BuilderFinding,
  type BuilderPlanResponse,
  type BuilderPackage,
  type DeploymentTarget,
  type InfrastructureProvider,
  type PublicPackageFeatureSetting,
  type RuntimeBuilderIntent,
  type RuntimeConfiguration,
  type RuntimeImage,
  type SelectedRuntimePackage
} from "@/features/runtime-builder/runtimeBuilderModels";
import { cn } from "@/lib/utils";
import { queryKeys } from "@/lib/query/queryClient";
import { sourceStatusTone, statusToneClass } from "@/lib/status/statusBadges";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { formatDateTime } from "@/lib/formatters";
import { ApiError } from "@/lib/api/httpClient";
import { createBuilderProvisioningHandoff } from "@/features/engine-provisioning/engineProvisioningModels";
import { useEngineProvisioningProviders } from "@/features/engine-provisioning/useEngineProvisioningProviders";
import { getDeploymentPermissions } from "@/features/deployments/deploymentApi";
import type { WorkspaceDeploymentPermissionsResponse } from "@/features/deployments/deploymentModels";

const defaultTarget: DeploymentTarget = "docker-compose";
type WizardStep = "runtime" | "features" | "settings" | "infrastructure" | "review";
const wizardSteps: Array<{ id: WizardStep; label: string }> = [
  { id: "runtime", label: "Runtime" },
  { id: "features", label: "Features" },
  { id: "settings", label: "Settings" },
  { id: "infrastructure", label: "Infrastructure" },
  { id: "review", label: "Review" }
];

type FeatureCatalogItem = {
  key: string;
  packageItem: BuilderPackage;
  version: BuilderPackage["versions"][number];
  feature: BuilderPackage["versions"][number]["features"][number];
};

type RemovedRuntimeFeature = {
  featureId: string;
  displayName: string;
  packageId: string;
  version: string;
};

type RuntimeKindNotice = {
  message: string;
  removedFeatures: RemovedRuntimeFeature[];
};

export function RuntimeBuilderPage() {
  const navigate = useNavigate();
  const workspaceContext = useWorkspaceContext();
  const effectiveWorkspaceId = workspaceContext.selectedWorkspaceId;
  const configurations = useQuery({
    queryKey: queryKeys.runtimeConfigurations(effectiveWorkspaceId),
    queryFn: () => listRuntimeConfigurations(effectiveWorkspaceId),
    enabled: Boolean(effectiveWorkspaceId)
  });

  if (workspaceContext.isLoading) return <RequestStateView state="loading" title="Loading workspace context" />;
  if (workspaceContext.isError) {
    return <RequestStateView state="unexpected" title="Workspace context could not load" />;
  }
  if (!effectiveWorkspaceId) {
    return <EmptyState title="No workspace selected" description="Select an organization workspace before managing saved build configurations." />;
  }

  return (
    <section className="space-y-5">
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h1 className="text-2xl font-semibold">Build configurations</h1>
          <p className="mt-1 max-w-3xl text-sm text-muted-foreground">
            Manage saved runtime build configurations and open the builder when you need to create or edit one.
          </p>
        </div>
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <SecondaryButton onClick={() => void configurations.refetch()} title="Refresh saved build configurations">
            <RefreshCw className="h-4 w-4" />
            Refresh
          </SecondaryButton>
          <Link to="/admin/runtime-builder/new" className={buttonClassName()}>
            <Plus className="h-4 w-4" />
            New configuration
          </Link>
        </div>
      </div>

      {configurations.isRefetchError ? <RequestStateView state="stale" title="Showing the last loaded build configurations" /> : null}
      {configurations.isError ? <RequestStateView state="unexpected" title="Saved build configurations could not load" /> : null}

      <RuntimeConfigurationsList
        configurations={configurations.data ?? []}
        isLoading={configurations.isLoading}
        onLoad={(configuration) => navigate(`/admin/runtime-builder/${encodeURIComponent(configuration.id)}/edit`)}
      />
    </section>
  );
}

export function NewRuntimeBuilderPage() {
  return <RuntimeBuilderWorkspace mode="new" />;
}

export function EditRuntimeBuilderPage() {
  const { configurationId } = useParams();
  return <RuntimeBuilderWorkspace mode="edit" configurationId={configurationId ?? ""} />;
}

function RuntimeBuilderWorkspace({ mode, configurationId = "" }: { mode: "new" | "edit"; configurationId?: string }) {
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [imageSlug, setImageSlug] = useState("");
  const [imageTag, setImageTag] = useState("");
  const [hostPort, setHostPort] = useState("");
  const [target, setTarget] = useState<DeploymentTarget>(defaultTarget);
  const [envOverrides, setEnvOverrides] = useState<Record<string, string>>({});
  const [selectedPackages, setSelectedPackages] = useState<Record<string, SelectedRuntimePackage>>({});
  const [selectedInfrastructure, setSelectedInfrastructure] = useState<Record<string, boolean>>({});
  const [featureSearch, setFeatureSearch] = useState("");
  const [selectedFeatureCategories, setSelectedFeatureCategories] = useState<string[]>([]);
  const [localPackagesEnabled, setLocalPackagesEnabled] = useState(false);
  const [localPackagesPath, setLocalPackagesPath] = useState("./packages");
  const [configurationName, setConfigurationName] = useState("Workflow runtime");
  const [configurationDescription, setConfigurationDescription] = useState("");
  const [bundle, setBundle] = useState<BuilderBundleResponse | null>(null);
  const [selectedFilePath, setSelectedFilePath] = useState<string | null>(null);
  const [wizardStep, setWizardStep] = useState<WizardStep>("runtime");
  const [appliedConfigurationId, setAppliedConfigurationId] = useState("");
  const [runtimeKindNotice, setRuntimeKindNotice] = useState<RuntimeKindNotice | null>(null);
  const workspaceContext = useWorkspaceContext();
  const effectiveWorkspaceId = workspaceContext.selectedWorkspaceId;
  const provisioning = useEngineProvisioningProviders(effectiveWorkspaceId);
  const providerAvailable = provisioning.hasProvider("azure");
  const permissions = useQuery<WorkspaceDeploymentPermissionsResponse>({
    queryKey: queryKeys.deploymentPermissions(effectiveWorkspaceId),
    queryFn: () => getDeploymentPermissions(effectiveWorkspaceId),
    enabled: Boolean(effectiveWorkspaceId) && providerAvailable,
    retry: false
  });

  const catalog = useQuery({
    queryKey: queryKeys.runtimeBuilderCatalog(effectiveWorkspaceId),
    queryFn: () => getBuilderCatalog(effectiveWorkspaceId),
    enabled: Boolean(effectiveWorkspaceId)
  });
  const configuration = useQuery({
    queryKey: queryKeys.runtimeConfiguration(effectiveWorkspaceId, configurationId),
    queryFn: () => getRuntimeConfiguration(effectiveWorkspaceId, configurationId),
    enabled: mode === "edit" && Boolean(effectiveWorkspaceId && configurationId)
  });

  useEffect(() => {
    if (!catalog.data || imageSlug) return;
    const image = catalog.data.images[0];
    if (!image) return;
    setImageSlug(image.slug);
    setImageTag(image.defaultTag);
    setHostPort(String(image.hostPort));
  }, [catalog.data, imageSlug]);

  useEffect(() => {
    setAppliedConfigurationId("");
  }, [configurationId]);

  useEffect(() => {
    if (mode !== "edit" || !catalog.data || !configuration.data || appliedConfigurationId === configuration.data.id) return;
    applyIntent(configuration.data.intent, catalog.data);
    setConfigurationName(configuration.data.name);
    setConfigurationDescription(configuration.data.description ?? "");
    setBundle(null);
    setSelectedFilePath(null);
    setWizardStep("runtime");
    setAppliedConfigurationId(configuration.data.id);
  }, [appliedConfigurationId, catalog.data, configuration.data, mode]);

  const selectedImage = useMemo(
    () => catalog.data?.images.find((image) => image.slug === imageSlug) ?? catalog.data?.images[0] ?? null,
    [catalog.data?.images, imageSlug]
  );

  const featureCatalogItems = useMemo(() => collectFeatureCatalogItems(catalog.data?.packages ?? []), [catalog.data?.packages]);
  const compatibleFeatureItems = useMemo(() => {
    return featureCatalogItems.filter((item) => isFeatureCompatibleWithRuntimeKinds(item.feature.runtimeKinds, selectedImage?.runtimeKinds ?? []));
  }, [featureCatalogItems, selectedImage?.runtimeKinds]);
  const featureCategoryOptions = useMemo(() => featureCategoryCounts(compatibleFeatureItems), [compatibleFeatureItems]);
  const featureCategoryLabels = useMemo(() => featureCategoryOptions.map((item) => item.category), [featureCategoryOptions]);
  const selectedFeatureCategorySet = useMemo(() => new Set(selectedFeatureCategories), [selectedFeatureCategories]);
  const allFeatureCategoriesSelected = selectedFeatureCategories.length === 0;

  const filteredFeatureItems = useMemo(() => {
    return filterFeatures(compatibleFeatureItems, featureSearch, selectedFeatureCategories);
  }, [compatibleFeatureItems, featureSearch, selectedFeatureCategories]);

  useEffect(() => {
    if (!catalog.data || !selectedImage)
      return;

    setSelectedPackages((current) => {
      const result = pruneSelectedPackagesForRuntimeKinds(current, catalog.data!, selectedImage.runtimeKinds);
      if (result.removedFeatureCount === 0)
        return current;

      setRuntimeKindNotice({
        message: `${result.removedFeatureCount} incompatible ${result.removedFeatureCount === 1 ? "feature was" : "features were"} removed for ${selectedImage.displayName}.`,
        removedFeatures: result.removedFeatures
      });
      setBundle(null);
      setSelectedFilePath(null);
      return result.packages;
    });
  }, [catalog.data, selectedImage]);

  useEffect(() => {
    setSelectedFeatureCategories((current) => {
      const next = current.filter((category) => featureCategoryLabels.includes(category));
      return next.length === current.length ? current : next;
    });
  }, [featureCategoryLabels]);

  const selectedPackageItems = useMemo(() => {
    return Object.values(selectedPackages)
      .map((selection) => {
        const packageItem = catalog.data?.packages.find(
          (item) => item.source.id === selection.sourceId && item.packageId.toLowerCase() === selection.packageId.toLowerCase()
        );
        return packageItem ? { selection, packageItem } : null;
      })
      .filter((item): item is { selection: SelectedRuntimePackage; packageItem: BuilderPackage } => Boolean(item));
  }, [catalog.data?.packages, selectedPackages]);

  const selectedFeatureItems = useMemo(() => {
    return selectedPackageItems.flatMap(({ selection, packageItem }) => {
      const version = packageItem.versions.find((item) => item.version === selection.version);
      if (!version) return [];
      return version.features
        .filter((feature) => selection.selectedFeatures.includes(feature.featureId))
        .map((feature) => ({
          key: `${packageItem.source.id}:${packageItem.packageId}:${version.version}:${feature.featureId}`,
          packageItem,
          version,
          feature
        }));
    });
  }, [selectedPackageItems]);

  const inferredInfrastructureRequirements = useMemo(() => {
    return selectedFeatureItems.flatMap((item) =>
      item.feature.infrastructure.map((requirement) => ({
        ...requirement,
        featureName: item.feature.displayName,
        packageId: item.packageItem.packageId
      }))
    );
  }, [selectedFeatureItems]);

  const currentIntent = useMemo(() => {
    if (!catalog.data || !selectedImage) return null;
    return buildIntent({
      catalog: catalog.data,
      selectedImage,
      imageTag,
      hostPort,
      target,
      envOverrides,
      selectedPackages,
      selectedInfrastructure,
      localPackagesEnabled,
      localPackagesPath
    });
  }, [
    catalog.data,
    envOverrides,
    hostPort,
    imageTag,
    localPackagesEnabled,
    localPackagesPath,
    selectedImage,
    selectedInfrastructure,
    selectedPackages,
    target
  ]);

  const plan = useMutation({
    mutationFn: () => planRuntime(effectiveWorkspaceId, currentIntent!),
    onSuccess: (response) => {
      if (catalog.data) {
        applyIntent(response.resolved, catalog.data);
      }
      setBundle(null);
      setSelectedFilePath(null);
      void queryClient.invalidateQueries({ queryKey: queryKeys.runtimeBuilderCatalog(effectiveWorkspaceId) });
    }
  });

  const bundleGeneration = useMutation({
    mutationFn: () => generateBundle(effectiveWorkspaceId, currentIntent!),
    onSuccess: (response) => {
      setBundle(response);
      setSelectedFilePath(response.files[0]?.path ?? null);
    }
  });

  const saveConfiguration = useMutation({
    mutationFn: () => {
      const request = {
        name: configurationName.trim(),
        description: configurationDescription.trim() || null,
        intent: currentIntent!
      };

      return mode === "edit" && configurationId
        ? updateRuntimeConfiguration(effectiveWorkspaceId, configurationId, request)
        : createRuntimeConfiguration(effectiveWorkspaceId, request);
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.runtimeConfigurations(effectiveWorkspaceId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.runtimeConfiguration(effectiveWorkspaceId, saved.id) });
      if (mode === "new") {
        navigate(`/admin/runtime-builder/${encodeURIComponent(saved.id)}/edit`, { replace: true });
      }
    }
  });

  function applyIntent(intent: RuntimeBuilderIntent, data: BuilderCatalog) {
    const image = data.images.find((item) => item.slug === intent.image.slug) ?? data.images[0];
    setImageSlug(image?.slug ?? intent.image.slug);
    setImageTag(intent.image.tag ?? image?.defaultTag ?? "");
    setHostPort(String(intent.image.hostPort ?? image?.hostPort ?? ""));
    setEnvOverrides(intent.image.envOverrides ?? {});
    setTarget((intent.target as DeploymentTarget | null) ?? defaultTarget);
    setLocalPackagesEnabled(Boolean(intent.localPackages?.enabled));
    setLocalPackagesPath(intent.localPackages?.directoryPath ?? "./packages");
    setSelectedInfrastructure(
      Object.fromEntries(intent.infrastructure.map((item) => [item.providerId, true]))
    );
    setSelectedPackages(
      Object.fromEntries(
        intent.packages.map((item) => [
          packageSelectionKey(item.sourceId, item.packageId),
          {
            sourceId: item.sourceId,
            packageId: item.packageId,
            version: item.version,
            selectedFeatures: [...(item.selectedFeatures ?? [])],
            settings: item.settings ?? undefined
          }
        ])
      )
    );
  }

  function toggleFeature(item: FeatureCatalogItem, enabled: boolean) {
    if (!catalog.data)
      return;

    setSelectedPackages((current) => {
      return resolveFeatureSelection(current, item, enabled, catalog.data!, selectedImage?.runtimeKinds ?? []);
    });

    if (enabled) {
      const providerIds = inferProviderIdsForFeatureClosure(item, catalog.data, selectedImage?.runtimeKinds ?? []);
      if (providerIds.length > 0) {
        setSelectedInfrastructure((current) => ({
          ...current,
          ...Object.fromEntries(providerIds.map((providerId) => [providerId, true]))
        }));
      }
    }

    setBundle(null);
  }

  function updateFeatureSetting(
    item: FeatureCatalogItem,
    setting: PublicPackageFeatureSetting,
    value: unknown
  ) {
    const packageKey = packageSelectionKey(item.packageItem.source.id, item.packageItem.packageId);
    setSelectedPackages((current) => {
      const selection = current[packageKey];
      if (!selection)
        return current;

      const nextSettings = {
        ...(selection.settings ?? {}),
        [item.feature.featureId]: {
          ...(selection.settings?.[item.feature.featureId] ?? {}),
          [setting.name]: value
        }
      };

      return {
        ...current,
        [packageKey]: {
          ...selection,
          settings: prunePackageSettings(nextSettings, selection.selectedFeatures)
        }
      };
    });
    setBundle(null);
  }

  function goToNextStep() {
    const index = wizardSteps.findIndex((step) => step.id === wizardStep);
    setWizardStep(wizardSteps[Math.min(index + 1, wizardSteps.length - 1)].id);
  }

  function goToPreviousStep() {
    const index = wizardSteps.findIndex((step) => step.id === wizardStep);
    setWizardStep(wizardSteps[Math.max(index - 1, 0)].id);
  }

  if (workspaceContext.isLoading) return <RequestStateView state="loading" title="Loading workspace context" />;
  if (workspaceContext.isError) {
    return <RequestStateView state="unexpected" title="Workspace context could not load" />;
  }
  if (!effectiveWorkspaceId) {
    return <EmptyState title="No workspace selected" description="Select an organization workspace before resolving packages and saved configurations." />;
  }
  if (mode === "edit" && !configurationId) return <RequestStateView state="unexpected" title="Runtime configuration could not load" />;
  if (mode === "edit" && configuration.isLoading) return <RequestStateView state="loading" title="Loading build configuration" />;
  if (mode === "edit" && configuration.isError) return <RequestStateView state="unexpected" title="Build configuration could not load" />;
  if (catalog.isLoading) return <RequestStateView state="loading" title="Loading build configuration catalog" />;
  if (catalog.isError && !catalog.data) return <RequestStateView state="unexpected" title="Build configuration catalog could not load" />;
  if (!catalog.data || !selectedImage) return <EmptyState title="Build configuration catalog is empty" description="Add approved packages and runtime images before building a runtime." />;

  const selectedFile = bundle?.files.find((file) => file.path === selectedFilePath) ?? bundle?.files[0] ?? null;
  const planErrorFindings = requestErrorFindings(plan.error, "planner.request");
  const bundleErrorFindings = requestErrorFindings(bundleGeneration.error, "bundle.request");
  const saveErrorFindings = requestErrorFindings(saveConfiguration.error, "configuration.save");
  const findings = [...(plan.data?.findings ?? []), ...(bundle?.findings ?? []), ...planErrorFindings, ...bundleErrorFindings, ...saveErrorFindings];
  const canSubmit = Boolean(currentIntent && effectiveWorkspaceId && selectedImage);
  const canSaveConfiguration = canSubmit && Boolean(configurationName.trim());
  const canProvision = providerAvailable && permissions.isSuccess && permissions.data.permissions.includes("deployments.setup.manage");
  const autoAdded = plan.data?.autoAdded;
  const hasPlannerAdditions = hasAutoAddedItems(autoAdded);
  const actionHandlers = {
    onPlan: () => plan.mutate(),
    onGenerateBundle: () => bundleGeneration.mutate(),
    onSave: () => saveConfiguration.mutate(),
    onProvision: () => navigate("/admin/engines/provision", {
      state: {
        workspaceId: effectiveWorkspaceId,
        runtimeConfigurationId: mode === "edit" ? configurationId : undefined,
        configurationName: configurationName.trim() || undefined,
        builderHandoffToken: currentIntent ? createBuilderProvisioningHandoff(currentIntent) : undefined
      }
    })
  };

  return (
    <section className="space-y-5">
      <nav aria-label="Breadcrumb" className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
        <Link to="/admin/runtime-builder" className="hover:text-foreground">Build configurations</Link>
        <span>/</span>
        <span className="text-foreground">{mode === "edit" ? configurationName : "New configuration"}</span>
      </nav>
      <div className="flex flex-col gap-3 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h1 className="text-2xl font-semibold">{mode === "edit" ? "Edit build configuration" : "New build configuration"}</h1>
          <p className="mt-1 max-w-3xl text-sm text-muted-foreground">
            Compose package selections, review inferred infrastructure, and generate deployment bundles for this workspace.
          </p>
        </div>
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
          <Link to="/admin/runtime-builder" className={buttonClassName("secondary")}>
            <ArrowLeft className="h-4 w-4" />
            Back to list
          </Link>
          <SecondaryButton onClick={() => void catalog.refetch()} title="Refresh build configuration catalog">
            <RefreshCw className="h-4 w-4" />
            Refresh
          </SecondaryButton>
        </div>
      </div>

      {catalog.isRefetchError ? <RequestStateView state="stale" title="Showing the last loaded build configuration catalog" /> : null}
      {plan.isError ? (
        <RequestErrorPanel
          title="Planning failed"
          description="Review the diagnostics below, then retry planning."
          findings={planErrorFindings}
        />
      ) : null}
      {bundleGeneration.isError ? (
        <RequestErrorPanel
          title="Bundle generation failed"
          description="Review the diagnostics below, then retry bundle generation."
          findings={bundleErrorFindings}
        />
      ) : null}
      {saveConfiguration.isError ? (
        <RequestErrorPanel
          title="Runtime configuration could not be saved"
          description="Review the diagnostics below, then retry saving."
          findings={saveErrorFindings}
        />
      ) : null}
      {saveConfiguration.isSuccess ? (
        <div className="rounded-ui border border-success/40 bg-surface p-3 text-sm text-success">
          Runtime configuration {mode === "edit" ? "updated" : "created"}.
        </div>
      ) : null}

      <section className="rounded-ui border border-border bg-surface">
        <WizardSteps currentStep={wizardStep} onStepChange={setWizardStep} />
        <div className="grid gap-0 xl:grid-cols-[minmax(0,1fr)_24rem]">
          <div className="min-w-0 border-border p-4 xl:border-r">
            {wizardStep === "runtime" ? (
              <WizardPane title="Configure runtime image" description="Choose the runtime image first. The selected image determines which feature capabilities are meaningful for the resulting runtime.">
                <div className="flex items-center gap-2">
                  <Settings2 className="h-4 w-4 text-primary" />
                  <h2 className="text-base font-medium">Runtime image</h2>
                </div>
                <div className="mt-4 grid gap-3 md:grid-cols-2">
                  <label className="space-y-1 text-sm">
                    <span className="font-medium">Image</span>
                    <Select
                      className="w-full"
                      value={selectedImage.slug}
                      onChange={(event) => {
                        const image = catalog.data!.images.find((item) => item.slug === event.target.value);
                        if (!image) return;
                        setImageSlug(image.slug);
                        setImageTag(image.defaultTag);
                        setHostPort(String(image.hostPort));
                        setEnvOverrides({});
                        setRuntimeKindNotice(null);
                        setBundle(null);
                      }}
                    >
                      {catalog.data.images.map((image) => (
                        <option key={image.slug} value={image.slug}>
                          {image.displayName}
                        </option>
                      ))}
                    </Select>
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="font-medium">Tag</span>
                    <Select value={imageTag || selectedImage.defaultTag} onChange={(event) => setImageTag(event.target.value)} className="w-full">
                      {selectedImage.availableTags.map((tag) => (
                        <option key={tag} value={tag}>
                          {tag}
                        </option>
                      ))}
                    </Select>
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="font-medium">Host port</span>
                    <Input value={hostPort} onChange={(event) => setHostPort(event.target.value)} inputMode="numeric" />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="font-medium">Target</span>
                    <Select value={target} onChange={(event) => setTarget(event.target.value as DeploymentTarget)} className="w-full">
                      {deploymentTargets.map((item) => (
                        <option key={item.value} value={item.value}>
                          {item.label}
                        </option>
                      ))}
                    </Select>
                  </label>
                </div>
                <div className="mt-3 flex flex-wrap gap-2">
                  <Badge>{selectedImage.image}</Badge>
                  <Badge>{selectedImage.licenseTier}</Badge>
                  <Badge>{selectedImage.stability}</Badge>
                  {selectedImage.runtimeKinds.map((runtimeKind) => (
                    <Badge key={runtimeKind}>{runtimeKind}</Badge>
                  ))}
                  {selectedImage.capabilities.map((capability) => (
                    <Badge key={capability}>{capability}</Badge>
                  ))}
                </div>
                <p className="mt-3 text-sm text-muted-foreground">{selectedImage.description}</p>

                {selectedImage.envVars.length > 0 ? (
                  <div className="mt-4 grid gap-3 md:grid-cols-2">
                    {selectedImage.envVars.map((envVar) => (
                      <label key={envVar.name} className="space-y-1 text-sm">
                        <span className="flex items-center gap-2 font-medium">
                          {envVar.displayName}
                          {envVar.required ? <Badge className={statusToneClass(sourceStatusTone("Blocking"))}>Required</Badge> : null}
                          {envVar.secret ? <Badge>Secret</Badge> : null}
                        </span>
                        <Input
                          type={envVar.secret ? "password" : "text"}
                          value={envOverrides[envVar.name] ?? ""}
                          onChange={(event) => setEnvOverrides((current) => ({ ...current, [envVar.name]: event.target.value }))}
                          placeholder={envVar.defaultValue ?? envVar.name}
                        />
                        <span className="block text-xs text-muted-foreground">{envVar.description}</span>
                      </label>
                    ))}
                  </div>
                ) : null}
              </WizardPane>
            ) : null}

            {wizardStep === "features" ? (
              <WizardPane
                title="Choose features"
                description="Select capabilities for the chosen runtime image. Runtime packages are inferred from the features you choose."
              >
                <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
                  <label className="relative block md:w-96">
                    <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                    <Input
                      value={featureSearch}
                      onChange={(event) => setFeatureSearch(event.target.value)}
                      className="pl-9"
                      placeholder="Search features"
                      aria-label="Search runtime features"
                    />
                  </label>
                  <Badge>{selectedFeatureItems.length} selected</Badge>
                </div>
                {runtimeKindNotice ? (
                  <div className="mt-3 rounded-ui border border-warning/40 bg-surface p-3 text-sm text-warning">
                    <p>{runtimeKindNotice.message}</p>
                    {runtimeKindNotice.removedFeatures.length > 0 ? (
                      <details className="mt-2 text-muted-foreground">
                        <summary className="cursor-pointer text-warning">Removed features</summary>
                        <ul className="mt-2 space-y-1">
                          {runtimeKindNotice.removedFeatures.map((feature) => (
                            <li key={`${feature.packageId}:${feature.version}:${feature.featureId}`}>
                              <span className="font-medium text-foreground">{feature.displayName}</span>
                              <span className="ml-2 text-xs">{feature.packageId} {feature.version}</span>
                            </li>
                          ))}
                        </ul>
                      </details>
                    ) : null}
                  </div>
                ) : null}
                <div className="mt-4 grid gap-4 lg:grid-cols-[14rem_minmax(0,1fr)]">
                  <aside className="lg:border-r lg:border-border lg:pr-4" aria-label="Runtime feature categories">
                    <div className="flex items-center justify-between gap-3">
                      <h3 className="text-sm font-medium">Categories</h3>
                      {selectedFeatureCategories.length > 0 ? (
                        <button type="button" className="text-xs font-medium text-primary hover:underline" onClick={() => setSelectedFeatureCategories([])}>
                          Clear
                        </button>
                      ) : null}
                    </div>
                    <div className="mt-2 flex gap-2 overflow-x-auto pb-1 lg:block lg:space-y-1 lg:overflow-visible lg:pb-0">
                      <button
                        type="button"
                        className={featureCategoryButtonClassName(allFeatureCategoriesSelected)}
                        aria-pressed={allFeatureCategoriesSelected}
                        onClick={() => setSelectedFeatureCategories([])}
                      >
                        <span>All features</span>
                        <span className={cn("text-xs", allFeatureCategoriesSelected ? "text-background/80" : "text-muted-foreground")}>{compatibleFeatureItems.length}</span>
                      </button>
                      {featureCategoryOptions.map(({ category, count }) => {
                        const selected = selectedFeatureCategorySet.has(category);
                        return (
                          <button
                            key={category}
                            type="button"
                            className={featureCategoryButtonClassName(selected)}
                            aria-pressed={selected}
                            onClick={() => toggleFeatureCategory(category, setSelectedFeatureCategories)}
                          >
                            <span className="inline-flex min-w-0 items-center gap-2">
                              {selected ? <Check className="h-3.5 w-3.5 shrink-0" /> : null}
                              <span className="truncate">{category}</span>
                            </span>
                            <span className={cn("text-xs", selected ? "text-background/80" : "text-muted-foreground")}>{count}</span>
                          </button>
                        );
                      })}
                    </div>
                  </aside>

                  <div className="min-w-0">
                    <p className="text-xs text-muted-foreground">
                      Showing {filteredFeatureItems.length} of {compatibleFeatureItems.length} features
                      {selectedFeatureCategories.length > 0 ? ` in ${selectedFeatureCategories.join(", ")}` : ""}
                      {featureSearch.trim() ? ` matching "${featureSearch.trim()}"` : ""}.
                    </p>
                    <div className="mt-3 grid grid-cols-[repeat(auto-fit,minmax(17.5rem,1fr))] gap-3">
                      {filteredFeatureItems.length === 0 ? (
                        <p className="rounded-ui border border-dashed border-border p-4 text-sm text-muted-foreground">No features match the selected filters.</p>
                      ) : (
                        filteredFeatureItems.map((item) => {
                          const packageKey = packageSelectionKey(item.packageItem.source.id, item.packageItem.packageId);
                          const checked = selectedPackages[packageKey]?.selectedFeatures.includes(item.feature.featureId) ?? false;
                          return (
                            <label
                              key={item.key}
                              className={cn(
                                "flex min-h-32 items-start gap-3 rounded-ui border bg-background p-3 text-sm transition-colors",
                                checked ? "border-primary bg-primary/10" : "border-border hover:border-primary/50"
                              )}
                            >
                              <input
                                type="checkbox"
                                className="mt-1 h-4 w-4 rounded border-border"
                                checked={checked}
                                onChange={(event) => toggleFeature(item, event.target.checked)}
                              />
                              <span className="min-w-0">
                                <span className="block font-medium">{item.feature.displayName}</span>
                                <span className="mt-1 block text-xs leading-5 text-muted-foreground">
                                  {item.feature.description ?? item.feature.featureId}
                                </span>
                                <span className="mt-2 flex flex-wrap gap-1">
                                  {featureCategories(item.feature).map((category) => (
                                    <Badge key={category}>{category}</Badge>
                                  ))}
                                  {item.feature.experimental ? <Badge>Experimental</Badge> : null}
                                  {item.feature.advanced ? <Badge>Advanced</Badge> : null}
                                </span>
                                <span className="mt-2 block text-xs text-muted-foreground">
                                  Package: {item.packageItem.packageId} {item.version.version}
                                </span>
                              </span>
                            </label>
                          );
                        })
                      )}
                    </div>
                  </div>
                </div>
              </WizardPane>
            ) : null}

            {wizardStep === "settings" ? (
              <WizardPane title="Configure feature settings" description="Provide values for settings exposed by the selected features. Secret references should be used instead of raw secrets when the feature supports them.">
                <FeatureSettingsPane
                  selectedPackages={selectedPackages}
                  selectedFeatureItems={selectedFeatureItems}
                  onSettingChange={updateFeatureSetting}
                />
              </WizardPane>
            ) : null}

            {wizardStep === "infrastructure" ? (
              <WizardPane title="Review infrastructure" description="Infrastructure suggestions come from selected feature requirements. Adjust providers before reviewing the build.">
                {inferredInfrastructureRequirements.length > 0 ? (
                  <div className="mb-4 rounded-ui border border-border bg-background p-3">
                    <p className="text-sm font-medium">Requirements inferred from features</p>
                    <div className="mt-2 flex flex-wrap gap-2">
                      {inferredInfrastructureRequirements.map((requirement, index) => (
                        <Badge key={`${requirement.featureName}:${requirement.id}:${index}`}>
                          {requirement.featureName}: {requirement.kind}
                        </Badge>
                      ))}
                    </div>
                  </div>
                ) : (
                  <p className="mb-4 rounded-ui border border-dashed border-border p-4 text-sm text-muted-foreground">
                    No infrastructure requirements were inferred from the selected features.
                  </p>
                )}
                <div className="grid gap-3 md:grid-cols-2">
                  {catalog.data.infrastructureProviders.map((provider) => (
                    <label key={provider.id} className="flex items-start gap-3 rounded-ui border border-border bg-background p-3">
                      <input
                        type="checkbox"
                        className="mt-1 h-4 w-4 rounded border-border"
                        checked={Boolean(selectedInfrastructure[provider.id])}
                        onChange={(event) => {
                          setSelectedInfrastructure((current) => ({ ...current, [provider.id]: event.target.checked }));
                          setBundle(null);
                        }}
                      />
                      <span className="min-w-0">
                        <span className="block text-sm font-medium">{provider.displayName}</span>
                        <span className="block text-xs text-muted-foreground">
                          {provider.kind} · {provider.strategy} · {provider.provider}
                        </span>
                        {provider.capabilities.length > 0 ? (
                          <span className="mt-2 flex flex-wrap gap-1">
                            {provider.capabilities.map((capability) => (
                              <Badge key={capability}>{capability}</Badge>
                            ))}
                          </span>
                        ) : null}
                      </span>
                    </label>
                  ))}
                </div>
              </WizardPane>
            ) : null}

            {wizardStep === "review" ? (
              <WizardPane title="Review and create configuration" description="Name the build configuration, plan the inferred package set, then save or generate a bundle.">
                <div className="grid gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
                  <label className="space-y-1 text-sm">
                    <span className="font-medium">Name</span>
                    <Input value={configurationName} onChange={(event) => setConfigurationName(event.target.value)} />
                  </label>
                  <label className="space-y-1 text-sm">
                    <span className="font-medium">Description</span>
                    <Input value={configurationDescription} onChange={(event) => setConfigurationDescription(event.target.value)} />
                  </label>
                </div>
                <div className="mt-3 grid gap-3 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
                  <label className="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      className="h-4 w-4 rounded border-border"
                      checked={localPackagesEnabled}
                      onChange={(event) => setLocalPackagesEnabled(event.target.checked)}
                    />
                    Include local packages directory
                  </label>
                  <Input
                    value={localPackagesPath}
                    onChange={(event) => setLocalPackagesPath(event.target.value)}
                    disabled={!localPackagesEnabled}
                    aria-label="Local packages directory"
                  />
                </div>
                <div className="mt-4 grid gap-4 lg:grid-cols-2">
                  <ReviewList title="Selected features" items={selectedFeatureItems.map((item) => item.feature.displayName)} emptyText="No features selected." />
                  <ReviewList title="Feature settings" items={featureSettingReviewItems(selectedFeatureItems, selectedPackages)} emptyText="No feature settings configured." />
                  <ReviewList title="Inferred packages" items={selectedPackageItems.map(({ selection }) => `${selection.packageId} ${selection.version}`)} emptyText="No packages inferred yet." />
                </div>
                <BuilderActions
                  className="mt-4 flex flex-col gap-2 sm:flex-row"
                  mode={mode}
                  canSubmit={canSubmit}
                  canSave={canSaveConfiguration}
                  isPlanning={plan.isPending}
                  isGenerating={bundleGeneration.isPending}
                  isSaving={saveConfiguration.isPending}
                  canProvision={canProvision}
                  {...actionHandlers}
                />
              </WizardPane>
            ) : null}

            <WizardNavigation currentStep={wizardStep} onBack={goToPreviousStep} onNext={goToNextStep} />
          </div>

          <aside className="space-y-4 p-4 xl:sticky xl:top-20 xl:self-start">
            <section className="rounded-ui border border-border bg-surface p-4">
              <h2 className="text-base font-medium">Configuration summary</h2>
              <dl className="mt-4 grid grid-cols-2 gap-3 text-sm">
                <SummaryItem label="Image" value={selectedImage.displayName} />
                <SummaryItem label="Tag" value={imageTag || selectedImage.defaultTag} />
                <SummaryItem label="Packages" value={selectedPackageItems.length.toString()} />
                <SummaryItem label="Features" value={countFeatures(selectedPackages).toString()} />
                <SummaryItem label="Settings" value={countConfiguredSettings(selectedPackages).toString()} />
                <SummaryItem label="Infrastructure" value={Object.values(selectedInfrastructure).filter(Boolean).length.toString()} />
                <SummaryItem label="Target" value={deploymentTargets.find((item) => item.value === target)?.label ?? target} />
              </dl>
              {hasPlannerAdditions && autoAdded ? (
                <div className="mt-4 rounded-ui border border-border bg-background p-3 text-sm">
                  <p className="font-medium">Planner additions</p>
                  <p className="mt-1 text-muted-foreground">
                    {autoAdded.packages.length} packages, {autoAdded.features.length} features, {autoAdded.infrastructure.length} infrastructure providers
                  </p>
                </div>
              ) : null}
            </section>

            {mode === "edit" && wizardStep !== "review" ? (
              <section className="rounded-ui border border-border bg-surface p-4">
                <h2 className="text-base font-medium">Quick actions</h2>
                <p className="mt-1 text-sm text-muted-foreground">Use the current configuration state without switching to review.</p>
                <BuilderActions
                  className="mt-4 grid gap-2"
                  actionClassName="w-full"
                  mode={mode}
                  canSubmit={canSubmit}
                  canSave={canSaveConfiguration}
                  isPlanning={plan.isPending}
                  isGenerating={bundleGeneration.isPending}
                  isSaving={saveConfiguration.isPending}
                  canProvision={canProvision}
                  {...actionHandlers}
                />
              </section>
            ) : null}

            <FindingsPanel findings={findings} />
            <BundlePreview bundle={bundle} selectedFile={selectedFile} onFileSelect={setSelectedFilePath} />
          </aside>
        </div>
      </section>
    </section>
  );
}

function BuilderActions({
  mode,
  canSubmit,
  canSave,
  isPlanning,
  isGenerating,
  isSaving,
  canProvision,
  onPlan,
  onGenerateBundle,
  onSave,
  onProvision,
  className,
  actionClassName
}: {
  mode: "new" | "edit";
  canSubmit: boolean;
  canSave: boolean;
  isPlanning: boolean;
  isGenerating: boolean;
  isSaving: boolean;
  canProvision: boolean;
  onPlan: () => void;
  onGenerateBundle: () => void;
  onSave: () => void;
  onProvision: () => void;
  className?: string;
  actionClassName?: string;
}) {
  return (
    <div className={className}>
      <Button type="button" className={actionClassName} disabled={!canSubmit || isPlanning} onClick={onPlan}>
        <Play className="h-4 w-4" />
        {isPlanning ? "Planning" : "Plan build"}
      </Button>
      <SecondaryButton type="button" className={actionClassName} disabled={!canSubmit || isGenerating} onClick={onGenerateBundle}>
        <FileCode2 className="h-4 w-4" />
        {isGenerating ? "Generating" : "Generate bundle"}
      </SecondaryButton>
      <Button type="button" className={actionClassName} disabled={!canSave || isSaving} onClick={onSave}>
        <Save className="h-4 w-4" />
        {mode === "edit" ? "Save changes" : "Create configuration"}
      </Button>
      {canProvision ? <SecondaryButton type="button" className={actionClassName} disabled={!canSubmit} onClick={onProvision}>
        <Rocket className="h-4 w-4" />
        Provision engine
      </SecondaryButton> : null}
    </div>
  );
}

function RequestErrorPanel({
  title,
  description,
  findings
}: {
  title: string;
  description: string;
  findings: BuilderFinding[];
}) {
  return (
    <section className="rounded-ui border border-destructive/40 bg-surface p-4">
      <h2 className="text-base font-medium">{title}</h2>
      <p className="mt-1 text-sm text-muted-foreground">{description}</p>
      {findings.length > 0 ? (
        <div className="mt-3">
          <FindingsList findings={findings} />
        </div>
      ) : null}
    </section>
  );
}

function RuntimeConfigurationsList({
  configurations,
  isLoading,
  onLoad
}: {
  configurations: RuntimeConfiguration[];
  isLoading: boolean;
  onLoad: (configuration: RuntimeConfiguration) => void;
}) {
  return (
    <section className="rounded-ui border border-border bg-surface">
      <div className="flex flex-col gap-3 border-b border-border px-4 py-3 md:flex-row md:items-center md:justify-between">
        <div>
          <h2 className="text-base font-medium">Saved build configurations</h2>
          <p className="mt-1 text-sm text-muted-foreground">Open an existing configuration or create a new one in the builder.</p>
        </div>
        <Badge>{configurations.length} saved</Badge>
      </div>

      {isLoading ? (
        <p className="px-4 py-5 text-sm text-muted-foreground">Loading saved build configurations.</p>
      ) : configurations.length === 0 ? (
        <p className="px-4 py-5 text-sm text-muted-foreground">No build configurations have been saved for this workspace.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm">
            <thead className="border-b border-border text-xs uppercase tracking-wider text-muted-foreground">
              <tr>
                <th className="px-4 py-3 font-medium">Name</th>
                <th className="px-4 py-3 font-medium">Image</th>
                <th className="px-4 py-3 font-medium">Packages</th>
                <th className="px-4 py-3 font-medium">Target</th>
                <th className="px-4 py-3 font-medium">Updated</th>
                <th className="px-4 py-3 text-right font-medium">Action</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border">
              {configurations.map((configuration) => (
                <tr key={configuration.id} className="align-top">
                  <td className="max-w-xs px-4 py-3">
                    <button
                      type="button"
                      className="text-left font-medium text-foreground underline-offset-4 hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      onClick={() => onLoad(configuration)}
                    >
                      {configuration.name}
                    </button>
                    {configuration.description ? <p className="mt-1 text-xs leading-5 text-muted-foreground">{configuration.description}</p> : null}
                  </td>
                  <td className="px-4 py-3 text-muted-foreground">
                    {configuration.intent.image.slug}
                    {configuration.intent.image.tag ? `:${configuration.intent.image.tag}` : null}
                  </td>
                  <td className="px-4 py-3">{configuration.intent.packages.length}</td>
                  <td className="px-4 py-3 text-muted-foreground">{configuration.intent.target ?? defaultTarget}</td>
                  <td className="px-4 py-3 text-muted-foreground">{formatDateTime(configuration.updatedAt)}</td>
                  <td className="px-4 py-3 text-right">
                    <SecondaryButton className="h-8" onClick={() => onLoad(configuration)}>
                      <Pencil className="h-4 w-4" />
                      Edit
                    </SecondaryButton>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function WizardSteps({ currentStep, onStepChange }: { currentStep: WizardStep; onStepChange: (step: WizardStep) => void }) {
  const currentIndex = wizardSteps.findIndex((step) => step.id === currentStep);
  return (
    <ol className="grid border-b border-border md:grid-cols-5">
      {wizardSteps.map((step, index) => {
        const isCurrent = step.id === currentStep;
        const isComplete = index < currentIndex;
        return (
          <li key={step.id}>
            <button
              type="button"
              className={cn(
                "flex w-full items-center gap-3 px-4 py-3 text-left text-sm transition-colors hover:bg-muted",
                isCurrent ? "bg-primary/10 text-foreground" : "text-muted-foreground"
              )}
              onClick={() => onStepChange(step.id)}
            >
              <span
                className={cn(
                  "inline-flex h-6 w-6 items-center justify-center rounded-full border text-xs font-semibold",
                  isCurrent || isComplete ? "border-primary bg-primary text-primary-foreground" : "border-border bg-background"
                )}
              >
                {isComplete ? <Check className="h-3.5 w-3.5" /> : index + 1}
              </span>
              <span className="font-medium">{step.label}</span>
            </button>
          </li>
        );
      })}
    </ol>
  );
}

function FeatureSettingsPane({
  selectedPackages,
  selectedFeatureItems,
  onSettingChange
}: {
  selectedPackages: Record<string, SelectedRuntimePackage>;
  selectedFeatureItems: FeatureCatalogItem[];
  onSettingChange: (item: FeatureCatalogItem, setting: PublicPackageFeatureSetting, value: unknown) => void;
}) {
  const configurableFeatures = selectedFeatureItems.filter((item) => item.feature.settings.length > 0);
  if (selectedFeatureItems.length === 0) {
    return (
      <p className="rounded-ui border border-dashed border-border p-4 text-sm text-muted-foreground">
        Select features first. Any settings exposed by those features will appear here.
      </p>
    );
  }
  if (configurableFeatures.length === 0) {
    return (
      <p className="rounded-ui border border-dashed border-border p-4 text-sm text-muted-foreground">
        The selected features do not expose configurable settings.
      </p>
    );
  }

  return (
    <div className="space-y-4">
      {configurableFeatures.map((item) => {
        const packageKey = packageSelectionKey(item.packageItem.source.id, item.packageItem.packageId);
        const values = selectedPackages[packageKey]?.settings?.[item.feature.featureId] ?? {};
        return (
          <section key={item.key} className="rounded-ui border border-border bg-background p-4">
            <div className="flex flex-col gap-2 md:flex-row md:items-start md:justify-between">
              <div>
                <h3 className="font-medium">{item.feature.displayName}</h3>
                <p className="mt-1 text-sm text-muted-foreground">
                  {item.packageItem.packageId} {item.version.version}
                </p>
              </div>
              {item.feature.category ? <Badge>{item.feature.category}</Badge> : null}
            </div>
            <div className="mt-4 grid gap-3 md:grid-cols-2">
              {item.feature.settings.map((setting) => (
                <FeatureSettingField
                  key={setting.name}
                  setting={setting}
                  value={values[setting.name]}
                  onChange={(value) => onSettingChange(item, setting, value)}
                />
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}

function FeatureSettingField({
  setting,
  value,
  onChange
}: {
  setting: PublicPackageFeatureSetting;
  value: unknown;
  onChange: (value: unknown) => void;
}) {
  const valueText = value === undefined || value === null ? "" : String(value);
  const label = setting.displayName || setting.name;
  if (setting.jsonType === "boolean") {
    return (
      <label className="flex min-h-24 items-start gap-3 rounded-ui border border-border bg-surface p-3 text-sm">
        <input
          type="checkbox"
          className="mt-1 h-4 w-4 rounded border-border"
          checked={value === undefined ? Boolean(setting.defaultValue) : Boolean(value)}
          onChange={(event) => onChange(event.target.checked)}
        />
        <span>
          <span className="flex flex-wrap items-center gap-2 font-medium">
            {label}
            {setting.required ? <Badge>Required</Badge> : null}
            {setting.restartRequired ? <Badge>Restart</Badge> : null}
          </span>
          <FeatureSettingHelp setting={setting} />
        </span>
      </label>
    );
  }

  if (setting.jsonType === "number" || setting.jsonType === "integer") {
    return (
      <FeatureSettingWrapper setting={setting}>
        <Input
          type="number"
          value={valueText}
          onChange={(event) => onChange(parseNumericSettingValue(event.target.value, setting.jsonType))}
          placeholder={setting.defaultValue === undefined ? setting.name : String(setting.defaultValue)}
        />
      </FeatureSettingWrapper>
    );
  }

  if (setting.jsonType === "array" || setting.jsonType === "object") {
    return (
      <FeatureSettingWrapper setting={setting}>
        <textarea
          className="min-h-24 w-full rounded-ui border border-border bg-background px-3 py-2 text-sm text-foreground placeholder:text-muted-foreground"
          value={valueText}
          onChange={(event) => onChange(parseStructuredSettingValue(event.target.value))}
          placeholder={setting.defaultValue === undefined ? "{}" : JSON.stringify(setting.defaultValue, null, 2)}
        />
      </FeatureSettingWrapper>
    );
  }

  return (
    <FeatureSettingWrapper setting={setting}>
      <Input
        type={setting.secret ? "password" : "text"}
        value={valueText}
        onChange={(event) => onChange(event.target.value)}
        placeholder={setting.defaultValue === undefined ? setting.name : String(setting.defaultValue)}
      />
    </FeatureSettingWrapper>
  );
}

function FeatureSettingWrapper({ setting, children }: { setting: PublicPackageFeatureSetting; children: ReactNode }) {
  return (
    <label className="space-y-1 text-sm">
      <span className="flex flex-wrap items-center gap-2 font-medium">
        {setting.displayName || setting.name}
        {setting.required ? <Badge>Required</Badge> : null}
        {setting.secret ? <Badge>Secret</Badge> : null}
        {setting.restartRequired ? <Badge>Restart</Badge> : null}
      </span>
      {children}
      <FeatureSettingHelp setting={setting} />
    </label>
  );
}

function FeatureSettingHelp({ setting }: { setting: PublicPackageFeatureSetting }) {
  return (
    <span className="mt-1 block text-xs leading-5 text-muted-foreground">
      {setting.description || setting.name}
      {setting.environmentVariable ? ` Environment variable: ${setting.environmentVariable}.` : ""}
      {setting.secret ? " Prefer a secret reference over a raw secret value." : ""}
    </span>
  );
}

function WizardPane({ title, description, children }: { title: string; description: string; children: ReactNode }) {
  return (
    <section>
      <div className="mb-4">
        <h2 className="text-lg font-semibold">{title}</h2>
        <p className="mt-1 max-w-3xl text-sm text-muted-foreground">{description}</p>
      </div>
      {children}
    </section>
  );
}

function WizardNavigation({ currentStep, onBack, onNext }: { currentStep: WizardStep; onBack: () => void; onNext: () => void }) {
  const index = wizardSteps.findIndex((step) => step.id === currentStep);
  const previous = wizardSteps[index - 1];
  const next = wizardSteps[index + 1];
  return (
    <div className="mt-5 flex items-center justify-between gap-3 border-t border-border pt-4">
      <SecondaryButton disabled={!previous} onClick={onBack}>
        <ArrowLeft className="h-4 w-4" />
        {previous ? previous.label : "Back"}
      </SecondaryButton>
      {next ? (
        <Button onClick={onNext}>
          {next.label}
          <ArrowRight className="h-4 w-4" />
        </Button>
      ) : null}
    </div>
  );
}

function ReviewList({ title, items, emptyText }: { title: string; items: string[]; emptyText: string }) {
  return (
    <section className="rounded-ui border border-border bg-background p-3">
      <h3 className="text-sm font-medium">{title}</h3>
      {items.length === 0 ? (
        <p className="mt-2 text-sm text-muted-foreground">{emptyText}</p>
      ) : (
        <ul className="mt-2 space-y-1 text-sm text-muted-foreground">
          {items.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      )}
    </section>
  );
}

function FindingsPanel({ findings }: { findings: BuilderFinding[] }) {
  return (
    <section className="rounded-ui border border-border bg-surface p-4">
      <h2 className="text-base font-medium">Findings</h2>
      {findings.length === 0 ? (
        <p className="mt-2 text-sm text-muted-foreground">No planner or bundle findings yet.</p>
      ) : (
        <FindingsList findings={findings} className="mt-3" />
      )}
    </section>
  );
}

function FindingsList({ findings, className }: { findings: BuilderFinding[]; className?: string }) {
  return (
    <ul className={cn("space-y-2", className)}>
      {findings.map((finding, index) => {
        const level = normalizeFindingLevel(finding.level);
        return (
          <li key={`${finding.code}:${index}`} className="rounded-ui border border-border bg-background p-3 text-sm">
            <div className="flex flex-wrap items-center gap-2">
              <Badge className={findingTone(level)}>{finding.level}</Badge>
              <span className="font-medium">{finding.code}</span>
            </div>
            <p className="mt-1 text-muted-foreground">{finding.message}</p>
            {finding.scope ? <p className="mt-1 text-xs text-muted-foreground">{finding.scope}</p> : null}
          </li>
        );
      })}
    </ul>
  );
}

function BundlePreview({
  bundle,
  selectedFile,
  onFileSelect
}: {
  bundle: BuilderBundleResponse | null;
  selectedFile: BuilderBundleFile | null;
  onFileSelect: (path: string) => void;
}) {
  return (
    <section className="rounded-ui border border-border bg-surface p-4">
      <h2 className="text-base font-medium">Bundle Preview</h2>
      {!bundle ? (
        <p className="mt-2 text-sm text-muted-foreground">Generate a bundle to inspect rendered files.</p>
      ) : (
        <div className="mt-3 space-y-3">
          <p className="text-sm text-muted-foreground">Bundle {bundle.bundleId}</p>
          <div className="flex flex-wrap gap-2">
            {bundle.files.map((file) => (
              <button
                key={file.path}
                type="button"
                className={cn(
                  "rounded-ui border border-border px-2 py-1 text-xs transition-colors",
                  selectedFile?.path === file.path ? "bg-foreground text-background" : "bg-background text-foreground hover:bg-muted"
                )}
                onClick={() => onFileSelect(file.path)}
              >
                {file.path}
              </button>
            ))}
          </div>
          {selectedFile ? (
            <pre className="max-h-[24rem] overflow-auto rounded-ui border border-border bg-background p-3 text-xs text-foreground">
              <code>{selectedFile.contents}</code>
            </pre>
          ) : null}
        </div>
      )}
    </section>
  );
}

function SummaryItem({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="mt-1 truncate font-medium">{value}</dd>
    </div>
  );
}

function collectFeatureCatalogItems(packages: BuilderPackage[]) {
  return packages.flatMap((packageItem) => {
    const version = latestPackageVersion(packageItem);
    if (!version) return [];
    return version.features.map((feature) => ({
      key: `${packageItem.source.id}:${packageItem.packageId}:${version.version}:${feature.featureId}`,
      packageItem,
      version,
      feature
    }));
  });
}

function filterFeatures(features: FeatureCatalogItem[], query: string, selectedCategories: string[]) {
  const term = query.trim();
  return features.filter((item) => featureMatchesSelectedCategories(item.feature, selectedCategories) && (!term || matchesSearch(featureSearchText(item), term)));
}

function featureSearchText(item: FeatureCatalogItem) {
  const feature = item.feature;
  return [
    feature.featureId,
    feature.typeName,
    feature.displayName,
    feature.description,
    feature.category,
    ...featureCategories(feature),
    ...feature.runtimeKinds,
    ...feature.requiredCapabilities,
    ...feature.infrastructure.flatMap((requirement) => [
      requirement.id,
      requirement.kind,
      requirement.reason,
      ...requirement.capabilities,
      ...requirement.providers,
      ...requirement.configurationKeys
    ]),
    ...(feature.dependencies ?? []).flatMap((dependency) => [
      dependency.packageId,
      dependency.featureId,
      dependency.versionRange,
      dependency.reason
    ]),
    item.packageItem.packageId,
    item.packageItem.displayName,
    item.packageItem.source.name,
    item.packageItem.source.url,
    item.version.version
  ].filter((value): value is string => Boolean(value));
}

function featureCategoryCounts(features: FeatureCatalogItem[]) {
  const counts = new Map<string, number>();
  features.forEach((item) => {
    featureCategories(item.feature).forEach((category) => counts.set(category, (counts.get(category) ?? 0) + 1));
  });
  return [...counts.entries()]
    .map(([category, count]) => ({ category, count }))
    .sort((left, right) => left.category.localeCompare(right.category));
}

function featureCategories(feature: BuilderPackage["versions"][number]["features"][number]) {
  const categories = (feature.categories ?? [])
    .map((category) => category.trim())
    .filter((category, index, values) => category.length > 0 && values.findIndex((value) => value.toLowerCase() === category.toLowerCase()) === index);

  if (categories.length > 0) return categories;

  const category = feature.category?.trim();
  return category ? [category] : ["Uncategorized"];
}

function featureMatchesSelectedCategories(feature: BuilderPackage["versions"][number]["features"][number], selectedCategories: string[]) {
  if (selectedCategories.length === 0) return true;
  const categories = featureCategories(feature);
  return selectedCategories.some((category) => categories.includes(category));
}

function toggleFeatureCategory(category: string, setSelectedFeatureCategories: Dispatch<SetStateAction<string[]>>) {
  setSelectedFeatureCategories((current) =>
    current.includes(category) ? current.filter((item) => item !== category) : [...current, category]
  );
}

function featureCategoryButtonClassName(selected: boolean) {
  return cn(
    "flex min-w-36 items-center justify-between gap-3 whitespace-nowrap rounded-ui border px-3 py-2 text-left text-sm transition-colors lg:w-full",
    selected ? "border-foreground bg-foreground text-background" : "border-border bg-background text-foreground hover:bg-muted"
  );
}

function matchesSearch(values: string[], query: string) {
  const rawTerm = query.toLowerCase();
  const normalizedTerm = normalizeSearchText(query);
  const compactTerm = compactSearchText(query);
  return values.some((value) => {
    const rawValue = value.toLowerCase();
    return rawValue.includes(rawTerm)
      || normalizeSearchText(value).includes(normalizedTerm)
      || compactSearchText(value).includes(compactTerm);
  });
}

function normalizeSearchText(value: string) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, " ").trim();
}

function compactSearchText(value: string) {
  return value.toLowerCase().replace(/[^a-z0-9]+/g, "");
}

function isFeatureCompatibleWithRuntimeKinds(featureRuntimeKinds: string[] | undefined, runtimeKinds: string[]) {
  const normalizedFeatureRuntimeKinds = normalizeRuntimeKinds(featureRuntimeKinds);
  const normalizedRuntimeKinds = normalizeRuntimeKinds(runtimeKinds);
  if (normalizedRuntimeKinds.length === 0)
    return true;

  return normalizedFeatureRuntimeKinds.length > 0 && normalizedFeatureRuntimeKinds.some((featureRuntimeKind) => normalizedRuntimeKinds.includes(featureRuntimeKind));
}

function normalizeRuntimeKinds(runtimeKinds: string[] | undefined) {
  return Array.from(new Set((runtimeKinds ?? []).map((runtimeKind) => runtimeKind.trim().toLowerCase()).filter(Boolean))).sort();
}

function pruneSelectedPackagesForRuntimeKinds(
  current: Record<string, SelectedRuntimePackage>,
  catalog: BuilderCatalog,
  runtimeKinds: string[]
) {
  let removedFeatureCount = 0;
  const removedFeatures: RemovedRuntimeFeature[] = [];
  const packages: Record<string, SelectedRuntimePackage> = {};

  for (const selection of Object.values(current)) {
    const packageItem = catalog.packages.find(
      (item) => item.source.id === selection.sourceId && item.packageId.toLowerCase() === selection.packageId.toLowerCase()
    );
    const version = packageItem?.versions.find((item) => item.version === selection.version);
    if (!packageItem || !version) {
      packages[packageSelectionKey(selection.sourceId, selection.packageId)] = selection;
      continue;
    }

    const selectedFeatures = selection.selectedFeatures.filter((featureId) => {
      const feature = version.features.find((candidate) => candidate.featureId.toLowerCase() === featureId.toLowerCase());
      const compatible = !feature || isFeatureCompatibleWithRuntimeKinds(feature.runtimeKinds, runtimeKinds);
      if (!compatible) {
        removedFeatureCount++;
        removedFeatures.push({
          featureId: feature.featureId,
          displayName: feature.displayName,
          packageId: packageItem.packageId,
          version: version.version
        });
      }
      return compatible;
    });

    if (selectedFeatures.length > 0) {
      packages[packageSelectionKey(selection.sourceId, selection.packageId)] = {
        ...selection,
        selectedFeatures,
        settings: prunePackageSettings(selection.settings ?? {}, selectedFeatures)
      };
    }
  }

  return { packages, removedFeatureCount, removedFeatures };
}

function inferProviderIds(item: FeatureCatalogItem, providers: InfrastructureProvider[]) {
  const ids = new Set<string>();
  for (const requirement of item.feature.infrastructure) {
    for (const provider of providers) {
      const providerNames = [provider.id, provider.provider, provider.displayName].map((value) => value.toLowerCase());
      const requirementNames = [requirement.id, requirement.kind, ...requirement.providers].map((value) => value.toLowerCase());
      if (providerNames.some((providerName) => requirementNames.includes(providerName))) {
        ids.add(provider.id);
      }
    }
  }
  return Array.from(ids);
}

function inferProviderIdsForFeatureClosure(item: FeatureCatalogItem, catalog: BuilderCatalog, runtimeKinds: string[]) {
  const providerIds = new Set(inferProviderIds(item, catalog.infrastructureProviders));
  for (const dependency of collectRequiredFeatureDependencies(item, catalog, runtimeKinds)) {
    for (const providerId of inferProviderIds(dependency, catalog.infrastructureProviders)) {
      providerIds.add(providerId);
    }
  }
  return Array.from(providerIds);
}

function resolveFeatureSelection(
  current: Record<string, SelectedRuntimePackage>,
  item: FeatureCatalogItem,
  enabled: boolean,
  catalog: BuilderCatalog,
  runtimeKinds: string[]
) {
  const next = { ...current };
  const packageKey = packageSelectionKey(item.packageItem.source.id, item.packageItem.packageId);
  const existing = next[packageKey];
  const selectedFeatures = new Set(existing?.version === item.version.version ? existing.selectedFeatures : []);

  if (enabled) {
    selectedFeatures.add(item.feature.featureId);
  } else {
    selectedFeatures.delete(item.feature.featureId);
  }

  setSelectedFeatureIds(
    next,
    packageKey,
    item.packageItem.source.id,
    item.packageItem.packageId,
    item.version.version,
    selectedFeatures
  );

  if (enabled) {
    for (const dependency of collectRequiredPackageDependencies(item, catalog)) {
      addPackageSelection(next, dependency);
    }
    for (const dependency of collectRequiredFeatureDependencies(item, catalog, runtimeKinds)) {
      addFeatureSelection(next, dependency);
    }
  }

  return next;
}

function collectRequiredFeatureDependencies(item: FeatureCatalogItem, catalog: BuilderCatalog, runtimeKinds: string[]) {
  const dependencies: FeatureCatalogItem[] = [];
  const visited = new Set<string>();
  const visit = (current: FeatureCatalogItem) => {
    for (const dependency of current.feature.dependencies ?? []) {
      if (dependency.optional)
        continue;

      const dependencyItem = findDependencyFeatureItem(current, dependency.packageId, dependency.featureId, catalog);
      if (!dependencyItem || visited.has(dependencyItem.key) || !isFeatureCompatibleWithRuntimeKinds(dependencyItem.feature.runtimeKinds, runtimeKinds))
        continue;

      visited.add(dependencyItem.key);
      dependencies.push(dependencyItem);
      visit(dependencyItem);
    }
  };

  visit(item);
  return dependencies;
}

function collectRequiredPackageDependencies(item: FeatureCatalogItem, catalog: BuilderCatalog) {
  return (item.feature.dependencies ?? [])
    .filter((dependency) => !dependency.optional && !dependency.featureId?.trim() && Boolean(dependency.packageId?.trim()))
    .map((dependency) => catalog.packages.find((packageItem) => packageItem.packageId.toLowerCase() === dependency.packageId!.trim().toLowerCase()))
    .filter((packageItem): packageItem is BuilderPackage => Boolean(packageItem));
}

function findDependencyFeatureItem(
  source: FeatureCatalogItem,
  packageId: string | null | undefined,
  featureId: string | null | undefined,
  catalog: BuilderCatalog
) {
  const requestedFeatureId = featureId?.trim();
  if (!requestedFeatureId)
    return null;

  const dependencyPackageId = packageId?.trim();
  const packageItems = dependencyPackageId
    ? catalog.packages.filter((candidate) => candidate.packageId.toLowerCase() === dependencyPackageId.toLowerCase())
    : catalog.packages;
  const candidates = packageItems
    .flatMap((packageItem) => {
      const preferredVersion = packageItem.packageId.toLowerCase() === source.packageItem.packageId.toLowerCase()
        ? packageItem.versions.find((candidate) => candidate.version === source.version.version) ?? latestPackageVersion(packageItem)
        : latestPackageVersion(packageItem);
      return preferredVersion ? [{ packageItem, version: preferredVersion }] : [];
    })
    .sort((left, right) => dependencyCandidateScore(right, source) - dependencyCandidateScore(left, source));

  for (const candidate of candidates) {
    const feature = candidate.version.features.find((item) => featureMatchesDependency(item, requestedFeatureId));
    if (feature) {
      return {
        key: `${candidate.packageItem.source.id}:${candidate.packageItem.packageId}:${candidate.version.version}:${feature.featureId}`,
        packageItem: candidate.packageItem,
        version: candidate.version,
        feature
      };
    }
  }

  return null;
}

function dependencyCandidateScore(
  candidate: { packageItem: BuilderPackage; version: BuilderPackage["versions"][number] },
  source: FeatureCatalogItem
) {
  let score = 0;
  if (candidate.packageItem.source.id === source.packageItem.source.id)
    score += 4;
  if (candidate.packageItem.packageId.toLowerCase() === source.packageItem.packageId.toLowerCase())
    score += 2;
  if (candidate.version.version === source.version.version)
    score += 1;
  return score;
}

function featureMatchesDependency(feature: BuilderPackage["versions"][number]["features"][number], requestedFeatureId: string) {
  if (feature.featureId.toLowerCase() === requestedFeatureId.toLowerCase())
    return true;

  const shellFeatureName = shellFeatureNameExtension(feature);
  return Boolean(
    shellFeatureName
    && (
      shellFeatureName.toLowerCase() === requestedFeatureId.toLowerCase()
      || requestedFeatureId.toLowerCase().endsWith(`.${shellFeatureName.toLowerCase()}`)
    )
  );
}

function shellFeatureNameExtension(feature: BuilderPackage["versions"][number]["features"][number]) {
  const value = feature.extensions?.cshellsFeatureName;
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

function addPackageSelection(selectedPackages: Record<string, SelectedRuntimePackage>, packageItem: BuilderPackage) {
  const packageKey = packageSelectionKey(packageItem.source.id, packageItem.packageId);
  if (selectedPackages[packageKey])
    return;

  const version = latestPackageVersion(packageItem);
  if (!version)
    return;

  selectedPackages[packageKey] = {
    sourceId: packageItem.source.id,
    packageId: packageItem.packageId,
    version: version.version,
    selectedFeatures: []
  };
}

function addFeatureSelection(selectedPackages: Record<string, SelectedRuntimePackage>, item: FeatureCatalogItem) {
  const packageKey = packageSelectionKey(item.packageItem.source.id, item.packageItem.packageId);
  const existing = selectedPackages[packageKey];
  const selectedFeatures = new Set(existing?.version === item.version.version ? existing.selectedFeatures : []);
  selectedFeatures.add(item.feature.featureId);
  setSelectedFeatureIds(
    selectedPackages,
    packageKey,
    item.packageItem.source.id,
    item.packageItem.packageId,
    item.version.version,
    selectedFeatures
  );
}

function setSelectedFeatureIds(
  selectedPackages: Record<string, SelectedRuntimePackage>,
  packageKey: string,
  sourceId: string,
  packageId: string,
  version: string,
  selectedFeatures: Set<string>
) {
  if (selectedFeatures.size === 0) {
    delete selectedPackages[packageKey];
    return;
  }

  selectedPackages[packageKey] = {
    sourceId,
    packageId,
    version,
    selectedFeatures: Array.from(selectedFeatures).sort((left, right) => left.localeCompare(right, undefined, { sensitivity: "base" })),
    settings: existingSettingsForSelectedFeatures(selectedPackages[packageKey]?.settings, selectedFeatures)
  };
}

function existingSettingsForSelectedFeatures(
  settings: Record<string, Record<string, unknown>> | undefined,
  selectedFeatures: Set<string>
) {
  return prunePackageSettings(settings ?? {}, Array.from(selectedFeatures));
}

function prunePackageSettings(
  settings: Record<string, Record<string, unknown>>,
  selectedFeatures: string[]
) {
  const selected = new Set(selectedFeatures.map((featureId) => featureId.toLowerCase()));
  const pruned: Record<string, Record<string, unknown>> = Object.fromEntries(
    Object.entries(settings)
      .filter(([featureId]) => selected.has(featureId.toLowerCase()))
      .map(([featureId, featureSettings]) => [
        featureId,
        Object.fromEntries(
          Object.entries(featureSettings).filter(([, value]) => !isEmptySettingValue(value))
        )
      ])
      .filter(([, featureSettings]) => Object.keys(featureSettings).length > 0)
  );
  return Object.keys(pruned).length > 0 ? pruned : undefined;
}

function buildIntent({
  catalog,
  selectedImage,
  imageTag,
  hostPort,
  target,
  envOverrides,
  selectedPackages,
  selectedInfrastructure,
  localPackagesEnabled,
  localPackagesPath
}: {
  catalog: BuilderCatalog;
  selectedImage: RuntimeImage;
  imageTag: string;
  hostPort: string;
  target: DeploymentTarget;
  envOverrides: Record<string, string>;
  selectedPackages: Record<string, SelectedRuntimePackage>;
  selectedInfrastructure: Record<string, boolean>;
  localPackagesEnabled: boolean;
  localPackagesPath: string;
}): RuntimeBuilderIntent {
  const packages = Object.values(selectedPackages).map((selection) => ({
    sourceId: selection.sourceId,
    packageId: selection.packageId,
    version: selection.version,
    selectedFeatures: selection.selectedFeatures,
    settings: prunePackageSettings(selection.settings ?? {}, selection.selectedFeatures) ?? null
  }));
  const packageSources = uniqueSources(catalog, packages);
  const infrastructure = catalog.infrastructureProviders
    .filter((provider) => selectedInfrastructure[provider.id])
    .map((provider) => providerToSelection(provider));
  const trimmedEnvOverrides = Object.fromEntries(Object.entries(envOverrides).filter(([, value]) => value.trim()));
  const parsedHostPort = Number.parseInt(hostPort, 10);

  return {
    image: {
      slug: selectedImage.slug,
      tag: imageTag || selectedImage.defaultTag,
      hostPort: Number.isFinite(parsedHostPort) ? parsedHostPort : selectedImage.hostPort,
      envOverrides: Object.keys(trimmedEnvOverrides).length > 0 ? trimmedEnvOverrides : null
    },
    packages,
    packageSources,
    infrastructure,
    localPackages: localPackagesEnabled ? { enabled: true, directoryPath: localPackagesPath.trim() || null } : null,
    target
  };
}

function uniqueSources(catalog: BuilderCatalog, packages: Array<{ sourceId: string; packageId: string }>) {
  const sources = new Map<string, { sourceId: string; name?: string | null; url?: string | null; kind?: string | null }>();
  for (const selected of packages) {
    const packageItem = catalog.packages.find(
      (item) => item.source.id === selected.sourceId && item.packageId.toLowerCase() === selected.packageId.toLowerCase()
    );
    if (packageItem) {
      sources.set(packageItem.source.id, {
        sourceId: packageItem.source.id,
        name: packageItem.source.name,
        url: packageItem.source.url,
        kind: "NuGet"
      });
    }
  }
  return Array.from(sources.values());
}

function providerToSelection(provider: InfrastructureProvider) {
  return {
    kind: provider.kind,
    providerId: provider.id,
    strategy: provider.strategy,
    settings: null
  };
}

function countFeatures(selectedPackages: Record<string, SelectedRuntimePackage>) {
  return Object.values(selectedPackages).reduce((count, selection) => count + selection.selectedFeatures.length, 0);
}

function countConfiguredSettings(selectedPackages: Record<string, SelectedRuntimePackage>) {
  return Object.values(selectedPackages).reduce((count, selection) => {
    const settings = prunePackageSettings(selection.settings ?? {}, selection.selectedFeatures);
    if (!settings)
      return count;

    return count + Object.values(settings).reduce((featureCount, featureSettings) => featureCount + Object.keys(featureSettings).length, 0);
  }, 0);
}

function featureSettingReviewItems(
  selectedFeatureItems: FeatureCatalogItem[],
  selectedPackages: Record<string, SelectedRuntimePackage>
) {
  return selectedFeatureItems.flatMap((item) => {
    const packageKey = packageSelectionKey(item.packageItem.source.id, item.packageItem.packageId);
    const values = selectedPackages[packageKey]?.settings?.[item.feature.featureId] ?? {};
    return item.feature.settings
      .filter((setting) => !isEmptySettingValue(values[setting.name]))
      .map((setting) => `${item.feature.displayName}: ${setting.displayName || setting.name}`);
  });
}

function isEmptySettingValue(value: unknown) {
  return value === undefined || value === null || value === "";
}

function parseNumericSettingValue(value: string, jsonType: string) {
  if (!value.trim())
    return "";

  const number = jsonType === "integer" ? Number.parseInt(value, 10) : Number.parseFloat(value);
  return Number.isFinite(number) ? number : value;
}

function parseStructuredSettingValue(value: string) {
  if (!value.trim())
    return "";

  try {
    return JSON.parse(value) as unknown;
  } catch {
    return value;
  }
}

function findingTone(level: string) {
  if (level === "error") return statusToneClass(sourceStatusTone("Error"));
  if (level === "warning") return statusToneClass(sourceStatusTone("Warning"));
  return statusToneClass(sourceStatusTone("Info"));
}

function hasAutoAddedItems(autoAdded: BuilderPlanResponse["autoAdded"] | undefined) {
  return Boolean(autoAdded && (autoAdded.packages.length > 0 || autoAdded.features.length > 0 || autoAdded.infrastructure.length > 0));
}

function requestErrorFindings(error: unknown, code: string): BuilderFinding[] {
  if (!error)
    return [];

  const scope = requestErrorScope(error);
  return requestErrorMessages(error).map((message, index) => ({
    level: "error",
    code: index === 0 ? code : `${code}.${index + 1}`,
    message,
    scope
  }));
}

function requestErrorMessages(error: unknown) {
  const messages: string[] = [];
  if (error instanceof ApiError) {
    messages.push(...problemMessages(error.details));
  }
  if (error instanceof Error) {
    messages.push(error.message);
  }
  if (messages.length === 0 && typeof error === "string") {
    messages.push(error);
  }

  return Array.from(new Set(messages.filter(Boolean))).length > 0
    ? Array.from(new Set(messages.filter(Boolean)))
    : ["The request failed without returning validation details."];
}

function requestErrorScope(error: unknown) {
  if (error instanceof ApiError) {
    return [`HTTP ${error.status ?? "unknown"}`, error.kind].filter(Boolean).join(" · ");
  }
  return null;
}

function problemMessages(details: unknown): string[] {
  if (!details || typeof details !== "object")
    return [];

  const messages: string[] = [];
  if ("errors" in details) {
    messages.push(...validationMessages(details.errors));
  }
  if ("findings" in details && Array.isArray(details.findings)) {
    messages.push(...details.findings.map((finding) => {
      if (finding && typeof finding === "object" && "message" in finding && typeof finding.message === "string") {
        return finding.message;
      }
      return null;
    }).filter((message): message is string => Boolean(message)));
  }
  if ("error" in details && typeof details.error === "string") {
    messages.push(details.error);
  }
  if ("detail" in details && typeof details.detail === "string") {
    messages.push(details.detail);
  }
  if ("title" in details && typeof details.title === "string") {
    messages.push(details.title);
  }

  return messages;
}

function validationMessages(errors: unknown): string[] {
  if (Array.isArray(errors)) {
    return errors.filter((error): error is string => typeof error === "string");
  }
  if (!errors || typeof errors !== "object") {
    return [];
  }
  return Object.values(errors).flatMap((value) => {
    if (Array.isArray(value)) {
      return value.filter((error): error is string => typeof error === "string");
    }
    return typeof value === "string" ? [value] : [];
  });
}
