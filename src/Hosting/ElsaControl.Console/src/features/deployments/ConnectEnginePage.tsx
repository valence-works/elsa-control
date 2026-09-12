import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, CheckCircle2, ChevronDown, ChevronUp, KeyRound, Link2, LoaderCircle, RadioTower, ShieldCheck } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import type { FormEvent, ReactNode } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { Badge, Button, buttonClassName, Input, SecondaryButton, Select } from "@/components/ui";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import {
  createDeploymentApplication,
  createDeploymentCredentialReference,
  createDeploymentEnvironment,
  createDeploymentSecretStore,
  getDeploymentCockpit,
  getDeploymentPermissions,
  getDeploymentCredentialReferences,
  getDeploymentSecretStores,
  getDeploymentTiers,
  registerDeploymentEngine
} from "@/features/deployments/deploymentApi";
import { engineRegistrationRequest } from "@/features/deployments/DeploymentSetupPanel";
import type {
  DeploymentCockpit,
  EnvironmentSummary,
  WorkflowEngineRegistration,
  WorkspaceDeploymentCredentialReference,
  WorkspaceDeploymentSecretStore,
  WorkspaceDeploymentTier
} from "@/features/deployments/deploymentModels";
import { queryKeys } from "@/lib/query/queryClient";
import { ApiError } from "@/lib/api/httpClient";
import "./connect-engine.css";

type PlacementMode = "existing" | "new";
type CredentialMode = "new" | "saved" | "deferred";

type PlacementTarget = {
  applicationId: string;
  applicationName: string;
  environmentId: string;
  environmentName: string;
};

type SavedPlacement = PlacementTarget & {
  createdApplication: boolean;
  createdEnvironment: boolean;
};

type ConnectEngineValues = {
  engineName: string;
  baseUrl: string;
  placementMode: PlacementMode;
  applicationName: string;
  environmentName: string;
  environmentId: string;
  tierId: string;
  credentialMode: CredentialMode;
  credentialReferenceId: string;
  credentialStoreId: string;
  credentialName: string;
  credentialSecret: string;
};

type ConnectEngineSuccess = {
  engine: WorkflowEngineRegistration;
  placement: SavedPlacement;
};

type InProgressPlacement = {
  applicationId: string;
  applicationName: string;
  environmentId?: string;
  environmentName: string;
};

type PlacementApplication = {
  id: string;
  name: string;
  environments: Array<{ id: string; name: string }>;
};

class ConnectEngineValidationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ConnectEngineValidationError";
  }
}

const defaultValues: ConnectEngineValues = {
  engineName: "",
  baseUrl: "",
  placementMode: "existing",
  applicationName: "My application",
  environmentName: "Development",
  environmentId: "",
  tierId: "",
  credentialMode: "deferred",
  credentialReferenceId: "",
  credentialStoreId: "",
  credentialName: "",
  credentialSecret: ""
};

export function ConnectEnginePage() {
  const workspace = useWorkspaceContext();
  const workspaceId = workspace.selectedWorkspaceId;
  const [searchParams] = useSearchParams();
  const requestedEnvironmentId = searchParams.get("environmentId") ?? "";
  const cockpit = useQuery({
    queryKey: queryKeys.deploymentCockpit(workspaceId),
    queryFn: () => getDeploymentCockpit(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const permissions = useQuery({
    queryKey: queryKeys.deploymentPermissions(workspaceId),
    queryFn: () => getDeploymentPermissions(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const credentialReferences = useQuery({
    queryKey: queryKeys.deploymentCredentialReferences(workspaceId),
    queryFn: () => getDeploymentCredentialReferences(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const secretStores = useQuery({
    queryKey: queryKeys.deploymentSecretStores(workspaceId),
    queryFn: () => getDeploymentSecretStores(workspaceId),
    enabled: Boolean(workspaceId)
  });
  const tiers = useQuery({
    queryKey: queryKeys.deploymentTiers(workspaceId),
    queryFn: () => getDeploymentTiers(workspaceId),
    enabled: Boolean(workspaceId)
  });

  if (workspace.isLoading) {
    return <RequestStateView state="loading" title="Loading engine connection" />;
  }

  if (workspace.isError || !workspaceId) {
    return <RequestStateView state="unexpected" title={!workspaceId ? "No workspace selected" : "Workspace context could not load"} />;
  }

  if (cockpit.isPending || permissions.isPending) {
    return <RequestStateView state="loading" title="Loading engine connection" />;
  }

  if (cockpit.isError || !cockpit.data) {
    return <RequestStateView state="unexpected" title="Deployment data could not load" />;
  }

  if (permissions.isError || !permissions.data) {
    return <RequestStateView state="unexpected" title="Deployment permissions could not load" />;
  }

  if (requestedEnvironmentId && !flattenPlacements(cockpit.data).some((placement) => placement.environmentId === requestedEnvironmentId)) {
    return <RequestStateView state="not-found" title="Environment not found" description="The requested engine environment is no longer available in this workspace." />;
  }

  const canManageSetup = Boolean(permissions.data?.permissions.includes("deployments.setup.manage"));
  if (!canManageSetup) {
    return (
      <section className="connect-engine-page connect-engine-page-denied">
        <Link to="/admin/deployments" className="connect-engine-back">
          <ArrowLeft className="h-4 w-4" />
          Back to deployments
        </Link>
        <div className="connect-engine-denied-panel">
          <div className="connect-engine-denied-content">
            <ShieldCheck className="connect-engine-denied-icon" />
            <div>
              <h1>Connect an engine</h1>
              <p>Deployment setup permission is required to register a workflow engine.</p>
            </div>
          </div>
        </div>
      </section>
    );
  }

  return (
    <ConnectEngineForm
      key={`${workspaceId}:${requestedEnvironmentId}`}
      workspaceId={workspaceId}
      cockpit={cockpit.data}
      credentials={credentialReferences.data?.items ?? []}
      credentialsLoading={credentialReferences.isPending}
      credentialsError={credentialReferences.isError}
      secretStores={secretStores.data?.items ?? []}
      secretStoresError={secretStores.isError}
      secretStoresLoading={secretStores.isPending}
      tiers={tiers.data?.tiers ?? []}
      tiersError={tiers.isError}
      tiersLoading={tiers.isPending}
      requestedEnvironmentId={requestedEnvironmentId}
    />
  );
}

function ConnectEngineForm({
  workspaceId,
  cockpit,
  credentials,
  credentialsLoading,
  credentialsError,
  secretStores,
  secretStoresError,
  secretStoresLoading,
  tiers,
  tiersError,
  tiersLoading,
  requestedEnvironmentId,
}: {
  workspaceId: string;
  cockpit: DeploymentCockpit;
  credentials: WorkspaceDeploymentCredentialReference[];
  credentialsLoading: boolean;
  credentialsError: boolean;
  secretStores: WorkspaceDeploymentSecretStore[];
  secretStoresError: boolean;
  secretStoresLoading: boolean;
  tiers: WorkspaceDeploymentTier[];
  tiersError: boolean;
  tiersLoading: boolean;
  requestedEnvironmentId: string;
}) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [values, setValues] = useState<ConnectEngineValues>(() => initialValues(cockpit, credentials, tiers, requestedEnvironmentId));
  const credentialModeTouched = useRef(false);
  const [placementOpen, setPlacementOpen] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState<ConnectEngineSuccess | null>(null);
  const [createdPlacement, setCreatedPlacement] = useState<InProgressPlacement | null>(null);
  const [createdCredentialReferenceId, setCreatedCredentialReferenceId] = useState<string | null>(null);
  const [createdSecretStoreId, setCreatedSecretStoreId] = useState<string | null>(null);
  const [retryBlocked, setRetryBlocked] = useState(false);

  const placements = useMemo(() => flattenPlacements(cockpit), [cockpit]);
  const activeCredentials = useMemo(() => credentials.filter((reference) => reference.status === "Active"), [credentials]);
  const activeSecretStores = useMemo(() => secretStores.filter((store) => store.status === "Active"), [secretStores]);
  const localSecretStores = useMemo(() => activeSecretStores.filter((store) => store.type === "LocalEncryptedDatabase"), [activeSecretStores]);
  const activeTiers = useMemo(() => tiers.filter((tier) => tier.status === "Active"), [tiers]);
  const selectedCredential = activeCredentials.find((reference) => reference.id === values.credentialReferenceId);
  const credentialLocked = Boolean(createdCredentialReferenceId);
  const applicationLocked = Boolean(createdPlacement?.applicationId);
  const environmentLocked = Boolean(createdPlacement?.environmentId);
  const canCreateCredential = values.credentialMode === "new" && !credentialLocked && !secretStoresError && values.credentialSecret.trim().length > 0;
  const canUseSavedCredential = values.credentialMode === "saved" && Boolean(selectedCredential || values.credentialReferenceId === createdCredentialReferenceId);
  const endpointError = values.baseUrl.trim() ? endpointValidationMessage(values.baseUrl) : null;
  const tierReady = values.placementMode === "existing" || (!tiersLoading && !tiersError && (activeTiers.length === 0 || Boolean(values.tierId && activeTiers.some((tier) => tier.id === values.tierId))));
  const placementReady = values.placementMode === "existing" || (values.applicationName.trim().length > 0 && values.environmentName.trim().length > 0);
  const waitingForCredentialDefault = !credentialModeTouched.current && values.credentialMode === "deferred" && activeCredentials.length > 0;
  const credentialSelectionReady = credentialModeTouched.current || (!credentialsLoading && !waitingForCredentialDefault);
  const formReady = !retryBlocked && credentialSelectionReady && tierReady && placementReady && values.engineName.trim().length > 0 && values.baseUrl.trim().length > 0 && !endpointError && (values.credentialMode === "deferred" || canUseSavedCredential || canCreateCredential);

  async function invalidateDeploymentData() {
    await Promise.allSettled([
      queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCockpit(workspaceId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCredentialReferences(workspaceId) }),
      queryClient.invalidateQueries({ queryKey: queryKeys.deploymentSecretStores(workspaceId) })
    ]);
  }

  useEffect(() => {
    if (createdCredentialReferenceId && values.credentialReferenceId === createdCredentialReferenceId) return;
    if (values.credentialMode === "saved" && !activeCredentials.some((reference) => reference.id === values.credentialReferenceId)) {
      setValues((current) => ({
        ...current,
        credentialMode: activeCredentials.length > 0 ? "saved" : "deferred",
        credentialReferenceId: activeCredentials[0]?.id ?? "",
        credentialStoreId: localSecretStores[0]?.id ?? ""
      }));
      return;
    }
    if (credentialModeTouched.current || values.credentialMode !== "deferred" || activeCredentials.length === 0) return;
    setValues((current) => ({
      ...current,
      credentialMode: "saved",
      credentialReferenceId: activeCredentials[0].id,
      credentialStoreId: localSecretStores[0]?.id ?? ""
    }));
  }, [activeCredentials, createdCredentialReferenceId, localSecretStores, values.credentialMode, values.credentialReferenceId]);

  useEffect(() => {
    if (values.credentialStoreId || localSecretStores.length === 0) return;
    setValues((current) => ({ ...current, credentialStoreId: localSecretStores[0].id }));
  }, [localSecretStores, values.credentialStoreId]);

  useEffect(() => {
    if (values.tierId || activeTiers.length === 0) return;
    const defaultTier = activeTiers.find((tier) => tier.isDefault) ?? activeTiers[0];
    setValues((current) => ({ ...current, tierId: defaultTier.id }));
  }, [activeTiers, values.tierId]);

  useEffect(() => {
    if (values.placementMode === "new" || placements.some((placement) => placement.environmentId === values.environmentId)) return;
    if (placements[0]) {
      setValues((current) => ({ ...current, environmentId: placements[0].environmentId }));
    }
  }, [placements, values.environmentId, values.placementMode]);

  function setValue<K extends keyof ConnectEngineValues>(key: K, value: ConnectEngineValues[K]) {
    if (key === "credentialMode") credentialModeTouched.current = true;
    setValues((current) => ({ ...current, [key]: value }));
    if (!retryBlocked) setError(null);
  }

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!formReady || isSubmitting) return;

    setIsSubmitting(true);
    setError(null);

    try {
      const placement = await ensurePlacement({
        workspaceId,
        cockpit,
        values,
        activeTiers,
        existingPlacement: createdPlacement,
        onPlacementProgress: setCreatedPlacement,
        onMutationConfirmed: invalidateDeploymentData
      });
      let credentialReferenceId = values.credentialMode === "saved" ? values.credentialReferenceId : null;
      if (values.credentialMode === "new") {
        credentialReferenceId = await ensureCredentialReference({
          workspaceId,
          values,
          activeSecretStores: localSecretStores,
          existingSecretStoreId: createdSecretStoreId,
          onSecretStoreCreated: setCreatedSecretStoreId,
          existingCredentialReferenceId: createdCredentialReferenceId,
          onCredentialCreated: setCreatedCredentialReferenceId,
          onMutationConfirmed: invalidateDeploymentData
        });
        setValues((current) => ({
          ...current,
          credentialMode: "saved",
          credentialReferenceId: credentialReferenceId ?? "",
          credentialSecret: ""
        }));
      }
      const request = engineRegistrationRequest({
        engineName: values.engineName.trim(),
        baseUrl: values.baseUrl.trim(),
        credentialAssignmentStatus: credentialReferenceId ? "Assigned" : "Deferred",
        credentialReferenceId
      });
      const engine = await registerDeploymentEngine(workspaceId, placement.environmentId, request);
      await invalidateDeploymentData();
      const saved = { engine, placement };
      setSuccess(saved);
      setValues((current) => ({ ...current, credentialReferenceId: "", credentialSecret: "" }));
    } catch (submissionError) {
      setRetryBlocked(isAmbiguousWriteError(submissionError));
      setError(errorMessage(submissionError, [values.credentialSecret, values.credentialSecret.trim()]));
    } finally {
      setIsSubmitting(false);
    }
  }

  if (success) {
    const { engine, placement } = success;
    const healthTone = engine.health === "Healthy" ? "is-healthy" : engine.health === "Degraded" ? "is-warning" : engine.health === "Unreachable" ? "is-error" : "is-neutral";
    return (
      <section className="connect-engine-page connect-engine-page-success">
        <Link to="/admin/deployments" className="connect-engine-back">
          <ArrowLeft className="h-4 w-4" />
          Back to deployments
        </Link>
        <div className="connect-engine-success-layout">
          <div className="connect-engine-success-intro">
            <span className="connect-engine-kicker">Connection saved</span>
            <h1>{engine.health === "Healthy" ? "Your engine is connected." : "Your engine is registered."}</h1>
            <p className="connect-engine-success-copy">
              {engine.health === "Healthy" ? "The endpoint is reachable and ready for workspace operations." : "The engine record is saved. Review its health status before using it for operations."}
            </p>
            <div className="connect-engine-benefits">
              <Benefit icon={<Link2 className="h-4 w-4" />} title="Endpoint saved" detail={engine.endpoint.baseUrl} />
              <Benefit icon={<KeyRound className="h-4 w-4" />} title="Credential assignment" detail={engine.credentialAssignmentStatus === "Assigned" ? "Saved credential assigned" : "Deferred"} />
              <Benefit icon={<RadioTower className="h-4 w-4" />} title="Placement" detail={`${placement.applicationName} / ${placement.environmentName}`} />
            </div>
          </div>
          <div className="connect-engine-success-card">
            <div className={`connect-engine-success-icon ${healthTone}`}><CheckCircle2 className="h-6 w-6" /></div>
            <Badge className={`connect-engine-success-badge ${healthTone}`}>{engine.health}</Badge>
            <h2>{engine.name}</h2>
            <p className="connect-engine-success-message">{engine.verificationMessage || "Registration completed."}</p>
            <dl className="connect-engine-summary-grid">
              <Summary label="Endpoint" value={engine.endpoint.baseUrl} />
              <Summary label="Environment" value={placement.environmentName} />
              <Summary label="Verification" value={engine.lastVerificationAt ? "Completed during registration" : "Not reported"} />
              <Summary label="Version" value={engine.endpoint.version || "Not reported"} />
            </dl>
            <div className="connect-engine-success-actions">
              <Button className="connect-engine-submit" type="button" onClick={() => navigate(enginePath(placement, engine.id))}>View engine</Button>
              <SecondaryButton className="connect-engine-secondary" type="button" onClick={() => { credentialModeTouched.current = false; setSuccess(null); setError(null); setRetryBlocked(false); setCreatedPlacement(null); setCreatedCredentialReferenceId(null); setValues(initialValues(cockpit, activeCredentials, activeTiers, requestedEnvironmentId)); }}>Connect another</SecondaryButton>
            </div>
          </div>
        </div>
      </section>
    );
  }

  const selectedPlacementSummary = values.placementMode === "new"
    ? `${values.applicationName.trim() || "Application name"} / ${values.environmentName.trim() || "Environment name"}`
    : (placements.find((placement) => placement.environmentId === values.environmentId)
      ? `${placements.find((placement) => placement.environmentId === values.environmentId)?.applicationName} / ${placements.find((placement) => placement.environmentId === values.environmentId)?.environmentName}`
      : "Choose an application and environment");

  return (
    <section className="connect-engine-page">
      <Link to="/admin/deployments" className="connect-engine-back">
        <ArrowLeft className="h-4 w-4" />
        Back to deployments
      </Link>
      <div className="connect-engine-layout">
        <div className="connect-engine-intro">
          <span className="connect-engine-kicker">NEW CONNECTION</span>
          <h1>Connect an engine</h1>
          <p>Add an Elsa engine to your workspace.</p>
        </div>

        <form className="connect-engine-form" onSubmit={handleSubmit} noValidate>
          <div className="connect-engine-form-body">
            <div className="connect-engine-field-grid">
              <Field label="Engine name" error={!values.engineName.trim() && error ? "Enter an engine name." : undefined}>
                <Input aria-label="Engine name" required value={values.engineName} onChange={(event) => setValue("engineName", event.target.value)} placeholder="elsa-dev" disabled={isSubmitting} />
              </Field>
              <Field label="Engine URL" hint="Use the base HTTP or HTTPS URL for the engine." hintId="connect-engine-url-hint" error={endpointError ?? undefined} errorId="connect-engine-url-error">
                <Input aria-label="Engine URL" aria-invalid={endpointError ? "true" : undefined} aria-describedby={endpointError ? "connect-engine-url-error" : "connect-engine-url-hint"} required type="url" value={values.baseUrl} onChange={(event) => setValue("baseUrl", event.target.value)} placeholder="https://elsa.example.com" autoComplete="url" spellCheck={false} disabled={isSubmitting} />
              </Field>
            </div>

            <section aria-labelledby="credential-heading" className="connect-engine-section connect-engine-credentials">
              <div className="connect-engine-section-heading">
                <div>
                  <h3 id="credential-heading">Authentication</h3>
                </div>
                <Link to="/admin/deployments/credentials" className="connect-engine-manage-link">Manage credentials</Link>
              </div>
              <div className="connect-engine-choice-group" role="group" aria-label="Credential assignment mode">
                <button type="button" aria-pressed={values.credentialMode === "new"} className={buttonClassName(values.credentialMode === "new" ? "primary" : "secondary", "connect-engine-choice")} disabled={isSubmitting || credentialLocked || secretStoresLoading || secretStoresError} onClick={() => setValue("credentialMode", "new")}>New API key</button>
                <button type="button" aria-pressed={values.credentialMode === "saved"} className={buttonClassName(values.credentialMode === "saved" ? "primary" : "secondary", "connect-engine-choice")} disabled={isSubmitting || credentialLocked || activeCredentials.length === 0} onClick={() => setValue("credentialMode", "saved")}>Saved credential</button>
                <button type="button" aria-pressed={values.credentialMode === "deferred"} className={buttonClassName(values.credentialMode === "deferred" ? "primary" : "secondary", "connect-engine-choice")} disabled={isSubmitting || credentialLocked} onClick={() => setValue("credentialMode", "deferred")}>Assign later</button>
              </div>
              {credentialsLoading ? <p className="connect-engine-helper">Loading saved credentials…</p> : null}
              {values.credentialMode === "new" ? (
                <div className="connect-engine-credential-fields">
                  <label className="connect-engine-field">
                    API key
                    <Input aria-label="Engine API key" type="password" value={values.credentialSecret} onChange={(event) => setValue("credentialSecret", event.target.value)} placeholder="Paste engine API key" autoComplete="new-password" disabled={isSubmitting || credentialLocked} />
                  </label>
                  <details className="connect-engine-credential-name">
                    <summary>Credential name <span>(optional)</span></summary>
                    <Input aria-label="Credential name" value={values.credentialName} onChange={(event) => setValue("credentialName", event.target.value)} placeholder={values.engineName.trim() ? `${values.engineName.trim()} API key` : "Engine API key"} disabled={isSubmitting || credentialLocked} />
                  </details>
                  {localSecretStores.length > 1 ? (
                    <label className="connect-engine-field connect-engine-field-span">
                      Credential store
                      <Select aria-label="Credential store" value={values.credentialStoreId} onChange={(event) => setValue("credentialStoreId", event.target.value)} disabled={isSubmitting || credentialLocked}>
                        {localSecretStores.map((store) => <option key={store.id} value={store.id}>{store.name}</option>)}
                      </Select>
                    </label>
                  ) : null}
                  {localSecretStores.length === 0 ? <p className="connect-engine-helper connect-engine-field-span">A protected credential store will be created with this connection.</p> : null}
                  <p className="connect-engine-helper connect-engine-field-span">The API key is protected and is never displayed again.</p>
                </div>
              ) : values.credentialMode === "saved" ? (
                <label className="connect-engine-field">
                  Credential reference
                  <Select aria-label="Credential reference" value={values.credentialReferenceId} onChange={(event) => setValue("credentialReferenceId", event.target.value)} disabled={isSubmitting || credentialLocked || activeCredentials.length === 0}>
                    {activeCredentials.length === 0 ? <option value="">No saved credentials available</option> : activeCredentials.map((reference) => <option key={reference.id} value={reference.id}>{reference.name} · {reference.secretStoreName}</option>)}
                  </Select>
                </label>
              ) : (
                <p className="connect-engine-helper">The engine will be registered without a credential assignment. You can add one from the engine detail screen.</p>
              )}
              {credentialsError ? <p className="connect-engine-warning">Saved credentials could not load. You can assign one later.</p> : null}
              {secretStoresError ? <p className="connect-engine-warning">Credential stores could not load. Use a saved credential or assign one later.</p> : null}
            </section>

            <section className="connect-engine-section connect-engine-placement" aria-labelledby="placement-heading">
              <div className="connect-engine-placement-heading">
                <div>
                  <span className="connect-engine-placement-kicker">Add to</span>
                  <h3 id="placement-heading">{selectedPlacementSummary}</h3>
                </div>
                <button type="button" className="connect-engine-placement-toggle" aria-expanded={placementOpen} disabled={isSubmitting} onClick={() => setPlacementOpen((open) => !open)}>
                  {environmentLocked ? "Saved" : placementOpen ? "Close" : "Change"}
                  {placementOpen ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
                </button>
              </div>
              {values.placementMode === "new" && tiersLoading ? <p className="connect-engine-helper">Loading workspace tiers…</p> : null}
              {values.placementMode === "new" && tiersError ? <p className="connect-engine-warning">Workspace tiers could not load. <button type="button" className="connect-engine-inline-action" disabled={isSubmitting} onClick={() => void queryClient.invalidateQueries({ queryKey: queryKeys.deploymentTiers(workspaceId) })}>Retry</button></p> : null}
              {placementOpen ? (
                <div className="connect-engine-placement-fields">
                  <label className="connect-engine-field">
                    Placement
                    <Select aria-label="Placement mode" value={values.placementMode === "new" ? "new" : values.environmentId} disabled={isSubmitting || applicationLocked} onChange={(event) => {
                      if (event.target.value === "new") {
                        setValues((current) => ({ ...current, placementMode: "new", applicationName: "My application", environmentName: "Development", environmentId: "" }));
                        if (!retryBlocked) setError(null);
                        return;
                      }
                      setValues((current) => ({ ...current, placementMode: "existing", environmentId: event.target.value }));
                      if (!retryBlocked) setError(null);
                    }}>
                      {placements.map((placement) => <option key={placement.environmentId} value={placement.environmentId}>{placement.applicationName} / {placement.environmentName}</option>)}
                      <option value="new">Create application and environment</option>
                    </Select>
                  </label>
                  {values.placementMode === "new" ? (
                    <div className="connect-engine-new-placement-fields">
                      <Field label="Application">
                        <Input aria-label="Application name" required value={values.applicationName} maxLength={100} onChange={(event) => setValue("applicationName", event.target.value)} disabled={isSubmitting || applicationLocked} />
                      </Field>
                      <Field label="Environment">
                        <Input aria-label="Environment name" required value={values.environmentName} maxLength={100} onChange={(event) => setValue("environmentName", event.target.value)} disabled={isSubmitting || environmentLocked} />
                      </Field>
                      {activeTiers.length > 0 ? (
                        <label className="connect-engine-field connect-engine-field-span">
                          Tier
                          <Select aria-label="Environment tier" value={values.tierId} onChange={(event) => setValue("tierId", event.target.value)} disabled={isSubmitting || environmentLocked}>
                            {activeTiers.map((tier) => <option key={tier.id} value={tier.id}>{tier.name}</option>)}
                          </Select>
                        </label>
                      ) : <p className="connect-engine-helper connect-engine-field-span">No active workspace tiers were returned. The API will apply its default tier.</p>}
                    </div>
                  ) : null}
                </div>
              ) : null}
            </section>

            {error ? <div role="alert" className="connect-engine-error"><strong>Connection could not be saved.</strong><p>{error}</p>{retryBlocked ? <p>The request may have reached the server. Refresh deployment data before trying again, then <Link to="/admin/deployments/credentials">review credentials</Link> and choose the existing placement if it appears.</p> : <p>Your entries are still here. Correct them and try again.</p>}</div> : null}
          </div>
          <div className="connect-engine-form-footer">
            <Button className="connect-engine-submit" type="submit" disabled={!formReady || isSubmitting}>{isSubmitting ? <><LoaderCircle className="h-4 w-4 animate-spin" />Connecting</> : "Connect engine →"}</Button>
          </div>
        </form>
      </div>
    </section>
  );
}

function Field({ label, hint, hintId, error, errorId, children }: { label: string; hint?: string; hintId?: string; error?: string; errorId?: string; children: ReactNode }) {
  return <div className="connect-engine-field"><span className="connect-engine-field-label">{label}</span><div className="connect-engine-field-control">{children}</div>{error ? <p id={errorId} className="connect-engine-error-text">{error}</p> : hint ? <p id={hintId} className="connect-engine-hint">{hint}</p> : null}</div>;
}

function Benefit({ icon, title, detail }: { icon: ReactNode; title: string; detail: string }) {
  return <div className="connect-engine-benefit"><span className="connect-engine-benefit-icon">{icon}</span><div><p>{title}</p><span>{detail}</span></div></div>;
}

function Summary({ label, value }: { label: string; value: string }) {
  return <div><dt>{label}</dt><dd>{value}</dd></div>;
}

function initialValues(cockpit: DeploymentCockpit, credentials: WorkspaceDeploymentCredentialReference[], tiers: WorkspaceDeploymentTier[], requestedEnvironmentId = ""): ConnectEngineValues {
  const placements = flattenPlacements(cockpit);
  const activeCredentials = credentials.filter((reference) => reference.status === "Active");
  const activeTiers = tiers.filter((tier) => tier.status === "Active");
  const firstTier = activeTiers.find((tier) => tier.name.toLowerCase() === "dev") ?? activeTiers[0];
  return {
    ...defaultValues,
    placementMode: placements.length > 0 ? "existing" : "new",
    environmentName: defaultValues.environmentName,
    environmentId: placements.find((placement) => placement.environmentId === requestedEnvironmentId)?.environmentId ?? placements[0]?.environmentId ?? "",
    tierId: firstTier?.id ?? "",
    credentialMode: activeCredentials.length > 0 ? "saved" : "deferred",
    credentialReferenceId: activeCredentials[0]?.id ?? ""
  };
}

function flattenPlacements(cockpit: DeploymentCockpit): PlacementTarget[] {
  return cockpit.applications.flatMap((application) => application.environments.map((environment) => ({
    applicationId: application.id,
    applicationName: application.name,
    environmentId: environment.id,
    environmentName: environment.name
  })));
}

async function ensurePlacement({ workspaceId, cockpit, values, activeTiers, existingPlacement, onPlacementProgress, onMutationConfirmed }: { workspaceId: string; cockpit: DeploymentCockpit; values: ConnectEngineValues; activeTiers: WorkspaceDeploymentTier[]; existingPlacement: InProgressPlacement | null; onPlacementProgress: (placement: InProgressPlacement | null) => void; onMutationConfirmed: () => Promise<void> }): Promise<SavedPlacement> {
  if (values.placementMode === "existing") {
    const placement = flattenPlacements(cockpit).find((item) => item.environmentId === values.environmentId);
    if (!placement) throw new ConnectEngineValidationError("Choose an application and environment before connecting the engine.");
    return { ...placement, createdApplication: false, createdEnvironment: false };
  }
  if (!values.applicationName.trim() || !values.environmentName.trim()) throw new ConnectEngineValidationError("Enter an application and environment name.");
  if (existingPlacement?.environmentId) {
    return {
      applicationId: existingPlacement.applicationId,
      applicationName: existingPlacement.applicationName,
      environmentId: existingPlacement.environmentId,
      environmentName: existingPlacement.environmentName,
      createdApplication: true,
      createdEnvironment: true
    };
  }

  let application: PlacementApplication | undefined = existingPlacement?.applicationId
    ? cockpit.applications.find((item) => item.id === existingPlacement.applicationId) ?? { id: existingPlacement.applicationId, name: existingPlacement.applicationName, environments: [] }
    : undefined;
  if (!application) {
    const created = await createDeploymentApplication(workspaceId, { name: values.applicationName.trim(), description: null });
    application = { id: created.id, name: created.name, environments: [] };
    await onMutationConfirmed();
  }
  onPlacementProgress({ applicationId: application.id, applicationName: application.name, environmentName: values.environmentName.trim() });

  let environment = existingPlacement?.environmentId
    ? application.environments.find((item) => item.id === existingPlacement.environmentId)
    : application.environments.find((item) => item.name === values.environmentName.trim());
  if (!environment) {
    const selectedTier = activeTiers.find((tier) => tier.id === values.tierId);
    environment = await createDeploymentEnvironment(workspaceId, application.id, {
      name: values.environmentName.trim(),
      tier: legacyTierFromName(selectedTier?.name),
      ...(selectedTier ? { tierId: selectedTier.id } : {})
    });
    await onMutationConfirmed();
  }
  onPlacementProgress({ applicationId: application.id, applicationName: application.name, environmentId: environment.id, environmentName: environment.name });

  return {
    applicationId: application.id,
    applicationName: application.name,
    environmentId: environment.id,
    environmentName: environment.name,
    createdApplication: !cockpit.applications.some((item) => item.id === application?.id),
    createdEnvironment: !cockpit.applications.some((item) => item.environments.some((candidate) => candidate.id === environment?.id))
  };
}

async function ensureCredentialReference({ workspaceId, values, activeSecretStores, existingSecretStoreId, onSecretStoreCreated, existingCredentialReferenceId, onCredentialCreated, onMutationConfirmed }: { workspaceId: string; values: ConnectEngineValues; activeSecretStores: WorkspaceDeploymentSecretStore[]; existingSecretStoreId: string | null; onSecretStoreCreated: (secretStoreId: string | null) => void; existingCredentialReferenceId: string | null; onCredentialCreated: (credentialReferenceId: string | null) => void; onMutationConfirmed: () => Promise<void> }) {
  if (existingCredentialReferenceId) return existingCredentialReferenceId;
  let store = activeSecretStores.find((item) => item.id === values.credentialStoreId);
  const name = values.credentialName.trim() || `${values.engineName.trim()} API key`;
  const secretValue = values.credentialSecret.trim();
  if (!secretValue) throw new ConnectEngineValidationError("Enter the API key.");
  if (!store && existingSecretStoreId) {
    store = { id: existingSecretStoreId, name: "Protected credential store", type: "LocalEncryptedDatabase" } as WorkspaceDeploymentSecretStore;
  }
  if (!store) {
    const createdStore = await createDeploymentSecretStore(workspaceId, {
      name: "Workspace encrypted credentials",
      provider: null,
      type: "LocalEncryptedDatabase",
      description: null
    });
    store = createdStore;
    onSecretStoreCreated(createdStore.id);
    await onMutationConfirmed();
  }
  const reference = `local://engine-credentials/${slugify(name)}`;
  const created = await createDeploymentCredentialReference(workspaceId, store.id, {
    name,
    reference,
    description: null,
    secretValue
  });
  onCredentialCreated(created.id);
  await onMutationConfirmed();
  return created.id;
}

function slugify(value: string) {
  return value.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "") || "engine-api-key";
}

function endpointValidationMessage(baseUrl: string) {
  if (!baseUrl.trim()) return "Enter the engine URL.";
  try {
    const parsed = new URL(baseUrl.trim());
    if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return "Use an HTTP or HTTPS URL.";
    if (parsed.username || parsed.password || parsed.search || parsed.hash) return "Use a base URL without credentials, a query, or a fragment.";
    return null;
  } catch {
    return "Enter a valid engine URL.";
  }
}

function legacyTierFromName(name?: string): EnvironmentSummary["tier"] {
  if (name === "Dev" || name === "Test" || name === "Stage" || name === "Production") return name;
  return "Production";
}

function enginePath(placement: PlacementTarget, engineId: string) {
  return `/admin/deployments/applications/${encodeURIComponent(placement.applicationId)}/environments/${encodeURIComponent(placement.environmentId)}/engines/${encodeURIComponent(engineId)}`;
}

function isAmbiguousWriteError(error: unknown) {
  if (error instanceof ConnectEngineValidationError) return false;
  return !(error instanceof ApiError) || error.kind === "Unavailable" || error.kind === "Unexpected";
}

function errorMessage(error: unknown, secrets: string[] = []) {
  const message = error instanceof Error && error.message.trim() ? error.message : "The connection request failed. Try again.";
  return secrets.filter((secret) => secret.length > 0).reduce((safe, secret) => safe.replaceAll(secret, "[redacted]"), message);
}
