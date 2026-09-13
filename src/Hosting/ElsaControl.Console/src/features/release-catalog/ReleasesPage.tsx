import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CheckCircle2, Copy, ExternalLink, LoaderCircle, ShieldAlert, TriangleAlert, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { Badge, Button, EmptyState, Input, SecondaryButton, Select, Table, buttonClassName } from "@/components/ui";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";
import { cn } from "@/lib/utils";
import {
  admitReleaseManifest,
  isReleaseCatalogIdentityConflict,
  listWorkspaceReleaseCatalog,
  releaseCatalogProblem
} from "@/features/release-catalog/releaseCatalogApi";
import {
  releaseCatalogCopy,
  type ReleaseCatalogAdmissionResponse,
  type ReleaseCatalogEntry,
  type ReleaseCatalogIdentityFacts,
  type ReleaseCatalogProblem
} from "@/features/release-catalog/releaseCatalogModels";
import {
  catalogEntryMatchesExistingFocus,
  existingCatalogHref,
  factsFromCatalogEntries,
  fingerprintsUnchanged,
  formatBuildIdentity,
  identityTuple,
  parseProducerFacts
} from "@/features/release-catalog/releaseManifestFacts";

const digestPattern = /^sha256:[0-9a-f]{64}$/i;

export function ReleasesPage() {
  const { selectedWorkspaceId, isLoading: workspaceLoading } = useWorkspaceContext();
  const queryClient = useQueryClient();
  const [searchParams] = useSearchParams();
  const existingFocus = searchParams.get("existing") ?? "";
  const [reference, setReference] = useState("");
  const [digest, setDigest] = useState("");
  const [payload, setPayload] = useState("");
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [lineFilter, setLineFilter] = useState("");
  const [lifecycleFilter, setLifecycleFilter] = useState("");
  const [topologyFilter, setTopologyFilter] = useState("");
  const [conflict, setConflict] = useState<ReleaseCatalogProblem | null>(null);
  const [outcome, setOutcome] = useState<ReleaseCatalogAdmissionResponse | null>(null);

  const incomingFacts = useMemo(() => parseProducerFacts(payload, digest), [digest, payload]);
  const catalog = useQuery({
    queryKey: queryKeys.releaseCatalog(selectedWorkspaceId, lineFilter, lifecycleFilter, topologyFilter),
    queryFn: () => listWorkspaceReleaseCatalog(selectedWorkspaceId, {
      releaseLine: lineFilter || undefined,
      lifecycle: lifecycleFilter || undefined,
      topologyId: topologyFilter || undefined
    }),
    enabled: Boolean(selectedWorkspaceId),
    retry: false
  });

  const admit = useMutation({
    mutationFn: () => admitReleaseManifest({
      reference: reference.trim(),
      digest: digest.trim(),
      payload
    }),
    onSuccess: (response) => {
      setConflict(null);
      setOutcome(response);
      void queryClient.invalidateQueries({ queryKey: ["release-catalog"] });
      void queryClient.invalidateQueries({ queryKey: queryKeys.managedElsaOnboardingOptions(selectedWorkspaceId) });
    },
    onError: (error) => {
      setOutcome(null);
      if (isReleaseCatalogIdentityConflict(error)) {
        setConflict(releaseCatalogProblem(error));
        return;
      }
      setConflict(null);
    }
  });

  if (workspaceLoading)
    return <RequestStateView state="loading" title="Loading workspace context" />;
  if (!selectedWorkspaceId)
    return <EmptyState title="No workspace selected" description="Select a workspace before listing admitted catalog identities." />;

  const canAdmit = Boolean(reference.trim() && digestPattern.test(digest.trim()) && payload.trim()) && !admit.isPending;
  const existingFacts = factsFromCatalogEntries(conflict?.existing)
    ?? matchingCatalogFacts(catalog.data, incomingFacts);
  const incomingConflictFacts = factsFromCatalogEntries(conflict?.incoming) ?? incomingFacts;
  const identical = fingerprintsUnchanged(existingFacts, incomingConflictFacts);

  return (
    <section className="space-y-6">
      <header className="max-w-3xl space-y-2">
        <p className="text-xs font-medium uppercase tracking-[0.16em] text-primary">Admit</p>
        <h1 className="font-display text-3xl font-semibold tracking-normal md:text-4xl">Admit release</h1>
        <p className="text-sm leading-6 text-muted-foreground md:text-base">
          Admit a signed producer manifest into the governed catalog. Preview identities must be unique (include build.N). {releaseCatalogCopy.admitBlockedUntilAdmitted} {releaseCatalogCopy.happyPath}
        </p>
      </header>

      <form
        className="space-y-5 rounded-ui border border-border bg-surface p-5"
        onSubmit={(event) => {
          event.preventDefault();
          if (!canAdmit) return;
          admit.mutate();
        }}
      >
        <label className="block space-y-1.5 text-sm">
          <span className="font-medium">OCI reference</span>
          <Input
            aria-label="OCI reference"
            value={reference}
            onChange={(event) => setReference(event.target.value)}
            placeholder="oci://valenceruntimeimages.azurecr.io/release-manifests/release-manifest@sha256:…"
            disabled={admit.isPending}
          />
        </label>
        <label className="block space-y-1.5 text-sm">
          <span className="font-medium">Manifest digest</span>
          <Input
            aria-label="Manifest digest"
            className="font-mono"
            value={digest}
            onChange={(event) => setDigest(event.target.value)}
            placeholder="sha256:…"
            disabled={admit.isPending}
          />
        </label>
        <label className="block space-y-1.5 text-sm">
          <span className="font-medium">Catalog lifecycle</span>
          <Input aria-label="Catalog lifecycle" value="preview" readOnly />
          <span className="block text-xs leading-5 text-muted-foreground">
            Control assigns catalog lifecycle. Internal Alpha admits as preview. Producer lifecycle stays evidence-only.
          </span>
        </label>
        <label className="block space-y-1.5 text-sm">
          <span className="font-medium">Signed manifest payload</span>
          <textarea
            aria-label="Signed manifest payload"
            className="min-h-36 w-full rounded-ui border border-border bg-background px-3 py-2 font-mono text-xs"
            value={payload}
            onChange={(event) => setPayload(event.target.value)}
            disabled={admit.isPending}
            placeholder="Paste the signed producer JSON payload."
          />
        </label>

        {incomingFacts ? <ProducerFacts facts={incomingFacts} /> : payload.trim() ? (
          <p role="status" className="text-sm text-muted-foreground">Payload is present but producer facts could not be read yet. Admit still sends the raw payload to the admin API.</p>
        ) : null}

        {outcome ? <AdmissionOutcome outcome={outcome} /> : null}
        {admit.isError && !conflict ? <AdmitError error={admit.error} /> : null}

        <div className="flex flex-wrap gap-3">
          <Button type="submit" disabled={!canAdmit}>
            {admit.isPending ? <LoaderCircle aria-hidden className="h-4 w-4 animate-spin" /> : null}
            {admit.isPending ? "Admitting…" : "Admit"}
          </Button>
          <SecondaryButton
            type="button"
            onClick={() => {
              setReference("");
              setDigest("");
              setPayload("");
              setOutcome(null);
              setConflict(null);
              admit.reset();
            }}
            disabled={admit.isPending}
          >
            Cancel
          </SecondaryButton>
        </div>
      </form>

      <section id="admitted-catalog" className="space-y-4">
        <div className="flex flex-col gap-3 md:flex-row md:items-end md:justify-between">
          <div>
            <h2 className="font-display text-xl font-semibold">Admitted catalog</h2>
            <p className="mt-1 text-sm text-muted-foreground">Filter identities that can be applied to a managed instance.</p>
          </div>
          <div className="flex flex-wrap gap-2">
            <Input aria-label="Filter release line" placeholder="Release line" value={lineFilter} onChange={(event) => setLineFilter(event.target.value)} />
            <Select aria-label="Filter catalog lifecycle" value={lifecycleFilter} onChange={(event) => setLifecycleFilter(event.target.value)}>
              <option value="">Any lifecycle</option>
              <option value="preview">preview</option>
              <option value="supported">supported</option>
            </Select>
            <Input aria-label="Filter topology" placeholder="Topology" value={topologyFilter} onChange={(event) => setTopologyFilter(event.target.value)} />
          </div>
        </div>
        {catalog.isLoading ? <RequestStateView state="loading" title="Loading admitted catalog" /> : null}
        {catalog.isError ? <RequestStateView state="unexpected" title="Admitted catalog could not load" description="Workspace catalog access is required to list admitted identities." /> : null}
        {catalog.data && catalog.data.length === 0 ? (
          <EmptyState title="No admitted identities" description="Admit a unique preview releaseVersion, including build.N, before Apply." />
        ) : null}
        {catalog.data && catalog.data.length > 0 ? <CatalogTable entries={catalog.data} focusKey={existingFocus} /> : null}
      </section>

      {conflict ? (
        <IdentityConflictDrawer
          problem={conflict}
          existing={existingFacts}
          incoming={incomingConflictFacts}
          identical={identical}
          advancedOpen={advancedOpen}
          onAdvancedOpenChange={setAdvancedOpen}
          onClose={() => setConflict(null)}
          onRetryIdentical={() => {
            if (!identical) return;
            admit.mutate();
          }}
          retrying={admit.isPending}
        />
      ) : null}
    </section>
  );
}

function ProducerFacts({ facts }: { facts: ReleaseCatalogIdentityFacts }) {
  return (
    <div className="rounded-ui border border-border bg-muted/20 p-4" aria-label="Producer facts">
      <p className="text-xs font-medium uppercase tracking-wide text-muted-foreground">Producer facts before commit</p>
      <dl className="mt-3 grid gap-3 text-sm sm:grid-cols-2">
        <Fact label="Release" value={facts.releaseVersion} />
        <Fact label="Line" value={facts.releaseLine} />
        <Fact label="Channel" value={facts.channel} />
        <Fact label="Lifecycle" value={facts.producerLifecycle || "—"} />
        <Fact label="Topologies" value={facts.topologies.join(", ") || "—"} />
        <Fact label="Capabilities" value={facts.capabilities.join(", ") || "—"} />
      </dl>
      <p className="mt-3 text-xs text-muted-foreground">{releaseCatalogCopy.admitBlockedUntilAdmitted}</p>
    </div>
  );
}

function AdmissionOutcome({ outcome }: { outcome: ReleaseCatalogAdmissionResponse }) {
  const stored = outcome.status === "Stored";
  return (
    <div role="status" className={cn("flex items-start gap-3 rounded-ui border p-4 text-sm", stored ? "border-primary/30 bg-primary/10" : "border-border bg-muted/30")}>
      <CheckCircle2 aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
      <div>
        <p className="font-medium">{stored ? "Stored" : "Unchanged"}</p>
        <p className="mt-1 text-muted-foreground">
          {stored
            ? "The signed manifest was admitted. Apply can now select this catalog identity."
            : "This exact projection already exists. No catalog row was overwritten."}
        </p>
        {outcome.entries[0] ? (
          <p className="mt-2 font-mono text-xs text-muted-foreground">{outcome.entries[0].distribution.releaseVersion} · {outcome.entries[0].distribution.catalogLifecycle}</p>
        ) : null}
      </div>
    </div>
  );
}

function AdmitError({ error }: { error: unknown }) {
  const forbidden = error instanceof ApiError && (error.status === 401 || error.status === 403);
  return (
    <div role="alert" className="flex items-start gap-3 rounded-ui border border-destructive/40 bg-destructive/5 p-4 text-sm">
      <ShieldAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <div>
        <p className="font-medium">{forbidden ? "Admit is not authorized" : "Release was not admitted"}</p>
        <p className="mt-1 text-muted-foreground">{forbidden ? releaseCatalogCopy.admitRequiresAdmin : error instanceof Error ? error.message : "The admin admit API rejected this request."}</p>
      </div>
    </div>
  );
}

function CatalogTable({ entries, focusKey }: { entries: ReleaseCatalogEntry[]; focusKey: string }) {
  const focusedRow = useRef<HTMLTableRowElement | null>(null);

  useEffect(() => {
    if (!focusKey || !focusedRow.current) return;
    focusedRow.current.scrollIntoView?.({ block: "nearest" });
    focusedRow.current.focus();
  }, [entries, focusKey]);

  const firstFocusIndex = entries.findIndex((entry) => catalogEntryMatchesExistingFocus(entry, focusKey));

  return (
    <Table>
      <table>
        <caption className="sr-only">Admitted catalog identities</caption>
        <thead>
          <tr>
            <th scope="col">Release</th>
            <th scope="col">Lifecycle</th>
            <th scope="col">Topology</th>
            <th scope="col">Digest</th>
            <th scope="col" className="text-right">Action</th>
          </tr>
        </thead>
        <tbody>
          {entries.map((entry, index) => {
            const focused = catalogEntryMatchesExistingFocus(entry, focusKey);
            const focusTarget = focused && index === firstFocusIndex;
            return (
              <tr
                key={`${catalogIdentityRowKey(entry)}`}
                id={catalogRowElementId(entry)}
                ref={focusTarget ? focusedRow : undefined}
                tabIndex={focusTarget ? -1 : undefined}
                aria-current={focused ? "true" : undefined}
                data-existing-focus={focused ? "true" : undefined}
                className={cn(focused && "bg-primary/10 outline outline-2 outline-offset-[-2px] outline-primary")}
              >
                <td>
                  <p className="font-medium">{entry.distribution.releaseVersion}</p>
                  <p className="mt-1 text-xs text-muted-foreground">{entry.distribution.releaseLine} · {entry.distribution.channel}</p>
                  {focused ? <p className="sr-only">Focused existing catalog identity</p> : null}
                </td>
                <td><Badge>{entry.distribution.catalogLifecycle}</Badge></td>
                <td>{entry.topology.id}</td>
                <td className="max-w-[16rem] truncate font-mono text-xs" title={entry.manifestDigest}>{entry.manifestDigest}</td>
                <td className="text-right">
                  <Link
                    to={`/admin/runtimes?apply=${encodeURIComponent(entry.distribution.releaseVersion)}`}
                    className={buttonClassName("secondary")}
                  >
                    Apply
                  </Link>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </Table>
  );
}

export function IdentityConflictDrawer({
  problem,
  existing,
  incoming,
  identical,
  advancedOpen,
  onAdvancedOpenChange,
  onClose,
  onRetryIdentical,
  retrying
}: {
  problem: ReleaseCatalogProblem;
  existing: ReleaseCatalogIdentityFacts | null;
  incoming: ReleaseCatalogIdentityFacts | null;
  identical: boolean;
  advancedOpen: boolean;
  onAdvancedOpenChange: (open: boolean) => void;
  onClose: () => void;
  onRetryIdentical: () => void;
  retrying: boolean;
}) {
  const primaryVersion = incoming?.releaseVersion ?? "";
  return (
    <div className="fixed inset-0 z-40 flex justify-end bg-foreground/20" role="presentation" onClick={onClose}>
      <aside
        role="dialog"
        aria-modal="true"
        aria-labelledby="identity-conflict-title"
        className="flex h-full w-full max-w-xl flex-col overflow-y-auto border-l border-border bg-surface p-5 shadow-xl"
        onClick={(event) => event.stopPropagation()}
      >
        <div className="flex items-start justify-between gap-3">
          <div>
            <p className="font-mono text-[10px] uppercase tracking-[0.16em] text-primary">releaseCatalog.identity.conflict</p>
            <h2 id="identity-conflict-title" className="mt-2 font-display text-2xl font-semibold">{releaseCatalogCopy.conflictTitle}</h2>
            <p className="mt-2 text-sm leading-6 text-muted-foreground">{releaseCatalogCopy.conflictBody}</p>
            <p className="mt-2 text-xs leading-5 text-muted-foreground">{releaseCatalogCopy.happyPath}</p>
          </div>
          <button type="button" className="console-icon-button" aria-label="Close conflict drawer" onClick={onClose}>
            <X aria-hidden size={18} />
          </button>
        </div>

        <div role="alert" className="mt-4 rounded-ui border border-warning/40 bg-warning/10 p-3 text-sm">
          {releaseCatalogCopy.admitBlockedUntilAdmitted}
        </div>

        <div className="mt-5 grid gap-4 sm:grid-cols-2">
          <CompareCard title="Existing (owns identity)" facts={existing} />
          <CompareCard title="Incoming (blocked)" facts={incoming} incoming />
        </div>

        {incoming || existing ? (
          <p className="mt-3 font-mono text-[11px] text-muted-foreground">
            Identity {identityTuple(incoming ?? existing!)}
          </p>
        ) : null}

        <ol className="mt-5 space-y-3" aria-label="Conflict recovery">
          <li className="rounded-ui border border-primary/40 bg-primary/5 p-4">
            <p className="text-sm font-semibold">A — Publish a new catalog identity <Badge className="ml-2 border-primary/30 bg-primary/10 text-primary">PRIMARY</Badge></p>
            <p className="mt-2 text-sm text-muted-foreground">{releaseCatalogCopy.primaryRecovery}</p>
          </li>
          <li className={cn("rounded-ui border p-4", identical ? "border-border bg-surface" : "border-border/70 bg-muted/20 opacity-70")}>
            <p className="text-sm font-semibold">B — Retry identical admit</p>
            <p className="mt-2 text-sm text-muted-foreground">
              {identical
                ? "Fingerprints match. Retrying will return Unchanged and will not overwrite the existing identity."
                : releaseCatalogCopy.idempotentRetry}
            </p>
            <SecondaryButton type="button" className="mt-3" disabled={!identical || retrying} onClick={onRetryIdentical}>
              {retrying ? "Retrying…" : "Retry identical admit"}
            </SecondaryButton>
          </li>
        </ol>

        <details className="mt-5 rounded-ui border border-border p-3" open={advancedOpen} onToggle={(event) => onAdvancedOpenChange(event.currentTarget.open)}>
          <summary className="cursor-pointer text-sm font-medium">Advanced (support)</summary>
          <pre className="mt-3 overflow-x-auto text-xs text-muted-foreground">{JSON.stringify(problem, null, 2)}</pre>
        </details>

        <p className="mt-4 text-xs text-muted-foreground">{releaseCatalogCopy.openEdge}</p>

        <div className="mt-6 flex flex-wrap justify-end gap-2">
          <SecondaryButton type="button" onClick={() => void copyText(primaryVersion || releaseCatalogCopy.primaryRecovery)}>
            <Copy aria-hidden className="h-4 w-4" />
            Copy PRIMARY
          </SecondaryButton>
          <Link to={existingCatalogHref(existing)} className={buttonClassName("secondary")} onClick={onClose}>
            Open existing
          </Link>
          <Button type="button" onClick={onClose}>Close</Button>
        </div>
      </aside>
    </div>
  );
}

function CompareCard({ title, facts, incoming = false }: { title: string; facts: ReleaseCatalogIdentityFacts | null; incoming?: boolean }) {
  return (
    <section className={cn("rounded-ui border p-4", incoming ? "border-destructive/30 bg-destructive/5" : "border-border bg-muted/20")}>
      <h3 className="text-xs font-semibold uppercase tracking-wide text-muted-foreground">{title}</h3>
      {facts ? (
        <dl className="mt-3 space-y-2 text-sm">
          <Fact label="Release" value={facts.releaseVersion} />
          <Fact label="Build" value={formatBuildIdentity(facts)} />
          <Fact label="Line" value={facts.releaseLine} />
          <Fact label="Channel" value={facts.channel} />
          <Fact label="Digest" value={facts.manifestDigest || "—"} mono />
          <Fact label="Source run" value={facts.sourceRunId || "—"} />
          <Fact label="Topologies" value={facts.topologies.join(", ") || "—"} />
        </dl>
      ) : (
        <p className="mt-3 text-sm text-muted-foreground">Facts are unavailable for this side.</p>
      )}
    </section>
  );
}

function Fact({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div data-fact={label.toLowerCase()}>
      <dt className="text-xs uppercase tracking-wide text-muted-foreground">{label}</dt>
      <dd className={cn("mt-0.5 break-all", mono ? "font-mono text-xs" : "text-sm")}>{value}</dd>
    </div>
  );
}

function catalogRowElementId(entry: ReleaseCatalogEntry) {
  return `catalog-row-${catalogIdentityRowKey(entry)}`.replace(/[^a-zA-Z0-9_-]/g, "-");
}

function matchingCatalogFacts(entries: ReleaseCatalogEntry[] | undefined, incoming: ReleaseCatalogIdentityFacts | null) {
  if (!entries || !incoming) return null;
  const matches = entries.filter((entry) =>
    entry.distribution.releaseVersion.toLowerCase() === incoming.releaseVersion.toLowerCase()
    && entry.distribution.releaseLine.toLowerCase() === incoming.releaseLine.toLowerCase()
    && entry.distribution.id.toLowerCase() === incoming.distributionId.toLowerCase()
  );
  return factsFromCatalogEntries(matches);
}

function catalogIdentityRowKey(entry: ReleaseCatalogEntry) {
  return `${entry.distribution.releaseVersion}|${entry.topology.id}|${entry.manifestDigest}`;
}

async function copyText(value: string) {
  try {
    await navigator.clipboard.writeText(value);
  } catch {
    // Clipboard is optional ops convenience.
  }
}

export function AdmitApplyHint() {
  return (
    <p className="text-sm text-muted-foreground">
      <TriangleAlert aria-hidden className="mr-1 inline h-3.5 w-3.5" />
      {releaseCatalogCopy.admitBlockedUntilAdmitted}
      {" "}
      <Link to="/admin/runtimes" className="underline underline-offset-2">Open instances</Link>
      <ExternalLink aria-hidden className="ml-1 inline h-3 w-3" />
    </p>
  );
}
