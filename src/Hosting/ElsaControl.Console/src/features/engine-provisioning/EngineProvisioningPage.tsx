import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, Check, CircleAlert, ExternalLink, LoaderCircle, Pencil, Rocket, Server, ShieldCheck, TriangleAlert } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { Link, useLocation, useSearchParams } from "react-router-dom";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { Badge, Button, EmptyState, Input, SecondaryButton, Select, buttonClassName } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { getDeploymentCockpit, getDeploymentPermissions } from "@/features/deployments/deploymentApi";
import type { DeploymentCockpit, EnvironmentSummary, WorkflowApplication, WorkspaceDeploymentPermissionsResponse } from "@/features/deployments/deploymentModels";
import { getManagedElsaOnboardingOptions, getManagedElsaOperation } from "@/features/managed-elsa/managedElsaApi";
import { buildManagedElsaIntent, onboardingChoices, slugify } from "@/features/managed-elsa/managedElsaOnboarding";
import type { ManagedElsaOnboardingOptions, ManagedElsaOperation, ManagedElsaReleaseOption } from "@/features/managed-elsa/managedElsaModels";
import { getBuilderCatalog, listRuntimeConfigurations } from "@/features/runtime-builder/runtimeBuilderApi";
import type { BuilderCatalog, RuntimeBuilderIntent, RuntimeConfiguration } from "@/features/runtime-builder/runtimeBuilderModels";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";
import { createEngineProvisioning, getEngineProvisioningTargets, previewEngineProvisioning } from "@/features/engine-provisioning/engineProvisioningApi";
import { consumeBuilderProvisioningHandoff, type EngineProvisioningFinding, type EngineProvisioningPreviewResponse } from "@/features/engine-provisioning/engineProvisioningModels";
import type { EngineProvisioningRequest, EngineProvisioningTarget } from "@/features/engine-provisioning/engineProvisioningModels";
import { useEngineProvisioningProviders } from "@/features/engine-provisioning/useEngineProvisioningProviders";

type Step = "configure" | "review" | "provisioning" | "complete";
type HandoffStatus = "none" | "pending" | "ready" | "missing";
type ProvisionRelease = ManagedElsaReleaseOption & { previewManifestDigest?: string | null };

type ProvisionRouteState = {
  workspaceId?: string;
  runtimeConfigurationId?: string;
  builderHandoffToken?: string;
  configurationName?: string;
};

type EmptyEnvironment = {
  application: WorkflowApplication;
  environment: EnvironmentSummary;
};

type PendingProvisioning = {
  instanceId: string;
  operationId: string;
  name: string;
  applicationName: string;
  environmentName: string;
  engineLink: string | null;
};

type ProvisioningAttempt = {
  key: string;
  request: EngineProvisioningRequest;
  display: {
    name: string;
    applicationName: string;
    environmentName: string;
  };
};

const pendingStoragePrefix = "engine-provisioning-operation:";
const attemptStoragePrefix = "engine-provisioning-attempt:";

export function EngineProvisioningPage() {
  const workspace = useWorkspaceContext();
  return <EngineProvisioningWorkspace key={workspace.selectedWorkspaceId || "none"} workspace={workspace} />;
}

type WorkspaceState = ReturnType<typeof useWorkspaceContext>;

function EngineProvisioningWorkspace({ workspace }: { workspace: WorkspaceState }) {
  const location = useLocation();
  const [searchParams] = useSearchParams();
  const routeState = useMemo(() => parseRouteState(location.state), [location.state]);
  const workspaceId = workspace.selectedWorkspaceId;
  const handoffOriginMatches = !routeState.workspaceId || routeState.workspaceId === workspaceId;
  const routeStateForWorkspace = handoffOriginMatches ? routeState : {};
  const handoffToken = handoffOriginMatches ? routeState.builderHandoffToken : undefined;
  const provisioning = useEngineProvisioningProviders(workspaceId);
  const providerAvailable = provisioning.hasProvider("azure");
  const permissions = useQuery<WorkspaceDeploymentPermissionsResponse>({
    queryKey: queryKeys.deploymentPermissions(workspaceId),
    queryFn: () => getDeploymentPermissions(workspaceId),
    enabled: Boolean(workspaceId) && providerAvailable,
    retry: false
  });
  const canManageSetup = permissions.data?.permissions.includes("deployments.setup.manage") ?? false;
  const targets = useQuery<EngineProvisioningTarget[]>({
    queryKey: queryKeys.engineProvisioningTargets(workspaceId),
    queryFn: () => getEngineProvisioningTargets(workspaceId),
    enabled: Boolean(workspaceId) && providerAvailable && canManageSetup,
    retry: false
  });
  const options = useQuery<ManagedElsaOnboardingOptions>({
    queryKey: queryKeys.managedElsaOnboardingOptions(workspaceId),
    queryFn: () => getManagedElsaOnboardingOptions(workspaceId),
    enabled: Boolean(workspaceId) && providerAvailable && canManageSetup,
    retry: false
  });
  const cockpit = useQuery<DeploymentCockpit>({
    queryKey: queryKeys.deploymentCockpit(workspaceId),
    queryFn: () => getDeploymentCockpit(workspaceId),
    enabled: Boolean(workspaceId) && providerAvailable && canManageSetup,
    retry: false
  });
  const configurations = useQuery<RuntimeConfiguration[]>({
    queryKey: queryKeys.runtimeConfigurations(workspaceId),
    queryFn: () => listRuntimeConfigurations(workspaceId),
    enabled: Boolean(workspaceId) && providerAvailable && canManageSetup,
    retry: false
  });

  const environments = useMemo(() => availableEnvironments(cockpit.data, targets.data), [cockpit.data, targets.data]);
  const choices = useMemo(() => onboardingChoices(options.data), [options.data]);
  const [step, setStep] = useState<Step>(() => loadPendingProvisioning(workspaceId) ? "provisioning" : "configure");
  const [name, setName] = useState(routeStateForWorkspace.configurationName ?? "");
  const [slug, setSlug] = useState(() => slugify(routeStateForWorkspace.configurationName ?? ""));
  const [applicationId, setApplicationId] = useState(searchParams.get("applicationId") ?? "");
  const [environmentId, setEnvironmentId] = useState(searchParams.get("environmentId") ?? "");
  const [releaseIndex, setReleaseIndex] = useState("0");
  const [selectedConfigurationId, setSelectedConfigurationId] = useState(routeStateForWorkspace.runtimeConfigurationId ?? "");
  const [builderIntent, setBuilderIntent] = useState<RuntimeBuilderIntent | undefined>();
  const [handoffStatus, setHandoffStatus] = useState<HandoffStatus>(() => {
    if (!handoffOriginMatches && routeState.builderHandoffToken) return "missing";
    return handoffToken ? "pending" : "none";
  });
  const [customizationOpen, setCustomizationOpen] = useState(false);
  const [previewConsent, setPreviewConsent] = useState(false);
  const [previewResult, setPreviewResult] = useState<EngineProvisioningPreviewResponse | null>(null);
  const [pending, setPending] = useState<PendingProvisioning | null>(() => loadPendingProvisioning(workspaceId));
  const [recoveryAttempt, setRecoveryAttempt] = useState<ProvisioningAttempt | null>(() => {
    const storedPending = loadPendingProvisioning(workspaceId);
    return storedPending ? null : loadProvisioningAttempt(workspaceId);
  });
  const [submittedAttempt, setSubmittedAttempt] = useState<ProvisioningAttempt | null>(null);
  const [attemptStorageError, setAttemptStorageError] = useState<string | null>(null);
  const [idempotencyKey, setIdempotencyKey] = useState(() => loadProvisioningAttempt(workspaceId)?.key ?? newIdempotencyKey());
  const previousWorkspaceId = useRef(workspaceId);
  const consumedHandoffToken = useRef<string>();
  const replayedAttemptKey = useRef<string>();
  const queryClient = useQueryClient();

  const selectedRelease = choices[Number(releaseIndex)] as ProvisionRelease | undefined;
  const previewReleaseRequired = Boolean(selectedRelease && "previewManifestDigest" in selectedRelease && selectedRelease.previewManifestDigest);
  const selectedConfiguration = configurations.data?.find((configuration) => configuration.id === selectedConfigurationId) ?? null;
  const selectedApplicationId = environments.some((item) => item.application.id === applicationId)
    ? applicationId
    : environments[0]?.application.id ?? "";
  const applicationEnvironments = environments.filter((item) => item.application.id === selectedApplicationId);
  const selectedEnvironmentId = applicationEnvironments.some((item) => item.environment.id === environmentId)
    ? environmentId
    : applicationEnvironments[0]?.environment.id ?? "";
  const selectedEnvironment = applicationEnvironments.find((item) => item.environment.id === selectedEnvironmentId) ?? null;
  const operation = useQuery<ManagedElsaOperation>({
    queryKey: queryKeys.managedElsaOperation(workspaceId, pending?.instanceId ?? "", pending?.operationId ?? ""),
    queryFn: () => getManagedElsaOperation(workspaceId, pending!.instanceId, pending!.operationId),
    enabled: Boolean(workspaceId && pending && providerAvailable),
    retry: false,
    refetchInterval: (query) => {
      if (query.state.status === "error") return false;
      const current = query.state.data;
      return current && isTerminal(current.state) ? false : 2000;
    }
  });

  useEffect(() => {
    if (!workspaceId) return;
    const workspaceChanged = previousWorkspaceId.current !== workspaceId;
    previousWorkspaceId.current = workspaceId;
    const stored = loadPendingProvisioning(workspaceId);
    const storedAttempt = stored ? null : loadProvisioningAttempt(workspaceId);
    setPending(stored);
    setRecoveryAttempt(storedAttempt);
    setSubmittedAttempt(null);
    setStep(stored ? "provisioning" : "configure");
    if (!workspaceChanged) return;
    setName(routeStateForWorkspace.configurationName ?? "");
    setSlug(slugify(routeStateForWorkspace.configurationName ?? ""));
    setApplicationId(searchParams.get("applicationId") ?? "");
    setEnvironmentId(searchParams.get("environmentId") ?? "");
    setReleaseIndex("0");
    setSelectedConfigurationId(routeStateForWorkspace.runtimeConfigurationId ?? "");
    setBuilderIntent(undefined);
    setHandoffStatus(!handoffOriginMatches && routeState.builderHandoffToken ? "missing" : handoffToken ? "missing" : "none");
    setCustomizationOpen(false);
    setPreviewConsent(false);
    setPreviewResult(null);
    setIdempotencyKey(storedAttempt?.key ?? newIdempotencyKey());
    setAttemptStorageError(null);
    replayedAttemptKey.current = undefined;
  }, [handoffOriginMatches, handoffToken, routeState.builderHandoffToken, routeStateForWorkspace.configurationName, routeStateForWorkspace.runtimeConfigurationId, searchParams, workspaceId]);

  useEffect(() => {
    if (!handoffOriginMatches && routeState.builderHandoffToken) {
      setBuilderIntent(undefined);
      setHandoffStatus("missing");
      return;
    }
    const token = handoffToken;
    if (!token) {
      setHandoffStatus("none");
      return;
    }
    if (consumedHandoffToken.current === token) return;
    consumedHandoffToken.current = token;
    const handoff = consumeBuilderProvisioningHandoff(token);
    if (handoff) {
      setBuilderIntent(handoff);
      setHandoffStatus("ready");
    } else {
      setBuilderIntent(undefined);
      setHandoffStatus("missing");
    }
  }, [handoffOriginMatches, handoffToken, routeState.builderHandoffToken]);

  useEffect(() => {
    if (choices.length > 0 && Number(releaseIndex) >= choices.length) setReleaseIndex("0");
  }, [choices.length, releaseIndex]);

  useEffect(() => {
    if (environments.length === 0) return;
    const requested = environments.find((item) => item.application.id === applicationId && item.environment.id === environmentId);
    if (requested) return;
    const first = environments[0];
    setApplicationId(first.application.id);
    setEnvironmentId(first.environment.id);
  }, [applicationId, environmentId, environments]);

  useEffect(() => {
    if (!pending || !operation.data || !matchesOperation(pending, operation.data)) return;
    if (!isTerminal(operation.data.state)) return;
    if (operation.data.state === "Succeeded") {
      const engineLink = operation.data.links?.engine ?? pending.engineLink;
      if (step === "complete" && pending.engineLink === (engineLink ?? null)) return;
      const completed = { ...pending, engineLink: engineLink ?? null };
      savePendingProvisioning(workspaceId, completed);
      setPending(completed);
      setStep("complete");
    }
  }, [operation.data, pending, step, workspaceId]);

  const configureDataRequired = step === "configure" || step === "review";
  const currentOperation = pending && operation.data && matchesOperation(pending, operation.data) ? operation.data : undefined;

  const buildRequest = (previewDigest?: string | null, reviewedBuilderIntent?: RuntimeBuilderIntent | null): EngineProvisioningRequest => {
    if (handoffStatus === "pending" || handoffStatus === "missing") throw new Error("builder-handoff-unavailable");
    if (!selectedRelease || !selectedEnvironment || !options.data) throw new Error("provisioning-selection-unavailable");
    const intent = buildManagedElsaIntent(selectedRelease, options.data);
    const effectiveBuilderIntent = reviewedBuilderIntent !== undefined ? reviewedBuilderIntent : builderIntent;
    return {
      name: name.trim(),
      slug: slug.trim(),
      applicationId: selectedEnvironment.application.id,
      environmentId: selectedEnvironment.environment.id,
      intent,
      ...(selectedConfigurationId ? { runtimeConfigurationId: selectedConfigurationId } : {}),
      ...(effectiveBuilderIntent ? { builderIntent: effectiveBuilderIntent } : {}),
      ...(previewDigest !== undefined ? { previewDigest } : {})
    };
  };

  const preview = useMutation({
    mutationFn: () => previewEngineProvisioning(workspaceId, buildRequest()),
    onSuccess: (result) => {
      setPreviewResult(result);
      clearProvisioningAttempt(workspaceId);
      setRecoveryAttempt(null);
      setSubmittedAttempt(null);
      setIdempotencyKey(newIdempotencyKey());
      create.reset();
      setStep("review");
    }
  });

  const create = useMutation({
    mutationFn: (attempt: ProvisioningAttempt) => createEngineProvisioning(workspaceId, attempt.request, attempt.key),
    onSuccess: (accepted, attempt) => {
      const next: PendingProvisioning = {
        instanceId: accepted.instance.instanceId,
        operationId: accepted.operation.id,
        name: attempt.display.name,
        applicationName: attempt.display.applicationName,
        environmentName: attempt.display.environmentName,
        engineLink: accepted.links?.engine ?? null
      };
      const pendingSaved = savePendingProvisioning(workspaceId, next);
      if (pendingSaved) {
        clearProvisioningAttempt(workspaceId);
        setRecoveryAttempt(null);
      } else {
        setAttemptStorageError("The operation was accepted, but this browser could not preserve its progress for reload.");
      }
      setPending(next);
      setStep("provisioning");
      void queryClient.invalidateQueries({ queryKey: queryKeys.managedElsaInstances(workspaceId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.deploymentCockpit(workspaceId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.engineProvisioningTargets(workspaceId) });
    },
    onError: (error, attempt) => {
      if (isDefinitiveProvisioningError(error)) clearProvisioningAttempt(workspaceId);
      if (error instanceof ApiError && error.status === 409) {
        if (!recoveryAttempt || recoveryAttempt.key !== attempt.key) {
          setPreviewResult(null);
          setSubmittedAttempt(null);
          setStep("configure");
        }
      }
    }
  });

  const submitAttempt = (attempt: ProvisioningAttempt) => {
    if (!saveProvisioningAttempt(workspaceId, attempt)) {
      setAttemptStorageError("This browser could not preserve the request safely for retry. No provisioning request was sent.");
      return;
    }
    setAttemptStorageError(null);
    setSubmittedAttempt(attempt);
    create.reset();
    create.mutate(attempt);
  };

  useEffect(() => {
    if (!recoveryAttempt || pending || !workspaceId || !providerAvailable || !canManageSetup || create.isPending || create.isSuccess || create.isError) return;
    if (replayedAttemptKey.current === recoveryAttempt.key) return;
    replayedAttemptKey.current = recoveryAttempt.key;
    create.mutate(recoveryAttempt);
  }, [canManageSetup, create, pending, providerAvailable, recoveryAttempt, workspaceId]);

  if (workspace.isLoading) return <RequestStateView state="loading" title="Loading workspace context" />;
  if (workspace.isError) return <RequestStateView state="unexpected" title="Workspace context could not load" />;
  if (!workspaceId) return <EmptyState title="No workspace selected" description="Select a workspace before provisioning an engine." />;
  if (provisioning.isPending || (providerAvailable && permissions.isPending)) return <RequestStateView state="loading" title="Checking engine provisioning" />;
  if (provisioning.isError) return <RequestStateView state="unexpected" title="Engine provisioning status unavailable" description="The console could not determine whether an engine provisioning module is enabled." />;
  if (!provisioning.hasProvider("azure")) {
    return <EmptyState title="Engine provisioning is unavailable" description="This workspace does not have an enabled engine provisioning provider. Connect an existing engine or ask an administrator to enable a provider." icon={<Server aria-hidden className="h-5 w-5" />} />;
  }
  if (permissions.isError) return <RequestStateView state="unexpected" title="Engine provisioning access could not be checked" />;
  if (!canManageSetup) {
    return <RequestStateView state="unauthorized" title="Engine setup permission required" description="You need deployment setup permission to provision an engine in this workspace." />;
  }
  if (recoveryAttempt && !pending) {
    const definitiveRecoveryError = create.isError && isDefinitiveProvisioningError(create.error);
    return (
      <section className="mx-auto max-w-5xl space-y-6">
        <nav aria-label="Breadcrumb" className="flex items-center gap-2 text-sm text-muted-foreground"><Link to="/admin/overview" className="hover:text-foreground">Workspace</Link><span aria-hidden>/</span><span className="text-foreground">Provision engine</span></nav>
        <RecoveryView attempt={recoveryAttempt} error={create.isError ? createError(create.error) : attemptStorageError} loading={create.isPending} definitiveError={definitiveRecoveryError} onRetry={() => submitAttempt(recoveryAttempt)} onReset={() => { clearProvisioningAttempt(workspaceId); setRecoveryAttempt(null); setSubmittedAttempt(null); setAttemptStorageError(null); create.reset(); setPreviewResult(null); setStep("configure"); setIdempotencyKey(newIdempotencyKey()); }} />
      </section>
    );
  }
  if (!configureDataRequired) {
    const resumed = pending;
    if (!resumed) return <RequestStateView state="unexpected" title="Provisioning operation could not be resumed" />;
    return (
      <section className="mx-auto max-w-5xl space-y-6">
        <nav aria-label="Breadcrumb" className="flex items-center gap-2 text-sm text-muted-foreground"><Link to="/admin/overview" className="hover:text-foreground">Workspace</Link><span aria-hidden>/</span><span className="text-foreground">Provision engine</span></nav>
        {step === "provisioning" ? <ProvisioningView pending={resumed} operation={currentOperation} loading={operation.isLoading} error={operation.isError || (operation.data && !matchesOperation(resumed, operation.data)) ? "Provisioning status could not be refreshed." : null} onRetry={() => void operation.refetch()} onBack={() => { clearPendingProvisioning(workspaceId); setPending(null); setPreviewResult(null); setStep("configure"); setIdempotencyKey(newIdempotencyKey()); }} /> : <CompleteView pending={resumed} operation={currentOperation} onAnother={() => { clearPendingProvisioning(workspaceId); setPending(null); setPreviewResult(null); setStep("configure"); setIdempotencyKey(newIdempotencyKey()); }} />}
      </section>
    );
  }
  if (options.isLoading || cockpit.isLoading || configurations.isLoading || targets.isLoading) return <RequestStateView state="loading" title="Loading provisioning choices" />;
  if (options.isError) return <RequestStateView state="unexpected" title="Provisioning choices could not load" description={onboardingError(options.error)} />;
  if (cockpit.isError) return <RequestStateView state="unexpected" title="Applications and environments could not load" />;
  if (targets.isError) return <RequestStateView state="unexpected" title="Provisioning targets could not load" description="The server could not determine which application environments are eligible for a new engine." />;
  if (configurations.isError) return <RequestStateView state="unexpected" title="Saved runtime configurations could not load" />;
  if (!options.data) return <RequestStateView state="unexpected" title="Provisioning choices are empty" />;
  if (choices.length === 0) return <EmptyState title="No managed Elsa releases are available" description="Provisioning needs a release and topology from the workspace onboarding catalog." />;
  if (environments.length === 0) return <EmptyState title="No compatible environments are available" description="Provisioning is offered only for application environments currently eligible on the server." action={<Link to="/admin/deployments/applications" className={buttonClassName("secondary")}>Review applications</Link>} />;
  const onboardingOptions = options.data;

  const isBusy = preview.isPending || create.isPending;
  const canReview = Boolean(name.trim() && slug.trim() && selectedRelease && selectedEnvironment && handoffStatus !== "pending" && handoffStatus !== "missing" && (!previewReleaseRequired || previewConsent));
  const reviewedAttempt = previewResult && selectedEnvironment ? {
    key: idempotencyKey,
    request: buildRequest(previewResult.previewDigest, previewResult.builderIntent),
    display: {
      name: name.trim(),
      applicationName: selectedEnvironment.application.name,
      environmentName: selectedEnvironment.environment.name
    }
  } satisfies ProvisioningAttempt : null;

  return (
    <section className="mx-auto max-w-5xl space-y-6">
      <nav aria-label="Breadcrumb" className="flex items-center gap-2 text-sm text-muted-foreground">
        <Link to="/admin/overview" className="hover:text-foreground">Workspace</Link>
        <span aria-hidden>/</span>
        <span className="text-foreground">Provision engine</span>
      </nav>
      {step === "configure" ? (
        <ConfigureView
          name={name}
          slug={slug}
          applicationId={selectedApplicationId}
          environmentId={selectedEnvironmentId}
          releaseIndex={releaseIndex}
          selectedConfigurationId={selectedConfigurationId}
          selectedConfiguration={selectedConfiguration}
          builderIntent={builderIntent}
          environments={environments}
          applicationEnvironments={applicationEnvironments}
          choices={choices}
          configurations={configurations.data ?? []}
          workspaceId={workspaceId}
          launchProfile={onboardingOptions.launchProfile}
          previewReleaseRequired={previewReleaseRequired}
          previewConsent={previewConsent}
          canReview={canReview}
          customizationOpen={customizationOpen}
          handoffStatus={handoffStatus}
          disabled={isBusy}
          error={preview.isError ? previewError(preview.error) : create.isError ? createError(create.error) : null}
          onNameChange={(value) => { setName(value); if (!slug || slug === slugify(name)) setSlug(slugify(value)); }}
          onSlugChange={(value) => setSlug(slugify(value))}
          onApplicationChange={(value) => {
            setApplicationId(value);
            const first = environments.find((item) => item.application.id === value);
            setEnvironmentId(first?.environment.id ?? "");
          }}
          onEnvironmentChange={setEnvironmentId}
          onReleaseChange={(value) => { setReleaseIndex(value); setPreviewConsent(false); }}
          onConfigurationChange={(value) => { setSelectedConfigurationId(value); setBuilderIntent(undefined); setCustomizationOpen(false); }}
          onCustomize={() => {
            if (!builderIntent && selectedConfiguration) setBuilderIntent(selectedConfiguration.intent);
            setCustomizationOpen(true);
          }}
          onCustomizationClose={() => setCustomizationOpen(false)}
          onBuilderIntentChange={setBuilderIntent}
          onPreviewConsentChange={setPreviewConsent}
          onSubmit={() => preview.mutate()}
        />
      ) : null}
      {step === "review" && previewResult ? (
        <ReviewView
          name={name}
          slug={slug}
          selectedEnvironment={selectedEnvironment!}
          selectedRelease={selectedRelease!}
          selectedConfiguration={selectedConfiguration}
          preview={previewResult}
          error={attemptStorageError ?? (create.isError ? createError(create.error) : null)}
          disabled={create.isPending || !previewResult.canProvision}
          onBack={() => {
            setStep("configure");
          }}
          onProvision={() => { const attempt = submittedAttempt ?? reviewedAttempt; if (attempt) submitAttempt(attempt); }}
        />
      ) : null}
    </section>
  );
}

function ConfigureView({
  name, slug, applicationId, environmentId, releaseIndex, selectedConfigurationId, selectedConfiguration, builderIntent,
  environments, applicationEnvironments, choices, configurations, workspaceId, launchProfile, previewReleaseRequired, previewConsent, canReview, customizationOpen, disabled, error,
  handoffStatus,
  onNameChange, onSlugChange, onApplicationChange, onEnvironmentChange, onReleaseChange, onConfigurationChange, onCustomize, onCustomizationClose, onBuilderIntentChange, onPreviewConsentChange, onSubmit
}: {
  name: string;
  slug: string;
  applicationId: string;
  environmentId: string;
  releaseIndex: string;
  selectedConfigurationId: string;
  selectedConfiguration: RuntimeConfiguration | null;
  builderIntent?: RuntimeBuilderIntent;
  environments: EmptyEnvironment[];
  applicationEnvironments: EmptyEnvironment[];
  choices: Array<ManagedElsaReleaseOption & { previewManifestDigest?: string | null }>;
  configurations: RuntimeConfiguration[];
  workspaceId: string;
  launchProfile: ManagedElsaOnboardingOptions["launchProfile"];
  previewReleaseRequired: boolean;
  previewConsent: boolean;
  canReview: boolean;
  customizationOpen: boolean;
  handoffStatus: HandoffStatus;
  disabled: boolean;
  error: string | null;
  onNameChange: (value: string) => void;
  onSlugChange: (value: string) => void;
  onApplicationChange: (value: string) => void;
  onEnvironmentChange: (value: string) => void;
  onReleaseChange: (value: string) => void;
  onConfigurationChange: (value: string) => void;
  onCustomize: () => void;
  onCustomizationClose: () => void;
  onBuilderIntentChange: (intent: RuntimeBuilderIntent | undefined) => void;
  onPreviewConsentChange: (value: boolean) => void;
  onSubmit: () => void;
}) {
  return (
    <>
      <PageHeading kicker="New engine" title="Provision an engine" description="Choose where your engine runs and what it includes." />
      <ProvisionSteps active="configure" />
      <form className="space-y-5" onSubmit={(event) => { event.preventDefault(); onSubmit(); }} noValidate>
        <Panel number="01" title="Engine">
          <div className="grid gap-4 md:grid-cols-[1.2fr_1fr_1fr]">
            <Field label="Engine name"><Input aria-label="Engine name" required maxLength={200} value={name} onChange={(event) => onNameChange(event.target.value)} placeholder="Name this engine" disabled={disabled} /></Field>
            <Field label="Engine address" hint="Lowercase letters, numbers, and hyphens."><Input aria-label="Engine address" required pattern="[a-z0-9]+(?:-[a-z0-9]+)*" maxLength={63} value={slug} onChange={(event) => onSlugChange(event.target.value)} placeholder="engine-address" disabled={disabled} /></Field>
            <Field label="Release"><Select aria-label="Release" className="w-full" value={releaseIndex} onChange={(event) => onReleaseChange(event.target.value)} disabled={disabled}>{choices.map((choice, index) => <option key={releaseKey(choice)} value={index}>Elsa {choice.version} · {choice.releaseLine} · {choice.channel} · {choice.topologyId}{choice.previewManifestDigest ? " · Preview" : ""}</option>)}</Select></Field>
          </div>
          {previewReleaseRequired ? <label className="mt-4 flex items-start gap-2 rounded-ui border border-warning/40 bg-warning/5 p-3 text-sm text-warning"><input type="checkbox" className="mt-1 h-4 w-4 accent-primary" checked={previewConsent} onChange={(event) => onPreviewConsentChange(event.target.checked)} disabled={disabled} /><span>Use this Preview release for this engine. Preview releases have no availability SLO.</span></label> : null}
        </Panel>

        <Panel number="02" title="Placement">
          <div className="grid gap-4 md:grid-cols-2">
            <Field label="Application"><Select aria-label="Application" className="w-full" value={applicationId} onChange={(event) => onApplicationChange(event.target.value)} disabled={disabled}>{[...new Map(environments.map((item) => [item.application.id, item.application])).values()].map((application) => <option key={application.id} value={application.id}>{application.name}</option>)}</Select></Field>
            <Field label="Environment" hint="Only environments currently eligible for a new engine are shown."><Select aria-label="Environment" className="w-full" value={environmentId} onChange={(event) => onEnvironmentChange(event.target.value)} disabled={disabled}>{applicationEnvironments.map((item) => <option key={item.environment.id} value={item.environment.id}>{item.environment.name}</option>)}</Select></Field>
          </div>
        </Panel>

        <Panel number="03" title="Runtime configuration">
          <div className="grid gap-4 md:grid-cols-[minmax(0,1fr)_16rem] md:items-end">
            <Field label="Saved configuration" hint="Compatibility is checked before provisioning."><Select aria-label="Saved configuration" className="w-full" value={selectedConfigurationId} onChange={(event) => onConfigurationChange(event.target.value)} disabled={disabled}><option value="">Control-managed default</option>{configurations.map((configuration) => <option key={configuration.id} value={configuration.id}>{configuration.name}</option>)}</Select></Field>
            <Link to="/admin/runtime-builder" className={buttonClassName("secondary")}><Pencil className="h-4 w-4" />Open Runtime Builder</Link>
          </div>
          <ConfigurationSummary configuration={selectedConfiguration} builderIntent={builderIntent} />
          <div className="mt-4 flex flex-wrap items-center justify-between gap-3"><p className="text-sm text-muted-foreground">Adjust selected runtime features before the server preview.</p><SecondaryButton type="button" onClick={customizationOpen ? onCustomizationClose : onCustomize} disabled={disabled || handoffStatus === "pending" || handoffStatus === "missing" || (!builderIntent && !selectedConfiguration)}>{customizationOpen ? "Close customization" : builderIntent || selectedConfiguration ? "Customize features" : "Choose a saved configuration to customize"}</SecondaryButton></div>
          {customizationOpen ? <InlineRuntimeCustomization workspaceId={workspaceId} intent={builderIntent ?? selectedConfiguration?.intent ?? null} onChange={onBuilderIntentChange} /> : null}
        </Panel>

        <Panel number="04" title="Hosting">
          <div className="flex flex-wrap items-start gap-4 rounded-ui border border-border bg-background p-4">
            <div className="grid h-10 w-10 place-items-center rounded-ui border border-border bg-surface text-primary"><Rocket aria-hidden className="h-5 w-5" /></div>
            <div className="min-w-0 flex-1"><p className="font-medium">{launchProfile.name}</p><p className="mt-1 text-sm text-muted-foreground">{launchProfile.description}</p></div>
            <dl className="grid min-w-[14rem] grid-cols-2 gap-x-5 gap-y-2 text-xs text-muted-foreground"><Detail label="Region" value={launchProfile.regionCode} /><Detail label="Capacity" value={launchProfile.capacityProfile} /><Detail label="Network" value={launchProfile.networkOutcome} /><Detail label="Domain" value={launchProfile.domainOutcome} /></dl>
          </div>
        </Panel>

        {handoffStatus === "pending" ? <div role="status" className="rounded-ui border border-primary/30 bg-primary/5 p-4 text-sm text-muted-foreground">Restoring the Runtime Builder snapshot before previewing this request…</div> : null}
        {handoffStatus === "missing" ? <ErrorNotice title="Runtime Builder handoff expired" message="This edited Builder configuration is no longer available in this browser session. Return to Runtime Builder and use Provision engine again; the request will not fall back to a saved or managed default." /> : null}
        {error ? <ErrorNotice title="Preview could not be created" message={error} /> : null}
        <div className="flex flex-col-reverse items-center justify-between gap-3 sm:flex-row"><Link to="/admin/overview" className="text-sm text-muted-foreground hover:text-foreground">Cancel</Link><Button type="submit" disabled={disabled || !canReview}>{disabled ? <LoaderCircle className="h-4 w-4 animate-spin" /> : <ArrowRight className="h-4 w-4" />}{disabled ? "Preparing preview…" : "Review deployment"}</Button></div>
      </form>
    </>
  );
}

function ReviewView({ name, slug, selectedEnvironment, selectedRelease, selectedConfiguration, preview, error, disabled, onBack, onProvision }: { name: string; slug: string; selectedEnvironment: EmptyEnvironment; selectedRelease: ManagedElsaReleaseOption & { previewManifestDigest?: string | null }; selectedConfiguration: RuntimeConfiguration | null; preview: EngineProvisioningPreviewResponse; error: string | null; disabled: boolean; onBack: () => void; onProvision: () => void }) {
  return (
    <>
      <PageHeading kicker="Review deployment" title="Ready to provision" description="Review the deployment before provisioning your engine." />
      <ProvisionSteps active="review" />
      <Panel title="Deployment summary">
        <dl className="divide-y divide-border/70">
          <ReviewRow label="Engine" value={name} detail={`${selectedEnvironment.application.name} / ${selectedEnvironment.environment.name} · ${slug}`} />
          <ReviewRow label="Release" value={`Elsa ${selectedRelease.version}`} detail={`${selectedRelease.releaseLine} · ${selectedRelease.channel} · ${selectedRelease.topologyId}`} />
          <ReviewRow label="Runtime" value={preview.configurationName || selectedConfiguration?.name || "Control-managed default"} detail={runtimeSummary(preview.builderIntent)} />
        </dl>
        <details className="mt-4 border-t border-border/70 pt-4">
          <summary className="cursor-pointer text-sm font-medium">Technical details</summary>
          <dl className="mt-3 divide-y divide-border/70">
            <ReviewRow label="Preview digest" value={preview.previewDigest ?? "Server did not return a preview digest"} mono />
            <ReviewRow label="Configuration digest" value={preview.configurationDigest ?? "Server did not return a configuration digest"} mono />
          </dl>
        </details>
      </Panel>
      <IncludedRuntime intent={preview.builderIntent} />
      <Findings findings={preview.findings} />
      {error ? <ErrorNotice title="Provisioning needs attention" message={error} /> : null}
      <div className="flex flex-col-reverse items-center justify-between gap-3 sm:flex-row"><button type="button" className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground" onClick={onBack}><ArrowLeft className="h-4 w-4" />Edit configuration</button><Button type="button" disabled={disabled} onClick={onProvision}>{disabled ? <ShieldCheck className="h-4 w-4" /> : <Rocket className="h-4 w-4" />}{preview.canProvision ? "Provision engine" : "Resolve preview findings first"}</Button></div>
    </>
  );
}

function ProvisioningView({ pending, operation, loading, error, onRetry, onBack }: { pending: PendingProvisioning; operation?: ManagedElsaOperation; loading: boolean; error: string | null; onRetry: () => void; onBack: () => void }) {
  const state = operation?.state ?? "Accepted";
  const failed = state === "Failed" || state === "RecoveryRequired" || state === "Cancelled";
  return (
    <>
      <PageHeading kicker="Provisioning" title={failed ? "Provisioning needs attention" : "Your engine is on its way"} description={`${pending.name} · ${pending.applicationName} / ${pending.environmentName}`} />
      <ProvisionSteps active="provisioning" />
      <Panel title="Provisioning status">
        <div className="flex items-center gap-3 border-b border-border/70 pb-4"><span className={`grid h-9 w-9 place-items-center rounded-full border ${failed ? "border-destructive/40 bg-destructive/10 text-destructive" : state === "Succeeded" ? "border-success/40 bg-success/10 text-success" : "border-primary/40 bg-primary/10 text-primary"}`}>{failed ? <TriangleAlert className="h-5 w-5" /> : state === "Succeeded" ? <Check className="h-5 w-5" /> : <LoaderCircle className="h-5 w-5 animate-spin" />}</span><div><p className="font-medium">{operationLabel(operation, loading)}</p><p className="mt-1 text-sm text-muted-foreground">Provisioning continues if you leave this page.</p></div></div>
        <dl className="mt-4 grid gap-4 text-sm sm:grid-cols-2"><Detail label="Instance" value={pending.instanceId} mono /><Detail label="Operation" value={pending.operationId} mono /></dl>
        {error ? <div className="mt-4 flex flex-wrap items-center gap-3"><p role="alert" className="text-sm text-warning">{error}</p><SecondaryButton type="button" onClick={onRetry} disabled={loading}>Retry status</SecondaryButton></div> : null}
        {failed ? <p role="alert" className="mt-4 text-sm text-destructive">The operation did not complete. Review the operation status before trying again.</p> : null}
      </Panel>
      <div className="flex justify-between gap-3"><button type="button" className="text-sm text-muted-foreground hover:text-foreground" onClick={onBack}>Start another request</button>{pending.engineLink ? <EngineLink href={pending.engineLink} label="Open engine" /> : null}</div>
    </>
  );
}

function RecoveryView({ attempt, error, loading, definitiveError, onRetry, onReset }: { attempt: ProvisioningAttempt; error: string | null; loading: boolean; definitiveError: boolean; onRetry: () => void; onReset: () => void }) {
  return (
    <>
      <PageHeading kicker="Provisioning" title="Checking your engine request" description="Recovering the result of your previous request." />
      <ProvisionSteps active="provisioning" />
      <Panel title="Provisioning request">
        <div className="flex items-start gap-3"><span className="grid h-9 w-9 place-items-center rounded-full border border-primary/40 bg-primary/10 text-primary"><LoaderCircle className={`h-5 w-5 ${loading ? "animate-spin" : ""}`} /></span><div><p className="font-medium">Recovering the result of your previous request.</p><p className="mt-1 text-sm text-muted-foreground">{attempt.display.name} · {attempt.display.applicationName} / {attempt.display.environmentName}</p></div></div>
        <details className="mt-5 border-t border-border/70 pt-4"><summary className="cursor-pointer text-sm font-medium">Technical details</summary><dl className="mt-3 grid gap-4 text-sm sm:grid-cols-2"><Detail label="Request key" value={attempt.key} mono /></dl></details>
        {error ? <div className="mt-4 flex flex-wrap items-center gap-3"><p role="alert" className="text-sm text-warning">{error}</p><SecondaryButton type="button" onClick={onRetry} disabled={loading}>Retry request</SecondaryButton>{definitiveError ? <button type="button" className="text-sm text-muted-foreground hover:text-foreground" onClick={onReset}>Return to configuration</button> : null}</div> : null}
      </Panel>
    </>
  );
}

function CompleteView({ pending, operation, onAnother }: { pending: PendingProvisioning; operation?: ManagedElsaOperation; onAnother: () => void }) {
  const engineLink = operation?.links?.engine ?? pending.engineLink;
  return (
    <>
      <PageHeading kicker="Ready" title="Your engine is registered" description={`${pending.name} is available in ${pending.applicationName} / ${pending.environmentName}.`} />
      <ProvisionSteps active="complete" />
      <Panel title="Registration complete">
        <div className="flex items-start gap-3"><span className="grid h-9 w-9 place-items-center rounded-full border border-success/40 bg-success/10 text-success"><Check className="h-5 w-5" /></span><div><p className="font-medium">Provisioning is complete.</p><p className="mt-1 text-sm text-muted-foreground">View engine details to check its health and connection status.</p></div></div>
        <dl className="mt-5 grid gap-4 border-t border-border/70 pt-4 text-sm sm:grid-cols-2"><Detail label="Engine" value={pending.name} /><Detail label="Placement" value={`${pending.applicationName} / ${pending.environmentName}`} /><Detail label="Operation" value={pending.operationId} mono /><Detail label="State" value={operation?.state ?? "Succeeded"} /></dl>
      </Panel>
      <div className="flex flex-col-reverse items-center justify-between gap-3 sm:flex-row"><button type="button" className="text-sm text-muted-foreground hover:text-foreground" onClick={onAnother}>Provision another engine</button>{engineLink ? <EngineLink href={engineLink} label="View engine detail" /> : <Link to="/admin/runtimes" className={buttonClassName()}><ExternalLink className="h-4 w-4" />Open managed runtimes</Link>}</div>
    </>
  );
}

function PageHeading({ kicker, title, description }: { kicker: string; title: string; description?: string }) {
  return <header><p className="font-mono text-[10px] font-medium uppercase tracking-[0.18em] text-primary">▪ {kicker}</p><h1 className="mt-3 font-display text-3xl font-semibold tracking-tight md:text-4xl">{title}</h1>{description ? <p className="mt-3 max-w-3xl text-sm leading-6 text-muted-foreground md:text-base">{description}</p> : null}</header>;
}

function ProvisionSteps({ active }: { active: "configure" | "review" | "provisioning" | "complete" }) {
  const steps = [{ id: "configure", label: "Configure" }, { id: "review", label: "Review" }, { id: "provisioning", label: "Provision" }];
  const currentIndex = active === "complete" ? 3 : steps.findIndex((step) => step.id === active);
  return <ol aria-label="Provisioning progress" className="grid border-y border-border/70 sm:grid-cols-3">{steps.map((item, index) => <li key={item.id}><div className={`flex items-center gap-3 px-3 py-3 text-sm ${index === currentIndex ? "text-foreground" : index < currentIndex ? "text-primary" : "text-muted-foreground"}`}><span className={`grid h-6 w-6 place-items-center rounded-full border text-xs font-medium ${index <= currentIndex ? "border-primary bg-primary text-primary-foreground" : "border-border"}`}>{index < currentIndex ? <Check className="h-3.5 w-3.5" /> : index + 1}</span><span>{item.label}</span></div></li>)}</ol>;
}

function Panel({ number, title, children }: { number?: string; title: string; children: ReactNode }) {
  return <section className="rounded-ui border border-border bg-surface p-4 md:p-5"><div className="mb-4 flex items-center gap-3 border-b border-border/70 pb-3"><span className="font-mono text-[10px] tracking-[0.12em] text-muted-foreground">{number ?? ""}</span><h2 className="font-display text-base font-semibold">{title}</h2></div>{children}</section>;
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return <label className="block space-y-1.5 text-sm"><span className="font-medium">{label}</span>{children}{hint ? <span className="block text-xs leading-5 text-muted-foreground">{hint}</span> : null}</label>;
}

function InlineRuntimeCustomization({ workspaceId, intent, onChange }: { workspaceId: string; intent: RuntimeBuilderIntent | null; onChange: (intent: RuntimeBuilderIntent) => void }) {
  const catalog = useQuery<BuilderCatalog>({
    queryKey: queryKeys.runtimeBuilderCatalog(workspaceId),
    queryFn: () => getBuilderCatalog(workspaceId),
    enabled: Boolean(intent),
    retry: false
  });
  if (!intent) return <div className="mt-3 rounded-ui border border-dashed border-border p-4 text-sm text-muted-foreground">Choose a saved Runtime Builder configuration to customize its selected features. Managed defaults are resolved by the server preview.</div>;
  if (catalog.isLoading) return <div className="mt-3 rounded-ui border border-border bg-background p-4 text-sm text-muted-foreground">Loading the approved feature catalog…</div>;
  if (catalog.isError || !catalog.data) return <div className="mt-3 rounded-ui border border-warning/40 bg-warning/5 p-4 text-sm text-warning">The approved feature catalog could not be loaded. Review this runtime in Runtime Builder before provisioning.</div>;

  const choices = runtimeFeatureChoices(intent, catalog.data);
  if (choices.length === 0) return <div className="mt-3 rounded-ui border border-dashed border-border p-4 text-sm text-muted-foreground">This configuration does not include catalog features that can be customized inline. Use Runtime Builder to add packages or features.</div>;
  return <section className="mt-3 rounded-ui border border-border bg-background p-4" aria-label="Inline runtime feature customization"><div className="flex items-start justify-between gap-3"><div><h3 className="text-sm font-medium">Runtime features</h3><p className="mt-1 text-xs leading-5 text-muted-foreground">Changes apply to this engine; the saved configuration stays unchanged.</p></div><Badge>{choices.filter((choice) => choice.selected).length} selected</Badge></div><div className="mt-3 grid gap-2 md:grid-cols-2">{choices.map((choice) => <label key={choice.key} className={`flex items-start gap-3 rounded-ui border p-3 text-sm ${choice.selected ? "border-primary bg-primary/10" : "border-border"}`}><input type="checkbox" className="mt-1 h-4 w-4 rounded border-border" checked={choice.selected} onChange={(event) => onChange(toggleRuntimeFeature(intent, choice, event.target.checked))} /><span><span className="block font-medium">{choice.displayName}</span><span className="mt-1 block text-xs leading-5 text-muted-foreground">{choice.description || choice.featureId}</span><span className="mt-1 block text-[11px] text-muted-foreground">{choice.packageId} {choice.version}</span></span></label>)}</div></section>;
}

type RuntimeFeatureChoice = {
  key: string;
  sourceId: string;
  packageId: string;
  version: string;
  featureId: string;
  displayName: string;
  description?: string | null;
  selected: boolean;
};

function runtimeFeatureChoices(intent: RuntimeBuilderIntent, catalog: BuilderCatalog): RuntimeFeatureChoice[] {
  return intent.packages.flatMap((selection) => {
    const packageItem = catalog.packages.find((item) => item.source.id === selection.sourceId && item.packageId.toLowerCase() === selection.packageId.toLowerCase());
    const version = packageItem?.versions.find((item) => item.version === selection.version);
    return (version?.features ?? []).map((feature) => ({
      key: `${selection.sourceId}:${selection.packageId}:${selection.version}:${feature.featureId}`,
      sourceId: selection.sourceId,
      packageId: selection.packageId,
      version: selection.version,
      featureId: feature.featureId,
      displayName: feature.displayName,
      description: feature.description,
      selected: selection.selectedFeatures?.includes(feature.featureId) ?? false
    }));
  });
}

function toggleRuntimeFeature(intent: RuntimeBuilderIntent, choice: RuntimeFeatureChoice, selected: boolean): RuntimeBuilderIntent {
  return {
    ...intent,
    packages: intent.packages.map((selection) => {
      if (selection.sourceId !== choice.sourceId || selection.packageId.toLowerCase() !== choice.packageId.toLowerCase() || selection.version !== choice.version) return selection;
      const current = selection.selectedFeatures ?? [];
      const next = selected ? [...new Set([...current, choice.featureId])] : current.filter((featureId) => featureId !== choice.featureId);
      return { ...selection, selectedFeatures: next };
    })
  };
}

function ConfigurationSummary({ configuration, builderIntent }: { configuration: RuntimeConfiguration | null; builderIntent?: RuntimeBuilderIntent }) {
  const intent = builderIntent ?? configuration?.intent;
  return <div className="mt-4 rounded-ui border border-border bg-background p-4"><div className="flex flex-wrap items-start justify-between gap-3"><div><p className="font-medium">{configuration?.name ?? (builderIntent ? "Edited builder snapshot" : "Control-managed default")}</p><p className="mt-1 text-sm text-muted-foreground">{configuration ? configuration.description || "Saved Runtime Builder configuration." : builderIntent ? "The current Builder values will be reviewed by the server." : "Use the workspace's governed release and feature defaults."}</p></div><Badge>{intent ? `${intent.packages.length} package${intent.packages.length === 1 ? "" : "s"}` : "Managed defaults"}</Badge></div>{intent ? <p className="mt-3 text-xs text-muted-foreground">Runtime image: <span className="font-mono text-foreground">{intent.image.slug}</span> · {countIntentFeatures(intent)} selected feature{countIntentFeatures(intent) === 1 ? "" : "s"}</p> : null}</div>;
}

function IncludedRuntime({ intent }: { intent: RuntimeBuilderIntent | null }) {
  if (!intent) {
    return <Panel title="Included in this engine"><p className="text-sm text-muted-foreground">The selected release provides the base runtime.</p></Panel>;
  }

  return (
    <Panel title="Included in this engine">
      <div className="grid gap-4 text-sm sm:grid-cols-2">
        <Detail label="Runtime image" value={`${intent.image.slug}${intent.image.tag ? `:${intent.image.tag}` : ""}`} mono />
        <Detail label="Deployment target" value={intent.target || "Provider-managed"} />
      </div>
      <div className="mt-4 border-t border-border/70 pt-4">
        <p className="text-xs uppercase tracking-wide text-muted-foreground">Packages and features</p>
        {intent.packages.length === 0 ? <p className="mt-2 text-sm text-muted-foreground">No additional packages selected. The selected release provides the base runtime.</p> : (
          <ul className="mt-2 grid gap-2 sm:grid-cols-2">
            {intent.packages.map((selection) => (
              <li key={`${selection.sourceId}:${selection.packageId}:${selection.version}`} className="rounded-ui border border-border bg-background p-3">
                <p className="font-medium">{selection.packageId} <span className="font-mono text-xs text-muted-foreground">{selection.version}</span></p>
                <p className="mt-1 text-xs text-muted-foreground">{selection.selectedFeatures?.length ? selection.selectedFeatures.join(", ") : "No selected features"}</p>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Panel>
  );
}

function Findings({ findings }: { findings: EngineProvisioningFinding[] }) {
  if (findings.length === 0) return <div className="flex items-start gap-3 rounded-ui border border-success/40 bg-success/5 p-4 text-sm"><ShieldCheck className="mt-0.5 h-4 w-4 shrink-0 text-success" /><div><p className="font-medium">Preview passed</p><p className="mt-1 text-muted-foreground">Your configuration is compatible.</p></div></div>;
  return <section className="rounded-ui border border-border bg-surface p-4"><div className="flex items-center gap-2"><CircleAlert className="h-4 w-4 text-warning" /><h2 className="font-display text-base font-semibold">Compatibility notes</h2></div><p className="mt-1 text-xs text-muted-foreground">Resolve any errors before provisioning.</p><ul className="mt-3 space-y-2" aria-label="Preview findings">{findings.map((finding, index) => <li key={`${finding.code}:${index}`} className={`rounded-ui border px-3 py-3 text-sm ${findingSeverityClass(finding.severity)}`}><div className="flex flex-wrap items-center justify-between gap-2"><p className="font-medium">{finding.message}</p><Badge>{findingStatus(finding.severity)}</Badge></div><p className="mt-1 text-xs opacity-75">{finding.code}{finding.scope ? ` · ${finding.scope}` : ""}</p></li>)}</ul></section>;
}

function ReviewRow({ label, value, detail, mono = false }: { label: string; value: string; detail?: string; mono?: boolean }) {
  return <div className="flex flex-col gap-1 py-3 sm:flex-row sm:items-start sm:justify-between sm:gap-5"><dt className="text-sm text-muted-foreground">{label}</dt><dd className={`text-left sm:max-w-[68%] sm:text-right ${mono ? "break-all font-mono text-xs" : "text-sm"}`}>{value}{detail ? <span className="mt-1 block text-xs text-muted-foreground">{detail}</span> : null}</dd></div>;
}

function Detail({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return <div><dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt><dd className={`mt-1 ${mono ? "break-all font-mono text-xs" : "text-sm"}`}>{value}</dd></div>;
}

function ErrorNotice({ title, message }: { title: string; message: string }) {
  return <div role="alert" className="rounded-ui border border-destructive/40 bg-destructive/5 p-4 text-sm"><p className="font-medium text-destructive">{title}</p><p className="mt-1 text-muted-foreground">{message}</p></div>;
}

function EngineLink({ href, label }: { href: string; label: string }) {
  if (href.startsWith("/")) return <Link to={href} className={buttonClassName()}><ExternalLink className="h-4 w-4" />{label}</Link>;
  return <a href={href} target="_blank" rel="noreferrer" className={buttonClassName()}><ExternalLink className="h-4 w-4" />{label}</a>;
}

function availableEnvironments(cockpit: DeploymentCockpit | undefined, targets: EngineProvisioningTarget[] | undefined): EmptyEnvironment[] {
  if (!cockpit || !targets) return [];
  const eligible = new Set(targets.map((target) => `${target.applicationId}:${target.environmentId}`));
  return cockpit.applications.flatMap((application) => application.environments
    .filter((environment) => eligible.has(`${application.id}:${environment.id}`))
    .map((environment) => ({ application, environment })));
}

function runtimeSummary(intent: RuntimeBuilderIntent | null) {
  if (!intent) return "Managed defaults";
  const featureCount = countIntentFeatures(intent);
  return `${intent.image.slug} · ${intent.packages.length} package${intent.packages.length === 1 ? "" : "s"} · ${featureCount} feature${featureCount === 1 ? "" : "s"}`;
}

function countIntentFeatures(intent: RuntimeBuilderIntent) {
  return intent.packages.reduce((total, selection) => total + (selection.selectedFeatures?.length ?? 0), 0);
}

function parseRouteState(value: unknown): ProvisionRouteState {
  if (!value || typeof value !== "object") return {};
  const state = value as Record<string, unknown>;
  return {
    workspaceId: typeof state.workspaceId === "string" ? state.workspaceId : undefined,
    runtimeConfigurationId: typeof state.runtimeConfigurationId === "string" ? state.runtimeConfigurationId : undefined,
    configurationName: typeof state.configurationName === "string" ? state.configurationName : undefined,
    builderHandoffToken: typeof state.builderHandoffToken === "string" ? state.builderHandoffToken : undefined
  };
}

function releaseKey(release: ManagedElsaReleaseOption & { previewManifestDigest?: string | null }) {
  return `${release.distributionId}|${release.releaseLine}|${release.version}|${release.channel}|${release.topologyId}|${release.previewManifestDigest ?? ""}`;
}

function findingSeverityClass(severity: string) {
  const normalized = severity.toLowerCase();
  if (normalized.includes("error") || normalized.includes("block")) return "border-destructive/40 bg-destructive/5 text-destructive";
  if (normalized.includes("warn")) return "border-warning/40 bg-warning/5 text-warning";
  return "border-border bg-background text-foreground";
}

function findingStatus(severity: string) {
  const normalized = severity.toLowerCase();
  if (normalized.includes("error") || normalized.includes("block") || normalized.includes("reject")) return "Rejected";
  if (normalized.includes("warn")) return "Needs review";
  return "Observed";
}

function operationLabel(operation: ManagedElsaOperation | undefined, loading: boolean) {
  if (loading) return "Checking status…";
  if (!operation) return "Accepted · waiting for worker";
  if (operation.state === "Failed") return `Failed${operation.failureCode ? ` (${operation.failureCode})` : ""}`;
  if (operation.state === "RecoveryRequired") return "Recovery required";
  if (operation.state === "Succeeded") return "Completed";
  return operation.state;
}

function isTerminal(state: ManagedElsaOperation["state"]) {
  return state === "Succeeded" || state === "Failed" || state === "RecoveryRequired" || state === "Cancelled";
}

function matchesOperation(pending: PendingProvisioning, operation: ManagedElsaOperation) {
  return pending.instanceId.toLowerCase() === operation.instanceId.toLowerCase() && pending.operationId.toLowerCase() === operation.id.toLowerCase();
}

function newIdempotencyKey() {
  return globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

function storageKey(workspaceId: string) { return `${pendingStoragePrefix}${workspaceId}`; }
function attemptStorageKey(workspaceId: string) { return `${attemptStoragePrefix}${workspaceId}`; }

function loadPendingProvisioning(workspaceId: string): PendingProvisioning | null {
  if (!workspaceId || typeof window === "undefined") return null;
  try {
    const value = JSON.parse(window.sessionStorage.getItem(storageKey(workspaceId)) ?? "null") as PendingProvisioning | null;
    return value && typeof value.instanceId === "string" && typeof value.operationId === "string" && typeof value.name === "string" ? value : null;
  } catch { return null; }
}

function savePendingProvisioning(workspaceId: string, value: PendingProvisioning) {
  if (!workspaceId || typeof window === "undefined") return false;
  try {
    const serialized = JSON.stringify(value);
    window.sessionStorage.setItem(storageKey(workspaceId), serialized);
    return window.sessionStorage.getItem(storageKey(workspaceId)) === serialized;
  } catch { return false; }
}

function clearPendingProvisioning(workspaceId: string) {
  try { window.sessionStorage.removeItem(storageKey(workspaceId)); } catch { /* no-op */ }
}

function loadProvisioningAttempt(workspaceId: string): ProvisioningAttempt | null {
  if (!workspaceId || typeof window === "undefined") return null;
  try {
    const value = JSON.parse(window.sessionStorage.getItem(attemptStorageKey(workspaceId)) ?? "null") as ProvisioningAttempt | null;
    return isProvisioningAttempt(value) ? value : null;
  } catch { return null; }
}

function saveProvisioningAttempt(workspaceId: string, value: ProvisioningAttempt) {
  if (!workspaceId || typeof window === "undefined") return false;
  try {
    const serialized = JSON.stringify(value);
    window.sessionStorage.setItem(attemptStorageKey(workspaceId), serialized);
    return window.sessionStorage.getItem(attemptStorageKey(workspaceId)) === serialized;
  } catch { return false; }
}

function clearProvisioningAttempt(workspaceId: string) {
  try { window.sessionStorage.removeItem(attemptStorageKey(workspaceId)); } catch { /* no-op */ }
}

function isProvisioningAttempt(value: ProvisioningAttempt | null): value is ProvisioningAttempt {
  return !!value && typeof value.key === "string" && value.key.length > 0 &&
    !!value.request && typeof value.request.name === "string" && typeof value.request.slug === "string" &&
    typeof value.request.applicationId === "string" && typeof value.request.environmentId === "string" &&
    !!value.request.intent && typeof value.display?.name === "string" &&
    typeof value.display.applicationName === "string" && typeof value.display.environmentName === "string";
}

function isDefinitiveProvisioningError(error: unknown) {
  return error instanceof ApiError && error.status !== undefined && [400, 403, 409, 422].includes(error.status);
}

function previewError(error: unknown) {
  if (error instanceof ApiError && error.status === 403) return "You do not have permission to preview engine provisioning.";
  if (error instanceof ApiError && error.status === 409) return "The selected application environment changed while preparing the preview. Refresh the page and try again.";
  return "The server could not preview this engine request. Review the selections and try again.";
}

function createError(error: unknown) {
  if (error instanceof ApiError && error.status === 403) return "You do not have permission to provision an engine.";
  if (error instanceof ApiError && error.status === 409) return "The preview is stale. Return to configuration and create a fresh preview before provisioning.";
  if (error instanceof ApiError && error.status === 422) return error.message || "The engine request was rejected by the provider compatibility checks.";
  return "The request result could not be confirmed. Retry to recover it when the Control API is available.";
}

function onboardingError(error: unknown) {
  if (error instanceof ApiError && error.status === 403) return "You do not have permission to view managed hosting options.";
  if (error instanceof ApiError && error.status === 422) return "Managed hosting is not enabled for this organization.";
  return "Managed hosting options could not be loaded from Elsa Control.";
}
