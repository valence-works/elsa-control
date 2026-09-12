import "./overview.css";

import { ArrowUpRight, CircleAlert, ChevronRight, Search, Server, ShieldCheck } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { getDeploymentCockpit } from "@/features/deployments/deploymentApi";
import type {
  DeploymentCockpit,
  DeploymentHealth,
  EnvironmentSummary,
  WorkflowApplication,
  WorkflowEngineRegistration
} from "@/features/deployments/deploymentModels";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";

type EngineHealth = DeploymentHealth | "Unknown";
type EngineFilter = "all" | "attention" | "healthy";

type EngineContext = {
  engine: WorkflowEngineRegistration;
  application: WorkflowApplication | null;
  environment: EnvironmentSummary | null;
};

export function OverviewPage() {
  const { selectedWorkspaceId, selectedWorkspace } = useWorkspaceContext();
  const [engineSearch, setEngineSearch] = useState("");
  const [engineFilter, setEngineFilter] = useState<EngineFilter>("all");
  const [selectedEngineId, setSelectedEngineId] = useState<string | null>(null);
  const cockpit = useQuery({
    queryKey: queryKeys.deploymentCockpit(selectedWorkspaceId ?? ""),
    queryFn: () => getDeploymentCockpit(selectedWorkspaceId as string),
    enabled: Boolean(selectedWorkspaceId)
  });
  const data = cockpit.data;
  const contexts = useMemo(() => data ? buildEngineContexts(data) : [], [data]);
  const filteredContexts = useMemo(
    () => filterEngines(contexts, engineSearch, engineFilter),
    [contexts, engineFilter, engineSearch]
  );
  const selectedContext = filteredContexts.find(({ engine }) => engine.id === selectedEngineId) ?? filteredContexts[0] ?? null;
  const workspaceName = selectedWorkspace?.name ?? data?.applications[0]?.workspaceName ?? "Workspace";

  useEffect(() => {
    setEngineSearch("");
    setEngineFilter("all");
    setSelectedEngineId(null);
  }, [selectedWorkspaceId]);

  if (!selectedWorkspaceId) {
    return <RequestStateView state="empty" title="Select a workspace" description="Choose a workspace to inspect its deployment control plane." />;
  }

  if (cockpit.isPending) {
    return <ApertureLoading />;
  }

  if (cockpit.isError) {
    if (cockpit.error instanceof ApiError && cockpit.error.kind === "Forbidden") {
      return <RequestStateView state="unauthorized" title="Workspace access required" description="You do not have permission to view this workspace control plane." />;
    }

    return <RequestStateView state="unexpected" title="Overview unavailable" description="The workspace control plane could not be loaded. Try again when the API is available." />;
  }

  if (!data) {
    return <RequestStateView state="unexpected" title="Overview unavailable" description="The workspace control plane returned no data." />;
  }

  return (
    <div className="aperture-overview">
      <div className="aperture-overview__main">
        <header className="aperture-overview__heading">
          <div>
            <p className="aperture-overview__kicker"><span className="aperture-overview__square" aria-hidden />WORKSPACE</p>
            <h1>{workspaceName}</h1>
            <p>{data.engines.length} engine{data.engines.length === 1 ? "" : "s"} · {data.applications.length} application{data.applications.length === 1 ? "" : "s"}</p>
          </div>
          <Link to="/admin/engines/connect" className="aperture-overview__primary">
            Connect engine <ArrowUpRight aria-hidden />
          </Link>
        </header>

        {data.engines.length === 0 ? (
          <ApertureEmptyState />
        ) : (
          <div className="aperture-overview__workspace">
            <EngineInventory
              contexts={filteredContexts}
              totalCount={contexts.length}
              search={engineSearch}
              filter={engineFilter}
              selectedEngineId={selectedContext?.engine.id ?? null}
              onSearchChange={setEngineSearch}
              onFilterChange={setEngineFilter}
              onSelect={setSelectedEngineId}
            />
            <EngineInspector context={selectedContext} />
          </div>
        )}

        <footer className="aperture-overview__footer">
          <span>{workspaceName}</span>
        </footer>
      </div>
    </div>
  );
}

function EngineInventory({
  contexts,
  totalCount,
  search,
  filter,
  selectedEngineId,
  onSearchChange,
  onFilterChange,
  onSelect
}: {
  contexts: EngineContext[];
  totalCount: number;
  search: string;
  filter: EngineFilter;
  selectedEngineId: string | null;
  onSearchChange: (value: string) => void;
  onFilterChange: (value: EngineFilter) => void;
  onSelect: (engineId: string) => void;
}) {
  return (
    <section className="aperture-overview__inventory" aria-label="Engine inventory">
      <div className="aperture-overview__section-head">
        <h2>Engine inventory</h2>
        <span className="aperture-overview__count">{String(contexts.length).padStart(2, "0")} / {String(totalCount).padStart(2, "0")} ENGINES</span>
      </div>
      <div className="aperture-overview__tools">
        <label className="aperture-overview__search">
          <Search aria-hidden />
          <span className="aperture-overview__sr-only">Search engines</span>
          <input
            type="search"
            aria-label="Search engines"
            value={search}
            onChange={(event) => onSearchChange(event.target.value)}
            placeholder="Search inventory"
          />
        </label>
        <label className="aperture-overview__filter">
          <span className="aperture-overview__sr-only">Filter engines</span>
          <select aria-label="Filter engines" value={filter} onChange={(event) => onFilterChange(event.target.value as EngineFilter)}>
            <option value="all">All states</option>
            <option value="attention">Needs attention</option>
            <option value="healthy">Healthy only</option>
          </select>
        </label>
      </div>
      {contexts.length > 0 ? (
        <div className="aperture-overview__rows">
          {contexts.map((context, index) => (
            <EngineRow
              key={context.engine.id}
              context={context}
              index={index}
              selected={context.engine.id === selectedEngineId}
              onSelect={onSelect}
            />
          ))}
        </div>
      ) : (
        <div className="aperture-overview__no-results" role="status">
          <p>No engines match this view.</p>
          <span>Adjust the search or health filter.</span>
        </div>
      )}
    </section>
  );
}

function EngineRow({ context, index, selected, onSelect }: { context: EngineContext; index: number; selected: boolean; onSelect: (engineId: string) => void }) {
  const { engine, application, environment } = context;
  const name = engine.name || engine.id;
  return (
    <button
      type="button"
      className={`aperture-overview__row${selected ? " is-selected" : ""}`}
      aria-label={`Inspect ${name}`}
      aria-describedby={`engine-placement-${engine.id} engine-health-${engine.id}`}
      aria-pressed={selected}
      onClick={() => onSelect(engine.id)}
    >
      <span className="aperture-overview__index">{String(index + 1).padStart(2, "0")}</span>
      <span className="aperture-overview__identity">
        <span className="aperture-overview__name">{name}</span>
        <span id={`engine-placement-${engine.id}`} className="aperture-overview__sub">{application?.name ?? "Unmapped application"} / {environment?.name ?? "Unmapped environment"}</span>
      </span>
      <span id={`engine-health-${engine.id}`}><HealthStatus health={engineHealth(engine)} /></span>
      <ChevronRight aria-hidden />
    </button>
  );
}

function EngineInspector({ context }: { context: EngineContext | null }) {
  return (
    <aside className="aperture-overview__inspector" aria-label="Selected engine">
      <p className="aperture-overview__mono aperture-overview__inspector-label">Selected engine</p>
      {context ? <SelectedEngine context={context} /> : <div className="aperture-overview__inspector-empty" role="status"><Server aria-hidden /><p>No engine selected</p><span>Adjust the inventory filters to inspect an engine.</span></div>}
    </aside>
  );
}

function SelectedEngine({ context }: { context: EngineContext }) {
  const { engine, application, environment } = context;
  const name = engine.name || engine.id;
  const detailPath = application && environment ? enginePath(application, environment, engine.id) : null;
  const certificateStatus = engine.endpoint?.certificateStatus ?? "Unknown";
  const credentialStatus = engine.credentialAssignmentStatus === "Deferred"
    ? "Deferred"
    : engine.credentialReference?.verificationStatus ?? "Unknown";
  const certificateDetail = certificateStatus === "Trusted" ? "Trusted" : certificateStatus === "Expiring" ? "Expiring" : certificateStatus === "Untrusted" ? "Untrusted" : "Not reported";
  const showDiagnostic = engineHealth(engine) !== "Healthy" || !["Verified", "NotVerifiable", "Deferred"].includes(credentialStatus);

  return (
    <>
      <h2>{name}</h2>
      <HealthStatus health={engineHealth(engine)} />
      <dl className="aperture-overview__details">
        <Detail label="Environment" value={environment ? environmentLabel(environment) : "Unmapped"} />
        <Detail label="Version" value={engine.endpoint?.version || "Not reported"} mono />
        <Detail label="Endpoint" value={engine.endpoint?.baseUrl || "Not reported"} />
        <Detail label="Credential" value={credentialStatus} />
      </dl>
      <div className={`aperture-overview__certificate${certificateStatus === "Trusted" ? " is-trusted" : ""}`}>
        {certificateStatus === "Trusted" ? <ShieldCheck aria-hidden /> : <CircleAlert aria-hidden />}
        <span>Certificate<br />{certificateDetail}</span>
      </div>
      {showDiagnostic && engine.verificationMessage ? <p className="aperture-overview__verification">{engine.verificationMessage}</p> : null}
      <div className="aperture-overview__inspector-links">
        {detailPath ? <Link to={detailPath}>View engine <ArrowUpRight aria-hidden /></Link> : null}
        {application ? <Link to={`/admin/deployments/applications/${encodeURIComponent(application.id)}`}>View application <ArrowUpRight aria-hidden /></Link> : null}
      </div>
    </>
  );
}

function Detail({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return <div><dt>{label}</dt><dd className={mono ? "aperture-overview__mono" : undefined}>{value}</dd></div>;
}

function HealthStatus({ health }: { health: EngineHealth }) {
  return <span className={`aperture-overview__health aperture-overview__health--${health.toLowerCase()}`}><span aria-hidden />{health}</span>;
}

function ApertureEmptyState() {
  return (
    <section className="aperture-overview__empty" role="status">
      <Server aria-hidden />
      <p className="aperture-overview__mono">ENGINE INVENTORY</p>
      <h2>Connect your first engine</h2>
      <span>Start receiving health and deployment signals from this workspace.</span>
    </section>
  );
}

function ApertureLoading() {
  return <section className="aperture-overview aperture-overview--loading" aria-busy="true" aria-label="Loading workspace overview"><div className="aperture-overview__main"><div className="aperture-overview__loading-heading" /><div className="aperture-overview__loading-grid"><div /><div /></div></div></section>;
}

function buildEngineContexts(data: DeploymentCockpit): EngineContext[] {
  const environments = new Map<string, { application: WorkflowApplication; environment: EnvironmentSummary }>();
  for (const application of data.applications) {
    for (const environment of application.environments) environments.set(environment.id, { application, environment });
  }
  return data.engines.map((engine) => ({
    engine,
    application: environments.get(engine.environmentId)?.application ?? null,
    environment: environments.get(engine.environmentId)?.environment ?? null
  }));
}

function filterEngines(contexts: EngineContext[], search: string, filter: EngineFilter) {
  const query = search.trim().toLowerCase();
  return contexts.filter((context) => {
    const health = engineHealth(context.engine);
    const matchesFilter = filter === "all" || (filter === "healthy" ? health === "Healthy" : hasAttention(context));
    const haystack = [
      context.engine.name,
      context.engine.id,
      context.application?.name,
      context.environment?.name,
      context.environment?.tierName,
      context.engine.endpoint?.region,
      context.engine.endpoint?.version
    ].filter(Boolean).join(" ").toLowerCase();
    return matchesFilter && (!query || haystack.includes(query));
  });
}

function hasAttention({ engine, environment }: EngineContext) {
  const credentialStatus = engine.credentialAssignmentStatus === "Deferred"
    ? "Deferred"
    : engine.credentialReference?.verificationStatus;
  return engineHealth(engine) !== "Healthy"
    || (credentialStatus !== undefined && !["Verified", "NotVerifiable"].includes(credentialStatus))
    || (engine.endpoint?.certificateStatus !== undefined && engine.endpoint.certificateStatus !== "Trusted")
    || environment?.driftStatus !== "InSync"
    || environment?.deploymentStatus === "Blocked";
}

function engineHealth(engine: WorkflowEngineRegistration): EngineHealth {
  return engine.health ?? "Unknown";
}

function environmentLabel(environment: EnvironmentSummary) {
  const tier = environment.tierName ?? environment.tier;
  return environment.name === tier ? environment.name : `${environment.name} · ${tier}`;
}

function enginePath(application: WorkflowApplication, environment: EnvironmentSummary, engineId: string) {
  return `/admin/deployments/applications/${encodeURIComponent(application.id)}/environments/${encodeURIComponent(environment.id)}/engines/${encodeURIComponent(engineId)}`;
}
