import { useState } from "react";
import type { FormEvent } from "react";
import { useMutation } from "@tanstack/react-query";
import { CheckCircle2, Copy, Plus } from "lucide-react";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { Button, Input, SecondaryButton } from "@/components/ui";
import { ApiError } from "@/lib/api/httpClient";
import { useAuth } from "@/lib/auth/AuthProvider";
import { createAdminOrganization, type AdminOrganizationCreateResponse } from "@/features/organizations/organizationApi";

export function AdminOrganizationsPage() {
  const auth = useAuth();
  const [name, setName] = useState("");
  const [ownerAccountId, setOwnerAccountId] = useState("");
  const [created, setCreated] = useState<AdminOrganizationCreateResponse | null>(null);
  const create = useMutation({
    mutationFn: () => createAdminOrganization({
      name,
      ...(ownerAccountId.trim() ? { ownerAccountId: ownerAccountId.trim() } : {})
    }),
    onSuccess: response => setCreated(response)
  });

  if (!auth.session?.isAdmin) {
    return <RequestStateView state="unauthorized" title="Operator access required" description="Organization creation is available only to Control administrators." />;
  }

  const error = create.error instanceof ApiError ? create.error.message : create.error ? "The organization could not be created." : null;
  const submit = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setCreated(null);
    create.mutate();
  };

  return (
    <section className="mx-auto max-w-4xl space-y-6">
      <header className="max-w-3xl space-y-2">
        <p className="text-xs font-medium uppercase tracking-[0.16em] text-primary">Operator administration</p>
        <h1 className="font-display text-3xl font-semibold tracking-normal md:text-4xl">Organizations</h1>
        <p className="text-sm leading-6 text-muted-foreground md:text-base">Create a named organization with its default workspace and assign an owner for controlled operational work.</p>
      </header>

      <form onSubmit={submit} className="rounded-ui border border-border bg-surface p-5 space-y-5">
        <div>
          <label htmlFor="organization-name" className="text-sm font-medium">Organization name</label>
          <Input id="organization-name" value={name} onChange={event => setName(event.target.value)} placeholder="Acme staging" required className="mt-2" />
          <p className="mt-2 text-xs text-muted-foreground">Required. The default workspace uses this name.</p>
        </div>
        <div>
          <label htmlFor="organization-owner" className="text-sm font-medium">Owner account ID <span className="font-normal text-muted-foreground">(optional)</span></label>
          <Input id="organization-owner" value={ownerAccountId} onChange={event => setOwnerAccountId(event.target.value)} placeholder="Defaults to the current operator" className="mt-2 font-mono text-xs" />
          <p className="mt-2 text-xs text-muted-foreground">Use an existing account ID when assigning ownership to someone else.</p>
        </div>
        {error ? <p role="alert" className="rounded-ui border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">{error}</p> : null}
        <Button type="submit" disabled={create.isPending || !name.trim()}><Plus aria-hidden size={16} />{create.isPending ? "Creating…" : "Create organization"}</Button>
      </form>

      {created ? <CreationResult result={created} /> : null}
    </section>
  );
}

function CreationResult({ result }: { result: AdminOrganizationCreateResponse }) {
  const copy = async (value: string) => {
    await navigator.clipboard?.writeText(value);
  };
  return (
    <section role="status" className="rounded-ui border border-primary/25 bg-primary/5 p-5">
      <div className="flex items-start gap-3"><CheckCircle2 aria-hidden className="mt-0.5 h-5 w-5 text-primary" /><div><h2 className="text-base font-semibold">Organization created</h2><p className="mt-1 text-sm text-muted-foreground">Save these identifiers for the next operational step.</p></div></div>
      <dl className="mt-5 grid gap-3 sm:grid-cols-3">
        <IdField label="Organization ID" value={result.organizationId} onCopy={copy} />
        <IdField label="Workspace ID" value={result.workspaceId} onCopy={copy} />
        <IdField label="Owner account ID" value={result.ownerAccountId} onCopy={copy} />
      </dl>
    </section>
  );
}

function IdField({ label, value, onCopy }: { label: string; value: string; onCopy: (value: string) => Promise<void> }) {
  return <div className="rounded-ui border border-border bg-background p-3"><dt className="text-[10px] font-medium uppercase tracking-[0.12em] text-muted-foreground">{label}</dt><dd className="mt-2 flex items-center gap-2 font-mono text-xs break-all"><span>{value}</span><SecondaryButton type="button" aria-label={`Copy ${label}`} onClick={() => onCopy(value)} className="ml-auto min-h-8 px-2"><Copy aria-hidden size={14} /></SecondaryButton></dd></div>;
}
