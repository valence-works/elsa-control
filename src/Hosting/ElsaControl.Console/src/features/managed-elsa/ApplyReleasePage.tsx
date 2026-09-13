import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, ExternalLink, LoaderCircle, ShieldAlert } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { Button, EmptyState, Select, buttonClassName } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";
import { cn } from "@/lib/utils";
import { releaseCatalogCopy } from "@/features/release-catalog/releaseCatalogModels";
import {
  getManagedElsaInstance,
  getManagedElsaInstanceHealth,
  getManagedElsaOnboardingOptions,
  getManagedElsaOperation,
  updateManagedElsaInstanceIntent
} from "@/features/managed-elsa/managedElsaApi";
import {
  openManagedElsaInstance
} from "@/features/managed-elsa/ManagedElsaInstancesPage";
import { OpenFailureModeHelp, OpenFailureNotice } from "@/features/managed-elsa/OpenFailureNotice";
import {
  classifyInstanceOpenFailure,
  classifyOpenFailureFromError,
  type OpenFailureClassification
} from "@/features/managed-elsa/openFailureTaxonomy";
import {
  operationalHealthGuidance,
  type ManagedElsaAccepted,
  type ManagedElsaInstance,
  type ManagedElsaInstanceIntent,
  type ManagedElsaOnboardingOptions
} from "@/features/managed-elsa/managedElsaModels";
import { buildManagedElsaIntent, onboardingChoices } from "@/features/managed-elsa/managedElsaOnboarding";

type ApplyRelease = ReturnType<typeof onboardingChoices>[number];

export function ApplyReleasePage() {
  const { instanceId = "" } = useParams();
  const [searchParams] = useSearchParams();
  const requestedVersion = searchParams.get("requestedVersion") ?? searchParams.get("apply") ?? "";
  const { selectedWorkspaceId, isLoading: workspaceLoading } = useWorkspaceContext();
  const queryClient = useQueryClient();
  const [releaseKey, setReleaseKey] = useState("");
  const [previewConsent, setPreviewConsent] = useState(false);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [idempotencyKey] = useState(() => crypto.randomUUID());
  const [accepted, setAccepted] = useState<ManagedElsaAccepted | null>(null);
  const [openError, setOpenError] = useState<OpenFailureClassification | null>(null);

  const instance = useQuery({
    queryKey: queryKeys.managedElsaInstance(selectedWorkspaceId, instanceId),
    queryFn: () => getManagedElsaInstance(selectedWorkspaceId, instanceId),
    enabled: Boolean(selectedWorkspaceId && instanceId),
    retry: false,
    refetchInterval: accepted ? 2000 : false
  });
  const options = useQuery({
    queryKey: queryKeys.managedElsaOnboardingOptions(selectedWorkspaceId),
    queryFn: () => getManagedElsaOnboardingOptions(selectedWorkspaceId),
    enabled: Boolean(selectedWorkspaceId),
    retry: false
  });
  const choices = useMemo(() => onboardingChoices(options.data), [options.data]);
  const selected = choices.find((choice) => choiceKey(choice) === releaseKey) ?? null;
  const previewRequired = Boolean(selected?.previewManifestDigest);
  const requestedNotAdmitted = Boolean(requestedVersion) && !choices.some((choice) => choice.version === requestedVersion);

  useEffect(() => {
    if (!choices.length || releaseKey) return;
    const preferred = choices.find((choice) => choice.version === requestedVersion) ?? choices[0];
    if (preferred) setReleaseKey(choiceKey(preferred));
  }, [choices, releaseKey, requestedVersion]);

  useEffect(() => {
    setPreviewConsent(false);
  }, [releaseKey]);

  const apply = useMutation({
    mutationFn: () => {
      if (!selected || !options.data || !instance.data)
        throw new Error("apply-selection-unavailable");
      if (requestedNotAdmitted || !isAdmittedChoice(selected, options.data))
        throw new ApplyNotAdmittedError();
      if (!instance.data.eTag)
        throw new Error("instance.if-match-required");
      if (previewRequired && !previewConsent)
        throw new Error("preview-consent-required");
      return updateManagedElsaInstanceIntent(
        selectedWorkspaceId,
        instanceId,
        { intent: applyIntent(selected, options.data, instance.data.intent ?? null), reason: "Apply admitted release" },
        instance.data.eTag,
        idempotencyKey
      );
    },
    onSuccess: (result) => {
      setAccepted(result);
      void queryClient.invalidateQueries({ queryKey: queryKeys.managedElsaInstances(selectedWorkspaceId) });
    }
  });

  const operation = useQuery({
    queryKey: queryKeys.managedElsaOperation(selectedWorkspaceId, instanceId, accepted?.operation.id ?? ""),
    queryFn: () => getManagedElsaOperation(selectedWorkspaceId, instanceId, accepted!.operation.id),
    enabled: Boolean(selectedWorkspaceId && instanceId && accepted),
    retry: false,
    refetchInterval: (query) => {
      const current = query.state.data;
      return current && isTerminal(current.state) ? false : 2000;
    }
  });
  const health = useQuery({
    queryKey: queryKeys.managedElsaInstanceHealth(selectedWorkspaceId, instanceId),
    queryFn: () => getManagedElsaInstanceHealth(selectedWorkspaceId, instanceId),
    enabled: Boolean(selectedWorkspaceId && instanceId && accepted),
    retry: false,
    refetchInterval: 2000
  });

  if (workspaceLoading || instance.isLoading || options.isLoading)
    return <RequestStateView state="loading" title="Loading apply release" />;
  if (!selectedWorkspaceId)
    return <EmptyState title="No workspace selected" description="Select a workspace before applying an admitted release." />;
  if (instance.isError)
    return <RequestStateView state="unexpected" title="Instance could not load" description="Refresh and try again when Elsa Control is available." />;
  if (options.isError)
    return <RequestStateView state="unexpected" title="Admitted releases could not load" description="The apply picker only lists admitted catalog identities." />;
  if (!instance.data)
    return <EmptyState title="Instance not found" description="This managed instance is no longer available." />;

  const current = instance.data;
  const applyBlocked = apply.isPending || Boolean(accepted && !isTerminal(operation.data?.state ?? accepted.operation.state));
  const canApply = Boolean(selected && current.eTag && (!previewRequired || previewConsent) && !requestedNotAdmitted && !applyBlocked);
  const healthyReady = current.health === "Healthy" && current.observedLifecycle === "Ready";
  const openEligible = current.canOpen && healthyReady;

  return (
    <section className="space-y-6">
      <nav aria-label="Breadcrumb" className="flex flex-wrap items-center gap-2 text-sm text-muted-foreground">
        <Link to="/admin/runtimes" className="hover:text-foreground">Instances</Link>
        <span aria-hidden>/</span>
        <span>{current.name}</span>
        <span aria-hidden>/</span>
        <span className="text-foreground">Apply</span>
      </nav>

      <header className="max-w-3xl space-y-2">
        <p className="text-xs font-medium uppercase tracking-[0.16em] text-primary">Admit → Apply → Open</p>
        <h1 className="font-display text-3xl font-semibold tracking-normal md:text-4xl">Apply release</h1>
        <p className="text-sm leading-6 text-muted-foreground md:text-base">
          Apply an admitted catalog entry to this instance. {releaseCatalogCopy.admitBlockedUntilAdmitted} {releaseCatalogCopy.happyPath}
        </p>
      </header>

      <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_18rem]">
        <div className="space-y-5">
          <section className="rounded-ui border border-border bg-surface p-5">
            <h2 className="font-display text-lg font-semibold">{current.name}</h2>
            <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-2">
              <Detail label="Status" value={`${current.health} / ${current.observedLifecycle}`} />
              <Detail label="Current" value={currentReleaseLabel(current)} />
              <Detail label="Topology" value={current.intent?.application.topologyId ?? "—"} />
              <Detail label="Lifecycle" value={current.intent?.release.channel ?? current.desiredLifecycle} />
              <Detail label="Open" value={openEligibility(current, openEligible)} />
              {!openEligible ? (
                <div className="sm:col-span-2">
                  <OpenFailureNotice failure={blockedOpenFailure(current, healthyReady)} compact />
                </div>
              ) : null}
            </dl>
          </section>

          <form
            className="space-y-4 rounded-ui border border-border bg-surface p-5"
            onSubmit={(event) => {
              event.preventDefault();
              if (!canApply) return;
              apply.mutate();
            }}
          >
            <label className="block space-y-1.5 text-sm">
              <span className="font-medium">Admitted release</span>
              <Select
                aria-label="Admitted release"
                className="w-full"
                value={releaseKey}
                onChange={(event) => setReleaseKey(event.target.value)}
                disabled={applyBlocked || choices.length === 0}
              >
                {choices.length === 0 ? <option value="">No admitted releases</option> : null}
                {choices.map((choice) => (
                  <option key={choiceKey(choice)} value={choiceKey(choice)}>
                    {choice.version} · {choice.channel} · {choice.topologyId}
                  </option>
                ))}
              </Select>
            </label>

            {selected ? (
              <div className="rounded-ui border border-primary/20 bg-primary/5 p-4 text-sm">
                <p className="font-medium">Selected · {selected.version}</p>
                <p className="mt-1 text-muted-foreground">
                  {selected.releaseLine} · {selected.channel} · {selected.topologyId}
                  {selected.previewManifestDigest ? ` · ${selected.previewManifestDigest}` : " · registry paid"}
                </p>
              </div>
            ) : null}

            {previewRequired ? (
              <label className="flex items-start gap-3 rounded-ui border border-warning/40 bg-warning/10 p-4 text-sm">
                <input
                  type="checkbox"
                  className="mt-1"
                  checked={previewConsent}
                  onChange={(event) => setPreviewConsent(event.target.checked)}
                  disabled={applyBlocked}
                />
                <span>
                  <span className="font-medium">I understand this is a Preview release with no SLO.</span>
                  <span className="mt-1 block text-muted-foreground">Continue apply to {current.name}.</span>
                </span>
              </label>
            ) : null}

            {requestedNotAdmitted || choices.length === 0 ? (
              <div role="alert" className="flex items-start gap-3 rounded-ui border border-warning/40 bg-warning/10 p-4 text-sm">
                <ShieldAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-warning" />
                <div>
                  <p className="font-medium">Apply is blocked until the release is admitted</p>
                  <p className="mt-1 text-muted-foreground">{releaseCatalogCopy.applyNotAdmitted}</p>
                  <Link to="/admin/releases" className="mt-2 inline-block underline underline-offset-2">Admit from Releases</Link>
                </div>
              </div>
            ) : null}

            {apply.isError ? <ApplyError error={apply.error} /> : null}

            <p className="text-xs text-muted-foreground">
              Apply uses UpdateIntent and reconciles with If-Match and a fresh Idempotency-Key. If the release is not admitted, apply stays blocked.
            </p>

            <div className="flex flex-wrap gap-3">
              <Button type="submit" disabled={!canApply}>
                {apply.isPending ? <LoaderCircle aria-hidden className="h-4 w-4 animate-spin" /> : null}
                {apply.isPending ? "Applying…" : "Apply release"}
              </Button>
              <Link to="/admin/runtimes" className={buttonClassName("secondary")}>Cancel</Link>
            </div>
          </form>

          <details className="rounded-ui border border-border bg-surface p-4" open={advancedOpen} onToggle={(event) => setAdvancedOpen(event.currentTarget.open)}>
            <summary className="cursor-pointer text-sm font-medium">Advanced (digests / raw intent)</summary>
            <pre className="mt-3 overflow-x-auto text-xs text-muted-foreground">
              {JSON.stringify({
                selected,
                eTag: current.eTag,
                idempotencyKey,
                intent: selected && options.data ? applyIntent(selected, options.data, current.intent ?? null) : current.intent
              }, null, 2)}
            </pre>
          </details>
        </div>

        <aside className="space-y-4">
          <ol aria-label="Admit Apply Open" className="space-y-3 rounded-ui border border-border bg-surface p-4 text-sm">
            <Step done label="Admit release to catalog" />
            <Step active={!accepted} done={Boolean(accepted)} label="Apply to instance" />
            <Step active={Boolean(accepted) && !healthyReady} done={healthyReady} label="Wait Healthy / Ready" />
            <Step active={healthyReady} done={openEligible} label="Open Studio (handoff)" />
          </ol>
          <p className="text-xs text-muted-foreground">{releaseCatalogCopy.openEdge}</p>
          <OpenFailureModeHelp />
          {accepted ? (
            <section className="rounded-ui border border-border bg-surface p-4 text-sm">
              <p className="font-medium">Reconcile</p>
              <p className="mt-1 text-muted-foreground">Operation {operation.data?.state ?? accepted.operation.state}</p>
              {health.data ? <p className="mt-2 text-muted-foreground">{operationalHealthGuidance[health.data.status] ?? health.data.status}</p> : null}
              {operation.isError ? <p role="alert" className="mt-2 text-warning">Operation status could not be refreshed.</p> : null}
            </section>
          ) : null}
          {openEligible ? (
            <div className="space-y-2">
              <Button
                type="button"
                onClick={() => {
                  setOpenError(null);
                  try {
                    openManagedElsaInstance(current);
                  } catch (error) {
                    setOpenError(classifyOpenFailureFromError(error, current));
                  }
                }}
              >
                <ExternalLink aria-hidden className="h-4 w-4" />
                Open
              </Button>
              <p className="text-xs text-muted-foreground">Open is offered after Healthy/Ready and handoff. This does not claim Studio success.</p>
              {openError ? <OpenFailureNotice failure={openError} /> : null}
            </div>
          ) : (
            <OpenFailureNotice failure={blockedOpenFailure(current, healthyReady)} compact />
          )}
        </aside>
      </div>
    </section>
  );
}

function ApplyError({ error }: { error: unknown }) {
  if (error instanceof ApplyNotAdmittedError) {
    return (
      <div role="alert" className="rounded-ui border border-warning/40 bg-warning/10 p-4 text-sm">
        <p className="font-medium">Apply refused</p>
        <p className="mt-1 text-muted-foreground">{releaseCatalogCopy.applyNotAdmitted}</p>
      </div>
    );
  }
  const catalogUnavailable = error instanceof ApiError && problemLooksLikeCatalogUnavailable(error);
  return (
    <div role="alert" className="rounded-ui border border-destructive/40 bg-destructive/5 p-4 text-sm">
      <p className="font-medium">{catalogUnavailable ? "The selected release is not admitted" : "Apply could not start"}</p>
      <p className="mt-1 text-muted-foreground">
        {catalogUnavailable ? releaseCatalogCopy.applyNotAdmitted : error instanceof Error ? error.message : "Retry after the current operation finishes."}
      </p>
    </div>
  );
}

function Step({ label, active = false, done = false }: { label: string; active?: boolean; done?: boolean }) {
  return (
    <li className={cn("flex items-center gap-3", done ? "text-primary" : active ? "text-foreground" : "text-muted-foreground")}>
      <span className={cn("grid h-6 w-6 place-items-center rounded-full border text-xs", done || active ? "border-primary bg-primary text-primary-foreground" : "border-border")}>
        {done ? <Check aria-hidden className="h-3.5 w-3.5" /> : null}
      </span>
      {label}
    </li>
  );
}

function Detail({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className="mt-1">{value}</dd>
    </div>
  );
}

function currentReleaseLabel(instance: ManagedElsaInstance) {
  return instance.currentResolvedRelease?.version
    ?? instance.intent?.release.requestedVersion
    ?? "Unknown current release";
}

function openEligibility(instance: ManagedElsaInstance, openEligible: boolean) {
  if (openEligible) return "Eligible when handoff + health hold";
  return blockedOpenFailure(instance, instance.health === "Healthy" && instance.observedLifecycle === "Ready").shortLabel;
}

function blockedOpenFailure(instance: ManagedElsaInstance, healthyReady: boolean): OpenFailureClassification {
  return classifyInstanceOpenFailure({
    ...instance,
    canOpen: instance.canOpen && healthyReady,
    identityBindingState: instance.canOpen && !healthyReady ? "instance-unavailable" : instance.identityBindingState,
    health: healthyReady ? instance.health : "Unknown"
  });
}

function applyIntent(
  selected: ApplyRelease,
  options: ManagedElsaOnboardingOptions,
  current: ManagedElsaInstanceIntent | null
): ManagedElsaInstanceIntent {
  const built = buildManagedElsaIntent(selected, options);
  if (!current) return built;
  const { previewManifestDigest: _previousPreview, ...currentRelease } = current.release;
  return {
    ...current,
    release: {
      ...currentRelease,
      distributionId: selected.distributionId,
      releaseLine: selected.releaseLine,
      requestedVersion: selected.version,
      channel: selected.channel,
      ...(selected.previewManifestDigest ? { previewManifestDigest: selected.previewManifestDigest } : {})
    },
    application: {
      ...current.application,
      topologyId: selected.topologyId
    },
    desiredLifecycle: "Running"
  };
}

function isAdmittedChoice(selected: ApplyRelease, options: ManagedElsaOnboardingOptions) {
  return onboardingChoices(options).some((choice) => choiceKey(choice) === choiceKey(selected));
}

function choiceKey(choice: ApplyRelease) {
  return `${choice.distributionId}|${choice.releaseLine}|${choice.version}|${choice.channel}|${choice.topologyId}|${choice.previewManifestDigest ?? ""}`;
}

function isTerminal(state: string) {
  return ["Succeeded", "Failed", "RecoveryRequired", "Cancelled"].includes(state);
}

function problemLooksLikeCatalogUnavailable(error: ApiError) {
  const details = error.details && typeof error.details === "object" ? error.details as { code?: string } : null;
  return details?.code === "instance.catalog-selection-unavailable" || error.status === 422;
}

class ApplyNotAdmittedError extends Error {
  constructor() {
    super(releaseCatalogCopy.applyNotAdmitted);
    this.name = "ApplyNotAdmittedError";
  }
}
