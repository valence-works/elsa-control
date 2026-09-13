import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, ExternalLink, LoaderCircle, RefreshCw, ShieldAlert, TriangleAlert } from "lucide-react";
import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { Badge, Button, EmptyState, SecondaryButton, Table, buttonClassName } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { getDeploymentPermissions } from "@/features/deployments/deploymentApi";
import type { WorkspaceDeploymentPermissionsResponse } from "@/features/deployments/deploymentModels";
import { issueManagedElsaHandoff, listManagedElsaInstances } from "@/features/managed-elsa/managedElsaApi";
import { managedElsaHandoffTokenType, type ManagedElsaInstance } from "@/features/managed-elsa/managedElsaModels";
import { useEngineProvisioningProviders } from "@/features/engine-provisioning/useEngineProvisioningProviders";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";
import { cn } from "@/lib/utils";

export function ManagedElsaInstancesPage() {
  const { selectedWorkspaceId, isLoading: workspaceLoading } = useWorkspaceContext();
  const [searchParams] = useSearchParams();
  const handoffContinuation = useMemo(
    () => parseHandoffContinuation(searchParams),
    [searchParams]
  );
  const [continuationScrubbed, setContinuationScrubbed] = useState(() => !handoffContinuation);
  useLayoutEffect(() => {
    if (!handoffContinuation)
      return;
    // Do this before enabling the authenticated list query. The state and
    // challenge are safe correlation values, but should not survive in a URL
    // that can be copied or placed in a referrer.
    window.history.replaceState(window.history.state, document.title, window.location.pathname + window.location.hash);
    setContinuationScrubbed(true);
  }, [handoffContinuation]);
  const instances = useQuery({
    queryKey: queryKeys.managedElsaInstances(selectedWorkspaceId),
    queryFn: () => listManagedElsaInstances(selectedWorkspaceId),
    enabled: Boolean(selectedWorkspaceId) && continuationScrubbed,
    retry: false
  });
  const provisioningProviders = useEngineProvisioningProviders(selectedWorkspaceId, continuationScrubbed);
  const provisioningPermissions = useQuery<WorkspaceDeploymentPermissionsResponse>({
    queryKey: queryKeys.deploymentPermissions(selectedWorkspaceId),
    queryFn: () => getDeploymentPermissions(selectedWorkspaceId),
    enabled: Boolean(selectedWorkspaceId) && continuationScrubbed && provisioningProviders.hasProvider("azure"),
    retry: false
  });
  const [openingInstanceId, setOpeningInstanceId] = useState<string | null>(null);
  const [openError, setOpenError] = useState<string | null>(null);
  const handledContinuation = useRef<string | null>(null);

  useEffect(() => {
    if (!handoffContinuation || workspaceLoading || instances.isLoading || instances.isError || !instances.data)
      return;
    if (handledContinuation.current === handoffContinuation.key)
      return;
    handledContinuation.current = handoffContinuation.key;
    // History was already scrubbed in layout. Do not navigate to /admin/runtimes
    // here: that remounts the console list and is the bounce that leaves Open on
    // the SPA instead of posting code+state to the runtime callback.

    const instance = instances.data.items.find((item) => item.instanceId === handoffContinuation.instanceId);
    if (!instance) {
      setOpenError("This managed instance is no longer available. Refresh the page and try again.");
      return;
    }

    if (handoffContinuation.failureStatus) {
      if (handoffContinuation.failureStatus === 401) {
        try {
          if (retryExpiredHandoff(instance))
            return;
        } catch {
          setOpenError("This managed instance is no longer available. Refresh the page and try again.");
          return;
        }
      }
      setOpenError(managedElsaHandoffFailure(handoffContinuation.failureStatus));
      return;
    }

    if (!instance.canOpen || !instance.audience || !instance.redirectUri ||
        !handoffContinuation.state || !handoffContinuation.codeChallenge) {
      setOpenError("Elsa Control could not verify the managed-instance handoff. Open the instance again.");
      return;
    }

    setOpenError(null);
    setOpeningInstanceId(instance.instanceId);
    // The runtime generated the state and retains the verifier in its protected
    // correlation state. Control only issues for the challenge and posts code/state.
    void issueAndSubmitHandoff(instance, handoffContinuation.state, handoffContinuation.codeChallenge)
      .catch((error) => setOpenError(managedElsaOpenError(error)))
      .finally(() => setOpeningInstanceId(null));
  }, [handoffContinuation, instances.data, instances.isError, instances.isLoading, workspaceLoading]);

  if (workspaceLoading || instances.isLoading)
    return <RequestStateView state="loading" title="Loading managed Elsa instances" description="Checking current health and access." />;
  if (instances.isError)
    return <RequestStateView state="unexpected" title="Managed instances could not load" description="Try again when Elsa Control is available." />;

  const items = instances.data?.items ?? [];
  return (
    <section className="space-y-6">
      <div className="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div className="max-w-3xl space-y-2">
          <p className="text-xs font-medium uppercase tracking-[0.16em] text-primary">Operate</p>
          <h1 className="font-display text-3xl font-semibold tracking-normal md:text-4xl">Managed Elsa</h1>
          <p className="text-sm leading-6 text-muted-foreground md:text-base">
            Open a healthy managed instance with your current Control session. Instance access and callback details are resolved by Elsa Control.
          </p>
        </div>
        <SecondaryButton type="button" onClick={() => instances.refetch()} disabled={instances.isFetching}>
          <RefreshCw aria-hidden className={cn("h-4 w-4", instances.isFetching && "animate-spin")} />
          Refresh
        </SecondaryButton>
      </div>

      {openError ? (
        <div role="alert" className="flex items-start gap-3 rounded-ui border border-warning/30 bg-warning/10 p-4 text-sm">
          <ShieldAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-warning" />
          <div className="space-y-1">
            <p className="font-medium">The managed instance could not be opened</p>
            <p className="text-muted-foreground">{openError}</p>
          </div>
        </div>
      ) : null}

      {selectedWorkspaceId && provisioningProviders.hasProvider("azure") && provisioningPermissions.isSuccess && provisioningPermissions.data.permissions.includes("deployments.setup.manage") ? (
        <div className="flex justify-end">
          <Link to="/admin/engines/provision" className={buttonClassName("secondary")}>Provision engine</Link>
        </div>
      ) : null}

      {items.length === 0 ? (
        <EmptyState
          title="No managed Elsa instances"
          description="Authorized managed instances will appear here when they are provisioned."
        />
      ) : (
        <Table>
          <table className="min-w-full divide-y divide-border text-left text-sm">
            <caption className="sr-only">Managed Elsa instances</caption>
            <thead className="bg-muted/30 text-xs uppercase tracking-wide text-muted-foreground">
              <tr>
                <th scope="col" className="px-4 py-3 font-medium">Instance</th>
                <th scope="col" className="px-4 py-3 font-medium">Health</th>
                <th scope="col" className="px-4 py-3 font-medium">Lifecycle</th>
                <th scope="col" className="px-4 py-3 text-right font-medium">Action</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-border bg-surface">
              {items.map((instance) => (
                <ManagedElsaInstanceRow
                  key={instance.instanceId}
                  instance={instance}
                  opening={openingInstanceId === instance.instanceId}
                  onOpen={() => {
                    setOpenError(null);
                    setOpeningInstanceId(instance.instanceId);
                    try {
                      openManagedElsaInstance(instance);
                    } catch (error) {
                      setOpenError(managedElsaOpenError(error));
                      setOpeningInstanceId(null);
                    }
                  }}
                />
              ))}
            </tbody>
          </table>
        </Table>
      )}
    </section>
  );
}

function ManagedElsaInstanceRow({
  instance,
  opening,
  onOpen
}: {
  instance: ManagedElsaInstance;
  opening: boolean;
  onOpen: () => void;
}) {
  const available = instance.canOpen;
  return (
    <tr>
      <td className="px-4 py-4 align-top">
        <p className="font-medium">{instance.name}</p>
        <p className="mt-1 text-xs text-muted-foreground">{instance.slug}</p>
      </td>
      <td className="px-4 py-4 align-top">
        <InstanceHealthBadge instance={instance} />
      </td>
      <td className="px-4 py-4 align-top">
        <span className="text-muted-foreground">{instance.observedLifecycle}</span>
        {instance.unavailableReason ? <p className="mt-1 max-w-xs text-xs text-muted-foreground">{instance.unavailableReason}</p> : null}
      </td>
      <td className="px-4 py-4 text-right align-top">
        {available ? (
          <Button type="button" onClick={onOpen} disabled={opening}>
            {opening ? <LoaderCircle aria-hidden className="h-4 w-4 animate-spin" /> : <ExternalLink aria-hidden className="h-4 w-4" />}
            {opening ? "Opening…" : "Open"}
          </Button>
        ) : (
          <span className="text-xs text-muted-foreground">Unavailable</span>
        )}
      </td>
    </tr>
  );
}

function InstanceHealthBadge({ instance }: { instance: ManagedElsaInstance }) {
  const healthy = instance.health === "Healthy" && instance.observedLifecycle === "Ready";
  return (
    <Badge className={healthy ? "border-primary/30 bg-primary/10 text-primary" : "border-warning/30 bg-warning/10 text-warning"}>
      {healthy ? <CheckCircle2 aria-hidden className="mr-1 h-3 w-3" /> : <TriangleAlert aria-hidden className="mr-1 h-3 w-3" />}
      {instance.health}
    </Badge>
  );
}

/**
 * Starts the runtime-owned handoff initiation flow. The runtime creates and
 * protects the callback state and PKCE verifier before it redirects back to this
 * console continuation; Control never creates or receives the verifier.
 */
export function openManagedElsaInstance(instance: ManagedElsaInstance): void {
  if (!instance.canOpen || !instance.audience || !instance.redirectUri)
    throw new ManagedElsaOpenError("unavailable");

  clearExpiredHandoffRetry(instance.instanceId);
  const callbackUri = trustedCallbackUri(instance.redirectUri);
  const startUri = new URL("/managed-elsa/handoff/start", callbackUri.origin);
  window.location.assign(startUri.toString());
}

async function issueAndSubmitHandoff(instance: ManagedElsaInstance, state: string, codeChallenge: string) {
  const callbackUri = trustedCallbackUri(instance.redirectUri ?? "");
  const issue = await issueManagedElsaHandoff({
    organizationId: instance.organizationId,
    instanceId: instance.instanceId,
    audience: instance.audience ?? "",
    redirectUri: callbackUri.toString(),
    codeChallenge
  });
  // The API must echo the authoritative binding selected for this instance.
  // Never post a code to a callback that disagrees with the row we opened.
  if (!issue.token || issue.tokenType !== managedElsaHandoffTokenType ||
      issue.audience !== instance.audience || issue.redirectUri !== callbackUri.toString())
    throw new ManagedElsaOpenError("unavailable");
  submitHandoffForm(callbackUri, issue.token, state);
}

function retryExpiredHandoff(instance: ManagedElsaInstance) {
  if (!instance.canOpen || !instance.redirectUri)
    return false;

  if (!claimExpiredHandoffRetry(instance.instanceId))
    return false;

  const callbackUri = trustedCallbackUri(instance.redirectUri);
  const startUri = new URL("/managed-elsa/handoff/start", callbackUri.origin);
  window.location.assign(startUri.toString());
  return true;
}

/** Claims the single browser-session retry owned by Control for a 401 callback. */
export function claimExpiredHandoffRetry(instanceId: string) {
  const retryKey = `managed-elsa-handoff-retry:${instanceId}`;
  try {
    if (window.sessionStorage.getItem(retryKey) === "1")
      return false;
    window.sessionStorage.setItem(retryKey, "1");
    return true;
  } catch {
    return false;
  }
}

function clearExpiredHandoffRetry(instanceId: string) {
  try {
    window.sessionStorage.removeItem(`managed-elsa-handoff-retry:${instanceId}`);
  } catch {
    // Session storage is only a retry guard; the handoff remains safe without it.
  }
}

function trustedCallbackUri(value: string) {
  let uri: URL;
  try {
    uri = new URL(value);
  } catch {
    throw new ManagedElsaOpenError("unavailable");
  }
  // WHATWG URL implementations normally retain brackets in hostname for IPv6
  // literals; accept both representations so local-only HTTP does not depend
  // on a browser-specific serialization detail.
  const localHttp = uri.protocol === "http:" && ["localhost", "127.0.0.1", "[::1]", "::1"].includes(uri.hostname);
  if ((uri.protocol !== "https:" && !localHttp) || uri.pathname !== "/managed-elsa/handoff/callback" ||
      uri.username || uri.password || uri.search || uri.hash)
    throw new ManagedElsaOpenError("unavailable");
  return uri;
}

function submitHandoffForm(callbackUri: URL, token: string, state: string) {
  const form = document.createElement("form");
  form.method = "post";
  form.action = callbackUri.toString();
  form.style.display = "none";
  appendHiddenField(form, "code", token);
  appendHiddenField(form, "state", state);
  document.body.append(form);
  form.submit();
}

function appendHiddenField(form: HTMLFormElement, name: string, value: string) {
  const field = document.createElement("input");
  field.type = "hidden";
  field.name = name;
  field.value = value;
  form.append(field);
}

class ManagedElsaOpenError extends Error {
  constructor(public readonly reason: "unavailable") {
    super(reason);
  }
}

function managedElsaOpenError(error: unknown) {
  if (error instanceof ManagedElsaOpenError)
    return "This managed instance is no longer available. Refresh the page and try again.";
  if (error instanceof ApiError) {
    switch (error.status) {
      case 401:
        return "Your Control session could not authorize this handoff. Sign in again and retry.";
      case 403:
        return "This managed instance is no longer available to your account.";
      case 409:
        return "This handoff has already been used. Open the instance again to create a new link.";
      case 503:
        return "Managed Elsa is temporarily unavailable. Try again shortly.";
    }
  }
  return "Elsa Control could not open this managed instance. Try again shortly.";
}

function managedElsaHandoffFailure(status: number) {
  switch (status) {
    case 401:
      return "Your managed-instance link has expired. Open Elsa again to create a new link.";
    case 403:
      return "This managed instance is no longer available to your account.";
    case 409:
      return "This handoff has already been used. Open the instance again to create a new link.";
    case 503:
      return "Managed Elsa is temporarily unavailable. Try again shortly.";
    default:
      return "Elsa Control could not open this managed instance. Try again shortly.";
  }
}

type HandoffContinuation = {
  key: string;
  instanceId: string;
  state: string | null;
  codeChallenge: string | null;
  failureStatus: number | null;
};

function parseHandoffContinuation(params: URLSearchParams): HandoffContinuation | null {
  const instanceId = params.get("instance_id") ?? params.get("instanceId");
  const state = params.get("state");
  const codeChallenge = params.get("code_challenge") ?? params.get("codeChallenge");
  const rawStatus = params.get("handoff_status") ?? params.get("handoff_error") ?? params.get("error") ?? params.get("status");
  const failureStatus = rawStatus && /^(401|403|409|503)$/.test(rawStatus) ? Number(rawStatus) : null;
  if (!instanceId || (!state && !codeChallenge && !failureStatus))
    return null;
  return {
    key: [instanceId, state, codeChallenge, failureStatus].join("|"),
    instanceId,
    state: state && /^[A-Za-z0-9_-]{16,256}$/.test(state) ? state : null,
    codeChallenge: codeChallenge && /^[A-Za-z0-9_-]{43}$/.test(codeChallenge) ? codeChallenge : null,
    failureStatus
  };
}
