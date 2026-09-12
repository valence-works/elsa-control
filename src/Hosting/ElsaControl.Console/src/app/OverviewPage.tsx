import {
  AlertTriangle,
  ArrowUpRight,
  Boxes,
  CheckCircle2,
  ChevronRight,
  Gauge,
  Network,
  Package,
  Search,
  Server
} from "lucide-react";
import { useState } from "react";
import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { Badge, buttonClassName, EmptyState, Input } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { listWorkspaceArtifacts } from "@/features/artifacts/artifactApi";
import { getDeploymentCockpit } from "@/features/deployments/deploymentApi";
import type {
  DeploymentCockpit,
  DeploymentHealth,
  EnvironmentSummary,
  WorkflowApplication,
  WorkflowEngineRegistration
} from "@/features/deployments/deploymentModels";
import { ApiError } from "@/lib/api/httpClient";
import { formatDateTime } from "@/lib/formatters";
import { queryKeys } from "@/lib/query/queryClient";
import { cn } from "@/lib/utils";

type EngineHealth = DeploymentHealth | "Unknown";
type EngineFilter = "all" | "attention" | "healthy";

type EngineContext = {
  engine: WorkflowEngineRegistration;
  application: WorkflowApplication | null;
  environment: EnvironmentSummary | null;
};

type AttentionSignal = {
  id: string;
  title: string;
  detail: string;
  tone: "warning" | "destructive" | "neutral";
  to?: string;
};

export function OverviewPage() {
  const { selectedWorkspaceId, selectedWorkspace } = useWorkspaceContext();
  const [engineFilter, setEngineFilter] = useState<EngineFilter>("all");
  const [engineSearch, setEngineSearch] = useState("");

  const cockpit = useQuery({
    queryKey: queryKeys.deploymentCockpit(selectedWorkspaceId ?? ""),
    queryFn: () => getDeploymentCockpit(selectedWorkspaceId as string),
    enabled: Boolean(selectedWorkspaceId)
  });
  const artifacts = useQuery({
    queryKey: queryKeys.artifacts(selectedWorkspaceId ?? ""),
    queryFn: () => listWorkspaceArtifacts(selectedWorkspaceId as string),
    enabled: Boolean(selectedWorkspaceId)
  });

  if (!selectedWorkspaceId) {
    return <RequestStateView state="empty" title="Select a workspace" description="Choose a workspace to inspect its deployment control plane." />;
  }

  if (cockpit.isPending) {
    return <OverviewLoading />;
  }

  if (cockpit.isError) {
    if (cockpit.error instanceof ApiError && cockpit.error.kind === "Forbidden") {
      return <RequestStateView state="unauthorized" title="Workspace access required" description="You do not have permission to view this workspace control plane." />;
    }

    return <RequestStateView state="unexpected" title="Overview unavailable" description="The workspace control plane could not be loaded. Try again when the API is available." />;
  }

  const data = cockpit.data;
  const engineContexts = buildEngineContexts(data);
  const attention = buildAttentionSignals(data, engineContexts);
  const filteredEngines = engineContexts.filter(({ engine, application, environment }) => {
    const health = engineHealth(engine);
    const matchesFilter = engineFilter === "all" || (engineFilter === "healthy" ? health === "Healthy" : health !== "Healthy");
    const query = engineSearch.trim().toLowerCase();
    const haystack = [
      engine.name,
      engine.id,
      application?.name,
      environment?.name,
      environment?.tierName,
      engine.endpoint?.region,
      engine.endpoint?.version,
      engine.hostingProvider
    ].filter(Boolean).join(" ").toLowerCase();
    return matchesFilter && (!query || haystack.includes(query));
  });
  const healthyEngineCount = data.engines.filter((engine) => engineHealth(engine) === "Healthy").length;
  const environmentCount = data.applications.reduce((count, application) => count + application.environments.length, 0);
  const workspaceName = selectedWorkspace?.name ?? data.applications[0]?.workspaceName ?? "Workspace";
  const isUnconfigured = data.engines.length === 0 && data.applications.length === 0;

  return (
    <section className="space-y-6 pb-8" aria-labelledby="overview-title">
      <OverviewHeader workspaceName={workspaceName} attentionCount={attention.length} engineCount={data.engines.length} />

      <OverviewMetricStrip
        applicationCount={data.applications.length}
        environmentCount={environmentCount}
        engineCount={data.engines.length}
        healthyEngineCount={healthyEngineCount}
        artifactCount={artifacts.data?.items.length}
      />

      {isUnconfigured ? <OverviewOnboarding /> : null}

      {!isUnconfigured ? <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_300px]">
        <section className="min-w-0 rounded-ui border border-border bg-surface" aria-labelledby="engine-fleet-title">
          <div className="flex flex-col gap-4 border-b border-border px-5 py-5 sm:flex-row sm:items-end sm:justify-between">
            <div>
              <h2 id="engine-fleet-title" className="mt-2 font-display text-xl font-semibold tracking-normal">Engine fleet</h2>
            </div>
            <Link to="/admin/deployments/applications" className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline">
              View applications <ArrowUpRight aria-hidden className="h-3.5 w-3.5" />
            </Link>
          </div>

          <div className="space-y-3 border-b border-border bg-background/40 px-5 py-4">
            {data.engines.length > 0 ? <>
              <label className="relative block">
                <Search aria-hidden className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
                <Input
                  aria-label="Search engines"
                  value={engineSearch}
                  onChange={(event) => setEngineSearch(event.target.value)}
                  placeholder="Search engines, environments, or regions"
                  className="pl-9"
                />
              </label>
              <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Engine filters">
                <EngineFilterButton active={engineFilter === "all"} onClick={() => setEngineFilter("all")}>
                  All <span className="text-muted-foreground">{data.engines.length}</span>
                </EngineFilterButton>
                <EngineFilterButton active={engineFilter === "attention"} onClick={() => setEngineFilter("attention")}>
                  Attention <span className="text-muted-foreground">{attentionEngineCount(data.engines)}</span>
                </EngineFilterButton>
                <EngineFilterButton active={engineFilter === "healthy"} onClick={() => setEngineFilter("healthy")}>
                  Healthy <span className="text-muted-foreground">{healthyEngineCount}</span>
                </EngineFilterButton>
              </div>
            </> : null}
          </div>

          <div className="divide-y divide-border">
            {data.engines.length === 0 ? <FleetEmptyState /> : filteredEngines.length > 0 ? filteredEngines.map((context) => <EngineRow key={context.engine.id} context={context} />) : (
              <div className="px-5 py-10 text-center">
                <Search aria-hidden className="mx-auto h-5 w-5 text-muted-foreground" />
                <p className="mt-3 text-sm font-medium">No engines match this view</p>
                <p className="mt-1 text-sm text-muted-foreground">Adjust the search or filter to see another part of the fleet.</p>
              </div>
            )}
          </div>
        </section>

        <AttentionPanel signals={attention} hasEngines={data.engines.length > 0} />
      </div> : null}

      {!isUnconfigured ? <EnvironmentTopology data={data} /> : null}
    </section>
  );
}

function OverviewHeader({ workspaceName, attentionCount, engineCount }: { workspaceName: string; attentionCount: number; engineCount: number }) {
  return (
    <header className="relative py-3 md:py-5">
      <div className="relative flex flex-col gap-5 lg:flex-row lg:items-end lg:justify-between">
        <div className="max-w-2xl">
          <p className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-[0.2em] text-primary">
            <span className="h-1.5 w-1.5 rounded-full bg-primary" aria-hidden />
            Workspace overview
          </p>
          <h1 id="overview-title" className="mt-3 font-display text-3xl font-semibold tracking-tight md:text-4xl">{workspaceName}</h1>
        </div>
        <div className="flex flex-wrap items-center gap-3">
          <span className={cn("inline-flex items-center gap-2 rounded-full border px-3 py-1.5 text-xs", engineCount === 0 ? "border-border bg-background text-muted-foreground" : attentionCount > 0 ? "border-warning/30 bg-warning/10 text-warning" : "border-success/30 bg-success/10 text-success")}>
            {engineCount === 0 ? <Server aria-hidden className="h-3.5 w-3.5" /> : attentionCount > 0 ? <AlertTriangle aria-hidden className="h-3.5 w-3.5" /> : <CheckCircle2 aria-hidden className="h-3.5 w-3.5" />}
            {engineCount === 0 ? "Fleet not connected" : attentionCount > 0 ? `${attentionCount} signal${attentionCount === 1 ? "" : "s"} need attention` : "All signals nominal"}
          </span>
          <Link to="/admin/engines/connect" className={buttonClassName("primary", "h-10 bg-primary text-primary-foreground hover:bg-primary/90")}>
            <Server aria-hidden className="h-4 w-4" />
            Connect engine
          </Link>
        </div>
      </div>
    </header>
  );
}

function OverviewOnboarding() {
  return (
    <section className="relative overflow-hidden border-y border-border py-12 md:py-16" aria-labelledby="onboarding-title">
      <div className="pointer-events-none absolute -right-16 top-1/2 h-64 w-64 -translate-y-1/2 rounded-full bg-primary/10 blur-3xl" />
      <div className="relative mx-auto max-w-xl text-center">
        <span className="mx-auto grid h-12 w-12 place-items-center rounded-full border border-primary/20 bg-primary/10 text-primary"><Server aria-hidden className="h-5 w-5" /></span>
        <h2 id="onboarding-title" className="mt-5 font-display text-2xl font-semibold tracking-tight">Connect your first engine</h2>
        <p className="mx-auto mt-2 max-w-md text-sm leading-6 text-muted-foreground">Start the workspace control plane with an engine endpoint and its credential reference.</p>
        <Link to="/admin/engines/connect" className={cn(buttonClassName("primary"), "mt-6 bg-primary text-primary-foreground")}>Connect engine <ArrowUpRight aria-hidden className="h-3.5 w-3.5" /></Link>
      </div>
    </section>
  );
}

function OverviewMetricStrip({ applicationCount, environmentCount, engineCount, healthyEngineCount, artifactCount }: {
  applicationCount: number;
  environmentCount: number;
  engineCount: number;
  healthyEngineCount: number;
  artifactCount?: number;
}) {
  const metrics = [
    { label: "Applications", value: applicationCount, icon: Boxes, to: "/admin/deployments/applications", detail: "registered" },
    { label: "Environments", value: environmentCount, icon: Network, to: "/admin/deployments", detail: "mapped" },
    { label: "Engines healthy", value: `${healthyEngineCount}/${engineCount}`, icon: Gauge, to: "/admin/deployments/applications", detail: engineCount === 1 ? "engine" : "engines" },
    { label: "Artifacts", value: artifactCount === undefined ? "—" : artifactCount, icon: Package, to: "/admin/artifacts", detail: "registered" }
  ];

  return (
    <div className="grid grid-cols-2 border-y border-border md:grid-cols-4" aria-label="Workspace metrics">
      {metrics.map((metric) => {
        const Icon = metric.icon;
        return (
          <Link key={metric.label} to={metric.to} className="group border-b border-border px-3 py-3 transition-colors hover:bg-muted/30 last:border-b-0 md:border-b-0 md:border-r md:px-4 md:last:border-r-0">
            <div className="flex items-start justify-between gap-3">
              <span className="text-[11px] font-semibold uppercase tracking-[0.13em] text-muted-foreground">{metric.label}</span>
              <Icon aria-hidden className="h-4 w-4 shrink-0 text-primary" />
            </div>
            <p className="mt-2 font-display text-xl font-semibold tracking-tight">{metric.value}</p>
            <p className="mt-0.5 text-xs text-muted-foreground">{metric.detail}</p>
          </Link>
        );
      })}
    </div>
  );
}

function EngineFilterButton({ active, children, onClick }: { active: boolean; children: ReactNode; onClick: () => void }) {
  return (
    <button type="button" aria-pressed={active} onClick={onClick} className={cn("inline-flex h-8 items-center gap-1.5 rounded-full border px-3 text-xs font-medium transition-colors", active ? "border-primary/40 bg-primary/10 text-primary" : "border-border bg-background text-muted-foreground hover:bg-muted hover:text-foreground")}>
      {children}
    </button>
  );
}

function FleetEmptyState() {
  return <div className="px-5 py-10"><div className="mx-auto max-w-sm text-center"><span className="mx-auto grid h-10 w-10 place-items-center rounded-full border border-primary/20 bg-primary/10 text-primary"><Server aria-hidden className="h-5 w-5" /></span><p className="mt-4 text-sm font-medium">No engines connected</p><p className="mt-1 text-sm leading-5 text-muted-foreground">Connect an engine to start receiving health and deployment signals.</p><Link to="/admin/engines/connect" className={cn(buttonClassName("primary"), "mt-5 bg-primary text-primary-foreground")}>Connect engine <ArrowUpRight aria-hidden className="h-3.5 w-3.5" /></Link></div></div>;
}

function EngineRow({ context }: { context: EngineContext }) {
  const { engine, application, environment } = context;
  const health = engineHealth(engine);
  const enginePath = application && environment ? `/admin/deployments/applications/${encodeURIComponent(application.id)}/environments/${encodeURIComponent(environment.id)}/engines/${encodeURIComponent(engine.id)}` : undefined;
  const content = (
    <div className="flex flex-col gap-4 px-5 py-4 transition-colors group-hover:bg-muted/20 md:flex-row md:items-center md:justify-between">
      <div className="flex min-w-0 items-start gap-3">
        <span className={cn("mt-1.5 h-2 w-2 shrink-0 rounded-full", healthDotClass(health))} aria-label={`Engine health: ${health}`} />
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <p className="truncate font-medium">{engine.name || engine.id}</p>
            <HealthBadge health={health} />
          </div>
          <p className="mt-1 truncate text-sm text-muted-foreground">
            {application?.name ?? "Unmapped application"} <span className="px-1 text-border">/</span> {environment?.name ?? "Unmapped environment"}
          </p>
        </div>
      </div>
      <div className="grid grid-cols-2 gap-x-7 gap-y-2 text-xs md:min-w-[260px] md:grid-cols-3">
        <EngineDetail label="Region" value={engine.endpoint?.region || "Not reported"} />
        <EngineDetail label="Version" value={engine.endpoint?.version || "Not reported"} />
        <EngineDetail label="Heartbeat" value={heartbeatLabel(engine.lastHeartbeatAt)} />
      </div>
      <ChevronRight aria-hidden className="hidden h-4 w-4 shrink-0 text-muted-foreground md:block" />
    </div>
  );

  return enginePath ? <Link to={enginePath} className="group block focus:outline-none focus:ring-2 focus:ring-inset focus:ring-primary/50" aria-label={`Open ${engine.name || engine.id}`}>{content}</Link> : <div>{content}</div>;
}

function EngineDetail({ label, value }: { label: string; value: string }) {
  return <div className="min-w-0"><p className="uppercase tracking-[0.1em] text-[10px] text-muted-foreground">{label}</p><p className="mt-1 truncate font-mono text-[11px] text-foreground/80">{value}</p></div>;
}

function HealthBadge({ health }: { health: EngineHealth }) {
  const tone = health === "Healthy" ? "border-success/30 bg-success/10 text-success" : health === "Unknown" ? "border-border bg-background text-muted-foreground" : health === "Degraded" ? "border-warning/30 bg-warning/10 text-warning" : "border-destructive/30 bg-destructive/10 text-destructive";
  return <Badge className={tone}>{health}</Badge>;
}

function AttentionPanel({ signals, hasEngines }: { signals: AttentionSignal[]; hasEngines: boolean }) {
  return (
    <section className="rounded-ui border border-border bg-surface" aria-labelledby="attention-title">
      <div className="flex items-start justify-between gap-3 border-b border-border px-5 py-5">
        <div>
          <h2 id="attention-title" className="font-display text-xl font-semibold tracking-normal">Attention</h2>
        </div>
        <span className="rounded-full border border-border px-2 py-1 font-mono text-[11px] text-muted-foreground">{signals.length}</span>
      </div>
      {signals.length === 0 ? (
        <div className="px-5 py-8">{hasEngines ? <CheckCircle2 aria-hidden className="h-5 w-5 text-success" /> : <Gauge aria-hidden className="h-5 w-5 text-muted-foreground" />}<p className="mt-3 text-sm font-medium">{hasEngines ? "No active signals" : "No health signals yet"}</p><p className="mt-1 text-sm leading-5 text-muted-foreground">{hasEngines ? "The current cockpit data reports healthy engines and in-sync environments." : "Connect an engine to begin verification."}</p></div>
      ) : (
        <div className="divide-y divide-border">
          {signals.slice(0, 6).map((signal) => {
            const item = <div className="flex gap-3 px-5 py-4 transition-colors group-hover:bg-muted/20"><span className={cn("mt-1 h-2 w-2 shrink-0 rounded-full", signal.tone === "destructive" ? "bg-destructive" : signal.tone === "warning" ? "bg-warning" : "bg-muted-foreground")} /><div className="min-w-0"><p className="text-sm font-medium">{signal.title}</p><p className="mt-1 max-h-10 overflow-hidden text-xs leading-5 text-muted-foreground">{signal.detail}</p></div><ChevronRight aria-hidden className="mt-0.5 ml-auto h-4 w-4 shrink-0 text-muted-foreground" /></div>;
            return signal.to ? <Link key={signal.id} to={signal.to} className="group block focus:outline-none focus:ring-2 focus:ring-inset focus:ring-primary/50">{item}</Link> : <div key={signal.id}>{item}</div>;
          })}
        </div>
      )}
      {signals.length > 6 ? <p className="border-t border-border px-5 py-3 text-xs text-muted-foreground">Showing 6 of {signals.length} signals.</p> : null}
    </section>
  );
}

function EnvironmentTopology({ data }: { data: DeploymentCockpit }) {
  return (
    <section className="rounded-ui border border-border bg-surface" aria-labelledby="environment-topology-title">
      <div className="flex flex-col gap-2 border-b border-border px-5 py-5 sm:flex-row sm:items-end sm:justify-between">
        <div><h2 id="environment-topology-title" className="font-display text-xl font-semibold tracking-normal">Applications and environments</h2></div>
        <p className="text-xs text-muted-foreground">{data.applications.length} application{data.applications.length === 1 ? "" : "s"} · {data.applications.reduce((count, app) => count + app.environments.length, 0)} environment{data.applications.reduce((count, app) => count + app.environments.length, 0) === 1 ? "" : "s"}</p>
      </div>
      {data.applications.length === 0 ? <div className="px-5 py-8"><EmptyState title="No applications registered" description="Create an application to map environments and connect an engine." action={<Link to="/admin/deployments/applications" className={buttonClassName("secondary")}>Open applications</Link>} /></div> : <div className="grid gap-3 p-4 md:grid-cols-2 xl:grid-cols-3">{data.applications.map((application) => <ApplicationTopologyCard key={application.id} application={application} engines={data.engines} />)}</div>}
    </section>
  );
}

function ApplicationTopologyCard({ application, engines }: { application: WorkflowApplication; engines: WorkflowEngineRegistration[] }) {
  const environmentEngineIds = new Set(application.environments.flatMap((environment) => environment.engineIds ?? []));
  const appEngines = engines.filter((engine) => environmentEngineIds.has(engine.id));
  const attention = application.environments.filter((environment) => {
    const engineCount = environment.engineIds?.length ?? appEngines.filter((engine) => engine.environmentId === environment.id).length;
    return engineCount === 0 || environment.health !== "Healthy" || environment.driftStatus !== "InSync" || environment.deploymentStatus === "Blocked";
  }).length;
  const hasEnvironments = application.environments.length > 0;
  return (
    <Link to={`/admin/deployments/applications/${encodeURIComponent(application.id)}`} className="group min-w-0 rounded-ui border border-border bg-background/50 p-4 transition-colors hover:border-primary/40 hover:bg-muted/20">
      <div className="flex flex-wrap items-start justify-between gap-3"><div className="flex min-w-0 flex-1 items-center gap-2"><span className="grid h-7 w-7 shrink-0 place-items-center rounded-ui border border-primary/20 bg-primary/10 text-primary"><Boxes aria-hidden className="h-3.5 w-3.5" /></span><span className="truncate font-medium">{application.name}</span></div>{!hasEnvironments ? <Badge aria-label={`${application.name}: no environments`} className="border-border bg-background text-muted-foreground">No environments</Badge> : attention > 0 ? <Badge className="border-warning/30 bg-warning/10 text-warning">{attention} attention</Badge> : <CheckCircle2 aria-label="Healthy" className="h-4 w-4 shrink-0 text-success" />}</div>
      <div className="mt-4 space-y-2">
        {hasEnvironments ? application.environments.map((environment) => <EnvironmentRow key={environment.id} environment={environment} engineCount={environment.engineIds?.length ?? appEngines.filter((engine) => engine.environmentId === environment.id).length} />) : <p className="border-t border-border/70 pt-2.5 text-xs text-muted-foreground">Add an environment to map deployment state.</p>}
      </div>
      <p className="mt-4 flex items-center gap-1 text-xs font-medium text-primary opacity-80 group-hover:opacity-100">Open application <ArrowUpRight aria-hidden className="h-3.5 w-3.5" /></p>
    </Link>
  );
}

function EnvironmentRow({ environment, engineCount }: { environment: EnvironmentSummary; engineCount: number }) {
  const state = environmentState(environment, engineCount);
  return <div className="flex flex-wrap items-center justify-between gap-2 border-t border-border/70 pt-2.5 text-xs"><div className="flex min-w-0 flex-1 items-center gap-2"><span className={cn("h-1.5 w-1.5 shrink-0 rounded-full", state === "No engine" ? "bg-muted-foreground" : healthDotClass(environment.health ?? "Unknown"))} /><span className="truncate">{environment.name}</span><span className="shrink-0 text-muted-foreground">{environment.tierName ?? environment.tier}</span></div><div className="flex shrink-0 items-center gap-2 text-muted-foreground"><span>{engineCount} engine{engineCount === 1 ? "" : "s"}</span><span className={cn("font-medium", state === "Healthy" ? "text-success" : "text-warning")}>{state}</span></div></div>;
}

function OverviewLoading() {
  return <section className="space-y-5" aria-label="Loading workspace overview"><div className="h-44 animate-pulse rounded-ui border border-border bg-surface" /><div className="grid grid-cols-2 gap-3 md:grid-cols-4">{Array.from({ length: 4 }, (_, index) => <div key={index} className="h-28 animate-pulse rounded-ui border border-border bg-surface" />)}</div><div className="h-96 animate-pulse rounded-ui border border-border bg-surface" /></section>;
}

function buildEngineContexts(data: DeploymentCockpit): EngineContext[] {
  const environments = new Map<string, { application: WorkflowApplication; environment: EnvironmentSummary }>();
  for (const application of data.applications) for (const environment of application.environments) environments.set(environment.id, { application, environment });
  return data.engines.map((engine) => ({ engine, application: environments.get(engine.environmentId)?.application ?? null, environment: environments.get(engine.environmentId)?.environment ?? null }));
}

function buildAttentionSignals(data: DeploymentCockpit, contexts: EngineContext[]): AttentionSignal[] {
  const signals: AttentionSignal[] = [];
  for (const { engine, application, environment } of contexts) {
    const name = engine.name || engine.id;
    const enginePath = application && environment ? `/admin/deployments/applications/${encodeURIComponent(application.id)}/environments/${encodeURIComponent(environment.id)}/engines/${encodeURIComponent(engine.id)}` : undefined;
    const health = engineHealth(engine);
    if (health !== "Healthy") signals.push({ id: `engine-health-${engine.id}`, title: `${name}: ${health === "Unknown" ? "health unavailable" : health}`, detail: engine.verificationMessage || "The latest engine health signal needs review.", tone: health === "Unreachable" ? "destructive" : "warning", to: enginePath });
    const credentialStatus = engine.credentialReference?.verificationStatus;
    if (credentialStatus && !["Verified", "NotVerifiable"].includes(credentialStatus)) signals.push({ id: `engine-credential-${engine.id}`, title: `${name}: credentials ${credentialStatus.toLowerCase()}`, detail: "Verify or update the credential reference before the next deployment.", tone: credentialStatus === "Expired" ? "destructive" : "warning", to: enginePath });
    const certificateStatus = engine.endpoint?.certificateStatus;
    if (certificateStatus && certificateStatus !== "Trusted") signals.push({ id: `engine-certificate-${engine.id}`, title: `${name}: certificate ${certificateStatus.toLowerCase()}`, detail: certificateStatus === "Expiring" ? "The endpoint certificate is nearing expiry; rotate it before the next deployment." : "The endpoint certificate is not trusted by the control plane.", tone: certificateStatus === "Untrusted" ? "destructive" : "warning", to: enginePath });
    if (environment && (environment.driftStatus !== "InSync" || environment.deploymentStatus === "Blocked")) signals.push({ id: `environment-${environment.id}`, title: `${application?.name ?? "Environment"} / ${environment.name}: ${environment.driftStatus === "DriftDetected" ? "drift detected" : environment.deploymentStatus === "Blocked" ? "deployment blocked" : "state unknown"}`, detail: `Desired revision ${environment.desiredRevision?.revision ?? "not set"}${environment.deployedRevision == null ? " has not been deployed" : ` · deployed revision ${environment.deployedRevision}`}.`, tone: environment.deploymentStatus === "Blocked" ? "destructive" : "warning", to: application ? `/admin/deployments/applications/${encodeURIComponent(application.id)}/environments/${encodeURIComponent(environment.id)}` : undefined });
  }
  for (const item of data.driftReport) {
    const environment = findEnvironment(data.applications, item.environmentId);
    const engine = data.engines.find((candidate) => candidate.id === item.engineId);
    signals.push({ id: `drift-${item.id}`, title: `${environment?.application.name ?? "Environment"} / ${environment?.environment.name ?? item.environmentId}: ${item.area} drift`, detail: `${item.desired} expected · ${item.observed} observed.`, tone: "warning", to: engine && environment ? `/admin/deployments/applications/${encodeURIComponent(environment.application.id)}/environments/${encodeURIComponent(environment.environment.id)}/engines/${encodeURIComponent(engine.id)}` : undefined });
  }
  for (const application of data.applications) for (const environment of application.environments) {
    const engineCount = environment.engineIds?.length ?? contexts.filter((context) => context.environment?.id === environment.id).length;
    if (engineCount === 0) signals.push({ id: `environment-engine-${environment.id}`, title: `${application.name} / ${environment.name}: no engine connected`, detail: "Connect an engine before expecting health or deployment signals.", tone: "warning", to: `/admin/deployments/applications/${encodeURIComponent(application.id)}/environments/${encodeURIComponent(environment.id)}` });
  }
  return signals;
}

function findEnvironment(applications: WorkflowApplication[], environmentId: string) {
  for (const application of applications) { const environment = application.environments.find((item) => item.id === environmentId); if (environment) return { application, environment }; }
  return undefined;
}

function attentionEngineCount(engines: WorkflowEngineRegistration[]) { return engines.filter((engine) => engineHealth(engine) !== "Healthy").length; }

function engineHealth(engine: WorkflowEngineRegistration): EngineHealth { return engine.health ?? "Unknown"; }

function environmentState(environment: EnvironmentSummary, engineCount: number) {
  if (engineCount === 0) return "No engine";
  if (environment.health === "Unreachable" || environment.deploymentStatus === "Blocked") return "Blocked";
  if (environment.health === "Degraded" || environment.driftStatus === "DriftDetected" || environment.driftStatus === "Unknown") return "Needs review";
  if (environment.health === "Healthy") return "Healthy";
  return "Unknown";
}

function healthDotClass(health: EngineHealth | DeploymentHealth) { return health === "Healthy" ? "bg-success" : health === "Unreachable" ? "bg-destructive" : health === "Degraded" ? "bg-warning" : "bg-muted-foreground"; }

function heartbeatLabel(value: string | null | undefined) {
  if (!value) return "Not reported";
  const timestamp = new Date(value);
  if (Number.isNaN(timestamp.getTime())) return "Not reported";
  return formatDateTime(value);
}
