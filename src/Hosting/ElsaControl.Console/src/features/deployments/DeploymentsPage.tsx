import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  Activity,
  AlertTriangle,
  ArrowLeft,
  ArrowUpRight,
  Boxes,
  Bot,
  CheckCircle2,
  ClipboardCheck,
  GitBranch,
  KeyRound,
  Pencil,
  Plus,
  RadioTower,
  RefreshCw,
  Rocket,
  Save,
  Search,
  Settings2,
  ShieldCheck,
  XCircle
} from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { Badge, Button, buttonClassName, EmptyState, Input, SecondaryButton, Select, Table } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import {
  createActionConfirmation,
  archiveDeploymentCredentialReference,
  archiveDeploymentSecretStore,
  createDeploymentApplication,
  createDeploymentCredentialReference,
  createDeploymentEnvironment,
  createDeploymentSecretStore,
  createDesiredStateRevision,
  getApplicationRevisions,
  getDeploymentCockpit,
  getEnvironmentDesiredStateRequirements,
  getDeploymentCredentialReferenceUsage,
  getDeploymentCredentialReferences,
  getDeploymentPermissions,
  getDeploymentSecretStores,
  getDeploymentTiers,
  getRevisionDeployability,
  getRevisionDetail,
  previewPromotion,
  promoteRevision,
  queueRollbackRun,
  registerDeploymentEngine,
  rotateDeploymentCredentialReference,
  runRuntimeControl,
  updateDeploymentApplication,
  updateDeploymentCredentialReference,
  updateDeploymentEngine,
  updateDeploymentEnvironment,
  updateDeploymentSecretStore,
  verifyDeploymentEngine
} from "@/features/deployments/deploymentApi";
import { getArtifactUploadCapabilities, listWorkspaceArtifacts, workspaceArtifactDownloadUrl } from "@/features/artifacts/artifactApi";
import type { WorkspaceArtifact } from "@/features/artifacts/artifactModels";
import {
  artifactDisplayName,
  artifactRevisionRecord,
  runDeployChain,
  useDeployChain
} from "@/features/deployments/deployFlow";
import { seedDefaultTiers } from "@/features/deployments/tierPresets";
import {
  EnvironmentRowsEditor,
  TierSeedCard,
  WizardArtifactStep,
  WizardDeployStep,
  WizardDoneScreen,
  type EnvironmentRow
} from "@/features/deployments/SetupWizardSteps";
import {
  DeploymentSetupPanel,
  engineRegistrationRequest,
  type DeploymentSetupValues,
  type EngineRegistrationValues
} from "@/features/deployments/DeploymentSetupPanel";
import { DeploymentRunsPanel } from "@/features/deployments/DeploymentRunsPanel";
import { PromotionPreviewPanel, type PromotionMode } from "@/features/deployments/PromotionPreviewPanel";
import { RuntimeControlsPanel } from "@/features/deployments/RuntimeControlsPanel";
import {
  deploymentTierCapabilities,
  engineLabel,
  environmentLabel,
  hasBlockingValidation,
  type DeploymentCockpit,
  type DeploymentHealth,
  type DeploymentStatus,
  type DriftStatus,
  type EnvironmentSummary,
  type ObservabilityBinding,
  type WorkspaceDesiredStateRecordRequest,
  type RuntimeControl,
  type ValidationSeverity,
  type WorkspaceDeploymentTier,
  type WorkspaceDeploymentCredentialReference,
  type WorkspaceDeploymentSecretStore,
  type DeploymentSecretStoreType,
  type WorkspaceDesiredStateRevisionDetail,
  type WorkspaceDesiredStateRevisionRecord,
  type WorkspaceDesiredStateRevisionSummary,
  type WorkflowEngineRegistration
} from "@/features/deployments/deploymentModels";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { formatDateTime } from "@/lib/formatters";
import { queryKeys } from "@/lib/query/queryClient";
import { permissionLinkProps } from "@/lib/auth/permissionLinkProps";
import { statusToneClass, type StatusTone } from "@/lib/status/statusBadges";
import { cn } from "@/lib/utils";

type DeploymentContext = {
  workspaceId: string;
  data: DeploymentCockpit;
  tiers: WorkspaceDeploymentTier[];
  activeTiers: WorkspaceDeploymentTier[];
  secretStores: WorkspaceDeploymentSecretStore[];
  credentialReferences: WorkspaceDeploymentCredentialReference[];
  canManageSetup: boolean;
  canManageDesiredState: boolean;
  canPreviewPromotion: boolean;
  canExecuteDeployment: boolean;
  canExecuteRollback: boolean;
  canExecuteControls: boolean;
  refreshDeploymentCockpit: () => Promise<unknown>;
};

type EnvironmentFormValues = {
  name: string;
  tierId: string | null;
  tier: EnvironmentSummary["tier"];
};

type ApplicationCreateValues = {
  name: string;
};

type CreatedSetupApplication = {
  id: string;
  name: string;
};

type CreatedSetupEnvironment = {
  id: string;
  name: string;
  tierId: string | null;
};

type CredentialStoreValues = {
  name: string;
  provider: string | null;
  type: DeploymentSecretStoreType;
  description: string | null;
};

type CredentialReferenceValues = {
  secretStoreId: string;
  name: string;
  reference: string;
  description: string | null;
  secretValue?: string | null;
};

type EngineCredentialMode = "Create" | "Existing" | "Deferred";

type SetupWizardStep = "application" | "environment" | "credentials" | "engine" | "artifact" | "deploy" | "done";

const secretStoreTypeOptions: { value: DeploymentSecretStoreType; label: string; description: string; referenceLabel: string; referencePlaceholder: string }[] = [
  {
    value: "LocalEncryptedDatabase",
    label: "Local encrypted database",
    description: "Elsa Control stores protected engine credential material.",
    referenceLabel: "Secret value",
    referencePlaceholder: "Paste engine API credential"
  },
  {
    value: "AzureKeyVault",
    label: "Azure Key Vault",
    description: "Elsa Control stores a Key Vault locator only.",
    referenceLabel: "Key Vault reference",
    referencePlaceholder: "kv://elsa-control/dev/engine-api"
  },
  {
    value: "KubernetesSecrets",
    label: "Kubernetes Secrets",
    description: "Elsa Control stores a namespace/name/key locator only.",
    referenceLabel: "Kubernetes secret locator",
    referencePlaceholder: "k8s://namespace/secret-name/key"
  },
  {
    value: "EnvironmentVariableName",
    label: "Environment variable name",
    description: "Elsa Control stores an engine-host environment variable name only.",
    referenceLabel: "Environment variable name",
    referencePlaceholder: "ELSA_ENGINE_API_KEY"
  },
  {
    value: "GenericExternalReference",
    label: "Generic external reference",
    description: "Elsa Control stores a customer-governed reference it cannot browse or verify.",
    referenceLabel: "External reference",
    referencePlaceholder: "external://secret-catalog/engine/dev-api"
  }
];

type DeploymentBlocker = {
  id: string;
  validationId?: string;
  scope: string;
  message: string;
  severity: ValidationSeverity;
  source: string;
  actionPath?: string;
  actionLabel?: string;
};

type PromotionReadinessIssue = {
  id: string;
  scope: string;
  message: string;
  severity: "Blocker" | "Warning";
  action?: {
    label: string;
    to: string;
    description?: string;
  };
};

type ApplicationSort = "name" | "health" | "environments" | "engines" | "drift";
type EnvironmentSort = "name" | "tier" | "health" | "deployment" | "drift";
type EngineSort = "name" | "health" | "verification" | "heartbeat";
type RevisionSort = "newest" | "environment" | "status";
type RevisionStatusFilter = "all" | "desired" | "deployed" | "superseded" | "never-deployed";

const observabilityRequirementId = "observability-binding";

const applicationSorts: { value: ApplicationSort; label: string }[] = [
  { value: "name", label: "Application" },
  { value: "health", label: "Health" },
  { value: "environments", label: "Environments" },
  { value: "engines", label: "Engines" },
  { value: "drift", label: "Drift" }
];

const environmentSorts: { value: EnvironmentSort; label: string }[] = [
  { value: "name", label: "Environment" },
  { value: "tier", label: "Tier" },
  { value: "health", label: "Health" },
  { value: "deployment", label: "Deployment" },
  { value: "drift", label: "Drift" }
];

const engineSorts: { value: EngineSort; label: string }[] = [
  { value: "name", label: "Engine" },
  { value: "health", label: "Health" },
  { value: "verification", label: "Credential verification" },
  { value: "heartbeat", label: "Last heartbeat" }
];

const revisionSorts: { value: RevisionSort; label: string }[] = [
  { value: "newest", label: "Newest" },
  { value: "environment", label: "Environment" },
  { value: "status", label: "Status" }
];

const revisionStatusFilters: { value: RevisionStatusFilter; label: string }[] = [
  { value: "all", label: "All revisions" },
  { value: "desired", label: "Current desired" },
  { value: "deployed", label: "Currently deployed" },
  { value: "superseded", label: "Superseded" },
  { value: "never-deployed", label: "Never deployed" }
];

export function DeploymentsPage() {
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;

  const { data, canManageSetup } = context.value;
  const totals = deploymentTotals(data);
  const healthyEngines = data.engines.filter((engine) => engine.health === "Healthy").length;

  return (
    <section className="space-y-5">
      <PageHeader
        title="Deployments"
        description="Releases, environment drift, and recent runs."
        actions={
          <>
            <Link to="/admin/deployments/applications" className={buttonClassName("secondary")}>
              <Rocket className="h-4 w-4" />
              Applications
            </Link>
            <Link to="/admin/deployments/new" className={buttonClassName("primary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
              <Plus className="h-4 w-4" />
              New application setup
            </Link>
            <Link to="/admin/deployments/tiers" className={buttonClassName("secondary")}>
              <Settings2 className="h-4 w-4" />
              Workspace tiers
            </Link>
          </>
        }
      />

      <div className="grid gap-3 md:grid-cols-4">
        <MetricCard label="Applications" value={String(data.applications.length)} />
        <MetricCard label="Environments" value={String(totals.environments)} />
        <MetricCard label="Registered engines" value={String(data.engines.length)} />
        <MetricCard label="Drift detected" value={String(totals.drift)} />
      </div>

      <div className="grid gap-4 lg:grid-cols-2">
        <Panel title="Deployment posture" icon={<ShieldCheck className="h-4 w-4" />}>
          <dl className="grid gap-3 text-sm sm:grid-cols-2">
            <Detail label="Healthy engines" value={`${healthyEngines} of ${data.engines.length}`} />
            <Detail label="Blocked environments" value={String(data.applications.flatMap((application) => application.environments).filter((environment) => environment.deploymentStatus === "Blocked").length)} />
            <Detail label="Production tiers" value={String(data.applications.flatMap((application) => application.environments).filter((environment) => environment.tierCapabilities?.includes("deployment.tier.production-like")).length)} />
            <Detail label="Assistant plans" value={String(data.assistantPlans.length)} />
          </dl>
        </Panel>

        <Panel title="Recent activity" icon={<Activity className="h-4 w-4" />}>
          {data.history[0] ? (
            <div className="space-y-2 text-sm">
              <div className="flex items-center justify-between gap-2">
                <span className="font-medium">r{data.history[0].revision}</span>
                <StatusBadge value={data.history[0].status} tone={deploymentTone(data.history[0].status as DeploymentStatus)} />
              </div>
              <p className="text-muted-foreground">{environmentLabel(data.history[0].environmentId, data.applications)} / {engineLabel(data.history[0].engineId, data.engines)}</p>
              <p className="text-xs text-muted-foreground">{formatDateTime(data.history[0].occurredAt)}</p>
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">No deployment history has been recorded.</p>
          )}
        </Panel>
      </div>

      <Panel title="Operational shortcuts" icon={<Settings2 className="h-4 w-4" />}>
        <div className="grid gap-3 md:grid-cols-3">
          <Link to="/admin/deployments/applications" className="rounded-ui border border-border bg-background p-3 text-sm transition-colors hover:bg-muted">
            <div className="font-medium">Applications</div>
            <p className="mt-1 text-xs text-muted-foreground">Manage application, environment, and engine hierarchy.</p>
          </Link>
          <Link to="/admin/deployments/tiers" className="rounded-ui border border-border bg-background p-3 text-sm transition-colors hover:bg-muted">
            <div className="font-medium">Workspace tiers</div>
            <p className="mt-1 text-xs text-muted-foreground">Configure tier capabilities and environment policy.</p>
          </Link>
          <Link to="/admin/deployments/new" className={cn("rounded-ui border border-border bg-background p-3 text-sm transition-colors hover:bg-muted", !canManageSetup ? "pointer-events-none opacity-50" : "")} {...permissionLinkProps(canManageSetup)}>
            <div className="font-medium">New application setup</div>
            <p className="mt-1 text-xs text-muted-foreground">Create an application with its first environment and engine.</p>
          </Link>
        </div>
      </Panel>
    </section>
  );
}

export function DeploymentApplicationsPage() {
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  return <DeploymentApplicationsReady context={context.value} />;
}

function DeploymentApplicationsReady({ context }: { context: DeploymentContext }) {
  const { data, canManageSetup } = context;
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<ApplicationSort>("name");
  const applications = useMemo(
    () => sortApplications(filterApplications(data.applications, data, query), data, sort),
    [data, query, sort]
  );

  return (
    <section className="space-y-5">
      <PageHeader
        title="Applications"
        description={`${data.applications.length} application${data.applications.length === 1 ? "" : "s"}`}
        actions={
          <Link to="/admin/deployments/new" className={buttonClassName("primary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
            <Plus className="h-4 w-4" />
            New application setup
          </Link>
        }
      />

      <div className="grid gap-3 lg:grid-cols-[minmax(16rem,26rem)_12rem] lg:items-center">
        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} className="pl-9" placeholder="Search applications" />
        </label>
        <Select value={sort} onChange={(event) => setSort(event.target.value as ApplicationSort)} aria-label="Sort applications">
          {applicationSorts.map((item) => (
            <option key={item.value} value={item.value}>{item.label}</option>
          ))}
        </Select>
      </div>

      {data.applications.length === 0 ? (
        <EmptyState
          title="No deployment setup"
          description="Connect an engine to create your first application and environment."
          action={
            <Link
              to="/admin/engines/connect"
              className={buttonClassName("primary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)}
              {...permissionLinkProps(canManageSetup)}
            >
              <Plus className="h-4 w-4" />
              Connect engine
            </Link>
          }
        />
      ) : applications.length === 0 ? (
        <EmptyState title="No matching applications" description="Clear the search to see all workflow applications." />
      ) : (
        <section className="space-y-3">
          <ApplicationCards applications={applications} data={data} />
        </section>
      )}
    </section>
  );
}
export function NewDeploymentSetupPage() {
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  return <NewDeploymentSetupReady context={context.value} />;
}

export function DeploymentCredentialsPage() {
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  return <DeploymentCredentialsReady context={context.value} />;
}

export function DeploymentCredentialStoreCreatePage() {
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  return <DeploymentCredentialStoreCreateReady context={context.value} />;
}

export function DeploymentCredentialStoreEditPage() {
  const { secretStoreId } = useParams();
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  if (!secretStoreId) return <RequestStateView state="not-found" title="Credential store not found" />;
  return <DeploymentCredentialStoreEditReady context={context.value} secretStoreId={secretStoreId} />;
}

export function DeploymentCredentialReferenceCreatePage() {
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  return <DeploymentCredentialReferenceCreateReady context={context.value} />;
}

export function DeploymentCredentialReferencePage() {
  const { credentialReferenceId } = useParams();
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  if (!credentialReferenceId) return <RequestStateView state="not-found" title="Credential reference not found" />;
  return <DeploymentCredentialReferenceReady context={context.value} credentialReferenceId={credentialReferenceId} />;
}

export function DeploymentCredentialReferenceEditPage() {
  const { credentialReferenceId } = useParams();
  const context = useDeploymentContext();
  if (context.status !== "ready") return context.state;
  if (!credentialReferenceId) return <RequestStateView state="not-found" title="Credential reference not found" />;
  return <DeploymentCredentialReferenceEditReady context={context.value} credentialReferenceId={credentialReferenceId} />;
}

function NewDeploymentSetupReady({ context }: { context: DeploymentContext }) {
  const queryClient = useQueryClient();
  const {
    workspaceId,
    data,
    activeTiers,
    canManageSetup,
    canManageDesiredState,
    canExecuteDeployment,
    secretStores,
    credentialReferences
  } = context;
  const canManageTiers = useWorkspaceContext().selectedWorkspace?.role === "Owner";
  const [step, setStep] = useState<SetupWizardStep>("application");
  const [createdApplication, setCreatedApplication] = useState<CreatedSetupApplication | null>(null);
  const [createdEnvironments, setCreatedEnvironments] = useState<CreatedSetupEnvironment[]>([]);
  const [engineFormVersion, setEngineFormVersion] = useState(0);
  const [engineCounts, setEngineCounts] = useState<Record<string, number>>({});
  const [selectedArtifact, setSelectedArtifact] = useState<WorkspaceArtifact | null>(null);
  const [selectedEngineId, setSelectedEngineId] = useState("");
  const [runId, setRunId] = useState<string | null>(null);
  const credentialManagement = useCredentialManagementMutations(workspaceId);
  const deployChain = useDeployChain(workspaceId);

  const uploadCapabilities = useQuery({
    queryKey: ["artifacts", workspaceId, "upload-capabilities"],
    queryFn: () => getArtifactUploadCapabilities(workspaceId),
    enabled: Boolean(workspaceId)
  });

  // The "deliver v1" chain deploys into the first Dev-tier environment (falling back to the first
  // created environment), so the wizard always ends at a running workflow in the lowest tier.
  const deployEnvironment = useMemo(() => {
    const devTierIds = new Set(activeTiers.filter((tier) => tier.name.toLowerCase() === "dev").map((tier) => tier.id));
    return createdEnvironments.find((environment) => environment.tierId && devTierIds.has(environment.tierId)) ?? createdEnvironments[0] ?? null;
  }, [activeTiers, createdEnvironments]);
  const deployEnginesForEnvironment = deployEnvironment ? enginesForEnvironment(data, deployEnvironment.id) : [];
  // The engine form registers against the most recently created environment.
  const activeEnvironment = createdEnvironments[createdEnvironments.length - 1] ?? null;

  useEffect(() => {
    if (deployEnginesForEnvironment.some((engine) => engine.id === selectedEngineId)) return;
    setSelectedEngineId(deployEnginesForEnvironment.length === 1 ? deployEnginesForEnvironment[0].id : "");
  }, [deployEnginesForEnvironment, selectedEngineId]);

  const refreshSetupData = () => queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCockpit(workspaceId) });
  const seedTiers = useMutation({
    mutationFn: () => seedDefaultTiers(workspaceId),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.deploymentTiers(workspaceId) }),
        refreshSetupData()
      ]);
    }
  });
  const createApplication = useMutation({
    mutationFn: (values: ApplicationCreateValues) =>
      createDeploymentApplication(workspaceId, { name: values.name, description: null }),
    onSuccess: async (application) => {
      setCreatedApplication({ id: application.id, name: application.name });
      setStep("environment");
      await refreshSetupData();
    }
  });
  const createEnvironments = useMutation({
    mutationFn: async (rows: EnvironmentRow[]) => {
      if (!createdApplication) throw new Error("Create the application before adding environments.");
      const created: CreatedSetupEnvironment[] = [];
      for (const row of rows) {
        const environment = await createDeploymentEnvironment(workspaceId, createdApplication.id, {
          name: row.name,
          tier: row.tier,
          tierId: row.tierId
        });
        created.push({ id: environment.id, name: environment.name || row.name, tierId: environment.tierId ?? row.tierId });
      }
      return created;
    },
    onSuccess: async (created) => {
      setCreatedEnvironments((current) => [...current, ...created]);
      setStep("credentials");
      await refreshSetupData();
    }
  });
  const registerEngine = useMutation({
    mutationFn: (values: EngineRegistrationValues) => {
      if (!activeEnvironment) throw new Error("Create an environment before adding engines.");
      return registerDeploymentEngine(workspaceId, activeEnvironment.id, engineRegistrationRequest(values));
    },
    onSuccess: async (engine) => {
      setEngineCounts((current) => ({ ...current, [engine.environmentId]: (current[engine.environmentId] ?? 0) + 1 }));
      setEngineFormVersion((current) => current + 1);
      await refreshSetupData();
    }
  });
  // The deploy step chains revision creation -> deployability check -> confirmation -> run behind a
  // single action. The user only ever sees "Deploy" and progress; confirmation is internal.
  const deploy = useMutation({
    mutationFn: async () => {
      if (!createdApplication || !deployEnvironment) throw new Error("Create an environment before deploying.");
      if (!selectedArtifact) throw new Error("Choose an artifact before deploying.");
      if (!selectedEngineId) throw new Error("Choose a target engine before deploying.");
      const revision = await createDesiredStateRevision(workspaceId, createdApplication.id, deployEnvironment.id, {
        label: `v1 - ${artifactDisplayName(selectedArtifact)}`,
        commit: null,
        records: [artifactRevisionRecord(selectedArtifact)]
      });
      const run = await deployChain.start({
        revisionId: revision.id,
        targetEnvironmentId: deployEnvironment.id,
        targetEngineId: selectedEngineId
      });
      return run;
    },
    onSuccess: async (run) => {
      setRunId(run.id);
      setStep("done");
      await refreshSetupData();
    }
  });

  const finishPath = createdApplication ? applicationPath(createdApplication.id) : "/admin/deployments/applications";
  const needsTierSeeding = activeTiers.length === 0;

  return (
    <FormPageShell
      title="New application setup"
      description="Create an application, add environments and engines, then deliver the first version to Dev."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: "New application setup" }
      ]}
    >
      <div className="space-y-4">
        <SetupWizardProgress step={step} />
        {needsTierSeeding && step !== "done" ? (
          <TierSeedCard
            isSeeding={seedTiers.isPending}
            error={seedTiers.error instanceof Error ? seedTiers.error.message : undefined}
            canManageTiers={canManageTiers}
            onSeed={() => seedTiers.mutate()}
          />
        ) : null}
        {createdApplication && step !== "done" ? (
          <SetupWizardSummary
            application={createdApplication}
            environments={createdEnvironments}
            engineCounts={engineCounts}
          />
        ) : null}
        {step === "application" ? (
          <ApplicationCreatePanel
            canManageSetup={canManageSetup}
            isSubmitting={createApplication.isPending}
            error={createApplication.error instanceof Error ? createApplication.error.message : undefined}
            onSubmit={(values) => createApplication.mutate(values)}
          />
        ) : null}
        {step === "environment" && createdApplication ? (
          <div className="space-y-3">
            <EnvironmentRowsEditor
              activeTiers={activeTiers}
              canManageSetup={canManageSetup}
              isSubmitting={createEnvironments.isPending}
              error={createEnvironments.error instanceof Error ? createEnvironments.error.message : undefined}
              createdEnvironmentNames={createdEnvironments.map((environment) => environment.name)}
              onCreate={(rows) => createEnvironments.mutate(rows)}
            />
            {createdEnvironments.length > 0 ? (
              <div className="flex flex-wrap gap-2">
                <Button type="button" onClick={() => setStep("credentials")}>
                  Continue to credentials
                </Button>
                <Link to={finishPath} className={buttonClassName("secondary")}>
                  Finish without deploying
                </Link>
              </div>
            ) : null}
          </div>
        ) : null}
        {step === "credentials" && createdApplication && activeEnvironment ? (
          <div className="space-y-3">
            <div className="rounded-ui border border-border bg-surface p-4">
              <div className="mb-4 flex flex-col gap-1">
                <h2 className="text-sm font-semibold">Engine credentials</h2>
                <p className="text-xs text-muted-foreground">Create control-plane-to-engine credential stores and references, or continue with credentials deferred.</p>
              </div>
              <SecretStoresPanel
                workspaceId={workspaceId}
                stores={secretStores}
                references={credentialReferences}
                canManageSetup={canManageSetup}
                isCreatingStore={credentialManagement.createSecretStore.isPending}
                isCreatingReference={credentialManagement.createCredentialReference.isPending}
                pendingActionId={credentialManagement.pendingActionId}
                error={credentialManagement.error}
                onCreateStore={(values) => credentialManagement.createSecretStore.mutate(values)}
                onUpdateStore={(secretStoreId, values) => credentialManagement.updateSecretStore.mutate({ secretStoreId, values })}
                onArchiveStore={(secretStoreId) => credentialManagement.archiveSecretStore.mutate(secretStoreId)}
                onCreateReference={(values) => credentialManagement.createCredentialReference.mutate(values)}
                onUpdateReference={(credentialReferenceId, values) => credentialManagement.updateCredentialReference.mutate({ credentialReferenceId, values })}
                onRotateReference={(credentialReferenceId, secretValue) => credentialManagement.rotateCredentialReference.mutate({ credentialReferenceId, secretValue })}
                onArchiveReference={(credentialReferenceId) => credentialManagement.archiveCredentialReference.mutate(credentialReferenceId)}
              />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button type="button" onClick={() => setStep("engine")}>
                Continue to engines
              </Button>
              <SecondaryButton type="button" onClick={() => setStep("engine")}>
                Skip credentials
              </SecondaryButton>
              <SecondaryButton type="button" onClick={() => setStep("environment")}>
                Back to environments
              </SecondaryButton>
            </div>
          </div>
        ) : null}
        {step === "engine" && createdApplication && activeEnvironment ? (
          <div className="space-y-3">
            <div className="rounded-ui border border-border bg-surface p-4">
              <EngineRegistrationPanel
                key={`${activeEnvironment.id}:${engineFormVersion}`}
                environment={activeEnvironment}
                secretStores={secretStores}
                credentialReferences={credentialReferences}
                isSubmitting={registerEngine.isPending || credentialManagement.createCredentialReference.isPending}
                error={registerEngine.error instanceof Error ? registerEngine.error.message : credentialManagement.error}
                cancelLabel="Back to environments"
                onCancel={() => setStep("environment")}
                onCreateReference={(values) => credentialManagement.createCredentialReference.mutateAsync(values)}
                onSubmit={(values) => registerEngine.mutate(values)}
              />
            </div>
            <div className="flex flex-wrap gap-2">
              <Button type="button" onClick={() => setStep("artifact")}>
                Continue to artifact
              </Button>
              <SecondaryButton type="button" onClick={() => setStep("environment")}>
                Add another environment
              </SecondaryButton>
              <Link to={finishPath} className={buttonClassName("secondary")}>
                Finish without deploying
              </Link>
            </div>
          </div>
        ) : null}
        {step === "artifact" && createdApplication ? (
          <WizardArtifactStep
            workspaceId={workspaceId}
            canManageSetup={canManageSetup}
            maxUploadBytes={uploadCapabilities.data?.maxUploadBytes ?? 52_428_800}
            selectedArtifactId={selectedArtifact?.id ?? ""}
            onSelectArtifact={setSelectedArtifact}
            onContinue={() => setStep("deploy")}
            onBack={() => setStep("engine")}
          />
        ) : null}
        {step === "deploy" && createdApplication && deployEnvironment ? (
          <div className="space-y-3">
            {!canManageDesiredState || !canExecuteDeployment ? (
              <p className="rounded-ui border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
                Desired-state and deployment execute permissions are required to deploy the first version.
              </p>
            ) : null}
            <WizardDeployStep
              phase={deployChain.phase}
              error={deploy.error instanceof Error ? deploy.error.message : deployChain.error ?? undefined}
              environmentName={deployEnvironment.name}
              artifactName={selectedArtifact ? artifactDisplayName(selectedArtifact) : ""}
              engineOptions={deployEnginesForEnvironment.map((engine) => ({ id: engine.id, name: engine.name }))}
              selectedEngineId={selectedEngineId}
              onSelectEngine={setSelectedEngineId}
              onDeploy={() => deploy.mutate()}
              onBack={() => setStep("artifact")}
            />
          </div>
        ) : null}
        {step === "done" && createdApplication && deployEnvironment && runId ? (
          <WizardDoneScreen
            workspaceId={workspaceId}
            runId={runId}
            environmentName={deployEnvironment.name}
            applicationPath={applicationPath(createdApplication.id)}
          />
        ) : null}
      </div>
    </FormPageShell>
  );
}

function DeploymentCredentialsReady({ context }: { context: DeploymentContext }) {
  const { secretStores, credentialReferences, canManageSetup } = context;
  const [statusFilter, setStatusFilter] = useSearchParams();
  const selectedStatus = statusFilter.get("status") === "archived" ? "Archived" : "Active";
  const visibleStores = secretStores.filter((store) => store.status === selectedStatus);

  const setSelectedStatus = (status: "Active" | "Archived") => {
    setStatusFilter(status === "Archived" ? { status: "archived" } : {});
  };

  return (
    <section className="space-y-5">
      <Breadcrumbs items={[{ label: "Deployments", to: "/admin/deployments" }, { label: "Engine credentials" }]} />
      <PageHeader
        title="Engine credentials"
        description="Manage workspace control-plane-to-engine credential stores. Runtime secrets remain managed inside runtimes."
        actions={
          canManageSetup ? (
            <Link to="/admin/deployments/credentials/stores/new" className={buttonClassName("primary")}>
              <Plus className="h-4 w-4" />
              New credential store
            </Link>
          ) : null
        }
      />
      <div className="rounded-ui border border-border bg-muted/30 p-3 text-sm text-muted-foreground">
        Engine credentials let Elsa Control interact with registered workflow engines. Runtime secrets and artifact secret references stay in the runtimes.
      </div>
      <div className="flex flex-wrap items-center gap-2">
        <label className="text-sm font-medium" htmlFor="credential-status-filter">Status</label>
        <Select id="credential-status-filter" className="w-auto min-w-36" value={selectedStatus} onChange={(event) => setSelectedStatus(event.target.value as "Active" | "Archived")}>
          <option value="Active">Active</option>
          <option value="Archived">Archived</option>
        </Select>
      </div>
      {!canManageSetup ? (
        <p className="rounded-ui border border-border bg-surface p-3 text-sm text-muted-foreground">
          Deployment setup permission is required to change engine credential stores and references.
        </p>
      ) : null}
      <section className="space-y-3">
        <SectionHeader title="Credential stores" description="Workspace-scoped stores that hold or locate credentials used by Elsa Control to call engines." />
        <div className="rounded-ui border border-border bg-surface">
          <Table>
            <table className="min-w-full text-sm">
              <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
                <tr>
                  <th className="px-3 py-2">Store</th>
                  <th className="px-3 py-2">Type</th>
                  <th className="px-3 py-2">Credentials</th>
                  <th className="px-3 py-2 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {visibleStores.length === 0 ? (
                  <tr>
                    <td className="px-3 py-6 text-center text-sm text-muted-foreground" colSpan={4}>
                      {selectedStatus === "Archived" ? "No archived engine credential stores." : "No active engine credential stores registered."}
                    </td>
                  </tr>
                ) : (
                  visibleStores.map((store) => (
                    <tr key={store.id}>
                      <td className="px-3 py-3 font-medium">{store.name}</td>
                      <td className="px-3 py-3">{secretStoreTypeLabel(store.type)}</td>
                      <td className="px-3 py-3">{credentialReferences.filter((reference) => reference.status === "Active" && reference.secretStoreId === store.id).length}</td>
                      <td className="px-3 py-3 text-right">
                        <Link to={`/admin/deployments/credentials/stores/${store.id}/edit`} className={buttonClassName("secondary")}>
                          Open
                        </Link>
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </Table>
        </div>
      </section>
    </section>
  );
}

function DeploymentCredentialStoreCreateReady({ context }: { context: DeploymentContext }) {
  const { workspaceId, canManageSetup } = context;
  const navigate = useNavigate();
  const credentialManagement = useCredentialManagementMutations(workspaceId);

  return (
    <FormPageShell
      title="New credential store"
      description="Create a workspace-scoped store for credentials Elsa Control uses to call registered engines."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Engine credentials", to: "/admin/deployments/credentials" },
        { label: "New credential store" }
      ]}
    >
      <CredentialStoreForm
        canManageSetup={canManageSetup}
        isSubmitting={credentialManagement.createSecretStore.isPending}
        error={credentialManagement.error}
        onSubmit={(values) => credentialManagement.createSecretStore.mutate(values, { onSuccess: () => navigate("/admin/deployments/credentials") })}
      />
    </FormPageShell>
  );
}

function DeploymentCredentialStoreEditReady({ context, secretStoreId }: { context: DeploymentContext; secretStoreId: string }) {
  const { workspaceId, secretStores, credentialReferences, canManageSetup } = context;
  const navigate = useNavigate();
  const credentialManagement = useCredentialManagementMutations(workspaceId);
  const store = secretStores.find((item) => item.id === secretStoreId);
  const storeReferences = credentialReferences.filter((reference) => reference.secretStoreId === secretStoreId);
  const activeReferenceCount = storeReferences.filter((reference) => reference.status === "Active").length;
  const [confirmArchive, setConfirmArchive] = useState(false);

  if (!store) {
    return <RequestStateView state="not-found" title="Credential store not found" description="The selected engine credential store does not exist in this workspace." />;
  }

  return (
    <FormPageShell
      title={`Edit ${store.name}`}
      description="Update credential store metadata or archive the store when it is no longer used."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Engine credentials", to: "/admin/deployments/credentials" },
        { label: store.name }
      ]}
    >
      <CredentialStoreForm
        store={store}
        canManageSetup={canManageSetup && store.status === "Active"}
        isSubmitting={credentialManagement.updateSecretStore.isPending}
        error={credentialManagement.error}
        onSubmit={(values) => credentialManagement.updateSecretStore.mutate({ secretStoreId, values }, { onSuccess: () => navigate("/admin/deployments/credentials") })}
      />
      <CredentialStoreReferencesSection
        store={store}
        references={storeReferences}
        canManageSetup={canManageSetup}
      />
      {store.status === "Active" ? (
        <div className="rounded-ui border border-border bg-surface p-4">
          <SectionHeader title="Archive credential store" description="Archiving a store also removes its active references from engine assignment choices." />
          {confirmArchive ? (
            <div className="mt-3 space-y-3">
              <p className="text-sm text-muted-foreground">{activeReferenceCount} active references will be archived with this store.</p>
              <div className="flex flex-wrap gap-2">
                <Button type="button" disabled={!canManageSetup || credentialManagement.pendingActionId === store.id} onClick={() => credentialManagement.archiveSecretStore.mutate(store.id, { onSuccess: () => navigate("/admin/deployments/credentials") })}>
                  Confirm archive
                </Button>
                <SecondaryButton type="button" onClick={() => setConfirmArchive(false)}>Cancel</SecondaryButton>
              </div>
            </div>
          ) : (
            <SecondaryButton type="button" className="mt-3" disabled={!canManageSetup} onClick={() => setConfirmArchive(true)}>
              Archive store
            </SecondaryButton>
          )}
        </div>
      ) : null}
    </FormPageShell>
  );
}

function DeploymentCredentialReferenceCreateReady({ context }: { context: DeploymentContext }) {
  const { workspaceId, secretStores, canManageSetup } = context;
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const credentialManagement = useCredentialManagementMutations(workspaceId);
  const initialStoreId = searchParams.get("storeId") ?? undefined;
  const returnPath = initialStoreId ? `/admin/deployments/credentials/stores/${initialStoreId}/edit` : "/admin/deployments/credentials";

  return (
    <FormPageShell
      title="New credential reference"
      description="Create a named engine credential reference under an active credential store."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Engine credentials", to: "/admin/deployments/credentials" },
        { label: "New credential reference" }
      ]}
    >
      <CredentialReferenceForm
        stores={secretStores}
        initialStoreId={initialStoreId}
        cancelTo={returnPath}
        canManageSetup={canManageSetup}
        isSubmitting={credentialManagement.createCredentialReference.isPending}
        error={credentialManagement.error}
        onSubmit={(values) => credentialManagement.createCredentialReference.mutate(values, { onSuccess: () => navigate(returnPath) })}
      />
    </FormPageShell>
  );
}

function DeploymentCredentialReferenceReady({ context, credentialReferenceId }: { context: DeploymentContext; credentialReferenceId: string }) {
  const { workspaceId, credentialReferences, canManageSetup } = context;
  const credentialManagement = useCredentialManagementMutations(workspaceId);
  const reference = credentialReferences.find((item) => item.id === credentialReferenceId);
  const [usageExpanded, setUsageExpanded] = useState(reference?.usageCount ? reference.usageCount > 0 : false);
  const [rotateValue, setRotateValue] = useState("");
  const [confirmArchive, setConfirmArchive] = useState(false);
  const usage = useQuery({
    queryKey: reference ? queryKeys.deploymentCredentialReferenceUsage(workspaceId, reference.id) : ["deployments", workspaceId, "credential-references", "usage", "none"],
    queryFn: () => getDeploymentCredentialReferenceUsage(workspaceId, reference!.id),
    enabled: Boolean(reference && usageExpanded)
  });

  if (!reference) {
    return <RequestStateView state="not-found" title="Credential reference not found" description="The selected engine credential reference does not exist in this workspace." />;
  }

  const canRotate = canManageSetup && reference.status === "Active" && reference.secretStoreType === "LocalEncryptedDatabase" && rotateValue.trim().length > 0;

  return (
    <section className="space-y-5">
      <Breadcrumbs items={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Engine credentials", to: "/admin/deployments/credentials" },
        { label: reference.name }
      ]} />
      <PageHeader
        title={reference.name}
        description="Inspect usage and manage lifecycle actions for this engine credential reference."
        actions={
          reference.status === "Active" && canManageSetup ? (
            <Link to={`/admin/deployments/credentials/references/${reference.id}/edit`} className={buttonClassName("secondary")}>
              Edit
            </Link>
          ) : null
        }
      />
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(18rem,0.45fr)]">
        <div className="rounded-ui border border-border bg-surface p-4">
          <SectionHeader title="Reference details" description="Safe metadata only. Protected local credential values are never displayed." />
          <dl className="mt-4 grid gap-3 sm:grid-cols-2">
            <Detail label="Store" value={reference.secretStoreName} />
            <Detail label="Provider" value={secretStoreTypeLabel(reference.secretStoreType)} />
            <Detail label="Reference" value={reference.hasProtectedSecret ? "Protected local credential" : reference.reference} />
            <Detail label="Status" value={reference.status} />
            <Detail label="Usage" value={reference.usageCount > 0 ? `${reference.usageCount} engines` : "No active engines"} />
            <Detail label="Verification" value={<StatusBadge value={reference.verificationStatus} tone={credentialTone(reference.verificationStatus)} />} />
          </dl>
          <div className="mt-4">
            <SecondaryButton type="button" onClick={() => setUsageExpanded((current) => !current)}>
              {usageExpanded ? "Hide usage" : "Show usage"}
            </SecondaryButton>
          </div>
          {usageExpanded ? (
            <div className="mt-4 border-t border-border pt-4">
              {usage.isLoading || usage.isFetching ? (
                <p className="text-sm text-muted-foreground">Loading credential usage.</p>
              ) : usage.isError ? (
                <p className="text-sm text-destructive">Credential usage could not load.</p>
              ) : usage.data?.items.length ? (
                <ul className="grid gap-2 text-sm text-muted-foreground sm:grid-cols-2">
                  {usage.data.items.map((item) => (
                    <li key={item.engineId} className="rounded-ui border border-border bg-muted/30 px-3 py-2">
                      <span className="block font-medium text-foreground">{item.engineName}</span>
                      <span>{item.applicationName} / {item.environmentName}</span>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-sm text-muted-foreground">No active engines currently use this credential reference.</p>
              )}
            </div>
          ) : null}
        </div>
        <div className="space-y-4">
          {reference.status === "Active" && reference.secretStoreType === "LocalEncryptedDatabase" ? (
            <form
              className="rounded-ui border border-border bg-surface p-4"
              onSubmit={(event) => {
                event.preventDefault();
                if (!canRotate) return;
                credentialManagement.rotateCredentialReference.mutate({ credentialReferenceId: reference.id, secretValue: rotateValue.trim() }, { onSuccess: () => setRotateValue("") });
              }}
            >
              <SectionHeader title="Rotate credential" description={reference.usageCount > 0 ? `${reference.usageCount} engines use this reference.` : "No active engines currently use this reference."} />
              <label className="mt-3 block text-sm font-medium">
                New credential value
                <Input className="mt-1" type="password" value={rotateValue} onChange={(event) => setRotateValue(event.target.value)} placeholder="New credential value" />
              </label>
              <Button type="submit" className="mt-3" disabled={!canRotate || credentialManagement.pendingActionId === reference.id}>
                Rotate
              </Button>
            </form>
          ) : null}
          {reference.status === "Active" ? (
            <div className="rounded-ui border border-border bg-surface p-4">
              <SectionHeader title="Archive reference" description="Archive only after reassignment or after confirming affected engines no longer need Elsa Control management." />
              {confirmArchive ? (
                <div className="mt-3 space-y-3">
                  <p className="text-sm text-muted-foreground">{reference.usageCount > 0 ? `${reference.usageCount} engines currently use this reference.` : "No active engines currently use this reference."}</p>
                  <div className="flex flex-wrap gap-2">
                    <Button type="button" disabled={!canManageSetup || credentialManagement.pendingActionId === reference.id} onClick={() => credentialManagement.archiveCredentialReference.mutate(reference.id)}>
                      Confirm archive
                    </Button>
                    <SecondaryButton type="button" onClick={() => setConfirmArchive(false)}>Cancel</SecondaryButton>
                  </div>
                </div>
              ) : (
                <SecondaryButton type="button" className="mt-3" disabled={!canManageSetup} onClick={() => setConfirmArchive(true)}>
                  Archive reference
                </SecondaryButton>
              )}
            </div>
          ) : null}
          {credentialManagement.error ? <p className="text-sm text-destructive">{credentialManagement.error}</p> : null}
        </div>
      </div>
    </section>
  );
}

function DeploymentCredentialReferenceEditReady({ context, credentialReferenceId }: { context: DeploymentContext; credentialReferenceId: string }) {
  const { workspaceId, secretStores, credentialReferences, canManageSetup } = context;
  const navigate = useNavigate();
  const credentialManagement = useCredentialManagementMutations(workspaceId);
  const reference = credentialReferences.find((item) => item.id === credentialReferenceId);

  if (!reference) {
    return <RequestStateView state="not-found" title="Credential reference not found" description="The selected engine credential reference does not exist in this workspace." />;
  }

  return (
    <FormPageShell
      title={`Edit ${reference.name}`}
      description="Update safe metadata for this engine credential reference."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Engine credentials", to: "/admin/deployments/credentials" },
        { label: reference.name, to: `/admin/deployments/credentials/references/${reference.id}` },
        { label: "Edit" }
      ]}
    >
      <CredentialReferenceForm
        reference={reference}
        stores={secretStores}
        canManageSetup={canManageSetup && reference.status === "Active"}
        isSubmitting={credentialManagement.updateCredentialReference.isPending}
        error={credentialManagement.error}
        onSubmit={(values) => credentialManagement.updateCredentialReference.mutate({ credentialReferenceId: reference.id, values }, { onSuccess: () => navigate(`/admin/deployments/credentials/references/${reference.id}`) })}
      />
    </FormPageShell>
  );
}

function CredentialStoreReferencesSection({
  store,
  references,
  canManageSetup
}: {
  store: WorkspaceDeploymentSecretStore;
  references: WorkspaceDeploymentCredentialReference[];
  canManageSetup: boolean;
}) {
  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <SectionHeader title="Credentials" description="Credential references registered under this store." />
        {store.status === "Active" && canManageSetup ? (
          <Link to={`/admin/deployments/credentials/references/new?storeId=${encodeURIComponent(store.id)}`} className={buttonClassName("secondary")}>
            <Plus className="h-4 w-4" />
            New credential reference
          </Link>
        ) : null}
      </div>
      <div className="rounded-ui border border-border bg-surface">
        {references.length === 0 ? (
          <EmptyState title="No credentials registered" description="Create credential references from this store before assigning them to engines." />
        ) : (
          <Table>
            <table className="min-w-full text-sm">
              <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
                <tr>
                  <th className="px-3 py-2">Credential</th>
                  <th className="px-3 py-2">Reference</th>
                  <th className="px-3 py-2">Usage</th>
                  <th className="px-3 py-2">Verification</th>
                  <th className="px-3 py-2">Status</th>
                  <th className="px-3 py-2 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {references.map((reference) => (
                  <tr key={reference.id}>
                    <td className="px-3 py-3 font-medium">{reference.name}</td>
                    <td className="px-3 py-3">{reference.hasProtectedSecret ? "Protected local credential" : reference.reference}</td>
                    <td className="px-3 py-3">{reference.usageCount > 0 ? `${reference.usageCount} engines` : "0"}</td>
                    <td className="px-3 py-3"><StatusBadge value={reference.verificationStatus} tone={credentialTone(reference.verificationStatus)} /></td>
                    <td className="px-3 py-3">{reference.status}</td>
                    <td className="px-3 py-3 text-right">
                      <Link to={`/admin/deployments/credentials/references/${reference.id}`} className={buttonClassName("secondary")}>
                        Open
                      </Link>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Table>
        )}
      </div>
    </section>
  );
}

const setupWizardSteps: { id: SetupWizardStep; label: string }[] = [
  { id: "application", label: "Application" },
  { id: "environment", label: "Environments" },
  { id: "credentials", label: "Credentials" },
  { id: "engine", label: "Engines" },
  { id: "artifact", label: "Artifact" },
  { id: "deploy", label: "Deploy" }
];

function SetupWizardProgress({ step }: { step: SetupWizardStep }) {
  const activeIndex = setupWizardSteps.findIndex((item) => item.id === step);
  const effectiveIndex = step === "done" ? setupWizardSteps.length : activeIndex;

  return (
    <div className="flex flex-wrap gap-2 text-xs font-medium uppercase text-muted-foreground">
      {setupWizardSteps.map((item, index) => (
        <span
          key={item.id}
          className={cn(
            "rounded-ui border border-border px-3 py-1",
            step === item.id
              ? "border-primary bg-primary/10 text-primary"
              : index < effectiveIndex
                ? "border-success/40 bg-success/10 text-success"
                : "bg-surface"
          )}
        >
          {item.label}
        </span>
      ))}
    </div>
  );
}

function SetupWizardSummary({
  application,
  environments,
  engineCounts
}: {
  application: CreatedSetupApplication;
  environments: CreatedSetupEnvironment[];
  engineCounts: Record<string, number>;
}) {
  return (
    <div className="rounded-ui border border-border bg-surface p-4 text-sm">
      <div className="font-medium">{application.name}</div>
      {environments.length === 0 ? (
        <p className="mt-1 text-muted-foreground">No environments added yet.</p>
      ) : (
        <div className="mt-3 flex flex-wrap gap-2">
          {environments.map((environment) => (
            <span key={environment.id} className="rounded-ui border border-border bg-muted/30 px-2 py-1 text-xs">
              {environment.name} - {engineCounts[environment.id] ?? 0} engines
            </span>
          ))}
        </div>
      )}
    </div>
  );
}

function ApplicationCreatePanel({
  canManageSetup,
  isSubmitting,
  error,
  onSubmit
}: {
  canManageSetup: boolean;
  isSubmitting: boolean;
  error?: string;
  onSubmit: (values: ApplicationCreateValues) => void;
}) {
  const [values, setValues] = useState<ApplicationCreateValues>({ name: "" });
  const canSubmit = canManageSetup && values.name.trim().length > 0;

  return (
    <form
      className="rounded-ui border border-border bg-surface p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (canSubmit) onSubmit(values);
      }}
    >
      <label className="block text-sm font-medium">
        Application
        <Input
          className="mt-1"
          value={values.name}
          onChange={(event) => setValues({ name: event.target.value })}
        />
      </label>
      {error ? <p className="mt-3 text-sm text-destructive">{error}</p> : null}
      {!canManageSetup ? <p className="mt-3 text-sm text-muted-foreground">Deployment setup permission is required.</p> : null}
      <div className="mt-4">
        <Button type="submit" disabled={!canSubmit || isSubmitting}>
          <Plus className="h-4 w-4" />
          Create application
        </Button>
      </div>
    </form>
  );
}

export function DeploymentApplicationPage() {
  const context = useDeploymentContext();
  const { applicationId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentApplicationReady context={context.value} applicationId={applicationId} />;
}

function DeploymentApplicationReady({ context, applicationId }: { context: DeploymentContext; applicationId: string }) {
  const { data, canManageSetup } = context;
  const application = findApplication(data, applicationId);
  if (!application) return <RequestStateView state="not-found" title="Application not found" />;

  const [environmentQuery, setEnvironmentQuery] = useState("");
  const [environmentSort, setEnvironmentSort] = useState<EnvironmentSort>("name");
  const environments = useMemo(
    () => sortEnvironments(filterEnvironments(application.environments, environmentQuery), environmentSort),
    [application.environments, environmentQuery, environmentSort]
  );

  return (
    <section className="space-y-5">
      <Breadcrumbs
        items={[
          { label: "Deployments", to: "/admin/deployments" },
          { label: "Applications", to: "/admin/deployments/applications" },
          { label: application.name }
        ]}
      />
      <PageHeader
        title={application.name}
        description="Manage environments for this workflow application."
        actions={
          <>
            <Link to={applicationRevisionsPath(application.id)} className={buttonClassName("secondary")}>
              <GitBranch className="h-4 w-4" />
              Revisions
            </Link>
            <Link to={`/admin/deployments/applications/${application.id}/edit`} className={buttonClassName("secondary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
              <Pencil className="h-4 w-4" />
              Edit application
            </Link>
            <Link to={`/admin/deployments/applications/${application.id}/environments/new`} className={buttonClassName("primary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
              <Plus className="h-4 w-4" />
              Add environment
            </Link>
          </>
        }
      />

      <div className="grid gap-3 lg:grid-cols-[minmax(16rem,26rem)_auto] lg:items-center">
        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
          <Input value={environmentQuery} onChange={(event) => setEnvironmentQuery(event.target.value)} className="pl-9" placeholder="Search environments" />
        </label>
        <Select value={environmentSort} onChange={(event) => setEnvironmentSort(event.target.value as EnvironmentSort)} aria-label="Sort environments">
          {environmentSorts.map((item) => (
            <option key={item.value} value={item.value}>{item.label}</option>
          ))}
        </Select>
      </div>

      <section className="space-y-3">
        <SectionHeader title="Environments" description="Open an environment to inspect engines, promotions, runs, drift, and approvals." />
        {application.environments.length === 0 ? (
          <EmptyState title="No environments registered" description="Add an environment, then register workflow engines from the environment page." />
        ) : environments.length === 0 ? (
          <EmptyState title="No matching environments" description="Clear the search to see all environments." />
        ) : (
          <EnvironmentTable application={application} environments={environments} data={data} />
        )}
      </section>
    </section>
  );
}

export function DeploymentApplicationRevisionsPage() {
  const context = useDeploymentContext();
  const { applicationId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentApplicationRevisionsReady context={context.value} applicationId={applicationId} />;
}

function DeploymentApplicationRevisionsReady({ context, applicationId }: { context: DeploymentContext; applicationId: string }) {
  const { workspaceId, data, canManageDesiredState } = context;
  const application = findApplication(data, applicationId);
  const [searchParams] = useSearchParams();
  const [query, setQuery] = useState("");
  const [environmentId, setEnvironmentId] = useState(searchParams.get("environment") ?? "all");
  const [status, setStatus] = useState<RevisionStatusFilter>(() => parseRevisionStatusFilter(searchParams.get("status")));
  const [sort, setSort] = useState<RevisionSort>("newest");
  const [newRevisionEnvironmentId, setNewRevisionEnvironmentId] = useState("");
  const revisions = useQuery({
    queryKey: queryKeys.deploymentApplicationRevisions(workspaceId, applicationId),
    queryFn: () => getApplicationRevisions(workspaceId, applicationId),
    enabled: Boolean(application)
  });

  useEffect(() => {
    if (!application) return;
    if (!application.environments.some((environment) => environment.id === newRevisionEnvironmentId)) {
      setNewRevisionEnvironmentId(application.environments[0]?.id ?? "");
    }
  }, [application, newRevisionEnvironmentId]);

  if (!application) return <RequestStateView state="not-found" title="Application not found" />;

  const revisionItems = revisions.data?.items ?? [];
  const visibleRevisions = sortRevisions(filterRevisions(revisionItems, query, environmentId, status), sort);
  const newRevisionPath = newRevisionEnvironmentId ? `${environmentPath(application.id, newRevisionEnvironmentId)}/revisions/new` : "";

  return (
    <section className="space-y-5">
      <Breadcrumbs
        items={[
          { label: "Deployments", to: "/admin/deployments" },
          { label: "Applications", to: "/admin/deployments/applications" },
          { label: application.name, to: applicationPath(application.id) },
          { label: "Revisions" }
        ]}
      />
      <PageHeader
        title={`${application.name} revisions`}
        description="Review desired-state revisions across this application's environments."
        actions={
          <div className="grid gap-2 sm:grid-cols-[minmax(12rem,18rem)_auto]">
            <Select
              value={newRevisionEnvironmentId}
              disabled={application.environments.length === 0}
              onChange={(event) => setNewRevisionEnvironmentId(event.target.value)}
              aria-label="New revision environment"
            >
              {application.environments.length === 0 ? (
                <option value="">No environments</option>
              ) : (
                application.environments.map((environment) => <option key={environment.id} value={environment.id}>{environment.name}</option>)
              )}
            </Select>
            <Link
              to={newRevisionPath || applicationPath(application.id)}
              className={buttonClassName("primary", !canManageDesiredState || !newRevisionPath ? "pointer-events-none opacity-50" : undefined)}
              {...permissionLinkProps(canManageDesiredState && Boolean(newRevisionPath))}
            >
              <Plus className="h-4 w-4" />
              New revision
            </Link>
          </div>
        }
      />

      <div className="grid gap-3 xl:grid-cols-[minmax(16rem,1fr)_minmax(12rem,18rem)_minmax(12rem,18rem)_auto] xl:items-center">
        <label className="relative block">
          <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
          <Input value={query} onChange={(event) => setQuery(event.target.value)} className="pl-9" placeholder="Search revisions" />
        </label>
        <Select value={environmentId} onChange={(event) => setEnvironmentId(event.target.value)} aria-label="Filter revisions by environment">
          <option value="all">All environments</option>
          {application.environments.map((environment) => <option key={environment.id} value={environment.id}>{environment.name}</option>)}
        </Select>
        <Select value={status} onChange={(event) => setStatus(event.target.value as RevisionStatusFilter)} aria-label="Filter revisions by status">
          {revisionStatusFilters.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
        </Select>
        <Select value={sort} onChange={(event) => setSort(event.target.value as RevisionSort)} aria-label="Sort revisions">
          {revisionSorts.map((item) => <option key={item.value} value={item.value}>{item.label}</option>)}
        </Select>
      </div>

      {revisions.isLoading || revisions.isFetching ? (
        <RequestStateView state="loading" title="Loading revisions" />
      ) : revisions.isError ? (
        <RequestStateView state="unexpected" title="Revisions could not load" />
      ) : revisionItems.length === 0 ? (
        <EmptyState
          title="No revisions yet"
          description="Create a desired-state revision from a registered artifact before deploying this application."
          action={
            newRevisionPath ? (
              <Link to={newRevisionPath} className={buttonClassName("secondary", !canManageDesiredState ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageDesiredState)}>
                <Plus className="h-4 w-4" />
                New revision
              </Link>
            ) : undefined
          }
        />
      ) : visibleRevisions.length === 0 ? (
        <EmptyState title="No matching revisions" description="Clear filters to see all revisions for this application." />
      ) : (
        <RevisionTable application={application} revisions={visibleRevisions} />
      )}
    </section>
  );
}

export function DeploymentRevisionDetailPage() {
  const context = useDeploymentContext();
  const { applicationId = "", revisionId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentRevisionDetailReady context={context.value} applicationId={applicationId} revisionId={revisionId} />;
}

function DeploymentRevisionDetailReady({ context, applicationId, revisionId }: { context: DeploymentContext; applicationId: string; revisionId: string }) {
  const queryClient = useQueryClient();
  const { workspaceId, data, canExecuteDeployment } = context;
  const application = findApplication(data, applicationId);
  const revision = useQuery({
    queryKey: queryKeys.deploymentRevision(workspaceId, revisionId),
    queryFn: () => getRevisionDetail(workspaceId, revisionId),
    enabled: Boolean(application && revisionId)
  });
  const detail = revision.data;
  const summary = detail?.summary;
  const environment = summary ? application?.environments.find((item) => item.id === summary.revision.environmentId) : undefined;
  const engines = summary ? enginesForEnvironment(data, summary.revision.environmentId) : [];
  const [selectedEngineId, setSelectedEngineId] = useState("");
  const deployability = useQuery({
    queryKey: summary && selectedEngineId
      ? queryKeys.deploymentRevisionDeployability(workspaceId, summary.revision.id, selectedEngineId)
      : ["deployments", workspaceId, "revisions", revisionId, "deployability", "none"],
    queryFn: () => getRevisionDeployability(workspaceId, summary!.revision.id, summary!.revision.environmentId, selectedEngineId),
    enabled: Boolean(summary && selectedEngineId && !summary.isCurrentDeployed)
  });
  const deploy = useMutation({
    mutationFn: async () => {
      if (!summary)
        throw new Error("Revision has not loaded.");
      if (!selectedEngineId)
        throw new Error("Choose an engine before deploying this revision.");
      const { run } = await runDeployChain(workspaceId, {
        revisionId: summary.revision.id,
        targetEnvironmentId: summary.revision.environmentId,
        targetEngineId: selectedEngineId,
        skipDeployabilityCheck: true
      });
      return run;
    },
    onSuccess: async () => {
      await Promise.all([
        context.refreshDeploymentCockpit(),
        queryClient.invalidateQueries({ queryKey: queryKeys.deploymentApplicationRevisions(workspaceId, applicationId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.deploymentRevision(workspaceId, revisionId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.deploymentRevisionDeployability(workspaceId, revisionId, selectedEngineId) })
      ]);
    }
  });

  useEffect(() => {
    if (engines.some((engine) => engine.id === selectedEngineId)) return;
    setSelectedEngineId(engines.length === 1 ? engines[0].id : "");
  }, [engines, selectedEngineId]);

  if (!application) return <RequestStateView state="not-found" title="Application not found" />;
  if (revision.isLoading || revision.isFetching) return <RequestStateView state="loading" title="Loading revision" />;
  if (revision.isError) return <RequestStateView state="unexpected" title="Revision could not load" />;
  if (!detail || !summary || summary.revision.applicationId !== application.id) return <RequestStateView state="not-found" title="Revision not found" />;

  const deployabilityResult = deployability.data;
  const deployabilityBlocked = deployabilityResult?.status === "Blocked";
  const deployDisabled = !canExecuteDeployment || summary.isCurrentDeployed || engines.length === 0 || !selectedEngineId || deploy.isPending || deployability.isLoading || deployabilityBlocked;

  return (
    <section className="space-y-5">
      <Breadcrumbs
        items={[
          { label: "Deployments", to: "/admin/deployments" },
          { label: "Applications", to: "/admin/deployments/applications" },
          { label: application.name, to: applicationPath(application.id) },
          { label: "Revisions", to: applicationRevisionsPath(application.id) },
          { label: `r${summary.revision.revisionNumber}` }
        ]}
      />
      <PageHeader
        title={`Revision r${summary.revision.revisionNumber}`}
        description={`${summary.environmentName} desired-state revision for ${application.name}.`}
        actions={
          <>
            <Link to={`${environmentPath(application.id, summary.revision.environmentId)}/revisions/new`} className={buttonClassName("secondary")}>
              <Plus className="h-4 w-4" />
              New revision
            </Link>
            <Link to={environmentPath(application.id, summary.revision.environmentId)} className={buttonClassName("secondary")}>
              <ShieldCheck className="h-4 w-4" />
              Environment
            </Link>
          </>
        }
      />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
        <div className="space-y-4">
          <Panel title="Revision metadata" icon={<GitBranch className="h-4 w-4" />}>
            <dl className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
              <Detail label="Status" value={<RevisionStateBadge revision={summary} />} />
              <Detail label="Environment" value={environment ? <Link to={environmentPath(application.id, environment.id)} className="text-primary hover:underline">{environment.name}</Link> : summary.environmentName} />
              <Detail label="Tier" value={summary.environmentTierName ?? summary.environmentTier} />
              <Detail label="Label" value={summary.revision.label} />
              <Detail label="Commit" value={summary.revision.commit} />
              <Detail label="Authored" value={formatDateTime(summary.revision.authoredAt)} />
              <Detail label="Content hash" value={summary.revision.contentHash} />
              <Detail label="Latest run" value={summary.latestRunStatus ? `${summary.latestRunStatus} · ${formatDateTime(summary.latestRunQueuedAt)}` : "No runs queued"} />
            </dl>
          </Panel>

          <Panel title="Desired-state records" icon={<ClipboardCheck className="h-4 w-4" />}>
            {detail.records.length === 0 ? (
              <EmptyState title="No structured records" description="This revision does not expose structured desired-state records." />
            ) : (
              <RevisionRecordTable records={detail.records} />
            )}
          </Panel>

          <Panel title="Deployment runs" icon={<Rocket className="h-4 w-4" />}>
            {detail.runs.length === 0 ? (
              <EmptyState title="No deployment runs" description="Queue deployment when this revision is ready to apply." />
            ) : (
              <RevisionRunTable runs={detail.runs} engines={data.engines} />
            )}
          </Panel>
        </div>

        <aside className="space-y-4">
          <Panel title="Deploy revision" icon={<Rocket className="h-4 w-4" />}>
            <div className="space-y-3">
              {summary.isCurrentDeployed ? (
                <p className="rounded-ui border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
                  This revision is already deployed in {summary.environmentName}.
                </p>
              ) : engines.length === 0 ? (
                <p className="rounded-ui border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
                  Register an engine before deploying this revision.
                </p>
              ) : (
                <label className="block text-sm font-medium">
                  Target engine
                  <Select className="mt-1 w-full" value={selectedEngineId} onChange={(event) => setSelectedEngineId(event.target.value)}>
                    <option value="" disabled>Choose engine</option>
                    {engines.map((engine) => <option key={engine.id} value={engine.id}>{engine.name}</option>)}
                  </Select>
                </label>
              )}
              <Button disabled={deployDisabled} onClick={() => deploy.mutate()}>
                <Rocket className="h-4 w-4" />
                {deploy.isPending ? "Queueing deployment" : "Deploy revision"}
              </Button>
              {selectedEngineId && !summary.isCurrentDeployed ? (
                <div className="rounded-ui border border-border bg-muted/30 px-3 py-2 text-sm" role={deployabilityBlocked ? "alert" : "status"}>
                  {deployability.isLoading || deployability.isFetching ? (
                    <p className="text-muted-foreground">Checking deployability.</p>
                  ) : deployability.isError ? (
                    <p className="text-destructive">Deployability could not be checked.</p>
                  ) : deployabilityResult ? (
                    <div className="space-y-2">
                      <p className={cn("font-medium", deployabilityBlocked ? "text-destructive" : "text-success")}>
                        {deployabilityResult.status}
                      </p>
                      {deployabilityResult.blockers.length === 0 ? (
                        <p className="text-muted-foreground">
                          All {deployabilityResult.artifacts.length} artifact records can be applied by the selected engine.
                        </p>
                      ) : (
                        <ul className="space-y-2">
                          {deployabilityResult.blockers.map((blocker) => (
                            <li key={`${blocker.id}-${blocker.artifactRecordId ?? ""}`} className="text-muted-foreground">
                              <span className="font-medium text-foreground">{blocker.scope}: </span>
                              {blocker.message}
                              <span className="block text-xs">{blocker.remediation}</span>
                            </li>
                          ))}
                        </ul>
                      )}
                    </div>
                  ) : null}
                </div>
              ) : null}
              {!canExecuteDeployment ? <p className="text-xs text-muted-foreground">Deployment execution permission is required.</p> : null}
              {deployabilityBlocked ? <p className="text-xs text-muted-foreground">Resolve deployability blockers before queueing deployment.</p> : null}
              {deploy.error instanceof Error ? <p role="alert" className="text-sm text-destructive">{deploy.error.message}</p> : null}
              {deploy.isSuccess ? <p role="status" className="text-sm text-success">Deployment run queued.</p> : null}
            </div>
          </Panel>
          <Panel title="Raw desired state" icon={<ClipboardCheck className="h-4 w-4" />}>
            <pre className="max-h-80 overflow-auto rounded-ui bg-muted/40 p-3 text-xs text-muted-foreground">{formatJson(summary.revision.desiredStateJson)}</pre>
          </Panel>
        </aside>
      </div>
    </section>
  );
}

export function DeploymentApplicationEditPage() {
  const context = useDeploymentContext();
  const { applicationId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentApplicationEditReady context={context.value} applicationId={applicationId} />;
}

function DeploymentApplicationEditReady({ context, applicationId }: { context: DeploymentContext; applicationId: string }) {
  const navigate = useNavigate();
  const { workspaceId, data } = context;
  const application = findApplication(data, applicationId);
  if (!application) return <RequestStateView state="not-found" title="Application not found" />;

  const updateApplication = useMutation({
    mutationFn: (name: string) => updateDeploymentApplication(workspaceId, application.id, { name, description: null }),
    onSuccess: async () => {
      await context.refreshDeploymentCockpit();
      navigate(applicationPath(application.id));
    }
  });

  return (
    <FormPageShell
      title="Edit application"
      description="Update the workflow application name."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: application.name, to: applicationPath(application.id) },
        { label: "Edit" }
      ]}
    >
      <ApplicationEditPanel
        application={application}
        isSubmitting={updateApplication.isPending}
        error={updateApplication.error instanceof Error ? updateApplication.error.message : undefined}
        onCancel={() => navigate(applicationPath(application.id))}
        onSubmit={(name) => updateApplication.mutate(name)}
      />
    </FormPageShell>
  );
}

export function DeploymentEnvironmentCreatePage() {
  const context = useDeploymentContext();
  const { applicationId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentEnvironmentCreateReady context={context.value} applicationId={applicationId} />;
}

function DeploymentEnvironmentCreateReady({ context, applicationId }: { context: DeploymentContext; applicationId: string }) {
  const navigate = useNavigate();
  const { workspaceId, data, activeTiers, canManageSetup } = context;
  const application = findApplication(data, applicationId);
  if (!application) return <RequestStateView state="not-found" title="Application not found" />;

  const createEnvironment = useMutation({
    mutationFn: async (values: DeploymentSetupValues) => {
      const environment = await createDeploymentEnvironment(workspaceId, application.id, {
        name: values.environmentName,
        tier: values.environmentTier,
        tierId: values.environmentTierId
      });
      return environment;
    },
    onSuccess: async (environment) => {
      await context.refreshDeploymentCockpit();
      navigate(environmentPath(application.id, environment.id));
    }
  });

  return (
    <FormPageShell
      title="Add environment"
      description="Create an environment for this application."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: application.name, to: applicationPath(application.id) },
        { label: "Add environment" }
      ]}
    >
      <DeploymentSetupPanel
        fixedApplicationName={application.name}
        canManageSetup={canManageSetup}
        tiers={activeTiers}
        submitLabel="Add environment"
        isSubmitting={createEnvironment.isPending}
        error={createEnvironment.error instanceof Error ? createEnvironment.error.message : undefined}
        onSubmit={(values) => createEnvironment.mutate(values)}
      />
    </FormPageShell>
  );
}

export function DeploymentEnvironmentPage() {
  const context = useDeploymentContext();
  const { applicationId = "", environmentId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentEnvironmentReady context={context.value} applicationId={applicationId} environmentId={environmentId} />;
}

function DeploymentEnvironmentReady({
  context,
  applicationId,
  environmentId
}: {
  context: DeploymentContext;
  applicationId: string;
  environmentId: string;
}) {
  const {
    data,
    canManageSetup,
    canManageDesiredState,
    canPreviewPromotion,
    canExecuteDeployment,
    canExecuteRollback,
    workspaceId
  } = context;
  const resolved = resolveEnvironment(data, applicationId, environmentId);
  if (!resolved.application) return <RequestStateView state="not-found" title="Application not found" />;
  if (!resolved.environment) return <RequestStateView state="not-found" title="Environment not found" />;

  const { application, environment } = resolved;
  const environmentEngines = enginesForEnvironment(data, environment.id);
  const initialPromotionMode = defaultPromotionMode(environment);
  const [promotionMode, setPromotionMode] = useState<PromotionMode>(() => initialPromotionMode);
  const effectivePromotionMode = normalizePromotionMode(environment, promotionMode);
  const allowedPromotionModes = promotionModesFor(environment);
  const selectablePromotionEnvironments = effectivePromotionMode === "from-current"
    ? eligiblePromotionTargets(application, environment, data.engines)
    : eligiblePromotionSources(application, environment);
  const [selectedPromotionEnvironmentId, setSelectedPromotionEnvironmentId] = useState(() =>
    defaultPromotionCounterpartId(data, application, environment, initialPromotionMode)
  );
  const effectiveSelectedPromotionEnvironmentId = selectablePromotionEnvironments.some((item) => item.id === selectedPromotionEnvironmentId)
    ? selectedPromotionEnvironmentId
    : selectablePromotionEnvironments[0]?.id ?? "";
  const sourceEnvironmentId = effectivePromotionMode === "from-current" ? environment.id : effectiveSelectedPromotionEnvironmentId;
  const targetEnvironmentId = effectivePromotionMode === "into-current" ? environment.id : effectiveSelectedPromotionEnvironmentId;
  const [previewComparison, setPreviewComparison] = useState<DeploymentCockpit["comparisons"][number] | null>(null);
  const [promotedTargetRevisionId, setPromotedTargetRevisionId] = useState<string | null>(null);
  const [promotionNotice, setPromotionNotice] = useState("");
  const [assistantOutcome, setAssistantOutcome] = useState<"Proposed" | "Approved" | "Rejected">("Proposed");
  const [engineQuery, setEngineQuery] = useState("");
  const [engineSort, setEngineSort] = useState<EngineSort>("name");
  const visibleEngines = useMemo(
    () => sortEngines(filterEngines(environmentEngines, engineQuery), engineSort),
    [engineQuery, engineSort, environmentEngines]
  );
  const deploymentBlockers = useMemo(
    () => collectDeploymentBlockers(data, environment, environmentEngines),
    [data, environment, environmentEngines]
  );
  const artifactList = useQuery({
    queryKey: queryKeys.artifacts(workspaceId),
    queryFn: () => listWorkspaceArtifacts(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const hasValidArtifacts = Boolean(artifactList.data?.items.some(isValidArtifactForRevision));

  const comparison = useMemo(() => {
    const cockpitComparison = data.comparisons.find(
      (item) => item.sourceEnvironmentId === sourceEnvironmentId && item.targetEnvironmentId === targetEnvironmentId
    );
    return previewComparison && previewComparison.sourceEnvironmentId === sourceEnvironmentId && previewComparison.targetEnvironmentId === targetEnvironmentId
      ? previewComparison
      : cockpitComparison;
  }, [data.comparisons, previewComparison, sourceEnvironmentId, targetEnvironmentId]);
  const promotionReadinessIssues = useMemo(
    () => collectPromotionReadinessIssues(data, sourceEnvironmentId, targetEnvironmentId, canPreviewPromotion, hasValidArtifacts),
    [canPreviewPromotion, data, hasValidArtifacts, sourceEnvironmentId, targetEnvironmentId]
  );

  function getEnvironment(targetId: string) {
    return data.applications.flatMap((item) => item.environments).find((item) => item.id === targetId);
  }

  function getTargetEngine(targetId: string) {
    return data.engines.find((engine) => engine.environmentId === targetId);
  }

  function getLatestTargetRun(targetId: string, engineId: string) {
    return data.history.find((event) => event.environmentId === targetId && event.engineId === engineId);
  }

  const preview = useMutation({
    mutationFn: () => {
      assertPromotionReady(promotionReadinessIssues);
      const source = getEnvironment(sourceEnvironmentId);
      const targetEngine = getTargetEngine(targetEnvironmentId);
      if (!source?.desiredRevision.id || !targetEngine)
        throw new Error("Choose a source revision and target engine before refreshing preview.");

      return previewPromotion(workspaceId, {
        sourceEnvironmentId,
        targetEnvironmentId,
        sourceRevisionId: source.desiredRevision.id,
        targetEngineId: targetEngine.id
      });
    },
    onSuccess: (comparisonResult) => {
      setPreviewComparison(comparisonResult);
      setPromotionNotice("Promotion preview refreshed from live validation.");
    }
  });
  const promoteTargetRevision = useMutation({
    mutationFn: async () => {
      assertPromotionReady(promotionReadinessIssues);
      const source = getEnvironment(sourceEnvironmentId);
      const targetEngine = getTargetEngine(targetEnvironmentId);
      if (!source?.desiredRevision.id || !targetEngine)
        throw new Error("Choose a source revision and target engine before creating the target revision.");

      return promoteRevision(workspaceId, {
        sourceEnvironmentId,
        targetEnvironmentId,
        sourceRevisionId: source.desiredRevision.id,
        targetEngineId: targetEngine.id,
        label: `Promoted from ${source.name} r${source.desiredRevision.revision}`,
        commit: source.desiredRevision.commit || null
      });
    },
    onSuccess: async (promotion) => {
      setPreviewComparison(promotion.comparison);
      setPromotedTargetRevisionId(promotion.targetRevision.id);
      setPromotionNotice(`Target revision r${promotion.targetRevision.revisionNumber} created. Review the gate, then deploy when ready.`);
      await context.refreshDeploymentCockpit();
    }
  });
  const deployRevision = useMutation({
    mutationFn: async () => {
      const targetEngine = getTargetEngine(comparison?.targetEnvironmentId ?? targetEnvironmentId);
      if (!comparison || !targetEngine || !promotedTargetRevisionId)
        throw new Error("Create a target revision before deployment.");

      const { run } = await runDeployChain(workspaceId, {
        revisionId: promotedTargetRevisionId,
        targetEnvironmentId: comparison.targetEnvironmentId,
        targetEngineId: targetEngine.id,
        skipDeployabilityCheck: true
      });
      return run;
    },
    onSuccess: (run) => {
      setPromotionNotice(`Deployment run ${run.status.toLowerCase()} for revision ${run.sourceRevisionId}.`);
      void context.refreshDeploymentCockpit();
    }
  });
  // One-click "Promote & deploy": create the target revision, then deploy it behind a single action.
  // The confirmation token is minted internally as part of the deploy chain.
  const promoteAndDeploy = useMutation({
    mutationFn: async () => {
      assertPromotionReady(promotionReadinessIssues);
      const source = getEnvironment(sourceEnvironmentId);
      const targetEngine = getTargetEngine(targetEnvironmentId);
      if (!source?.desiredRevision.id || !targetEngine)
        throw new Error("Choose a source revision and target engine before promoting and deploying.");

      const promotion = await promoteRevision(workspaceId, {
        sourceEnvironmentId,
        targetEnvironmentId,
        sourceRevisionId: source.desiredRevision.id,
        targetEngineId: targetEngine.id,
        label: `Promoted from ${source.name} r${source.desiredRevision.revision}`,
        commit: source.desiredRevision.commit || null
      });
      setPreviewComparison(promotion.comparison);
      setPromotedTargetRevisionId(promotion.targetRevision.id);

      // The promotion preview already ran live validation, so the deploy chain skips the redundant
      // deployability check and goes straight to minting the confirmation and queueing the run.
      const { run } = await runDeployChain(workspaceId, {
        revisionId: promotion.targetRevision.id,
        targetEnvironmentId: promotion.comparison.targetEnvironmentId,
        targetEngineId: targetEngine.id,
        skipDeployabilityCheck: true
      });
      return { promotion, run };
    },
    onSuccess: async ({ promotion, run }) => {
      setPromotionNotice(
        `Target revision r${promotion.targetRevision.revisionNumber} promoted and deployment run ${run.status.toLowerCase()}.`
      );
      await context.refreshDeploymentCockpit();
    }
  });
  const rollbackRevision = useMutation({
    mutationFn: async () => {
      const targetEngine = getTargetEngine(comparison?.targetEnvironmentId ?? targetEnvironmentId);
      const rollbackSourceRun = getLatestTargetRun(comparison?.targetEnvironmentId ?? targetEnvironmentId, targetEngine?.id ?? "");
      const sourceRevisionId = comparison?.rollbackRevisionId;
      if (!comparison || !targetEngine || !rollbackSourceRun || !sourceRevisionId)
        throw new Error("A previous compatible run is required before rollback can be queued.");

      const confirmation = await createActionConfirmation(workspaceId, {
        actionType: "Rollback",
        targetId: sourceRevisionId,
        lifetimeSeconds: null
      });
      return queueRollbackRun(workspaceId, {
        sourceRevisionId,
        targetEnvironmentId: comparison.targetEnvironmentId,
        targetEngineId: targetEngine.id,
        confirmationId: confirmation.id,
        rollbackSourceRunId: rollbackSourceRun.id,
        mode: "Apply"
      });
    },
    onSuccess: (run) => {
      setPromotionNotice(`Rollback run ${run.status.toLowerCase()} for revision ${run.sourceRevisionId}.`);
      void context.refreshDeploymentCockpit();
    }
  });
  const targetAllowsRollback = hasTierCapability(getEnvironment(targetEnvironmentId), deploymentTierCapabilities.rollbackEnabled);

  useEffect(() => {
    const nextMode = normalizePromotionMode(environment, promotionMode);
    if (nextMode !== promotionMode) {
      setPromotionMode(nextMode);
      setSelectedPromotionEnvironmentId(defaultPromotionCounterpartId(data, application, environment, nextMode));
      resetPromotionState();
      return;
    }

    if (selectedPromotionEnvironmentId !== effectiveSelectedPromotionEnvironmentId) {
      setSelectedPromotionEnvironmentId(effectiveSelectedPromotionEnvironmentId);
      resetPromotionState();
    }
  }, [application, data, effectiveSelectedPromotionEnvironmentId, environment, promotionMode, selectedPromotionEnvironmentId]);

  function resetPromotionState() {
    setPreviewComparison(null);
    setPromotedTargetRevisionId(null);
    setPromotionNotice("");
    preview.reset();
    promoteTargetRevision.reset();
    promoteAndDeploy.reset();
    deployRevision.reset();
    rollbackRevision.reset();
  }

  function changePromotionMode(nextMode: PromotionMode) {
    const normalizedMode = normalizePromotionMode(environment, nextMode);
    setPromotionMode(normalizedMode);
    setSelectedPromotionEnvironmentId(defaultPromotionCounterpartId(data, application, environment, normalizedMode));
    resetPromotionState();
  }

  function changeSelectedPromotionEnvironment(nextId: string) {
    setSelectedPromotionEnvironmentId(nextId);
    resetPromotionState();
  }

  return (
    <section className="space-y-5">
      <Breadcrumbs
        items={[
          { label: "Deployments", to: "/admin/deployments" },
          { label: "Applications", to: "/admin/deployments/applications" },
          { label: application.name, to: applicationPath(application.id) },
          { label: environment.name }
        ]}
      />
      <PageHeader
        title={environment.name}
        description={`${application.name} deployment environment`}
        actions={
          <>
            <Link to={`${environmentPath(application.id, environment.id)}/edit`} className={buttonClassName("secondary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
              <Pencil className="h-4 w-4" />
              Edit environment
            </Link>
            <Link to={`${environmentPath(application.id, environment.id)}/revisions/new`} className={buttonClassName("secondary", !canManageDesiredState ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageDesiredState)}>
              <GitBranch className="h-4 w-4" />
              New revision
            </Link>
            <Link to={`${environmentPath(application.id, environment.id)}/engines/new`} className={buttonClassName("primary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
              <Plus className="h-4 w-4" />
              Connect engine
            </Link>
          </>
        }
      />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_360px]">
        <div className="space-y-4">
          <div className="grid gap-3 md:grid-cols-4">
            <MetricCard label="Health" value={environment.health} tone={healthTone(environment.health)} />
            <MetricCard label="Deployment" value={environment.deploymentStatus} tone={deploymentTone(environment.deploymentStatus)} />
            <MetricCard label="Drift" value={driftLabel(environment.driftStatus)} tone={driftTone(environment.driftStatus)} />
            <MetricCard label="Engines" value={String(environmentEngines.length)} />
          </div>

          {deploymentBlockers.length > 0 ? <DeploymentBlockersPanel blockers={deploymentBlockers} canManageDesiredState={canManageDesiredState} /> : null}

          <Panel title="Engine registrations" icon={<RadioTower className="h-4 w-4" />}>
            {environmentEngines.length === 0 ? (
              <EmptyState
                title="No engine registered"
                description="Register an engine before verifying runtime health or running controls."
                action={
                  <Link
                    to={`${environmentPath(application.id, environment.id)}/engines/new`}
                    className={buttonClassName("primary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)}
                    {...permissionLinkProps(canManageSetup)}
                  >
                    Connect engine
                  </Link>
                }
              />
            ) : (
              <div className="space-y-3">
                <div className="grid gap-3 lg:grid-cols-[minmax(16rem,26rem)_auto] lg:items-center">
                  <label className="relative block">
                    <Search className="pointer-events-none absolute left-3 top-2.5 h-4 w-4 text-muted-foreground" />
                    <Input value={engineQuery} onChange={(event) => setEngineQuery(event.target.value)} className="pl-9" placeholder="Search engines" />
                  </label>
                  <Select value={engineSort} onChange={(event) => setEngineSort(event.target.value as EngineSort)} aria-label="Sort engines">
                    {engineSorts.map((item) => (
                      <option key={item.value} value={item.value}>{item.label}</option>
                    ))}
                  </Select>
                </div>
                {visibleEngines.length === 0 ? (
                  <EmptyState title="No matching engines" description="Clear the search to see all engine registrations." />
                ) : (
                  <EngineTable applicationId={application.id} environmentId={environment.id} engines={visibleEngines} />
                )}
              </div>
            )}
          </Panel>

          <Panel title="Promotion" icon={<GitBranch className="h-4 w-4" />}>
            <PromotionPreviewPanel
              data={data}
              promotionMode={effectivePromotionMode}
              allowedPromotionModes={allowedPromotionModes}
              selectableEnvironments={selectablePromotionEnvironments}
              selectedEnvironmentId={effectiveSelectedPromotionEnvironmentId}
              sourceEnvironmentId={sourceEnvironmentId}
              targetEnvironmentId={targetEnvironmentId}
              comparison={comparison}
              readinessIssues={promotionReadinessIssues}
              canManageDesiredState={canManageDesiredState}
              canPreview={canPreviewPromotion}
              canDeploy={canExecuteDeployment}
              hasPromotedTargetRevision={Boolean(promotedTargetRevisionId)}
              canRollback={canExecuteRollback && targetAllowsRollback}
              rollbackBlockedReason={targetAllowsRollback ? undefined : "Rollback is not enabled for the target tier."}
              isPreviewing={preview.isPending}
              isQueueingDeployment={deployRevision.isPending}
              isQueueingRollback={rollbackRevision.isPending}
              isPromoting={promoteTargetRevision.isPending}
              isPromotingAndDeploying={promoteAndDeploy.isPending}
              notice={promotionNotice}
              error={
                preview.error instanceof Error
                  ? preview.error.message
                  : promoteTargetRevision.error instanceof Error
                    ? promoteTargetRevision.error.message
                  : promoteAndDeploy.error instanceof Error
                    ? promoteAndDeploy.error.message
                  : deployRevision.error instanceof Error
                    ? deployRevision.error.message
                    : rollbackRevision.error instanceof Error
                      ? rollbackRevision.error.message
                      : undefined
              }
              onPromotionModeChange={changePromotionMode}
              onSelectedEnvironmentChange={changeSelectedPromotionEnvironment}
              onRefreshPreview={() => {
                setPromotedTargetRevisionId(null);
                preview.mutate();
              }}
              onPromote={() => promoteTargetRevision.mutate()}
              onPromoteAndDeploy={() => promoteAndDeploy.mutate()}
              onDeploy={() => deployRevision.mutate()}
              onRollback={() => rollbackRevision.mutate()}
            />
          </Panel>

          <EnvironmentOperations data={data} environment={environment} />
          <AssistantPlanView
            data={data}
            targetEnvironmentId={environment.id}
            outcome={assistantOutcome}
            onApprove={() => setAssistantOutcome("Approved")}
            onReject={() => setAssistantOutcome("Rejected")}
          />
        </div>

        <aside className="space-y-4">
          <Panel title="Environment" icon={<ShieldCheck className="h-4 w-4" />}>
            <dl className="space-y-3">
              <Detail label="Tier" value={`${environment.tierName || environment.tier}${environment.tierStatus === "Archived" ? " (archived)" : ""}`} />
              <Detail
                label="Desired revision"
                value={
                  <Link to={revisionDetailPath(application.id, environment.desiredRevision.id)} className="text-primary hover:underline">
                    r{environment.desiredRevision.revision} · {environment.desiredRevision.commit}
                  </Link>
                }
              />
              <Detail
                label="Deployed revision"
                value={environment.deployedRevision ? (
                  <Link to={applicationRevisionsPath(application.id, `environment=${encodeURIComponent(environment.id)}&status=deployed`)} className="text-primary hover:underline">
                    r{environment.deployedRevision}
                  </Link>
                ) : "Not deployed"}
              />
              <Detail label="Desired label" value={environment.desiredRevision.label} />
              <Detail label="Authored" value={formatDateTime(environment.desiredRevision.authoredAt)} />
            </dl>
            <Link to={applicationRevisionsPath(application.id, `environment=${encodeURIComponent(environment.id)}`)} className="mt-4 inline-flex text-xs font-medium text-primary hover:underline">
              View all revisions
            </Link>
          </Panel>
          <Panel title="Tier capabilities" icon={<ClipboardCheck className="h-4 w-4" />}>
            <div className="flex flex-wrap gap-2">
              {(environment.tierCapabilities ?? []).length > 0 ? (
                environment.tierCapabilities?.map((capability) => <Badge key={capability}>{capability}</Badge>)
              ) : (
                <p className="text-sm text-muted-foreground">No tier capabilities are reported for this environment.</p>
              )}
            </div>
          </Panel>
        </aside>
      </div>
    </section>
  );
}

export function DeploymentEnvironmentEditPage() {
  const context = useDeploymentContext();
  const { applicationId = "", environmentId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentEnvironmentEditReady context={context.value} applicationId={applicationId} environmentId={environmentId} />;
}

function DeploymentEnvironmentEditReady({
  context,
  applicationId,
  environmentId
}: {
  context: DeploymentContext;
  applicationId: string;
  environmentId: string;
}) {
  const navigate = useNavigate();
  const { workspaceId, data, tiers } = context;
  const resolved = resolveEnvironment(data, applicationId, environmentId);
  if (!resolved.application) return <RequestStateView state="not-found" title="Application not found" />;
  if (!resolved.environment) return <RequestStateView state="not-found" title="Environment not found" />;
  const { application, environment } = resolved;

  const updateEnvironmentMutation = useMutation({
    mutationFn: (values: EnvironmentFormValues) =>
      updateDeploymentEnvironment(workspaceId, application.id, environment.id, {
        name: values.name,
        tier: values.tier,
        tierId: values.tierId
    }),
    onSuccess: async () => {
      await context.refreshDeploymentCockpit();
      navigate(environmentPath(application.id, environment.id));
    }
  });

  return (
    <FormPageShell
      title="Edit environment"
      description="Update environment metadata and tier assignment."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: application.name, to: applicationPath(application.id) },
        { label: environment.name, to: environmentPath(application.id, environment.id) },
        { label: "Edit" }
      ]}
    >
      <EnvironmentForm
        environment={environment}
        tiers={tiers}
        isSubmitting={updateEnvironmentMutation.isPending}
        error={updateEnvironmentMutation.error instanceof Error ? updateEnvironmentMutation.error.message : undefined}
        onCancel={() => navigate(environmentPath(application.id, environment.id))}
        onSubmit={(values) => updateEnvironmentMutation.mutate(values)}
      />
    </FormPageShell>
  );
}

export function DeploymentRevisionCreatePage() {
  const context = useDeploymentContext();
  const { applicationId = "", environmentId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentRevisionCreateReady context={context.value} applicationId={applicationId} environmentId={environmentId} />;
}

function DeploymentRevisionCreateReady({
  context,
  applicationId,
  environmentId
}: {
  context: DeploymentContext;
  applicationId: string;
  environmentId: string;
}) {
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const { workspaceId, data, canManageDesiredState } = context;
  const resolved = resolveEnvironment(data, applicationId, environmentId);
  const [label, setLabel] = useState("");
  const [commit, setCommit] = useState("");
  const [artifactRecordId, setArtifactRecordId] = useState("");
  const [observabilityKind, setObservabilityKind] = useState<ObservabilityBinding["kind"]>("Traces");
  const [observabilityProvider, setObservabilityProvider] = useState("OpenTelemetry Collector");
  const [observabilityScope, setObservabilityScope] = useState(() => `${resolved.environment?.name ?? "Environment"} / workflow runtime`);
  const [observabilitySample, setObservabilitySample] = useState("Runtime telemetry is expected for promotion and deployment review.");
  const artifacts = useQuery({
    queryKey: queryKeys.artifacts(workspaceId),
    queryFn: () => listWorkspaceArtifacts(workspaceId)
  });
  const desiredStateRequirements = useQuery({
    queryKey: queryKeys.deploymentDesiredStateRequirements(workspaceId, environmentId),
    queryFn: () => getEnvironmentDesiredStateRequirements(workspaceId, environmentId),
    enabled: Boolean(resolved.environment)
  });
  const artifactItems = (artifacts.data?.items ?? []).filter((artifact) => artifact.status !== "Archived");
  const selectedArtifact = artifactItems.find((artifact) => artifact.id === artifactRecordId) ?? artifactItems[0];
  const contextualObservabilityRequested = searchParams.get("includeRequirement") === observabilityRequirementId || searchParams.get("includeObservability") === "1";
  const tierObservabilityRequirement = desiredStateRequirements.data?.requirements.find((requirement) => requirement.id === observabilityRequirementId);
  const showObservability = Boolean(tierObservabilityRequirement) || contextualObservabilityRequested;
  const observabilityRequired = Boolean(tierObservabilityRequirement?.required) || contextualObservabilityRequested;
  const observabilityReason = tierObservabilityRequirement
    ? `Required by ${desiredStateRequirements.data?.tierName ?? resolved.environment?.tierName ?? resolved.environment?.tier} tier.`
    : "Included from a validation action for a target environment.";
  const isLoadingRequirements = desiredStateRequirements.isLoading || desiredStateRequirements.isFetching;

  const createRevision = useMutation({
    mutationFn: async () => {
      if (!resolved.application || !resolved.environment)
        throw new Error("Environment not found.");
      if (!selectedArtifact)
        throw new Error("Choose an artifact before creating a revision.");
      if (showObservability && observabilityRequired && (!observabilityProvider.trim() || !observabilityScope.trim()))
        throw new Error("Observability provider and scope are required.");

      const records = [artifactRevisionRecord(selectedArtifact)];
      if (showObservability) {
        records.push(observabilityRevisionRecord({
          kind: observabilityKind,
          provider: observabilityProvider,
          scope: observabilityScope,
          sample: observabilitySample
        }));
      }

      return createDesiredStateRevision(workspaceId, resolved.application.id, resolved.environment.id, {
        label: label.trim() || artifactDisplayName(selectedArtifact),
        commit: commit.trim() || null,
        records
      });
    },
    onSuccess: async (createdRevision) => {
      if (!resolved.application || !resolved.environment) return;
      await context.refreshDeploymentCockpit();
      navigate(revisionDetailPath(resolved.application.id, createdRevision.id));
    }
  });

  if (!resolved.application) return <RequestStateView state="not-found" title="Application not found" />;
  if (!resolved.environment) return <RequestStateView state="not-found" title="Environment not found" />;
  const { application, environment } = resolved;
  const isLoadingArtifacts = artifacts.isLoading || artifacts.isFetching;
  const submitDisabled = !canManageDesiredState || isLoadingArtifacts || isLoadingRequirements || desiredStateRequirements.error instanceof Error || artifactItems.length === 0 || createRevision.isPending;
  const requirementTierName = desiredStateRequirements.data?.tierName ?? environment.tierName ?? environment.tier;
  const hasCurrentTierRequirements = (desiredStateRequirements.data?.requirements.length ?? 0) > 0;
  const revisionFlowActions = [
    "The revision becomes the latest desired state for this environment.",
    hasCurrentTierRequirements
      ? "Complete required desired-state records before creating the revision."
      : "No additional desired-state records are required by this environment tier.",
    "Use Promotion to copy it into a higher environment.",
    "Use Deploy Target Revision after promotion validation passes."
  ];

  return (
    <FormPageShell
      title="New revision"
      description="Create a desired-state revision from a registered deployment artifact."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: application.name, to: applicationPath(application.id) },
        { label: environment.name, to: environmentPath(application.id, environment.id) },
        { label: "New revision" }
      ]}
    >
      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_320px]">
        <form
          className="space-y-4 rounded-ui border border-border bg-surface p-4"
          onSubmit={(event) => {
            event.preventDefault();
            createRevision.mutate();
          }}
        >
          {!canManageDesiredState ? (
            <p className="rounded-ui border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
              You need desired-state management permission to create revisions.
            </p>
          ) : null}
          {artifacts.error instanceof Error ? <p role="alert" className="text-sm text-destructive">{artifacts.error.message}</p> : null}
          {desiredStateRequirements.error instanceof Error ? <p role="alert" className="text-sm text-destructive">{desiredStateRequirements.error.message}</p> : null}
          {createRevision.error instanceof Error ? <p role="alert" className="text-sm text-destructive">{createRevision.error.message}</p> : null}

          {isLoadingArtifacts || isLoadingRequirements ? (
            <RequestStateView state="loading" title="Loading revision context" description="Fetching artifacts and desired-state requirements." />
          ) : artifactItems.length === 0 ? (
            <EmptyState
              title="No artifacts registered"
              description="Register a deployment artifact before creating a desired-state revision."
              action={<Link to="/admin/artifacts" className={buttonClassName("secondary")}>Open artifacts</Link>}
            />
          ) : (
            <>
              <label className="block text-sm font-medium">
                Artifact
                <Select
                  className="mt-1 w-full"
                  value={selectedArtifact?.id ?? ""}
                  onChange={(event) => setArtifactRecordId(event.target.value)}
                >
                  {artifactItems.map((artifact) => (
                    <option key={artifact.id} value={artifact.id}>{artifactDisplayName(artifact)}</option>
                  ))}
                </Select>
              </label>
              <div className="grid gap-3 md:grid-cols-2">
                <label className="block text-sm font-medium">
                  Revision label
                  <Input value={label} onChange={(event) => setLabel(event.target.value)} placeholder={selectedArtifact ? artifactDisplayName(selectedArtifact) : "Revision label"} className="mt-1" />
                </label>
                <label className="block text-sm font-medium">
                  Commit
                  <Input value={commit} onChange={(event) => setCommit(event.target.value)} placeholder="Optional source commit" className="mt-1" />
                </label>
              </div>
              <div className="rounded-ui border border-border bg-muted/20 p-3">
                <div className="flex items-start gap-2 text-sm">
                  <ClipboardCheck className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
                  <div>
                    <div className="font-medium">Desired-state requirements</div>
                    {hasCurrentTierRequirements || contextualObservabilityRequested ? (
                      <p className="mt-1 text-xs leading-5 text-muted-foreground">
                        Complete records required by {requirementTierName} or requested by validation.
                      </p>
                    ) : (
                      <p className="mt-1 text-xs leading-5 text-muted-foreground">
                        No additional desired-state records are required for {requirementTierName}.
                      </p>
                    )}
                  </div>
                </div>
                {showObservability ? (
                  <div className="mt-3 rounded-ui border border-border bg-background p-3">
                    <div className="flex flex-wrap items-center gap-2 text-sm">
                      <span className="font-medium">Observability binding</span>
                      <StatusBadge value={observabilityRequired ? "Required" : "Optional"} tone={observabilityRequired ? "warning" : "neutral"} />
                    </div>
                    <p className="mt-1 text-xs leading-5 text-muted-foreground">{observabilityReason}</p>
                    <div className="mt-3 grid gap-3 md:grid-cols-2">
                      <label className="block text-sm font-medium">
                        Signal
                        <Select
                          className="mt-1 w-full"
                          value={observabilityKind}
                          onChange={(event) => setObservabilityKind(event.target.value as ObservabilityBinding["kind"])}
                        >
                          <option value="Traces">Traces</option>
                          <option value="Logs">Logs</option>
                          <option value="Metrics">Metrics</option>
                          <option value="Console">Console</option>
                        </Select>
                      </label>
                      <label className="block text-sm font-medium">
                        Provider
                        <Input
                          value={observabilityProvider}
                          onChange={(event) => setObservabilityProvider(event.target.value)}
                          placeholder="OpenTelemetry Collector"
                          className="mt-1"
                        />
                      </label>
                      <label className="block text-sm font-medium">
                        Scope
                        <Input
                          value={observabilityScope}
                          onChange={(event) => setObservabilityScope(event.target.value)}
                          placeholder={`${environment.name} / workflow runtime`}
                          className="mt-1"
                        />
                      </label>
                      <label className="block text-sm font-medium">
                        Sample or note
                        <Input
                          value={observabilitySample}
                          onChange={(event) => setObservabilitySample(event.target.value)}
                          placeholder="Runtime telemetry endpoint configured."
                          className="mt-1"
                        />
                      </label>
                    </div>
                  </div>
                ) : null}
              </div>
              <div className="flex flex-wrap justify-end gap-2">
                <Link to={environmentPath(application.id, environment.id)} className={buttonClassName("secondary")}>Cancel</Link>
                <Button type="submit" disabled={submitDisabled}>
                  <Save className="h-4 w-4" />
                  {createRevision.isPending ? "Creating revision" : "Create revision"}
                </Button>
              </div>
            </>
          )}
        </form>
        <aside className="space-y-3">
          <Panel title="Revision flow" icon={<GitBranch className="h-4 w-4" />}>
            <ActionList
              title="After creation"
              icon={<ClipboardCheck className="h-4 w-4" />}
              actions={revisionFlowActions}
            />
          </Panel>
          {selectedArtifact ? (
            <Panel title="Selected artifact" icon={<Rocket className="h-4 w-4" />}>
              <dl className="grid gap-3">
                <Detail label="Artifact" value={artifactDisplayName(selectedArtifact)} />
                <Detail label="Type" value={selectedArtifact.artifactTypeId ?? "Unknown"} />
                <Detail label="Digest" value={`${selectedArtifact.contentDigest.algorithm}:${selectedArtifact.contentDigest.value}`} />
                <Detail
                  label="Reference"
                  value={
                    <a
                      className="text-primary underline-offset-2 hover:underline"
                      href={workspaceArtifactDownloadUrl(workspaceId, selectedArtifact.id)}
                      download
                    >
                      {selectedArtifact.reference}
                    </a>
                  }
                />
              </dl>
            </Panel>
          ) : null}
        </aside>
      </div>
    </FormPageShell>
  );
}

export function DeploymentEngineRegisterPage() {
  const context = useDeploymentContext();
  const { applicationId = "", environmentId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentEngineRegisterReady context={context.value} applicationId={applicationId} environmentId={environmentId} />;
}

function DeploymentEngineRegisterReady({
  context,
  applicationId,
  environmentId
}: {
  context: DeploymentContext;
  applicationId: string;
  environmentId: string;
}) {
  const navigate = useNavigate();
  const { workspaceId, data, secretStores, credentialReferences } = context;
  const resolved = resolveEnvironment(data, applicationId, environmentId);
  if (!resolved.application) return <RequestStateView state="not-found" title="Application not found" />;
  if (!resolved.environment) return <RequestStateView state="not-found" title="Environment not found" />;
  const { application, environment } = resolved;

  const registerEngine = useMutation({
    mutationFn: (values: EngineRegistrationValues) => registerDeploymentEngine(workspaceId, environment.id, engineRegistrationRequest(values)),
    onSuccess: async (engine) => {
      await context.refreshDeploymentCockpit();
      navigate(enginePath(application.id, environment.id, engine.id));
    }
  });
  const credentialManagement = useCredentialManagementMutations(workspaceId);

  return (
    <FormPageShell
      title="Register engine"
      description="Add another Elsa workflow engine endpoint to this environment."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: application.name, to: applicationPath(application.id) },
        { label: environment.name, to: environmentPath(application.id, environment.id) },
        { label: "Register engine" }
      ]}
    >
      <div className="rounded-ui border border-border bg-surface p-4">
        <EngineRegistrationPanel
          environment={environment}
          secretStores={secretStores}
          credentialReferences={credentialReferences}
          isSubmitting={registerEngine.isPending || credentialManagement.createCredentialReference.isPending}
          error={registerEngine.error instanceof Error ? registerEngine.error.message : credentialManagement.error}
          onCancel={() => navigate(environmentPath(application.id, environment.id))}
          onCreateReference={(values) => credentialManagement.createCredentialReference.mutateAsync(values)}
          onSubmit={(values) => registerEngine.mutate(values)}
        />
      </div>
    </FormPageShell>
  );
}

export function DeploymentEnginePage() {
  const context = useDeploymentContext();
  const { applicationId = "", environmentId = "", engineId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentEngineReady context={context.value} applicationId={applicationId} environmentId={environmentId} engineId={engineId} />;
}

function DeploymentEngineReady({
  context,
  applicationId,
  environmentId,
  engineId
}: {
  context: DeploymentContext;
  applicationId: string;
  environmentId: string;
  engineId: string;
}) {
  const resolved = resolveEnvironment(context.data, applicationId, environmentId);
  if (!resolved.application) return <RequestStateView state="not-found" title="Application not found" />;
  if (!resolved.environment) return <RequestStateView state="not-found" title="Environment not found" />;
  const engine = context.data.engines.find((item) => item.id === engineId && item.environmentId === resolved.environment?.id);
  if (!engine) return <RequestStateView state="not-found" title="Engine not found" />;

  return <DeploymentEngineDetailReady context={context} application={resolved.application} environment={resolved.environment} engine={engine} />;
}

function DeploymentEngineDetailReady({
  context,
  application,
  environment,
  engine
}: {
  context: DeploymentContext;
  application: DeploymentCockpit["applications"][number];
  environment: EnvironmentSummary;
  engine: WorkflowEngineRegistration;
}) {
  const [operationNotice, setOperationNotice] = useState("");
  const verifyEngine = useMutation({
    mutationFn: () => verifyDeploymentEngine(context.workspaceId, engine.id),
    onSuccess: (result) => {
      setOperationNotice(result.message);
      void context.refreshDeploymentCockpit();
    }
  });
  const runControl = useMutation({
    mutationFn: async (control: RuntimeControl) => {
      const confirmation = await createActionConfirmation(context.workspaceId, {
        actionType: "RuntimeControl",
        targetId: `${engine.id}:${control.id}`,
        lifetimeSeconds: null
      });
      return runRuntimeControl(context.workspaceId, engine.id, control.id, { confirmationId: confirmation.id });
    },
    onSuccess: (execution) => {
      setOperationNotice(execution.message);
      void context.refreshDeploymentCockpit();
    }
  });

  return (
    <section className="space-y-5">
      <Breadcrumbs
        items={[
          { label: "Deployments", to: "/admin/deployments" },
          { label: "Applications", to: "/admin/deployments/applications" },
          { label: application.name, to: applicationPath(application.id) },
          { label: environment.name, to: environmentPath(application.id, environment.id) },
          { label: engine.name }
        ]}
      />
      <PageHeader title={engine.name} description={`${environment.name} workflow engine registration`} />
      <EngineDetailSection
        application={application}
        environment={environment}
        engine={engine}
        operationNotice={operationNotice}
        canManageSetup={context.canManageSetup}
        canExecuteControls={context.canExecuteControls}
        isVerifying={verifyEngine.isPending}
        verifyError={verifyEngine.error instanceof Error ? verifyEngine.error.message : undefined}
        isRunningControl={runControl.isPending}
        controlError={runControl.error instanceof Error ? runControl.error.message : undefined}
        onVerify={() => verifyEngine.mutate()}
        onRunControl={(control) => runControl.mutate(control)}
      />
    </section>
  );
}

export function DeploymentEngineEditPage() {
  const context = useDeploymentContext();
  const { applicationId = "", environmentId = "", engineId = "" } = useParams();
  if (context.status !== "ready") return context.state;
  return <DeploymentEngineEditReady context={context.value} applicationId={applicationId} environmentId={environmentId} engineId={engineId} />;
}

function DeploymentEngineEditReady({
  context,
  applicationId,
  environmentId,
  engineId
}: {
  context: DeploymentContext;
  applicationId: string;
  environmentId: string;
  engineId: string;
}) {
  const navigate = useNavigate();
  const { workspaceId, data, secretStores, credentialReferences } = context;
  const resolved = resolveEnvironment(data, applicationId, environmentId);
  if (!resolved.application) return <RequestStateView state="not-found" title="Application not found" />;
  if (!resolved.environment) return <RequestStateView state="not-found" title="Environment not found" />;
  const { application, environment } = resolved;
  const engine = data.engines.find((item) => item.id === engineId && item.environmentId === environment.id);
  if (!engine) return <RequestStateView state="not-found" title="Engine not found" />;

  const updateEngine = useMutation({
    mutationFn: (values: EngineEditValues) =>
      updateDeploymentEngine(workspaceId, engine.id, {
        name: values.name,
        baseUrl: values.baseUrl,
        region: values.region || null,
        credentialProvider: values.credentialProvider,
        credentialReference: values.credentialReference,
        credentialReferenceId: values.credentialReferenceId,
        credentialAssignmentStatus: values.credentialAssignmentStatus,
        capabilities: engine.capabilities,
        controls: engine.controls,
        hostingProvider: engine.hostingProvider
    }),
    onSuccess: async () => {
      await context.refreshDeploymentCockpit();
      navigate(enginePath(application.id, environment.id, engine.id));
    }
  });
  const credentialManagement = useCredentialManagementMutations(workspaceId);

  return (
    <FormPageShell
      title="Edit engine"
      description="Update endpoint and credential-reference metadata for this engine registration."
      breadcrumbs={[
        { label: "Deployments", to: "/admin/deployments" },
        { label: "Applications", to: "/admin/deployments/applications" },
        { label: application.name, to: applicationPath(application.id) },
        { label: environment.name, to: environmentPath(application.id, environment.id) },
        { label: engine.name, to: enginePath(application.id, environment.id, engine.id) },
        { label: "Edit" }
      ]}
    >
      <EngineEditPanel
        engine={engine}
        secretStores={secretStores}
        credentialReferences={credentialReferences}
        isSubmitting={updateEngine.isPending || credentialManagement.createCredentialReference.isPending}
        error={updateEngine.error instanceof Error ? updateEngine.error.message : credentialManagement.error}
        onCancel={() => navigate(enginePath(application.id, environment.id, engine.id))}
        onCreateReference={(values) => credentialManagement.createCredentialReference.mutateAsync(values)}
        onSubmit={(values) => updateEngine.mutate(values)}
      />
    </FormPageShell>
  );
}

function useCredentialManagementMutations(workspaceId: string) {
  const queryClient = useQueryClient();
  const refreshSecretMetadata = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: queryKeys.deploymentSecretStores(workspaceId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCredentialReferences(workspaceId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCockpit(workspaceId) })
    ]);
  };
  const createSecretStore = useMutation({
    mutationFn: (values: CredentialStoreValues) => createDeploymentSecretStore(workspaceId, values),
    onSuccess: refreshSecretMetadata
  });
  const updateSecretStore = useMutation({
    mutationFn: ({ secretStoreId, values }: { secretStoreId: string; values: CredentialStoreValues }) => updateDeploymentSecretStore(workspaceId, secretStoreId, values),
    onSuccess: refreshSecretMetadata
  });
  const archiveSecretStore = useMutation({
    mutationFn: (secretStoreId: string) => archiveDeploymentSecretStore(workspaceId, secretStoreId),
    onSuccess: refreshSecretMetadata
  });
  const createCredentialReference = useMutation({
    mutationFn: (values: CredentialReferenceValues) =>
      createDeploymentCredentialReference(workspaceId, values.secretStoreId, {
        name: values.name,
        reference: values.reference,
        description: values.description,
        secretValue: values.secretValue ?? null
      }),
    onSuccess: refreshSecretMetadata
  });
  const updateCredentialReference = useMutation({
    mutationFn: ({ credentialReferenceId, values }: { credentialReferenceId: string; values: CredentialReferenceValues }) =>
      updateDeploymentCredentialReference(workspaceId, credentialReferenceId, {
        name: values.name,
        reference: values.reference,
        description: values.description,
        secretValue: values.secretValue ?? null
      }),
    onSuccess: refreshSecretMetadata
  });
  const rotateCredentialReference = useMutation({
    mutationFn: ({ credentialReferenceId, secretValue }: { credentialReferenceId: string; secretValue: string }) =>
      rotateDeploymentCredentialReference(workspaceId, credentialReferenceId, secretValue),
    onSuccess: refreshSecretMetadata
  });
  const archiveCredentialReference = useMutation({
    mutationFn: (credentialReferenceId: string) => archiveDeploymentCredentialReference(workspaceId, credentialReferenceId),
    onSuccess: refreshSecretMetadata
  });
  const mutations = [
    createSecretStore,
    updateSecretStore,
    archiveSecretStore,
    createCredentialReference,
    updateCredentialReference,
    rotateCredentialReference,
    archiveCredentialReference
  ];
  const error = mutations.find((mutation) => mutation.error instanceof Error)?.error;
  const pendingActionId =
    (archiveSecretStore.isPending ? archiveSecretStore.variables : null) ??
    (archiveCredentialReference.isPending ? archiveCredentialReference.variables : null) ??
    (updateSecretStore.isPending ? updateSecretStore.variables?.secretStoreId : null) ??
    (updateCredentialReference.isPending ? updateCredentialReference.variables?.credentialReferenceId : null) ??
    (rotateCredentialReference.isPending ? rotateCredentialReference.variables?.credentialReferenceId : null) ??
    null;

  return {
    createSecretStore,
    updateSecretStore,
    archiveSecretStore,
    createCredentialReference,
    updateCredentialReference,
    rotateCredentialReference,
    archiveCredentialReference,
    pendingActionId,
    error: error instanceof Error ? error.message : undefined
  };
}

type DeploymentContextResult = { status: "ready"; value: DeploymentContext } | { status: "state"; state: ReactNode };

function useDeploymentContext(): DeploymentContextResult {
  const queryClient = useQueryClient();
  const workspaceContext = useWorkspaceContext();
  const workspaceId = workspaceContext.selectedWorkspaceId;
  const permissions = useQuery({
    queryKey: queryKeys.deploymentPermissions(workspaceId),
    queryFn: () => getDeploymentPermissions(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const cockpit = useQuery({
    queryKey: queryKeys.deploymentCockpit(workspaceId),
    queryFn: () => getDeploymentCockpit(workspaceId),
    enabled: Boolean(workspaceId),
    refetchInterval: 5_000
  });
  const tiers = useQuery({
    queryKey: queryKeys.deploymentTiers(workspaceId),
    queryFn: () => getDeploymentTiers(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const secretStores = useQuery({
    queryKey: queryKeys.deploymentSecretStores(workspaceId),
    queryFn: () => getDeploymentSecretStores(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const credentialReferences = useQuery({
    queryKey: queryKeys.deploymentCredentialReferences(workspaceId),
    queryFn: () => getDeploymentCredentialReferences(workspaceId),
    enabled: Boolean(workspaceId)
  });

  if (workspaceContext.isLoading || cockpit.isLoading) return { status: "state", state: <RequestStateView state="loading" title="Loading deployments" /> };
  if (workspaceContext.isError) return { status: "state", state: <RequestStateView state="unexpected" title="Workspace context could not load" /> };
  if (!workspaceId) {
    return { status: "state", state: <RequestStateView state="empty" title="No workspace selected" description="Select an organization workspace to view deployments." /> };
  }
  if (cockpit.isError || !cockpit.data) return { status: "state", state: <RequestStateView state="unexpected" title="Deployments could not load" /> };

  const deploymentTiers = tiers.data?.tiers ?? [];
  return {
    status: "ready",
    value: {
      workspaceId,
      data: cockpit.data,
      tiers: deploymentTiers,
      activeTiers: deploymentTiers.filter((tier) => tier.status === "Active"),
      secretStores: secretStores.data?.items ?? [],
      credentialReferences: credentialReferences.data?.items ?? [],
      canManageSetup: Boolean(permissions.data?.permissions.includes("deployments.setup.manage")),
      canManageDesiredState: Boolean(permissions.data?.permissions.includes("deployments.desired-state.manage")),
      canPreviewPromotion: Boolean(permissions.data?.permissions.includes("deployments.promotion.preview")),
      canExecuteDeployment: Boolean(permissions.data?.permissions.includes("deployments.run.execute")),
      canExecuteRollback: Boolean(permissions.data?.permissions.includes("deployments.rollback.execute")),
      canExecuteControls: Boolean(permissions.data?.permissions.includes("deployments.controls.execute")),
      refreshDeploymentCockpit: () => queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCockpit(workspaceId) })
    }
  };
}

function ApplicationCards({ applications, data }: { applications: DeploymentCockpit["applications"]; data: DeploymentCockpit }) {
  return <section className="console-application-grid" aria-label="Workflow applications">
    {applications.map((application) => {
      const engines = enginesForApplication(data, application);
      const driftCount = application.environments.filter(environment => environment.driftStatus === "DriftDetected").length;
      return <Link key={application.id} to={applicationPath(application.id)} aria-label={application.name} aria-describedby={`application-health-${application.id} application-summary-${application.id} application-action-${application.id}`} className="console-application-card">
        <div id={`application-health-${application.id}`} className="console-application-card-top"><Boxes aria-hidden size={24} strokeWidth={1.5} /><StatusBadge value={summarizeApplicationHealth(application, data.engines)} tone={applicationHealthTone(application, data.engines)} /></div>
        <h2>{application.name}</h2>
        <p id={`application-summary-${application.id}`}>{application.environments.length} environment{application.environments.length === 1 ? "" : "s"} · {engines.length} engine{engines.length === 1 ? "" : "s"}</p>
        <div id={`application-action-${application.id}`} className="console-application-card-footer"><span>{driftCount ? `${driftCount} with drift` : "Open application"}</span><ArrowUpRight aria-hidden size={16} /></div>
      </Link>;
    })}
  </section>;
}

function EnvironmentTable({
  application,
  environments,
  data
}: {
  application: DeploymentCockpit["applications"][number];
  environments: EnvironmentSummary[];
  data: DeploymentCockpit;
}) {
  return (
    <Table>
      <table className="min-w-full divide-y divide-border text-sm">
        <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-3 py-2">Environment</th>
            <th className="px-3 py-2">Tier</th>
            <th className="px-3 py-2">Health</th>
            <th className="px-3 py-2">Deployment</th>
            <th className="px-3 py-2">Desired</th>
            <th className="px-3 py-2">Deployed</th>
            <th className="px-3 py-2">Engines</th>
            <th className="px-3 py-2">Drift</th>
            <th className="px-3 py-2">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {environments.map((environment) => {
            const engines = enginesForEnvironment(data, environment.id);
            return (
              <tr key={environment.id}>
                <td className="px-3 py-3 font-medium"><Link to={environmentPath(application.id, environment.id)}>{environment.name}</Link></td>
                <td className="px-3 py-3 text-muted-foreground">
                  {environment.tierName || environment.tier}
                  {environment.tierStatus === "Archived" ? " (archived)" : ""}
                </td>
                <td className="px-3 py-3"><StatusBadge value={environment.health} tone={healthTone(environment.health)} /></td>
                <td className="px-3 py-3"><StatusBadge value={environment.deploymentStatus} tone={deploymentTone(environment.deploymentStatus)} /></td>
                <td className="px-3 py-3">
                  <Link to={revisionDetailPath(application.id, environment.desiredRevision.id)} className="text-primary hover:underline">r{environment.desiredRevision.revision}</Link>
                </td>
                <td className="px-3 py-3">
                  {environment.deployedRevision ? (
                    <Link to={applicationRevisionsPath(application.id, `environment=${encodeURIComponent(environment.id)}&status=deployed`)} className="text-primary hover:underline">
                      r{environment.deployedRevision}
                    </Link>
                  ) : "-"}
                </td>
                <td className="px-3 py-3">{engines.length}</td>
                <td className="px-3 py-3">{driftLabel(environment.driftStatus)}</td>
                <td className="px-3 py-3"><Link to={environmentPath(application.id, environment.id)} className="text-xs font-medium text-primary hover:underline">Open</Link></td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </Table>
  );
}

function RevisionTable({
  application,
  revisions
}: {
  application: DeploymentCockpit["applications"][number];
  revisions: WorkspaceDesiredStateRevisionSummary[];
}) {
  return (
    <Table>
      <table className="min-w-full divide-y divide-border text-sm">
        <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-3 py-2">Revision</th>
            <th className="px-3 py-2">Environment</th>
            <th className="px-3 py-2">Status</th>
            <th className="px-3 py-2">Label</th>
            <th className="px-3 py-2">Commit</th>
            <th className="px-3 py-2">Authored</th>
            <th className="px-3 py-2">Latest run</th>
            <th className="px-3 py-2">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {revisions.map((revision) => (
            <tr key={revision.revision.id}>
              <td className="px-3 py-3 font-medium">
                <Link to={revisionDetailPath(application.id, revision.revision.id)}>r{revision.revision.revisionNumber}</Link>
              </td>
              <td className="px-3 py-3">
                <Link to={environmentPath(application.id, revision.revision.environmentId)} className="text-primary hover:underline">{revision.environmentName}</Link>
              </td>
              <td className="px-3 py-3"><RevisionStateBadge revision={revision} /></td>
              <td className="px-3 py-3 text-muted-foreground">{revision.revision.label}</td>
              <td className="px-3 py-3 text-muted-foreground">{revision.revision.commit || "-"}</td>
              <td className="px-3 py-3">{formatDateTime(revision.revision.authoredAt)}</td>
              <td className="px-3 py-3">{revision.latestRunStatus ? `${revision.latestRunStatus} · ${formatDateTime(revision.latestRunQueuedAt)}` : "-"}</td>
              <td className="px-3 py-3">
                <Link to={revisionDetailPath(application.id, revision.revision.id)} className="text-xs font-medium text-primary hover:underline">Open</Link>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Table>
  );
}

function RevisionRecordTable({ records }: { records: WorkspaceDesiredStateRevisionRecord[] }) {
  return (
    <Table>
      <table className="min-w-full divide-y divide-border text-sm">
        <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-3 py-2">Kind</th>
            <th className="px-3 py-2">Name</th>
            <th className="px-3 py-2">Artifact</th>
            <th className="px-3 py-2">Digest</th>
            <th className="px-3 py-2">Payload</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {records.map((record) => (
            <tr key={record.id}>
              <td className="px-3 py-3"><Badge>{record.kind}</Badge></td>
              <td className="px-3 py-3 font-medium">{record.name}</td>
              <td className="px-3 py-3 text-muted-foreground">{record.artifactId ?? "-"}</td>
              <td className="max-w-xs truncate px-3 py-3 text-muted-foreground">
                {record.artifactDigest ? `${record.artifactDigest.algorithm}:${record.artifactDigest.value}` : "-"}
              </td>
              <td className="max-w-md px-3 py-3">
                <pre className="max-h-32 overflow-auto rounded-ui bg-muted/40 p-2 text-xs text-muted-foreground">{formatJson(record.payloadJson)}</pre>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </Table>
  );
}

function RevisionRunTable({ runs, engines }: { runs: WorkspaceDesiredStateRevisionDetail["runs"]; engines: WorkflowEngineRegistration[] }) {
  return (
    <Table>
      <table className="min-w-full divide-y divide-border text-sm">
        <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-3 py-2">Run</th>
            <th className="px-3 py-2">Status</th>
            <th className="px-3 py-2">Validation</th>
            <th className="px-3 py-2">Engine</th>
            <th className="px-3 py-2">Queued</th>
            <th className="px-3 py-2">Completed</th>
            <th className="px-3 py-2">Failure</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {runs.map((run) => (
            <tr key={run.id}>
              <td className="px-3 py-3 font-mono text-xs">{run.id.slice(0, 8)}</td>
              <td className="px-3 py-3"><StatusBadge value={run.status} tone={deploymentRunTone(run.status)} /></td>
              <td className="px-3 py-3">{run.validationOutcome}</td>
              <td className="px-3 py-3">{engineLabel(run.engineId, engines)}</td>
              <td className="px-3 py-3">{formatDateTime(run.queuedAt)}</td>
              <td className="px-3 py-3">{formatDateTime(run.completedAt)}</td>
              <td className="px-3 py-3 text-muted-foreground">{run.failureMessage ?? "-"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Table>
  );
}

function EngineTable({
  applicationId,
  environmentId,
  engines
}: {
  applicationId: string;
  environmentId: string;
  engines: WorkflowEngineRegistration[];
}) {
  return (
    <Table>
      <table className="min-w-full divide-y divide-border text-sm">
        <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
          <tr>
            <th className="px-3 py-2">Engine</th>
            <th className="px-3 py-2">Endpoint</th>
            <th className="px-3 py-2">Health</th>
            <th className="px-3 py-2">Credential</th>
            <th className="px-3 py-2">Capabilities</th>
            <th className="px-3 py-2">Controls</th>
            <th className="px-3 py-2">Last heartbeat</th>
            <th className="px-3 py-2">Actions</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-border">
          {engines.map((engine) => (
            <tr key={engine.id}>
              <td className="px-3 py-3 font-medium"><Link to={enginePath(applicationId, environmentId, engine.id)}>{engine.name}</Link></td>
              <td className="max-w-xs truncate px-3 py-3 text-muted-foreground">{engine.endpoint.baseUrl}</td>
              <td className="px-3 py-3"><StatusBadge value={engine.health} tone={healthTone(engine.health)} /></td>
              <td className="px-3 py-3">{engine.credentialReference.verificationStatus}</td>
              <td className="px-3 py-3">{engine.capabilities.length}</td>
              <td className="px-3 py-3">{engine.controls.length}</td>
              <td className="px-3 py-3">{formatDateTime(engine.lastHeartbeatAt)}</td>
              <td className="px-3 py-3"><Link to={enginePath(applicationId, environmentId, engine.id)} className="text-xs font-medium text-primary hover:underline">Open</Link></td>
            </tr>
          ))}
        </tbody>
      </table>
    </Table>
  );
}

function EngineDetailSection({
  application,
  environment,
  engine,
  operationNotice,
  canManageSetup,
  canExecuteControls,
  isVerifying,
  verifyError,
  isRunningControl,
  controlError,
  onVerify,
  onRunControl
}: {
  application: DeploymentCockpit["applications"][number];
  environment: EnvironmentSummary;
  engine: WorkflowEngineRegistration;
  operationNotice: string;
  canManageSetup: boolean;
  canExecuteControls: boolean;
  isVerifying: boolean;
  verifyError?: string;
  isRunningControl: boolean;
  controlError?: string;
  onVerify: () => void;
  onRunControl: (control: RuntimeControl) => void;
}) {
  return (
    <section className="space-y-3">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
        <h2 className="text-sm font-semibold">Engine details</h2>
        <div className="flex flex-wrap gap-2">
          <SecondaryButton disabled={!canManageSetup || isVerifying} onClick={onVerify}>
            <RefreshCw className="h-4 w-4" />
            {isVerifying ? "Verifying" : "Verify"}
          </SecondaryButton>
          <Link to={`${enginePath(application.id, environment.id, engine.id)}/edit`} className={buttonClassName("secondary", !canManageSetup ? "pointer-events-none opacity-50" : undefined)} {...permissionLinkProps(canManageSetup)}>
            <Pencil className="h-4 w-4" />
            Edit engine
          </Link>
        </div>
      </div>
      <div className="grid gap-3 lg:grid-cols-[1.2fr_1fr]">
        <Panel title={engine.name} icon={<RadioTower className="h-4 w-4" />}>
          <dl className="grid gap-3 sm:grid-cols-2">
            <Detail label="Endpoint" value={engine.endpoint.baseUrl} />
            <Detail label="Region" value={engine.endpoint.region} />
            <Detail label="Version" value={engine.endpoint.version} />
            <Detail label="Certificate" value={engine.endpoint.certificateStatus} />
            <Detail label="Health" value={<StatusBadge value={engine.health} tone={healthTone(engine.health)} />} />
            <Detail label="Last heartbeat" value={formatDateTime(engine.lastHeartbeatAt)} />
            <Detail label="Last verification" value={formatDateTime(engine.lastVerificationAt)} />
            <Detail label="Diagnostic" value={engine.verificationMessage || "No verification has run yet."} />
          </dl>
        </Panel>
        <Panel title="Credential reference" icon={<KeyRound className="h-4 w-4" />}>
          <dl className="space-y-3">
            <Detail label="Provider" value={engine.credentialReference.provider} />
            <Detail label="Reference" value={engine.credentialReference.reference} />
            <Detail label="Verification" value={<StatusBadge value={engine.credentialReference.verificationStatus} tone={credentialTone(engine.credentialReference.verificationStatus)} />} />
            <Detail label="Last verified" value={formatDateTime(engine.credentialReference.lastVerifiedAt)} />
          </dl>
        </Panel>
      </div>
      {verifyError ? <div role="alert" className="rounded-ui border border-destructive/30 bg-destructive/10 px-3 py-2 text-sm text-destructive">{verifyError}</div> : null}
      <div className="grid gap-3 lg:grid-cols-2">
        <Panel title="Advertised capabilities" icon={<ClipboardCheck className="h-4 w-4" />}>
          <div className="flex flex-wrap gap-2">
            {engine.capabilities.map((capability) => (
              <Badge key={capability.id} className="gap-1">
                <span className="font-medium">{capability.label}</span>
                <span className="text-muted-foreground">{capability.boundary}</span>
              </Badge>
            ))}
          </div>
        </Panel>
        <Panel title="Supported controls" icon={<RefreshCw className="h-4 w-4" />}>
          <RuntimeControlsPanel
            engine={engine}
            canExecuteControls={canExecuteControls}
            isRunning={isRunningControl}
            notice={operationNotice}
            error={controlError}
            onRunControl={onRunControl}
          />
        </Panel>
      </div>
    </section>
  );
}

function EnvironmentOperations({ data, environment }: { data: DeploymentCockpit; environment: EnvironmentSummary }) {
  const driftItems = data.driftReport.filter((item) => item.environmentId === environment.id);
  const environmentHistory = data.history.filter((item) => item.environmentId === environment.id);
  const scopedData = { ...data, history: environmentHistory, driftReport: driftItems };

  return (
    <div className="grid gap-4 xl:grid-cols-2">
      <Panel title="Observability" icon={<Activity className="h-4 w-4" />}>
        {data.observabilityBindings.length === 0 ? (
          <p className="rounded-ui border border-dashed border-border px-3 py-6 text-center text-sm text-muted-foreground">
            No observability metadata has been recorded.
          </p>
        ) : (
          <div className="grid gap-3 md:grid-cols-2">
            {data.observabilityBindings.map((binding) => (
              <div key={binding.id} className="rounded-ui border border-border p-3 text-sm">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium">{binding.kind}</span>
                  <StatusBadge value={binding.status} tone={binding.status === "Connected" ? "success" : binding.status === "Degraded" ? "warning" : "destructive"} />
                </div>
                <p className="mt-2 text-muted-foreground">{binding.provider}</p>
                <p className="mt-1 text-xs text-muted-foreground">{binding.scope}</p>
                <p className="mt-1 text-xs text-muted-foreground">Revision r{binding.correlatedRevision} · {binding.sample}</p>
              </div>
            ))}
          </div>
        )}
      </Panel>
      <Panel title="Drift report" icon={<AlertTriangle className="h-4 w-4" />}>
        {driftItems.length === 0 ? (
          <p className="rounded-ui border border-dashed border-border px-3 py-6 text-center text-sm text-muted-foreground">
            No drift metadata has been recorded for this environment.
          </p>
        ) : (
          <div className="space-y-2">
            {driftItems.map((item) => (
              <div key={item.id} className="rounded-ui border border-border p-3 text-sm">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="font-medium">{item.area}</span>
                  <StatusBadge value={item.action} tone={item.action === "Redeploy" ? "warning" : "neutral"} />
                </div>
                <div className="mt-2 grid gap-2 text-xs text-muted-foreground sm:grid-cols-2">
                  <div>Desired: {item.desired}</div>
                  <div>Observed: {item.observed}</div>
                </div>
                <div className="mt-2 text-xs text-muted-foreground">{engineLabel(item.engineId, data.engines)}</div>
              </div>
            ))}
          </div>
        )}
      </Panel>
      <div className="xl:col-span-2">
        <DeploymentRunsPanel data={scopedData} />
      </div>
    </div>
  );
}

function ApplicationEditPanel({
  application,
  isSubmitting,
  error,
  onCancel,
  onSubmit
}: {
  application: DeploymentCockpit["applications"][number];
  isSubmitting: boolean;
  error?: string;
  onCancel: () => void;
  onSubmit: (name: string) => void;
}) {
  const [name, setName] = useState(application.name);
  const canSubmit = name.trim().length > 0 && name !== application.name;

  return (
    <form
      className="rounded-ui border border-border bg-surface p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (canSubmit) onSubmit(name.trim());
      }}
    >
      <div className="grid gap-3 md:grid-cols-[1fr_auto] md:items-end">
        <label className="text-sm font-medium">
          Application name
          <Input className="mt-1" value={name} onChange={(event) => setName(event.target.value)} />
        </label>
        <div className="flex gap-2">
          <Button type="submit" disabled={!canSubmit || isSubmitting}>
            <Save className="h-4 w-4" />
            Save
          </Button>
          <SecondaryButton type="button" onClick={onCancel}>Cancel</SecondaryButton>
        </div>
      </div>
      {error ? <p className="mt-3 text-sm text-destructive">{error}</p> : null}
    </form>
  );
}

function EnvironmentForm({
  environment,
  tiers,
  isSubmitting,
  error,
  onCancel,
  onSubmit
}: {
  environment: EnvironmentSummary;
  tiers: WorkspaceDeploymentTier[];
  isSubmitting: boolean;
  error?: string;
  onCancel: () => void;
  onSubmit: (values: EnvironmentFormValues) => void;
}) {
  const initialTierId = environment.tierId ?? tiers.find((tier) => tier.name === (environment.tierName ?? environment.tier))?.id ?? "";
  const [name, setName] = useState(environment.name);
  const [tierId, setTierId] = useState(initialTierId);
  const selectedTier = tiers.find((tier) => tier.id === tierId);
  const canSubmit = name.trim().length > 0 && (name !== environment.name || tierId !== initialTierId);

  return (
    <form
      className="rounded-ui border border-border bg-surface p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (!canSubmit) return;
        onSubmit({
          name: name.trim(),
          tierId: tierId || null,
          tier: legacyTierFromName(selectedTier?.name ?? environment.tierName)
        });
      }}
    >
      <div className="grid gap-3 md:grid-cols-2">
        <label className="text-sm font-medium">
          Environment
          <Input className="mt-1" value={name} onChange={(event) => setName(event.target.value)} />
        </label>
        <label className="text-sm font-medium">
          Tier
          <Select className="mt-1 w-full" value={tierId} onChange={(event) => setTierId(event.target.value)}>
            <option value="" disabled>Select a tier</option>
            {tiers.map((tier) => (
              <option key={tier.id} value={tier.id}>{tier.name}{tier.status === "Archived" ? " (archived)" : ""}</option>
            ))}
          </Select>
        </label>
      </div>
      <div className="mt-4 flex gap-2">
        <Button type="submit" disabled={!canSubmit || isSubmitting}>
          <Save className="h-4 w-4" />
          Save
        </Button>
        <SecondaryButton type="button" onClick={onCancel}>Cancel</SecondaryButton>
      </div>
      {error ? <p className="mt-3 text-sm text-destructive">{error}</p> : null}
    </form>
  );
}

function CredentialStoreForm({
  store,
  canManageSetup,
  isSubmitting,
  error,
  onSubmit
}: {
  store?: WorkspaceDeploymentSecretStore;
  canManageSetup: boolean;
  isSubmitting: boolean;
  error?: string;
  onSubmit: (values: CredentialStoreValues) => void;
}) {
  const [name, setName] = useState(store?.name ?? "");
  const [type, setType] = useState<DeploymentSecretStoreType>(store?.type ?? "AzureKeyVault");
  const typeOption = secretStoreTypeOptions.find((option) => option.value === type) ?? secretStoreTypeOptions[0];
  const canSubmit = canManageSetup && name.trim().length > 0;
  const isEditing = Boolean(store);

  return (
    <form
      className="grid gap-4 rounded-ui border border-border bg-surface p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (!canSubmit) return;
        onSubmit({ name: name.trim(), provider: null, type, description: store?.description ?? null });
      }}
    >
      {!canManageSetup ? <p className="text-sm text-muted-foreground">Deployment setup permission is required to change engine credential stores.</p> : null}
      <label className="text-sm font-medium">
        Store name
        <Input className="mt-1" value={name} disabled={!canManageSetup} onChange={(event) => setName(event.target.value)} placeholder="Control engine credentials" acceptPlaceholderOnTab />
      </label>
      <label className="text-sm font-medium">
        Store type
        <Select className="mt-1 w-full" value={type} disabled={!canManageSetup || isEditing} onChange={(event) => setType(event.target.value as DeploymentSecretStoreType)}>
          {secretStoreTypeOptions.map((option) => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </Select>
      </label>
      <p className="text-xs text-muted-foreground">{typeOption.description}</p>
      {error ? <p className="text-sm text-destructive">{error}</p> : null}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={!canSubmit || isSubmitting}>
          {isEditing ? "Save store" : "Create store"}
        </Button>
        <Link to="/admin/deployments/credentials" className={buttonClassName("secondary")}>Cancel</Link>
      </div>
    </form>
  );
}

function CredentialReferenceForm({
  reference,
  stores,
  initialStoreId,
  cancelTo = "/admin/deployments/credentials",
  canManageSetup,
  isSubmitting,
  error,
  onSubmit
}: {
  reference?: WorkspaceDeploymentCredentialReference;
  stores: WorkspaceDeploymentSecretStore[];
  initialStoreId?: string;
  cancelTo?: string;
  canManageSetup: boolean;
  isSubmitting: boolean;
  error?: string;
  onSubmit: (values: CredentialReferenceValues) => void;
}) {
  const activeStores = stores.filter((store) => store.status === "Active");
  const normalizedInitialStoreId = !reference && initialStoreId && activeStores.some((store) => store.id === initialStoreId) ? initialStoreId : undefined;
  const defaultStoreId = reference?.secretStoreId ?? normalizedInitialStoreId ?? activeStores[0]?.id ?? "";
  const [secretStoreId, setSecretStoreId] = useState(defaultStoreId);
  const [name, setName] = useState(reference?.name ?? "");
  const [referenceValue, setReferenceValue] = useState(reference?.hasProtectedSecret ? "" : reference?.reference ?? "");
  const selectedStore = stores.find((store) => store.id === secretStoreId);
  const selectedStoreType = selectedStore?.type ?? reference?.secretStoreType ?? "GenericExternalReference";
  const selectedTypeOption = secretStoreTypeOptions.find((option) => option.value === selectedStoreType) ?? secretStoreTypeOptions[secretStoreTypeOptions.length - 1];
  const isEditing = Boolean(reference);
  const isProtectedLocalEdit = isEditing && reference?.hasProtectedSecret;
  const requiresReferenceValue = !isProtectedLocalEdit;
  const canSubmit = canManageSetup && secretStoreId.length > 0 && name.trim().length > 0 && (!requiresReferenceValue || referenceValue.trim().length > 0);

  if (!isEditing && activeStores.length === 0) {
    return (
      <div className="rounded-ui border border-border bg-surface p-4">
        <EmptyState
          title="Create a credential store first"
          description="Credential references are registered under an active engine credential store."
          action={<Link to="/admin/deployments/credentials/stores/new" className={buttonClassName()}>New credential store</Link>}
        />
      </div>
    );
  }

  return (
    <form
      className="grid gap-4 rounded-ui border border-border bg-surface p-4"
      onSubmit={(event) => {
        event.preventDefault();
        if (!canSubmit) return;
        const trimmedName = name.trim();
        const submittedReference = isProtectedLocalEdit
          ? reference.reference
          : selectedStoreType === "LocalEncryptedDatabase"
            ? `local://engine-credentials/${slugify(trimmedName)}`
            : referenceValue.trim();
        onSubmit({
          secretStoreId,
          name: trimmedName,
          reference: submittedReference,
          description: reference?.description ?? null,
          secretValue: !isEditing && selectedStoreType === "LocalEncryptedDatabase" ? referenceValue.trim() : null
        });
      }}
    >
      {!canManageSetup ? <p className="text-sm text-muted-foreground">Deployment setup permission is required to change engine credential references.</p> : null}
      <label className="text-sm font-medium">
        Engine credential store
        <Select className="mt-1 w-full" value={secretStoreId} disabled={!canManageSetup || isEditing} onChange={(event) => setSecretStoreId(event.target.value)}>
          <option value="" disabled>Select an engine credential store</option>
          {(isEditing ? stores : activeStores).map((store) => (
            <option key={store.id} value={store.id}>{store.name} ({secretStoreTypeLabel(store.type)})</option>
          ))}
        </Select>
      </label>
      <label className="text-sm font-medium">
        Reference name
        <Input className="mt-1" value={name} disabled={!canManageSetup} onChange={(event) => setName(event.target.value)} placeholder="Test engine API" acceptPlaceholderOnTab />
      </label>
      {isProtectedLocalEdit ? (
        <div className="rounded-ui border border-border bg-muted/30 p-3 text-sm text-muted-foreground">
          This reference uses a protected local credential. Rotate the secret from the reference detail page.
        </div>
      ) : (
        <label className="text-sm font-medium">
          {selectedTypeOption.referenceLabel}
          <Input
            className="mt-1"
            type={selectedStoreType === "LocalEncryptedDatabase" ? "password" : "text"}
            value={referenceValue}
            disabled={!canManageSetup}
            onChange={(event) => setReferenceValue(event.target.value)}
            placeholder={selectedTypeOption.referencePlaceholder}
          />
        </label>
      )}
      <p className="text-xs text-muted-foreground">
        {selectedStoreType === "LocalEncryptedDatabase" ? "The submitted value is protected and never displayed again." : selectedTypeOption.description}
      </p>
      {error ? <p className="text-sm text-destructive">{error}</p> : null}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" disabled={!canSubmit || isSubmitting}>
          {isEditing ? "Save reference" : "Create reference"}
        </Button>
        <Link to={reference ? `/admin/deployments/credentials/references/${reference.id}` : cancelTo} className={buttonClassName("secondary")}>Cancel</Link>
      </div>
    </form>
  );
}

function SecretStoresPanel({
  workspaceId,
  stores,
  references,
  canManageSetup,
  isCreatingStore,
  isCreatingReference,
  pendingActionId,
  error,
  showStatusFilter = false,
  showManagementCopy = false,
  onCreateStore,
  onUpdateStore,
  onArchiveStore,
  onCreateReference,
  onUpdateReference,
  onRotateReference,
  onArchiveReference
}: {
  workspaceId: string;
  stores: WorkspaceDeploymentSecretStore[];
  references: WorkspaceDeploymentCredentialReference[];
  canManageSetup: boolean;
  isCreatingStore: boolean;
  isCreatingReference: boolean;
  pendingActionId: string | null;
  error?: string;
  showStatusFilter?: boolean;
  showManagementCopy?: boolean;
  onCreateStore: (values: CredentialStoreValues) => void;
  onUpdateStore: (secretStoreId: string, values: CredentialStoreValues) => void;
  onArchiveStore: (secretStoreId: string) => void;
  onCreateReference: (values: CredentialReferenceValues) => void;
  onUpdateReference: (credentialReferenceId: string, values: CredentialReferenceValues) => void;
  onRotateReference: (credentialReferenceId: string, secretValue: string) => void;
  onArchiveReference: (credentialReferenceId: string) => void;
}) {
  const [statusFilter, setStatusFilter] = useState<"Active" | "Archived">("Active");
  const activeStores = stores.filter((store) => store.status === "Active");
  const activeReferences = references.filter((reference) => reference.status === "Active");
  const visibleStores = showStatusFilter ? stores.filter((store) => store.status === statusFilter) : activeStores;
  const visibleReferences = showStatusFilter ? references.filter((reference) => reference.status === statusFilter) : activeReferences;
  const [storeName, setStoreName] = useState("");
  const [storeType, setStoreType] = useState<DeploymentSecretStoreType>("AzureKeyVault");
  const [referenceStoreId, setReferenceStoreId] = useState(activeStores[0]?.id ?? "");
  const [referenceName, setReferenceName] = useState("");
  const [referenceValue, setReferenceValue] = useState("");
  const [usageReferenceId, setUsageReferenceId] = useState<string | null>(null);
  const [activeCredentialForm, setActiveCredentialForm] = useState<"store" | "reference" | null>(null);
  const [editingStore, setEditingStore] = useState<WorkspaceDeploymentSecretStore | null>(null);
  const [editingReference, setEditingReference] = useState<WorkspaceDeploymentCredentialReference | null>(null);
  const [confirmArchiveStoreId, setConfirmArchiveStoreId] = useState<string | null>(null);
  const [confirmArchiveReferenceId, setConfirmArchiveReferenceId] = useState<string | null>(null);
  const [rotateReferenceId, setRotateReferenceId] = useState<string | null>(null);
  const [rotateValue, setRotateValue] = useState("");
  const selectedStore = activeStores.find((store) => store.id === referenceStoreId);
  const usageReference = references.find((reference) => reference.id === usageReferenceId);
  const usage = useQuery({
    queryKey: usageReferenceId ? queryKeys.deploymentCredentialReferenceUsage(workspaceId, usageReferenceId) : ["deployments", workspaceId, "credential-references", "usage", "none"],
    queryFn: () => getDeploymentCredentialReferenceUsage(workspaceId, usageReferenceId!),
    enabled: Boolean(usageReferenceId)
  });
  const selectedStoreType = selectedStore?.type ?? "GenericExternalReference";
  const selectedTypeOption = secretStoreTypeOptions.find((option) => option.value === selectedStoreType) ?? secretStoreTypeOptions[secretStoreTypeOptions.length - 1];
  const storeTypeOption = secretStoreTypeOptions.find((option) => option.value === storeType) ?? secretStoreTypeOptions[0];
  useEffect(() => {
    if (activeStores.some((store) => store.id === referenceStoreId)) return;
    setReferenceStoreId(activeStores[0]?.id ?? "");
  }, [activeStores, referenceStoreId]);

  const canCreateStore = canManageSetup && storeName.trim().length > 0;
  const canCreateReference = canManageSetup && referenceStoreId.length > 0 && referenceName.trim().length > 0 && referenceValue.trim().length > 0;
  const canUpdateStore = canManageSetup && editingStore !== null && editingStore.name.trim().length > 0;
  const canUpdateReference = canManageSetup && editingReference !== null && editingReference.name.trim().length > 0 && editingReference.reference.trim().length > 0;
  const canRotateReference = canManageSetup && rotateReferenceId !== null && rotateValue.trim().length > 0;

  return (
    <section className="space-y-3">
      <SectionHeader title="Engine credential stores" description="Register control-plane-to-engine credentials. Runtime secrets remain managed inside runtimes." />
      {showManagementCopy ? (
        <div className="rounded-ui border border-border bg-muted/30 p-3 text-sm text-muted-foreground">
          Engine credentials let Elsa Control interact with registered workflow engines. Runtime secrets and artifact secret references stay in the runtimes.
        </div>
      ) : null}
      {showStatusFilter ? (
        <div className="flex flex-wrap items-center gap-2">
          <label className="text-sm font-medium" htmlFor="credential-status-filter">Status</label>
          <Select id="credential-status-filter" className="w-auto min-w-36" value={statusFilter} onChange={(event) => setStatusFilter(event.target.value as "Active" | "Archived")}>
            <option value="Active">Active</option>
            <option value="Archived">Archived</option>
          </Select>
        </div>
      ) : null}
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.1fr)_minmax(18rem,0.9fr)]">
        <div className="rounded-ui border border-border bg-surface">
          <Table>
            <table className="min-w-full text-sm">
              <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
                <tr>
                  <th className="px-3 py-2">Store</th>
                  <th className="px-3 py-2">Type</th>
                  <th className="px-3 py-2">Credential references</th>
                  <th className="px-3 py-2 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {visibleStores.length === 0 ? (
                  <tr>
                    <td className="px-3 py-6 text-center text-sm text-muted-foreground" colSpan={4}>
                      {statusFilter === "Archived" ? "No archived engine credential stores." : "No active engine credential stores registered."}
                    </td>
                  </tr>
                ) : (
                  visibleStores.map((store) => (
                    <tr key={store.id}>
                      <td className="px-3 py-3 font-medium">
                        {editingStore?.id === store.id ? (
                          <Input value={editingStore.name} aria-label={`Store name for ${store.name}`} onChange={(event) => setEditingStore({ ...editingStore, name: event.target.value })} />
                        ) : store.name}
                      </td>
                      <td className="px-3 py-3">{secretStoreTypeLabel(store.type)}</td>
                      <td className="px-3 py-3">{activeReferences.filter((reference) => reference.secretStoreId === store.id).length}</td>
                      <td className="px-3 py-3 text-right">
                        {editingStore?.id === store.id ? (
                          <div className="flex justify-end gap-2">
                            <SecondaryButton type="button" onClick={() => setEditingStore(null)}>Cancel</SecondaryButton>
                            <Button
                              type="button"
                              disabled={!canUpdateStore || pendingActionId === store.id}
                              onClick={() => {
                                onUpdateStore(store.id, { name: editingStore.name.trim(), provider: null, type: store.type, description: store.description });
                                setEditingStore(null);
                              }}
                            >
                              Save
                            </Button>
                          </div>
                        ) : confirmArchiveStoreId === store.id ? (
                          <div className="flex flex-col items-end gap-2">
                            <span className="text-xs text-muted-foreground">{activeReferences.filter((reference) => reference.secretStoreId === store.id).length} active references will be archived with this store.</span>
                            <div className="flex justify-end gap-2">
                              <SecondaryButton type="button" onClick={() => setConfirmArchiveStoreId(null)}>Cancel</SecondaryButton>
                              <Button
                                type="button"
                                disabled={!canManageSetup || pendingActionId === store.id}
                                onClick={() => {
                                  onArchiveStore(store.id);
                                  setConfirmArchiveStoreId(null);
                                }}
                              >
                                Confirm archive
                              </Button>
                            </div>
                          </div>
                        ) : (
                          <div className="flex justify-end gap-2">
                            {store.status === "Active" ? <SecondaryButton type="button" disabled={!canManageSetup} onClick={() => setEditingStore(store)}>Edit</SecondaryButton> : null}
                            {store.status === "Active" ? <SecondaryButton type="button" disabled={!canManageSetup || pendingActionId === store.id} onClick={() => setConfirmArchiveStoreId(store.id)}>Archive</SecondaryButton> : null}
                          </div>
                        )}
                      </td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </Table>
        </div>
        <div className="space-y-4 rounded-ui border border-border bg-surface p-4">
          {canManageSetup ? (
            <div className="space-y-3">
              <div>
                <h3 className="text-sm font-semibold">Credential actions</h3>
                <p className="mt-1 text-xs text-muted-foreground">Create stores and references only when this application needs additional engine credentials.</p>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button type="button" disabled={!canManageSetup} onClick={() => setActiveCredentialForm("store")}>
                  <Plus className="h-4 w-4" />
                  New credential store
                </Button>
                <SecondaryButton type="button" disabled={!canManageSetup || activeStores.length === 0} onClick={() => setActiveCredentialForm("reference")}>
                  <Plus className="h-4 w-4" />
                  New credential reference
                </SecondaryButton>
              </div>
              {activeStores.length === 0 ? (
                <p className="text-xs text-muted-foreground">Create a credential store before adding credential references.</p>
              ) : null}
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">Deployment setup permission is required to change engine credential stores and references.</p>
          )}
          {activeCredentialForm === "store" ? (
            <form
              className="grid gap-3 border-t border-border pt-4"
              onSubmit={(event) => {
                event.preventDefault();
                if (!canCreateStore) return;
                onCreateStore({ name: storeName.trim(), provider: null, type: storeType, description: null });
                setStoreName("");
              }}
            >
              <h3 className="text-sm font-semibold">Register engine credential store</h3>
              <label className="text-sm font-medium">
                Store name
                <Input className="mt-1" value={storeName} onChange={(event) => setStoreName(event.target.value)} placeholder="Control engine credentials" acceptPlaceholderOnTab />
              </label>
              <label className="text-sm font-medium">
                Store type
                <Select className="mt-1 w-full" value={storeType} onChange={(event) => setStoreType(event.target.value as DeploymentSecretStoreType)}>
                  {secretStoreTypeOptions.map((option) => (
                    <option key={option.value} value={option.value}>{option.label}</option>
                  ))}
                </Select>
              </label>
              <p className="text-xs text-muted-foreground">{storeTypeOption.description}</p>
              <div className="flex gap-2">
                <Button type="submit" disabled={!canCreateStore || isCreatingStore}>
                  <Plus className="h-4 w-4" />
                  Register store
                </Button>
                <SecondaryButton type="button" onClick={() => setActiveCredentialForm(null)}>Cancel</SecondaryButton>
              </div>
            </form>
          ) : null}
          {activeCredentialForm === "reference" ? (
            activeStores.length === 0 ? (
              <div className="border-t border-border pt-4 text-sm text-muted-foreground">
                Register an engine credential store first. After it is created, add credential references for the engine secrets it manages.
              </div>
            ) : (
              <form
                className="grid gap-3 border-t border-border pt-4"
                onSubmit={(event) => {
                  event.preventDefault();
                  if (!canCreateReference) return;
                  const name = referenceName.trim();
                  onCreateReference({
                    secretStoreId: referenceStoreId,
                    name,
                    reference: selectedStoreType === "LocalEncryptedDatabase" ? `local://engine-credentials/${slugify(name)}` : referenceValue.trim(),
                    description: null,
                    secretValue: selectedStoreType === "LocalEncryptedDatabase" ? referenceValue.trim() : null
                  });
                  setReferenceName("");
                  setReferenceValue("");
                }}
              >
                <h3 className="text-sm font-semibold">Register credential reference</h3>
                <label className="text-sm font-medium">
                  Engine credential store
                  <Select className="mt-1 w-full" value={referenceStoreId} disabled={activeStores.length === 0} onChange={(event) => setReferenceStoreId(event.target.value)}>
                    <option value="" disabled>{activeStores.length === 0 ? "No engine credential stores" : "Select an engine credential store"}</option>
                    {activeStores.map((store) => <option key={store.id} value={store.id}>{store.name} ({secretStoreTypeLabel(store.type)})</option>)}
                  </Select>
                </label>
                <label className="text-sm font-medium">
                  Reference name
                  <Input className="mt-1" value={referenceName} onChange={(event) => setReferenceName(event.target.value)} placeholder="Test engine API" acceptPlaceholderOnTab />
                </label>
                <label className="text-sm font-medium">
                  {selectedTypeOption.referenceLabel}
                  <Input
                    className="mt-1"
                    type={selectedStoreType === "LocalEncryptedDatabase" ? "password" : "text"}
                    value={referenceValue}
                    onChange={(event) => setReferenceValue(event.target.value)}
                    placeholder={selectedTypeOption.referencePlaceholder}
                  />
                </label>
                <p className="text-xs text-muted-foreground">{selectedStoreType === "LocalEncryptedDatabase" ? "The submitted value is protected and never displayed again." : selectedTypeOption.description}</p>
                <div className="flex gap-2">
                  <Button type="submit" disabled={!canCreateReference || isCreatingReference}>
                    <Plus className="h-4 w-4" />
                    Register reference
                  </Button>
                  <SecondaryButton type="button" onClick={() => setActiveCredentialForm(null)}>Cancel</SecondaryButton>
                </div>
              </form>
            )
          ) : null}
          {error ? <p className="text-sm text-destructive">{error}</p> : null}
        </div>
      </div>
      {visibleReferences.length > 0 ? (
        <div className="rounded-ui border border-border bg-surface">
          <Table>
            <table className="min-w-full text-sm">
              <thead className="bg-muted/40 text-left text-xs uppercase text-muted-foreground">
                <tr>
                  <th className="px-3 py-2">Credential</th>
                  <th className="px-3 py-2">Store</th>
                  <th className="px-3 py-2">Reference</th>
                  <th className="px-3 py-2">Usage</th>
                  <th className="px-3 py-2">Verification</th>
                  <th className="px-3 py-2 text-right">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {visibleReferences.map((reference) => (
                  <tr key={reference.id}>
                    <td className="px-3 py-3 font-medium">
                      {editingReference?.id === reference.id ? (
                        <Input value={editingReference.name} aria-label={`Reference name for ${reference.name}`} onChange={(event) => setEditingReference({ ...editingReference, name: event.target.value })} />
                      ) : reference.name}
                    </td>
                    <td className="px-3 py-3">{reference.secretStoreName}</td>
                    <td className="px-3 py-3">
                      {editingReference?.id === reference.id && !reference.hasProtectedSecret ? (
                        <Input value={editingReference.reference} aria-label={`Reference locator for ${reference.name}`} onChange={(event) => setEditingReference({ ...editingReference, reference: event.target.value })} />
                      ) : reference.hasProtectedSecret ? "Protected local credential" : reference.reference}
                    </td>
                    <td className="px-3 py-3">
                      {reference.usageCount > 0 ? (
                        <button
                          type="button"
                          className="text-primary hover:underline"
                          onClick={() => setUsageReferenceId((current) => current === reference.id ? null : reference.id)}
                        >
                          {reference.usageCount} engines
                        </button>
                      ) : "0"}
                    </td>
                    <td className="px-3 py-3"><StatusBadge value={reference.verificationStatus} tone={credentialTone(reference.verificationStatus)} /></td>
                    <td className="px-3 py-3 text-right">
                      {editingReference?.id === reference.id ? (
                        <div className="flex justify-end gap-2">
                          <SecondaryButton type="button" onClick={() => setEditingReference(null)}>Cancel</SecondaryButton>
                          <Button
                            type="button"
                            disabled={!canUpdateReference || pendingActionId === reference.id}
                            onClick={() => {
                              onUpdateReference(reference.id, {
                                secretStoreId: reference.secretStoreId,
                                name: editingReference.name.trim(),
                                reference: editingReference.reference.trim(),
                                description: editingReference.description,
                                secretValue: null
                              });
                              setEditingReference(null);
                            }}
                          >
                            Save
                          </Button>
                        </div>
                      ) : confirmArchiveReferenceId === reference.id ? (
                        <div className="flex flex-col items-end gap-2">
                          <span className="text-xs text-muted-foreground">{reference.usageCount > 0 ? `${reference.usageCount} engines currently use this reference.` : "No active engines currently use this reference."}</span>
                          <div className="flex justify-end gap-2">
                            <SecondaryButton type="button" onClick={() => setConfirmArchiveReferenceId(null)}>Cancel</SecondaryButton>
                            <Button
                              type="button"
                              disabled={!canManageSetup || pendingActionId === reference.id}
                              onClick={() => {
                                onArchiveReference(reference.id);
                                setConfirmArchiveReferenceId(null);
                              }}
                            >
                              Confirm archive
                            </Button>
                          </div>
                        </div>
                      ) : rotateReferenceId === reference.id ? (
                        <form
                          className="flex flex-col items-end gap-2"
                          onSubmit={(event) => {
                            event.preventDefault();
                            if (!canRotateReference) return;
                            onRotateReference(reference.id, rotateValue.trim());
                            setRotateReferenceId(null);
                            setRotateValue("");
                          }}
                        >
                          {reference.usageCount > 0 ? <span className="text-xs text-muted-foreground">{reference.usageCount} engines use this reference.</span> : null}
                          <Input aria-label={`New credential value for ${reference.name}`} className="w-56" type="password" value={rotateValue} onChange={(event) => setRotateValue(event.target.value)} placeholder="New credential value" />
                          <div className="flex justify-end gap-2">
                            <SecondaryButton type="button" onClick={() => { setRotateReferenceId(null); setRotateValue(""); }}>Cancel</SecondaryButton>
                            <Button type="submit" disabled={!canRotateReference || pendingActionId === reference.id}>Rotate</Button>
                          </div>
                        </form>
                      ) : (
                        <div className="flex justify-end gap-2">
                          {reference.status === "Active" ? <SecondaryButton type="button" disabled={!canManageSetup} onClick={() => setEditingReference(reference)}>Edit</SecondaryButton> : null}
                          {reference.status === "Active" && reference.secretStoreType === "LocalEncryptedDatabase" ? (
                            <SecondaryButton type="button" disabled={!canManageSetup} onClick={() => { setRotateReferenceId(reference.id); setUsageReferenceId(reference.usageCount > 0 ? reference.id : usageReferenceId); }}>
                              Rotate
                            </SecondaryButton>
                          ) : null}
                          {reference.status === "Active" ? (
                            <SecondaryButton
                              type="button"
                              disabled={!canManageSetup || pendingActionId === reference.id}
                              onClick={() => {
                                setConfirmArchiveReferenceId(reference.id);
                                if (reference.usageCount > 0) setUsageReferenceId(reference.id);
                              }}
                            >
                              Archive
                            </SecondaryButton>
                          ) : null}
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </Table>
          {usageReference ? (
            <div className="border-t border-border px-3 py-3">
              <div className="mb-2 flex items-center gap-2 text-sm font-medium">
                <AlertTriangle className="h-4 w-4 text-warning" />
                {usageReference.name} is assigned to {usageReference.usageCount} engines
              </div>
              {usage.isLoading || usage.isFetching ? (
                <p className="text-sm text-muted-foreground">Loading credential usage.</p>
              ) : usage.isError ? (
                <p className="text-sm text-destructive">Credential usage could not load.</p>
              ) : usage.data?.items.length ? (
                <ul className="grid gap-2 text-sm text-muted-foreground sm:grid-cols-2">
                  {usage.data.items.map((item) => (
                    <li key={item.engineId} className="rounded-ui border border-border bg-muted/30 px-3 py-2">
                      <span className="block font-medium text-foreground">{item.engineName}</span>
                      <span>{item.applicationName} / {item.environmentName}</span>
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="text-sm text-muted-foreground">No active engines currently use this credential reference.</p>
              )}
              <p className="mt-2 text-xs text-muted-foreground">Archive only after reassignment or after confirming the affected engines no longer need Elsa Control management.</p>
            </div>
          ) : null}
        </div>
      ) : statusFilter === "Archived" ? (
        <EmptyState title="No archived credential references" description="Archived engine credential references will remain inspectable here." />
      ) : (
        <EmptyState title="No credential references registered" description="Register a credential reference under an active engine credential store, or continue engine setup with credentials deferred." />
      )}
    </section>
  );
}

function EngineRegistrationPanel({
  environment,
  secretStores,
  credentialReferences,
  isSubmitting,
  error,
  cancelLabel = "Cancel",
  onCancel,
  onCreateReference,
  onSubmit
}: {
  environment: Pick<EnvironmentSummary, "id" | "name">;
  secretStores: WorkspaceDeploymentSecretStore[];
  credentialReferences: WorkspaceDeploymentCredentialReference[];
  isSubmitting: boolean;
  error?: string;
  cancelLabel?: string;
  onCancel: () => void;
  onCreateReference: (values: CredentialReferenceValues) => Promise<WorkspaceDeploymentCredentialReference>;
  onSubmit: (values: EngineRegistrationValues) => void;
}) {
  const activeSecretStores = secretStores.filter((store) => store.status === "Active");
  const [selectedSecretStoreId, setSelectedSecretStoreId] = useState(activeSecretStores[0]?.id ?? "");
  const scopedCredentialReferences = credentialReferences.filter((reference) => reference.status === "Active" && reference.secretStoreId === selectedSecretStoreId);
  const selectedSecretStore = activeSecretStores.find((store) => store.id === selectedSecretStoreId);
  const selectedStoreType = selectedSecretStore?.type ?? "GenericExternalReference";
  const selectedTypeOption = secretStoreTypeOptions.find((option) => option.value === selectedStoreType) ?? secretStoreTypeOptions[secretStoreTypeOptions.length - 1];
  const [credentialMode, setCredentialMode] = useState<EngineCredentialMode>(activeSecretStores.length > 0 ? "Create" : "Deferred");
  const [referenceName, setReferenceName] = useState("");
  const [referenceValue, setReferenceValue] = useState("");
  const [values, setValues] = useState<EngineRegistrationValues>({
    engineName: "",
    baseUrl: "",
    credentialAssignmentStatus: activeSecretStores.length > 0 ? "Assigned" : "Deferred",
    credentialReferenceId: scopedCredentialReferences[0]?.id ?? null
  });
  useEffect(() => {
    if (activeSecretStores.some((store) => store.id === selectedSecretStoreId)) return;
    setSelectedSecretStoreId(activeSecretStores[0]?.id ?? "");
  }, [activeSecretStores, selectedSecretStoreId]);
  useEffect(() => {
    if (scopedCredentialReferences.some((reference) => reference.id === values.credentialReferenceId)) return;
    setValues((current) => ({
      ...current,
      credentialReferenceId: scopedCredentialReferences[0]?.id ?? null,
      credentialAssignmentStatus: scopedCredentialReferences.length > 0 ? current.credentialAssignmentStatus ?? "Assigned" : "Deferred"
    }));
  }, [scopedCredentialReferences, values.credentialReferenceId]);
  useEffect(() => {
    if (activeSecretStores.length > 0 || credentialMode === "Deferred") return;
    setCredentialMode("Deferred");
  }, [activeSecretStores.length, credentialMode]);
  const credentialsDeferred = credentialMode === "Deferred";
  const referenceNameForSubmit = referenceName.trim() || `${values.engineName.trim()} engine credential`;
  const canCreateReference =
    credentialMode === "Create" &&
    selectedSecretStore !== undefined &&
    referenceNameForSubmit.trim().length > 0 &&
    referenceValue.trim().length > 0;
  const canSubmit =
    values.engineName.trim().length > 0 &&
    values.baseUrl.trim().length > 0 &&
    (credentialsDeferred ||
      (credentialMode === "Existing" && Boolean(values.credentialReferenceId)) ||
      canCreateReference);

  return (
    <form
      onSubmit={async (event) => {
        event.preventDefault();
        if (!canSubmit) return;
        if (credentialMode === "Create") {
          let reference: WorkspaceDeploymentCredentialReference;
          try {
            reference = await onCreateReference({
              secretStoreId: selectedSecretStoreId,
              name: referenceNameForSubmit,
              reference: selectedStoreType === "LocalEncryptedDatabase" ? `local://engine-credentials/${slugify(referenceNameForSubmit)}` : referenceValue.trim(),
              description: null,
              secretValue: selectedStoreType === "LocalEncryptedDatabase" ? referenceValue.trim() : null
            });
          } catch {
            return;
          }
          onSubmit({
            engineName: values.engineName.trim(),
            baseUrl: values.baseUrl.trim(),
            credentialAssignmentStatus: "Assigned",
            credentialReferenceId: reference.id
          });
          return;
        }
        onSubmit({
          engineName: values.engineName.trim(),
          baseUrl: values.baseUrl.trim(),
          credentialAssignmentStatus: credentialsDeferred ? "Deferred" : "Assigned",
          credentialReferenceId: credentialMode === "Existing" ? values.credentialReferenceId : null
        });
      }}
    >
      <div className="mb-3 flex flex-col gap-1">
        <h2 className="text-sm font-semibold">Register engine for {environment.name}</h2>
        <p className="text-xs text-muted-foreground">Add another Elsa workflow engine endpoint to this environment.</p>
      </div>
      <div className="grid gap-3 md:grid-cols-2">
        <label className="text-sm font-medium">
          Engine name
          <Input className="mt-1" placeholder="test-weu-01" value={values.engineName} onChange={(event) => setValues((current) => ({ ...current, engineName: event.target.value }))} />
        </label>
        <label className="text-sm font-medium">
          Engine base URL
          <Input className="mt-1" placeholder="https://test-engine.example.com" value={values.baseUrl} onChange={(event) => setValues((current) => ({ ...current, baseUrl: event.target.value }))} />
        </label>
      </div>
      <div className="mt-4 space-y-3 rounded-ui border border-border bg-muted/20 p-3">
        <SectionHeader title="Engine credentials" description="Create a credential for this engine, reuse an existing reference, or defer credentials." />
        <div className="flex flex-wrap gap-2" role="group" aria-label="Credential assignment mode">
          <button
            type="button"
            className={buttonClassName(credentialMode === "Create" ? "primary" : "secondary")}
            disabled={activeSecretStores.length === 0}
            onClick={() => setCredentialMode("Create")}
          >
            Create for this engine
          </button>
          <button
            type="button"
            className={buttonClassName(credentialMode === "Existing" ? "primary" : "secondary")}
            disabled={scopedCredentialReferences.length === 0}
            onClick={() => setCredentialMode("Existing")}
          >
            Use existing
          </button>
          <button type="button" className={buttonClassName(credentialMode === "Deferred" ? "primary" : "secondary")} onClick={() => setCredentialMode("Deferred")}>
            Defer credentials
          </button>
        </div>
        {credentialMode === "Create" ? (
          activeSecretStores.length === 0 ? (
            <p className="text-sm text-muted-foreground">
              Create an engine credential store before adding engine-specific credentials, or register this engine with credentials deferred.
            </p>
          ) : (
            <div className="grid gap-3 md:grid-cols-2">
              <label className="text-sm font-medium">
                Credential store
                <Select className="mt-1 w-full" value={selectedSecretStoreId} onChange={(event) => setSelectedSecretStoreId(event.target.value)}>
                  {activeSecretStores.map((store) => (
                    <option key={store.id} value={store.id}>{store.name} ({secretStoreTypeLabel(store.type)})</option>
                  ))}
                </Select>
              </label>
              <label className="text-sm font-medium">
                Reference name
                <Input className="mt-1" value={referenceName} onChange={(event) => setReferenceName(event.target.value)} placeholder={values.engineName.trim() ? `${values.engineName.trim()} engine credential` : "Engine API credential"} acceptPlaceholderOnTab />
              </label>
              <label className="text-sm font-medium md:col-span-2">
                {selectedTypeOption.referenceLabel}
                <Input
                  className="mt-1"
                  type={selectedStoreType === "LocalEncryptedDatabase" ? "password" : "text"}
                  value={referenceValue}
                  onChange={(event) => setReferenceValue(event.target.value)}
                  placeholder={selectedTypeOption.referencePlaceholder}
                />
              </label>
              <p className="text-xs text-muted-foreground md:col-span-2">
                {selectedStoreType === "LocalEncryptedDatabase" ? "The submitted value is protected and never displayed again." : selectedTypeOption.description}
              </p>
            </div>
          )
        ) : null}
        {credentialMode === "Existing" ? (
          <div className="grid gap-3 md:grid-cols-2">
            <label className="text-sm font-medium">
              Credential store
              <Select
                className="mt-1 w-full"
                value={selectedSecretStoreId}
                onChange={(event) => setSelectedSecretStoreId(event.target.value)}
                disabled={activeSecretStores.length === 0}
              >
                <option value="" disabled>{activeSecretStores.length === 0 ? "No credential stores registered" : "Select a credential store"}</option>
                {activeSecretStores.map((store) => (
                  <option key={store.id} value={store.id}>{store.name} ({secretStoreTypeLabel(store.type)})</option>
                ))}
              </Select>
            </label>
            <label className="text-sm font-medium">
              Credential reference
              <Select
                className="mt-1 w-full"
                value={values.credentialReferenceId ?? ""}
                onChange={(event) => setValues((current) => ({ ...current, credentialReferenceId: event.target.value }))}
                disabled={scopedCredentialReferences.length === 0}
              >
                <option value="" disabled>{selectedSecretStoreId ? "Select a credential reference" : "Select a credential store first"}</option>
                {scopedCredentialReferences.map((reference) => (
                  <option key={reference.id} value={reference.id}>{reference.name} - {reference.hasProtectedSecret ? "Protected local credential" : reference.reference}</option>
                ))}
              </Select>
            </label>
          </div>
        ) : null}
        {credentialMode === "Deferred" ? (
          <p className="text-sm text-muted-foreground">Register this engine now and assign credentials later from the engine edit screen.</p>
        ) : null}
      </div>
      {activeSecretStores.length === 0 ? (
        <p className="mt-3 text-sm text-muted-foreground">
          No engine credential stores are registered. You can continue with credentials deferred or <Link to="/admin/deployments/credentials/stores/new" className="text-primary hover:underline">create a credential store</Link>.
        </p>
      ) : credentialMode === "Existing" && selectedSecretStoreId && scopedCredentialReferences.length === 0 ? (
        <p className="mt-3 text-sm text-muted-foreground">
          No active credential references are registered for the selected store. Create a credential for this engine or <Link to="/admin/deployments/credentials" className="text-primary hover:underline">open credential inventory</Link>.
        </p>
      ) : null}
      {error ? <p className="mt-3 text-sm text-destructive">{error}</p> : null}
      <div className="mt-4 flex gap-2">
        <Button type="submit" disabled={!canSubmit || isSubmitting}>
          <Plus className="h-4 w-4" />
          Register engine
        </Button>
        <SecondaryButton type="button" onClick={onCancel}>{cancelLabel}</SecondaryButton>
      </div>
    </form>
  );
}

type EngineEditValues = {
  name: string;
  baseUrl: string;
  region: string | null;
  credentialProvider: string | null;
  credentialReference: string | null;
  credentialReferenceId: string | null;
  credentialAssignmentStatus: WorkflowEngineRegistration["credentialAssignmentStatus"];
};

function EngineEditPanel({
  engine,
  secretStores,
  credentialReferences,
  isSubmitting,
  error,
  onCancel,
  onCreateReference,
  onSubmit
}: {
  engine: WorkflowEngineRegistration;
  secretStores: WorkspaceDeploymentSecretStore[];
  credentialReferences: WorkspaceDeploymentCredentialReference[];
  isSubmitting: boolean;
  error?: string;
  onCancel: () => void;
  onCreateReference: (values: CredentialReferenceValues) => Promise<WorkspaceDeploymentCredentialReference>;
  onSubmit: (values: EngineEditValues) => void;
}) {
  const activeStores = useMemo(() => secretStores.filter((store) => store.status === "Active"), [secretStores]);
  const activeReferences = useMemo(() => credentialReferences.filter((reference) => reference.status === "Active"), [credentialReferences]);
  const initialCredentialReference = useMemo(
    () => activeReferences.find((reference) =>
      reference.reference === engine.credentialReference.reference &&
      reference.secretStoreProvider === engine.credentialReference.provider
    ) ?? null,
    [activeReferences, engine.credentialReference.provider, engine.credentialReference.reference]
  );
  const [name, setName] = useState(engine.name);
  const [baseUrl, setBaseUrl] = useState(engine.endpoint.baseUrl);
  const [region, setRegion] = useState(engine.endpoint.region);
  const [selectedStoreId, setSelectedStoreId] = useState(initialCredentialReference?.secretStoreId ?? "");
  const [selectedReferenceId, setSelectedReferenceId] = useState(initialCredentialReference?.id ?? "");
  const [credentialMode, setCredentialMode] = useState<EngineCredentialMode>(initialCredentialReference ? "Existing" : "Deferred");
  const [newReferenceStoreId, setNewReferenceStoreId] = useState(activeStores[0]?.id ?? "");
  const [newReferenceName, setNewReferenceName] = useState("");
  const [newReferenceValue, setNewReferenceValue] = useState("");
  useEffect(() => {
    if (selectedReferenceId || !initialCredentialReference) return;
    setSelectedStoreId(initialCredentialReference.secretStoreId);
    setSelectedReferenceId(initialCredentialReference.id);
  }, [initialCredentialReference, selectedReferenceId]);
  useEffect(() => {
    if (activeStores.some((store) => store.id === newReferenceStoreId)) return;
    setNewReferenceStoreId(activeStores[0]?.id ?? "");
  }, [activeStores, newReferenceStoreId]);
  const scopedReferences = activeReferences.filter((reference) => reference.secretStoreId === selectedStoreId);
  const selectedReference = activeReferences.find((reference) => reference.id === selectedReferenceId) ?? null;
  const newReferenceStore = activeStores.find((store) => store.id === newReferenceStoreId);
  const newReferenceStoreType = newReferenceStore?.type ?? "GenericExternalReference";
  const newReferenceTypeOption = secretStoreTypeOptions.find((option) => option.value === newReferenceStoreType) ?? secretStoreTypeOptions[secretStoreTypeOptions.length - 1];
  const initialReferenceId = initialCredentialReference?.id ?? "";
  const newReferenceNameForSubmit = newReferenceName.trim() || `${name.trim()} engine credential`;
  const canCreateReference =
    credentialMode === "Create" &&
    newReferenceStore !== undefined &&
    newReferenceNameForSubmit.trim().length > 0 &&
    newReferenceValue.trim().length > 0;
  const canSubmit =
    name.trim().length > 0 &&
    baseUrl.trim().length > 0 &&
    (name !== engine.name ||
      baseUrl !== engine.endpoint.baseUrl ||
      region !== engine.endpoint.region ||
      (credentialMode === "Existing" && selectedReferenceId !== initialReferenceId) ||
      credentialMode === "Create" ||
      (credentialMode === "Deferred" && initialReferenceId !== "")) &&
    (credentialMode === "Deferred" ||
      (credentialMode === "Existing" && Boolean(selectedReferenceId)) ||
      canCreateReference);

  return (
    <form
      className="rounded-ui border border-border bg-surface p-4"
      onSubmit={async (event) => {
        event.preventDefault();
        if (!canSubmit) return;
        if (credentialMode === "Create") {
          let reference: WorkspaceDeploymentCredentialReference;
          try {
            reference = await onCreateReference({
              secretStoreId: newReferenceStoreId,
              name: newReferenceNameForSubmit,
              reference: newReferenceStoreType === "LocalEncryptedDatabase" ? `local://engine-credentials/${slugify(newReferenceNameForSubmit)}` : newReferenceValue.trim(),
              description: null,
              secretValue: newReferenceStoreType === "LocalEncryptedDatabase" ? newReferenceValue.trim() : null
            });
          } catch {
            return;
          }
          onSubmit({
            name: name.trim(),
            baseUrl: baseUrl.trim(),
            region: region.trim() || null,
            credentialProvider: reference.secretStoreProvider,
            credentialReference: reference.reference,
            credentialReferenceId: reference.id,
            credentialAssignmentStatus: "Assigned"
          });
          return;
        }
        onSubmit({
          name: name.trim(),
          baseUrl: baseUrl.trim(),
          region: region.trim() || null,
          credentialProvider: credentialMode === "Existing" ? selectedReference?.secretStoreProvider ?? null : null,
          credentialReference: credentialMode === "Existing" ? selectedReference?.reference ?? null : null,
          credentialReferenceId: credentialMode === "Existing" ? selectedReference?.id ?? null : null,
          credentialAssignmentStatus: credentialMode === "Existing" && selectedReference ? "Assigned" : "Deferred"
        });
      }}
    >
      <div className="grid gap-3 md:grid-cols-2">
        <label className="text-sm font-medium">
          Engine name
          <Input className="mt-1" value={name} onChange={(event) => setName(event.target.value)} />
        </label>
        <label className="text-sm font-medium">
          Engine base URL
          <Input className="mt-1" value={baseUrl} onChange={(event) => setBaseUrl(event.target.value)} />
        </label>
        <label className="text-sm font-medium">
          Region
          <Input className="mt-1" value={region} onChange={(event) => setRegion(event.target.value)} />
        </label>
      </div>
      <div className="mt-4 space-y-3 rounded-ui border border-border bg-muted/20 p-3">
        <SectionHeader title="Engine credentials" description="Create a credential for this engine, reuse an existing reference, or defer credentials." />
        <div className="flex flex-wrap gap-2" role="group" aria-label="Credential assignment mode">
          <button
            type="button"
            className={buttonClassName(credentialMode === "Create" ? "primary" : "secondary")}
            disabled={activeStores.length === 0}
            onClick={() => setCredentialMode("Create")}
          >
            Create for this engine
          </button>
          <button
            type="button"
            className={buttonClassName(credentialMode === "Existing" ? "primary" : "secondary")}
            disabled={activeReferences.length === 0}
            onClick={() => setCredentialMode("Existing")}
          >
            Use existing
          </button>
          <button type="button" className={buttonClassName(credentialMode === "Deferred" ? "primary" : "secondary")} onClick={() => setCredentialMode("Deferred")}>
            Defer credentials
          </button>
        </div>
        {credentialMode === "Create" ? (
          activeStores.length === 0 ? (
            <p className="text-sm text-muted-foreground">Create an engine credential store before adding engine-specific credentials.</p>
          ) : (
            <div className="grid gap-3 md:grid-cols-2">
              <label className="text-sm font-medium">
                Credential store
                <Select className="mt-1 w-full" value={newReferenceStoreId} onChange={(event) => setNewReferenceStoreId(event.target.value)}>
                  {activeStores.map((store) => (
                    <option key={store.id} value={store.id}>{store.name} ({secretStoreTypeLabel(store.type)})</option>
                  ))}
                </Select>
              </label>
              <label className="text-sm font-medium">
                Reference name
                <Input className="mt-1" value={newReferenceName} onChange={(event) => setNewReferenceName(event.target.value)} placeholder={`${name.trim()} engine credential`} acceptPlaceholderOnTab />
              </label>
              <label className="text-sm font-medium md:col-span-2">
                {newReferenceTypeOption.referenceLabel}
                <Input
                  className="mt-1"
                  type={newReferenceStoreType === "LocalEncryptedDatabase" ? "password" : "text"}
                  value={newReferenceValue}
                  onChange={(event) => setNewReferenceValue(event.target.value)}
                  placeholder={newReferenceTypeOption.referencePlaceholder}
                />
              </label>
              <p className="text-xs text-muted-foreground md:col-span-2">
                {newReferenceStoreType === "LocalEncryptedDatabase" ? "The submitted value is protected and never displayed again." : newReferenceTypeOption.description}
              </p>
            </div>
          )
        ) : null}
        {credentialMode === "Existing" ? (
          <div className="grid gap-3 md:grid-cols-2">
            <label className="text-sm font-medium">
              Credential store
              <Select
                className="mt-1 w-full"
                value={selectedStoreId}
                onChange={(event) => {
                  const storeId = event.target.value;
                  const firstReference = activeReferences.find((reference) => reference.secretStoreId === storeId);
                  setSelectedStoreId(storeId);
                  setSelectedReferenceId(firstReference?.id ?? "");
                }}
              >
                <option value="">Select a credential store</option>
                {activeStores.map((store) => (
                  <option key={store.id} value={store.id}>{store.name} ({secretStoreTypeLabel(store.type)})</option>
                ))}
              </Select>
            </label>
            <label className="text-sm font-medium">
              Credential reference
              <Select
                className="mt-1"
                value={selectedReferenceId}
                disabled={!selectedStoreId || scopedReferences.length === 0}
                onChange={(event) => setSelectedReferenceId(event.target.value)}
              >
                <option value="" disabled>{selectedStoreId ? "No active credential references" : "Select a credential store first"}</option>
                {scopedReferences.map((reference) => (
                  <option key={reference.id} value={reference.id}>{reference.name} - {reference.hasProtectedSecret ? "Protected local credential" : reference.reference}</option>
                ))}
              </Select>
            </label>
          </div>
        ) : null}
        {credentialMode === "Deferred" ? (
          <p className="text-sm text-muted-foreground">Save this engine without control-plane-to-engine credentials.</p>
        ) : null}
      </div>
      {activeStores.length === 0 ? (
        <p className="mt-3 text-sm text-muted-foreground">
          No credential stores are registered. Saving can keep this engine's credentials deferred, or you can <Link to="/admin/deployments/credentials/stores/new" className="text-primary hover:underline">create a credential store</Link>.
        </p>
      ) : credentialMode === "Existing" && selectedStoreId && scopedReferences.length === 0 ? (
        <p className="mt-3 text-sm text-muted-foreground">
          No active credential references are registered for the selected store. Create a credential for this engine or <Link to="/admin/deployments/credentials" className="text-primary hover:underline">open credential inventory</Link>.
        </p>
      ) : null}
      {error ? <p className="mt-3 text-sm text-destructive">{error}</p> : null}
      <div className="mt-4 flex gap-2">
        <Button type="submit" disabled={!canSubmit || isSubmitting}>
          <Save className="h-4 w-4" />
          Save
        </Button>
        <SecondaryButton type="button" onClick={onCancel}>Cancel</SecondaryButton>
      </div>
    </form>
  );
}

function AssistantPlanView({
  data,
  targetEnvironmentId,
  outcome,
  onApprove,
  onReject
}: {
  data: DeploymentCockpit;
  targetEnvironmentId: string;
  outcome: "Proposed" | "Approved" | "Rejected";
  onApprove: () => void;
  onReject: () => void;
}) {
  const plan = data.assistantPlans.find((item) => item.targetEnvironmentId === targetEnvironmentId) ?? data.assistantPlans[0];
  if (!plan) {
    return (
      <RequestStateView
        state="empty"
        title="No assistant plan available"
        description="Assistant review will appear after a deployment plan is generated for this environment."
      />
    );
  }

  const displayedStatus = outcome === "Proposed" ? plan.status : outcome;
  const blocked = hasBlockingValidation(plan.validations);

  return (
    <div className="grid gap-4 xl:grid-cols-[1.2fr_1fr]">
      <Panel title={`Assistant plan ${plan.id} v${plan.version}`} icon={<Bot className="h-4 w-4" />}>
        <div className="space-y-4">
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge value={displayedStatus} tone={displayedStatus === "Approved" ? "success" : displayedStatus === "Rejected" ? "destructive" : "warning"} />
            <Badge>{plan.allOrNothing ? "All-or-nothing execution" : "Partial execution"}</Badge>
            <Badge>Created {formatDateTime(plan.createdAt)}</Badge>
          </div>
          <p className="text-sm text-muted-foreground">{plan.summary}</p>
          <dl className="grid gap-3 sm:grid-cols-3">
            <Detail label="Workspace" value={plan.workspaceName} />
            <Detail label="Environment" value={environmentLabel(plan.targetEnvironmentId, data.applications)} />
            <Detail label="Engine" value={engineLabel(plan.targetEngineId, data.engines)} />
          </dl>
          <div className="grid gap-3 md:grid-cols-2">
            <ActionList title="Proposed actions" actions={plan.proposedActions} icon={<ClipboardCheck className="h-4 w-4 text-warning" />} />
            <ActionList title="Executed actions" actions={plan.executedActions.length > 0 ? plan.executedActions : ["No Elsa Control mutations executed."]} icon={<ShieldCheck className="h-4 w-4 text-success" />} muted={plan.executedActions.length === 0} />
          </div>
          <div className="rounded-ui border border-border bg-muted/30 p-3 text-sm">
            <span className="font-medium">Rollback path: </span>
            <span className="text-muted-foreground">{plan.rollbackPath}</span>
          </div>
        </div>
      </Panel>

      <div className="space-y-4">
        <ValidationPanel validations={plan.validations} />
        <Panel title="Decision" icon={<ShieldCheck className="h-4 w-4" />}>
          <div className="space-y-3">
            <p className="text-sm text-muted-foreground">
              Approval records the exact plan artifact, deciding account, workspace, environment, validations, and final outcome before execution is allowed.
            </p>
            <div className="flex flex-col gap-2 sm:flex-row">
              <Button disabled={blocked || outcome !== "Proposed"} onClick={onApprove}>
                <CheckCircle2 className="h-4 w-4" />
                Approve Plan
              </Button>
              <SecondaryButton disabled={outcome !== "Proposed"} onClick={onReject}>
                <XCircle className="h-4 w-4" />
                Reject Plan
              </SecondaryButton>
            </div>
            {blocked ? <p className="text-xs text-destructive">This proposed plan cannot execute while validation blockers remain.</p> : null}
            {outcome !== "Proposed" ? <div role="status" className="rounded-ui border border-border bg-muted/40 px-3 py-2 text-sm">Plan marked {outcome}; no executed actions were added by this local review.</div> : null}
          </div>
        </Panel>
      </div>
    </div>
  );
}

function ValidationPanel({ validations }: { validations: DeploymentCockpit["comparisons"][number]["validations"] }) {
  return (
    <Panel title="Validations" icon={<ClipboardCheck className="h-4 w-4" />}>
      <div className="space-y-2">
        {validations.map((validation) => (
          <div key={validation.id} className="flex gap-2 rounded-ui border border-border p-3 text-sm">
            {validation.severity === "Blocker" ? (
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            ) : validation.severity === "Warning" ? (
              <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-warning" />
            ) : (
              <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-success" />
            )}
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{validation.scope}</span>
                <StatusBadge value={validation.severity} tone={validationTone(validation.severity)} />
              </div>
              <p className="mt-1 text-muted-foreground">{validation.message}</p>
            </div>
          </div>
        ))}
      </div>
    </Panel>
  );
}

function DeploymentBlockersPanel({
  blockers,
  canManageDesiredState
}: {
  blockers: DeploymentBlocker[];
  canManageDesiredState: boolean;
}) {
  return (
    <Panel title="Deployment blockers" icon={<AlertTriangle className="h-4 w-4" />}>
      <div className="space-y-2">
        {blockers.map((blocker) => (
          <div key={blocker.id} className="flex gap-2 rounded-ui border border-border bg-background p-3 text-sm">
            <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{blocker.scope}</span>
                <Badge>{blocker.source}</Badge>
                <StatusBadge value={blocker.severity} tone={validationTone(blocker.severity)} />
              </div>
              <p className="mt-1 text-muted-foreground">{blocker.message}</p>
              {blocker.validationId === "deployment.tier.observability-required" ? (
                <div className="mt-3 space-y-3 rounded-ui border border-border bg-muted/30 p-3 text-xs text-muted-foreground">
                  <p>
                    Production promotion requires the source revision to declare where runtime telemetry will be sent.
                    Add at least one logs, metrics, traces, or console binding with a provider and scope.
                  </p>
                  {blocker.actionPath ? (
                    <Link
                      to={blocker.actionPath}
                      className={buttonClassName("secondary", !canManageDesiredState ? "pointer-events-none opacity-50" : undefined)}
                      {...permissionLinkProps(canManageDesiredState)}
                    >
                      <GitBranch className="h-4 w-4" />
                      {blocker.actionLabel ?? "Add binding to new revision"}
                    </Link>
                  ) : null}
                </div>
              ) : null}
            </div>
          </div>
        ))}
      </div>
    </Panel>
  );
}

function FormPageShell({
  title,
  description,
  breadcrumbs,
  children
}: {
  title: string;
  description: string;
  breadcrumbs: Array<{ label: string; to?: string }>;
  children: ReactNode;
}) {
  const backTarget = [...breadcrumbs].reverse().find((item) => item.to)?.to ?? "/admin/deployments";

  return (
    <section className="mx-auto max-w-5xl space-y-5">
      <Breadcrumbs items={breadcrumbs} />
      <PageHeader
        title={title}
        description={description}
        actions={
          <Link to={backTarget} className={buttonClassName("secondary")}>
            <ArrowLeft className="h-4 w-4" />
            Back
          </Link>
        }
      />
      {children}
    </section>
  );
}

function PageHeader({ title, description, actions }: { title: string; description: string; actions?: ReactNode }) {
  return (
    <div className="console-page-header flex flex-col gap-4 pb-4 pt-2 lg:flex-row lg:items-end lg:justify-between">
      <div>
        <h1 className="font-display text-3xl font-semibold">{title}</h1>
        {description && <p className="mt-2 max-w-2xl text-sm text-muted-foreground">{description}</p>}
      </div>
      {actions ? <div className="flex flex-wrap gap-2">{actions}</div> : null}
    </div>
  );
}

function SectionHeader({ title, description }: { title: string; description: string }) {
  return (
    <div>
      <h2 className="text-sm font-semibold">{title}</h2>
      {description && <p className="mt-1 text-xs text-muted-foreground">{description}</p>}
    </div>
  );
}

function Breadcrumbs({ items }: { items: Array<{ label: string; to?: string }> }) {
  return (
    <nav aria-label="Breadcrumb" className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
      {items.map((item, index) => (
        <span key={`${item.label}:${index}`} className="flex items-center gap-2">
          {item.to ? <Link to={item.to} className="hover:text-foreground">{item.label}</Link> : <span className="text-foreground">{item.label}</span>}
          {index < items.length - 1 ? <span>/</span> : null}
        </span>
      ))}
    </nav>
  );
}

function ActionList({ title, actions, icon, muted = false }: { title: string; actions: string[]; icon: ReactNode; muted?: boolean }) {
  return (
    <div className="rounded-ui border border-border p-3">
      <div className="mb-2 flex items-center gap-2 text-sm font-medium">{icon}{title}</div>
      <ul className={cn("space-y-1 text-sm", muted ? "text-muted-foreground" : "")}>
        {actions.map((action) => <li key={action}>{action}</li>)}
      </ul>
    </div>
  );
}

function MetricCard({ label, value, tone }: { label: string; value: string; tone?: StatusTone }) {
  return (
    <div className="rounded-ui border border-border bg-surface px-5 py-4">
      <div className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">{label}</div>
      <div className={cn("mt-2 font-display text-3xl font-medium", tone ? statusTextClass(tone) : "")}>{value}</div>
    </div>
  );
}

function Panel({ title, icon, children }: { title: string; icon: ReactNode; children: ReactNode }) {
  return (
    <section className="rounded-ui border border-border bg-surface p-5">
      <h2 className="mb-4 flex items-center gap-2 border-b border-border pb-4 font-display text-base font-semibold">{icon}{title}</h2>
      {children}
    </section>
  );
}

function Detail({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-xs font-medium text-muted-foreground">{label}</dt>
      <dd className="mt-1 min-w-0 break-words text-sm [overflow-wrap:anywhere]">{value || "-"}</dd>
    </div>
  );
}

function StatusBadge({ value, tone }: { value: string; tone: StatusTone }) {
  return <Badge className={statusToneClass(tone)}>{value}</Badge>;
}

function applicationPath(applicationId: string) {
  return `/admin/deployments/applications/${encodeURIComponent(applicationId)}`;
}

function applicationRevisionsPath(applicationId: string, query?: string) {
  const path = `${applicationPath(applicationId)}/revisions`;
  return query ? `${path}?${query}` : path;
}

function revisionDetailPath(applicationId: string, revisionId: string) {
  return `${applicationRevisionsPath(applicationId)}/${encodeURIComponent(revisionId)}`;
}

function environmentPath(applicationId: string, environmentId: string) {
  return `${applicationPath(applicationId)}/environments/${encodeURIComponent(environmentId)}`;
}

function newRevisionPathForEnvironment(data: DeploymentCockpit, environmentId: string, query?: string) {
  const application = data.applications.find((item) => item.environments.some((environment) => environment.id === environmentId));
  if (!application) return undefined;
  const path = `${environmentPath(application.id, environmentId)}/revisions/new`;
  return query ? `${path}?${query}` : path;
}

function enginePath(applicationId: string, environmentId: string, engineId: string) {
  return `${environmentPath(applicationId, environmentId)}/engines/${encodeURIComponent(engineId)}`;
}

function observabilityRevisionRecord({
  kind,
  provider,
  scope,
  sample
}: {
  kind: ObservabilityBinding["kind"];
  provider: string;
  scope: string;
  sample: string;
}): WorkspaceDesiredStateRecordRequest {
  return {
    kind: "ObservabilityBinding",
    name: `${kind} - ${provider.trim()}`,
    payload: {
      kind,
      provider: provider.trim(),
      scope: scope.trim(),
      sample: sample.trim() || null
    }
  };
}

function findApplication(data: DeploymentCockpit, applicationId: string) {
  return data.applications.find((application) => application.id === applicationId);
}

function resolveEnvironment(data: DeploymentCockpit, applicationId: string, environmentId: string) {
  const application = findApplication(data, applicationId);
  const environment = application?.environments.find((item) => item.id === environmentId);
  return { application, environment };
}

function enginesForApplication(data: DeploymentCockpit, application: DeploymentCockpit["applications"][number]) {
  const environmentIds = new Set(application.environments.map((environment) => environment.id));
  return data.engines.filter((engine) => environmentIds.has(engine.environmentId));
}

function enginesForEnvironment(data: DeploymentCockpit, environmentId: string) {
  return data.engines.filter((engine) => engine.environmentId === environmentId);
}

function filterApplications(applications: DeploymentCockpit["applications"], data: DeploymentCockpit, query: string) {
  const term = query.trim().toLowerCase();
  if (!term) return applications;
  return applications.filter((application) => {
    const engines = enginesForApplication(data, application);
    const haystack = [
      application.name,
      application.workspaceName,
      summarizeApplicationHealth(application, data.engines),
      ...application.environments.map((environment) => `${environment.name} ${environment.tierName ?? environment.tier}`),
      ...engines.map((engine) => engine.name)
    ].join(" ").toLowerCase();
    return haystack.includes(term);
  });
}

function sortApplications(applications: DeploymentCockpit["applications"], data: DeploymentCockpit, sort: ApplicationSort) {
  return [...applications].sort((left, right) => {
    const leftEngines = enginesForApplication(data, left);
    const rightEngines = enginesForApplication(data, right);
    switch (sort) {
      case "health":
        return compareText(summarizeApplicationHealth(left, data.engines), summarizeApplicationHealth(right, data.engines));
      case "environments":
        return compareNumber(right.environments.length, left.environments.length);
      case "engines":
        return compareNumber(rightEngines.length, leftEngines.length);
      case "drift":
        return compareNumber(
          right.environments.filter((environment) => environment.driftStatus === "DriftDetected").length,
          left.environments.filter((environment) => environment.driftStatus === "DriftDetected").length
        );
      default:
        return compareText(left.name, right.name);
    }
  });
}

function filterEnvironments(environments: EnvironmentSummary[], query: string) {
  const term = query.trim().toLowerCase();
  if (!term) return environments;
  return environments.filter((environment) =>
    [
      environment.name,
      environment.tierName ?? environment.tier,
      environment.health,
      environment.deploymentStatus,
      driftLabel(environment.driftStatus),
      ...(environment.tierCapabilities ?? [])
    ].join(" ").toLowerCase().includes(term)
  );
}

function sortEnvironments(environments: EnvironmentSummary[], sort: EnvironmentSort) {
  return [...environments].sort((left, right) => {
    switch (sort) {
      case "tier":
        return compareText(left.tierName ?? left.tier, right.tierName ?? right.tier);
      case "health":
        return compareText(left.health, right.health);
      case "deployment":
        return compareText(left.deploymentStatus, right.deploymentStatus);
      case "drift":
        return compareText(driftLabel(left.driftStatus), driftLabel(right.driftStatus));
      default:
        return compareText(left.name, right.name);
    }
  });
}

function filterEngines(engines: WorkflowEngineRegistration[], query: string) {
  const term = query.trim().toLowerCase();
  if (!term) return engines;
  return engines.filter((engine) =>
    [
      engine.name,
      engine.endpoint.baseUrl,
      engine.endpoint.region,
      engine.health,
      engine.credentialReference.provider,
      engine.credentialReference.reference,
      engine.credentialReference.verificationStatus,
      engine.hostingProvider ?? "",
      ...engine.capabilities.map((capability) => `${capability.label} ${capability.boundary}`),
      ...engine.controls.map((control) => `${control.label} ${control.boundary}`)
    ].join(" ").toLowerCase().includes(term)
  );
}

function sortEngines(engines: WorkflowEngineRegistration[], sort: EngineSort) {
  return [...engines].sort((left, right) => {
    switch (sort) {
      case "health":
        return compareText(left.health, right.health);
      case "verification":
        return compareText(left.credentialReference.verificationStatus, right.credentialReference.verificationStatus);
      case "heartbeat":
        return compareText(right.lastHeartbeatAt, left.lastHeartbeatAt);
      default:
        return compareText(left.name, right.name);
    }
  });
}

function filterRevisions(
  revisions: WorkspaceDesiredStateRevisionSummary[],
  query: string,
  environmentId: string,
  status: RevisionStatusFilter
) {
  const term = query.trim().toLowerCase();
  return revisions.filter((revision) => {
    if (environmentId !== "all" && revision.revision.environmentId !== environmentId) return false;
    if (!matchesRevisionStatus(revision, status)) return false;
    if (!term) return true;

    return [
      `r${revision.revision.revisionNumber}`,
      revision.revision.label,
      revision.revision.commit ?? "",
      revision.environmentName,
      revision.environmentTierName ?? revision.environmentTier,
      revisionStateLabel(revision),
      revision.latestRunStatus ?? ""
    ].join(" ").toLowerCase().includes(term);
  });
}

function sortRevisions(revisions: WorkspaceDesiredStateRevisionSummary[], sort: RevisionSort) {
  return [...revisions].sort((left, right) => {
    switch (sort) {
      case "environment":
        return compareText(left.environmentName, right.environmentName) || compareNumber(right.revision.revisionNumber, left.revision.revisionNumber);
      case "status":
        return compareText(revisionStateLabel(left), revisionStateLabel(right)) || compareText(right.revision.authoredAt, left.revision.authoredAt);
      default:
        return compareText(right.revision.authoredAt, left.revision.authoredAt) || compareNumber(right.revision.revisionNumber, left.revision.revisionNumber);
    }
  });
}

function matchesRevisionStatus(revision: WorkspaceDesiredStateRevisionSummary, status: RevisionStatusFilter) {
  if (status === "all") return true;
  if (status === "desired") return revision.isCurrentDesired;
  if (status === "deployed") return revision.isCurrentDeployed;
  if (status === "superseded") return !revision.isCurrentDesired && !revision.isCurrentDeployed && Boolean(revision.latestRunStatus);
  return !revision.isCurrentDesired && !revision.isCurrentDeployed && !revision.latestRunStatus;
}

function revisionStateLabel(revision: WorkspaceDesiredStateRevisionSummary) {
  if (revision.isCurrentDesired && revision.isCurrentDeployed) return "Desired + deployed";
  if (revision.isCurrentDesired) return "Current desired";
  if (revision.isCurrentDeployed) return "Currently deployed";
  if (revision.latestRunStatus) return "Superseded";
  return "Never deployed";
}

function parseRevisionStatusFilter(value: string | null): RevisionStatusFilter {
  return revisionStatusFilters.some((item) => item.value === value) ? value as RevisionStatusFilter : "all";
}

function RevisionStateBadge({ revision }: { revision: WorkspaceDesiredStateRevisionSummary }) {
  const tone: StatusTone = revision.isCurrentDesired || revision.isCurrentDeployed ? "success" : revision.latestRunStatus ? "warning" : "neutral";
  return <StatusBadge value={revisionStateLabel(revision)} tone={tone} />;
}

function formatJson(json: string) {
  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

function collectPromotionReadinessIssues(
  data: DeploymentCockpit,
  sourceEnvironmentId: string,
  targetEnvironmentId: string,
  canPreview: boolean,
  hasValidArtifacts: boolean
) {
  const issues: PromotionReadinessIssue[] = [];
  const source = findEnvironmentById(data, sourceEnvironmentId);
  const target = findEnvironmentById(data, targetEnvironmentId);
  const targetEngines = data.engines.filter((engine) => engine.environmentId === targetEnvironmentId);

  if (!canPreview) {
    issues.push({
      id: "permission.preview",
      scope: "Permission",
      severity: "Blocker",
      message: "Promotion preview permission is required for live validation."
    });
  }
  if (!source) {
    issues.push({
      id: "source.missing",
      scope: "Source environment",
      severity: "Blocker",
      message: "Choose a source environment before previewing promotion."
    });
  }
  if (!target) {
    issues.push({
      id: "target.missing",
      scope: "Target environment",
      severity: "Blocker",
      message: "Choose a target environment before previewing promotion."
    });
  }
  if (source && target && source.id === target.id) {
    issues.push({
      id: "selection.same-environment",
      scope: "Selection",
      severity: "Blocker",
      message: "Choose different source and target environments."
    });
  }
  if (source && !hasUsableDesiredRevision(source)) {
    const sourceApplication = data.applications.find((application) => application.environments.some((environment) => environment.id === source.id));
    issues.push({
      id: "source.revision-missing",
      scope: "Source revision",
      severity: "Blocker",
      message: `${source.name} does not have a desired-state revision yet. Create or choose a source revision before previewing promotion.`,
      action: hasValidArtifacts && sourceApplication
        ? {
            label: `Create ${source.name} revision`,
            to: `${environmentPath(sourceApplication.id, source.id)}/revisions/new`,
            description: "Use a verified artifact to author the source desired state."
          }
        : {
            label: "Upload artifact",
            to: "/admin/artifacts/new",
            description: "A valid artifact is required before this environment can receive a desired-state revision."
          }
    });
  }
  if (target && targetEngines.length === 0) {
    issues.push({
      id: "target.engine-missing",
      scope: "Target engine",
      severity: "Blocker",
      message: `${target.name} has no registered workflow engine. Register a target engine before previewing promotion.`
    });
  }
  if (source && !hasTierCapability(source, deploymentTierCapabilities.promotionSource)) {
    issues.push({
      id: "source.tier",
      scope: "Source tier",
      severity: "Blocker",
      message: `${source.tierName || source.tier} is not configured as a promotion source.`
    });
  }
  if (target && !hasTierCapability(target, deploymentTierCapabilities.promotionTarget)) {
    issues.push({
      id: "target.tier",
      scope: "Target tier",
      severity: "Blocker",
      message: `${target.tierName || target.tier} is not configured as a promotion target.`
    });
  }

  const unhealthyTargetEngine = targetEngines.find((engine) => engine.health !== "Healthy");
  if (unhealthyTargetEngine) {
    issues.push({
      id: `target.engine-health.${unhealthyTargetEngine.id}`,
      scope: "Target engine health",
      severity: "Warning",
      message: `${unhealthyTargetEngine.name} is ${unhealthyTargetEngine.health.toLowerCase()}. Promotion preview can run, but deployment may be blocked until the engine verifies as healthy.`
    });
  }

  return issues;
}

function assertPromotionReady(issues: PromotionReadinessIssue[]) {
  const blockers = issues.filter((issue) => issue.severity === "Blocker");
  if (blockers.length === 0) return;
  throw new Error(blockers.map((issue) => issue.message).join(" "));
}

function findEnvironmentById(data: DeploymentCockpit, environmentId: string) {
  return data.applications.flatMap((application) => application.environments).find((environment) => environment.id === environmentId);
}

function canPromoteFrom(environment: EnvironmentSummary | undefined) {
  return hasTierCapability(environment, deploymentTierCapabilities.promotionSource);
}

function canPromoteInto(environment: EnvironmentSummary | undefined) {
  return hasTierCapability(environment, deploymentTierCapabilities.promotionTarget);
}

function promotionModesFor(environment: EnvironmentSummary): PromotionMode[] {
  const modes: PromotionMode[] = [];
  if (canPromoteInto(environment)) modes.push("into-current");
  if (canPromoteFrom(environment)) modes.push("from-current");
  return modes;
}

function defaultPromotionMode(environment: EnvironmentSummary): PromotionMode {
  return canPromoteInto(environment) ? "into-current" : "from-current";
}

function normalizePromotionMode(environment: EnvironmentSummary, mode: PromotionMode): PromotionMode {
  const modes = promotionModesFor(environment);
  return modes.includes(mode) ? mode : defaultPromotionMode(environment);
}

function eligiblePromotionSources(
  application: DeploymentCockpit["applications"][number],
  targetEnvironment: EnvironmentSummary
) {
  return application.environments.filter((environment) =>
    environment.id !== targetEnvironment.id &&
    canPromoteFrom(environment) &&
    hasUsableDesiredRevision(environment)
  );
}

function eligiblePromotionTargets(
  application: DeploymentCockpit["applications"][number],
  sourceEnvironment: EnvironmentSummary,
  engines: WorkflowEngineRegistration[]
) {
  const environmentIdsWithEngines = new Set(engines.map((engine) => engine.environmentId));
  return application.environments.filter((environment) =>
    environment.id !== sourceEnvironment.id &&
    canPromoteInto(environment) &&
    environmentIdsWithEngines.has(environment.id)
  );
}

function defaultPromotionCounterpartId(
  data: DeploymentCockpit,
  application: DeploymentCockpit["applications"][number],
  environment: EnvironmentSummary,
  mode: PromotionMode
) {
  const options = mode === "from-current"
    ? eligiblePromotionTargets(application, environment, data.engines)
    : eligiblePromotionSources(application, environment);
  const comparison = mode === "from-current"
    ? data.comparisons.find((item) =>
        item.sourceEnvironmentId === environment.id &&
        options.some((option) => option.id === item.targetEnvironmentId))
    : data.comparisons.find((item) =>
        item.targetEnvironmentId === environment.id &&
        options.some((option) => option.id === item.sourceEnvironmentId));
  const comparisonCounterpartId = mode === "from-current"
    ? comparison?.targetEnvironmentId
    : comparison?.sourceEnvironmentId;
  return comparisonCounterpartId ?? options[0]?.id ?? "";
}

function hasUsableDesiredRevision(environment: EnvironmentSummary) {
  return Boolean(environment.desiredRevision.id) && environment.desiredRevision.revision > 0;
}

function isValidArtifactForRevision(artifact: WorkspaceArtifact) {
  return artifact.status !== "Archived" && artifact.inspectionStatus === "Valid" && artifact.checksumStatus === "Verified";
}

function collectDeploymentBlockers(
  data: DeploymentCockpit,
  environment: EnvironmentSummary,
  environmentEngines: WorkflowEngineRegistration[]
) {
  const blockers: DeploymentBlocker[] = [];

  for (const comparison of data.comparisons.filter((item) => item.targetEnvironmentId === environment.id)) {
    for (const validation of comparison.validations.filter((item) => item.severity === "Blocker")) {
      blockers.push({
        id: `comparison:${comparison.sourceEnvironmentId}:${comparison.sourceRevisionId}:${validation.id}`,
        validationId: validation.id,
        scope: validation.scope,
        message: validation.message,
        severity: validation.severity,
        source: `Promotion r${comparison.sourceRevision}`,
        actionPath: validation.id === "deployment.tier.observability-required"
          ? newRevisionPathForEnvironment(data, comparison.sourceEnvironmentId, `includeRequirement=${observabilityRequirementId}`)
          : undefined,
        actionLabel: validation.id === "deployment.tier.observability-required" ? "Add binding to new revision" : undefined
      });
    }

    for (const artifact of comparison.artifacts) {
      for (const validation of artifact.runtimeCompatibility.filter((item) => item.severity === "Blocker")) {
        blockers.push({
          id: `artifact:${comparison.sourceEnvironmentId}:${artifact.name}:${validation.id}`,
          scope: validation.scope,
          message: `${artifact.name}: ${validation.message}`,
          severity: validation.severity,
          source: "Runtime compatibility"
        });
      }
    }
  }

  for (const plan of data.assistantPlans.filter((item) => item.targetEnvironmentId === environment.id && item.status !== "Rejected")) {
    for (const validation of plan.validations.filter((item) => item.severity === "Blocker")) {
      blockers.push({
        id: `assistant:${plan.id}:${validation.id}`,
        scope: validation.scope,
        message: validation.message,
        severity: validation.severity,
        source: `Assistant plan v${plan.version}`
      });
    }
  }

  for (const engine of environmentEngines) {
    if (engine.health !== "Healthy") {
      blockers.push({
        id: `engine:${engine.id}:health`,
        scope: "Engine health",
        message: `${engine.name} is ${engine.health.toLowerCase()}.${engine.verificationMessage ? ` ${engine.verificationMessage}` : ""}`,
        severity: "Blocker",
        source: "Engine"
      });
    }
    if (engine.credentialReference.verificationStatus !== "Verified") {
      blockers.push({
        id: `engine:${engine.id}:credential`,
        scope: "Credential reference",
        message: `${engine.name} credential is ${engine.credentialReference.verificationStatus.toLowerCase()}.`,
        severity: "Blocker",
        source: "Engine"
      });
    }
  }

  if (environmentEngines.length === 0) {
    blockers.push({
      id: "environment:no-engines",
      scope: "Engine",
      message: "No workflow engine is registered for this environment.",
      severity: "Blocker",
      source: "Environment"
    });
  }

  if (environment.tierStatus === "Archived") {
    blockers.push({
      id: "environment:archived-tier",
      scope: "Tier",
      message: `${environment.tierName || environment.tier} is archived.`,
      severity: "Blocker",
      source: "Environment"
    });
  }

  for (const event of data.history.filter((item) => item.environmentId === environment.id && (item.status === "Blocked" || item.validationOutcome === "Blocked"))) {
    const diagnostics = (event.commands ?? []).flatMap((command) =>
      command.diagnostics.filter((diagnostic) => diagnostic.severity === "Error").map((diagnostic) => ({
        id: `history:${event.id}:${command.id}:${diagnostic.code}`,
        scope: command.action,
        message: diagnostic.message,
        severity: "Blocker" as const,
        source: `Run r${event.revision}`
      }))
    );

    if (diagnostics.length > 0) {
      blockers.push(...diagnostics);
    } else {
      blockers.push({
        id: `history:${event.id}:blocked`,
        scope: "Deployment run",
        message: `Run r${event.revision} was blocked with validation outcome ${event.validationOutcome.toLowerCase()}.`,
        severity: "Blocker",
        source: "Run history"
      });
    }
  }

  if (environment.deploymentStatus === "Blocked" && blockers.length === 0) {
    blockers.push({
      id: "environment:blocked-status",
      scope: "Deployment",
      message: "The backend reports this environment as blocked, but no structured validation detail is available yet. Refresh validation or inspect the latest run history.",
      severity: "Blocker",
      source: "Environment"
    });
  }

  return uniqueDeploymentBlockers(blockers);
}

function uniqueDeploymentBlockers(blockers: DeploymentBlocker[]) {
  const seen = new Set<string>();
  return blockers.filter((blocker) => {
    const key = `${blocker.scope}:${blocker.message}:${blocker.source}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function compareText(left?: string | null, right?: string | null) {
  return (left ?? "").localeCompare(right ?? "", undefined, { numeric: true, sensitivity: "base" });
}

function compareNumber(left: number, right: number) {
  return left - right;
}

function deploymentTotals(data: DeploymentCockpit) {
  const environments = data.applications.reduce((total, application) => total + application.environments.length, 0);
  const drift = data.applications.reduce(
    (total, application) => total + application.environments.filter((environment) => environment.driftStatus === "DriftDetected").length,
    0
  );
  return { environments, drift };
}

function summarizeApplicationHealth(application: DeploymentCockpit["applications"][number], engines: WorkflowEngineRegistration[]) {
  const environmentIds = new Set(application.environments.map((environment) => environment.id));
  const applicationEngines = engines.filter((engine) => environmentIds.has(engine.environmentId));
  if (application.environments.length === 0 || applicationEngines.length === 0) return "Needs setup";
  if (applicationEngines.some((engine) => engine.health === "Unreachable")) return "Unreachable";
  if (
    applicationEngines.some((engine) => engine.health === "Degraded") ||
    application.environments.some((environment) => environment.driftStatus !== "InSync" || environment.deploymentStatus === "Blocked")
  ) {
    return "Needs review";
  }
  const connectedEnvironmentIds = new Set(applicationEngines.map(engine => engine.environmentId));
  if (application.environments.some(environment => !connectedEnvironmentIds.has(environment.id))) return "Needs setup";
  return applicationEngines.every(engine => engine.health === "Healthy") ? "Healthy" : "Not checked";
}

function applicationHealthTone(application: DeploymentCockpit["applications"][number], engines: WorkflowEngineRegistration[]): StatusTone {
  const health = summarizeApplicationHealth(application, engines);
  if (health === "Healthy") return "success";
  if (health === "Not checked") return "neutral";
  if (health === "Needs review" || health === "Needs setup") return "warning";
  return "destructive";
}

function legacyTierFromName(name?: string): EnvironmentSummary["tier"] {
  if (name === "Dev" || name === "Test" || name === "Stage" || name === "Production") return name;
  return "Production";
}

function hasTierCapability(environment: EnvironmentSummary | undefined, capability: string) {
  if (!environment) return false;
  if (environment.tierCapabilities) return environment.tierCapabilities.includes(capability);

  if (capability === deploymentTierCapabilities.rollbackEnabled) return environment.tier === "Production";
  if (capability === deploymentTierCapabilities.promotionSource) return environment.tier !== "Production";
  if (capability === deploymentTierCapabilities.promotionTarget) return environment.tier !== "Dev";
  if (capability === deploymentTierCapabilities.confirmationRequired) return environment.tier === "Production";
  if (capability === deploymentTierCapabilities.productionLike) return environment.tier === "Production";
  return false;
}

function healthTone(health: DeploymentHealth): StatusTone {
  if (health === "Healthy") return "success";
  if (health === "Degraded") return "warning";
  return "destructive";
}

function driftTone(status: DriftStatus): StatusTone {
  if (status === "InSync") return "success";
  if (status === "DriftDetected") return "warning";
  return "neutral";
}

function deploymentTone(status: DeploymentStatus | string): StatusTone {
  if (status === "Succeeded") return "success";
  if (status === "Running" || status === "RolledBack" || status === "Queued" || status === "RecoveryRequired") return "warning";
  if (status === "Cancelled") return "neutral";
  return "destructive";
}

function deploymentRunTone(status: string): StatusTone {
  if (status === "Failed" || status === "Blocked") return "destructive";
  return deploymentTone(status);
}

function credentialTone(status: string): StatusTone {
  if (status === "Verified") return "success";
  if (status === "Unverified") return "warning";
  return "destructive";
}

function secretStoreTypeLabel(type: DeploymentSecretStoreType): string {
  return secretStoreTypeOptions.find((option) => option.value === type)?.label ?? type;
}

function slugify(value: string): string {
  const slug = value.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
  return slug.length > 0 ? slug : "credential";
}

function validationTone(status: ValidationSeverity): StatusTone {
  if (status === "Pass") return "success";
  if (status === "Warning") return "warning";
  return "destructive";
}

function driftLabel(status: DriftStatus) {
  return status === "InSync" ? "In sync" : status === "DriftDetected" ? "Drift detected" : "Unknown";
}

function statusTextClass(tone: StatusTone) {
  if (tone === "success") return "text-success";
  if (tone === "warning") return "text-warning";
  if (tone === "destructive") return "text-destructive";
  return "";
}
