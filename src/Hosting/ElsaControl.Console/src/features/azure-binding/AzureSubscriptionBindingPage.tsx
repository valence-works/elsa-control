import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, CheckCircle2, CircleAlert, ExternalLink, LoaderCircle, ShieldCheck, TriangleAlert } from "lucide-react";
import { useEffect, useState } from "react";
import { useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { Badge, Button, Input } from "@/components/ui";
import {
  createAzureSubscriptionBind,
  getAzureSubscriptionBinding,
  verifyAzureSubscriptionBind
} from "@/features/azure-binding/azureBindingApi";
import type {
  AzureSubscriptionBindState,
  AzureSubscriptionBindingView,
  CreateAzureSubscriptionBindRequest
} from "@/features/azure-binding/azureBindingModels";
import { ApiError } from "@/lib/api/httpClient";
import { queryKeys } from "@/lib/query/queryClient";
import { cn } from "@/lib/utils";

type BindingStep = "review" | "progress" | "landing";

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function AzureSubscriptionBindingPage() {
  const workspace = useWorkspaceContext();
  const organization = workspace.selectedOrganization;
  const organizationId = organization?.id ?? "";
  const binding = useQuery({
    queryKey: queryKeys.azureSubscriptionBinding(organizationId),
    queryFn: () => getAzureSubscriptionBinding(organizationId),
    enabled: Boolean(organizationId),
    retry: false
  });

  if (workspace.isLoading || binding.isPending) {
    return <RequestStateView state="loading" title="Loading Azure subscription binding" description="Checking the guided onboarding state for this organization." />;
  }

  if (workspace.isError) {
    return <RequestStateView state="unexpected" title="Organization context could not load" description="Try again when Elsa Control is available." />;
  }

  if (!organization) {
    return <RequestStateView state="empty" title="No organization selected" description="Select an organization before starting guided Azure onboarding." />;
  }

  if (binding.isError || !binding.data) {
    return <RequestStateView state="unexpected" title="Azure binding is unavailable" description="The guided subscription binding surface could not load. Try again when the Control API is available." />;
  }

  return <AzureSubscriptionBindingSurface organizationId={organizationId} organizationName={organization.name} initialView={binding.data} />;
}

export function AzureSubscriptionBindingSurface({
  organizationId,
  organizationName,
  initialView
}: {
  organizationId: string;
  organizationName: string;
  initialView: AzureSubscriptionBindingView;
}) {
  const queryClient = useQueryClient();
  const [currentView, setCurrentView] = useState(initialView);
  const [step, setStep] = useState<BindingStep>(() => stepFor(initialView.bind?.state));
  const [subscriptionId, setSubscriptionId] = useState(initialView.bind?.subscriptionId ?? "");
  const [customerTenantId, setCustomerTenantId] = useState(initialView.bind?.customerTenantId ?? "");
  const [consentConfirmed, setConsentConfirmed] = useState(false);

  useEffect(() => {
    setCurrentView(initialView);
    setStep(stepFor(initialView.bind?.state));
    if (initialView.bind?.subscriptionId) setSubscriptionId(initialView.bind.subscriptionId);
    if (initialView.bind?.customerTenantId) setCustomerTenantId(initialView.bind.customerTenantId);
  }, [initialView]);

  const createBind = useMutation({
    mutationFn: (request: CreateAzureSubscriptionBindRequest) => createAzureSubscriptionBind(organizationId, request),
    onSuccess: (nextView) => {
      setCurrentView(nextView);
      setStep("progress");
      setConsentConfirmed(false);
      queryClient.setQueryData(queryKeys.azureSubscriptionBinding(organizationId), nextView);
    }
  });
  const verifyBind = useMutation({
    mutationFn: () => verifyAzureSubscriptionBind(organizationId),
    onSuccess: (nextView) => {
      setCurrentView(nextView);
      setStep(stepFor(nextView.bind?.state));
      queryClient.setQueryData(queryKeys.azureSubscriptionBinding(organizationId), nextView);
    }
  });

  const bind = currentView.bind;
  const offer = currentView.offer;
  const subscriptionIdError = subscriptionId.trim().length > 0 && !guidPattern.test(subscriptionId.trim())
    ? "Enter a subscription ID in GUID format."
    : null;
  const customerTenantIdError = customerTenantId.trim().length > 0 && !guidPattern.test(customerTenantId.trim())
    ? "Enter a customer tenant ID in GUID format."
    : null;
  const canContinue = Boolean(
    guidPattern.test(subscriptionId.trim()) &&
    guidPattern.test(customerTenantId.trim()) &&
    consentConfirmed &&
    !createBind.isPending
  );
  const failureCode = bind?.lastPreflightCode ?? problemCode(verifyBind.error);

  return (
    <section className="space-y-6">
      <header className="max-w-3xl space-y-2">
        <p className="text-xs font-medium uppercase tracking-[0.16em] text-primary">Guided Azure onboarding</p>
        <h1 className="font-display text-3xl font-semibold tracking-normal md:text-4xl">Bind a customer Azure subscription</h1>
        <p className="text-sm leading-6 text-muted-foreground md:text-base">
          Guided onboarding for design partners: work with Valence to delegate operation of managed Elsa in {organizationName}&apos;s subscription.
          This is not a self-serve Azure Marketplace flow.
        </p>
      </header>

      <BindingStepRail step={step} />

      {step === "review" ? (
        <ReviewStep
          offer={offer}
          subscriptionId={subscriptionId}
          customerTenantId={customerTenantId}
          subscriptionIdError={subscriptionIdError}
          customerTenantIdError={customerTenantIdError}
          consentConfirmed={consentConfirmed}
          isSubmitting={createBind.isPending}
          error={createBind.error}
          onSubscriptionIdChange={setSubscriptionId}
          onCustomerTenantIdChange={setCustomerTenantId}
          onConsentChange={setConsentConfirmed}
          onContinue={() => {
            if (!canContinue) return;
            createBind.mutate({
              subscriptionId: subscriptionId.trim().toLowerCase(),
              customerTenantId: customerTenantId.trim(),
              consentConfirmed: true
            });
          }}
        />
      ) : step === "progress" ? (
        <ProgressStep
          bind={bind}
          offer={offer}
          failureCode={failureCode}
          isVerifying={verifyBind.isPending}
          error={verifyBind.error}
          onVerify={() => verifyBind.mutate()}
        />
      ) : (
        <LandingStep bind={bind} />
      )}
    </section>
  );
}

function BindingStepRail({ step }: { step: BindingStep }) {
  const items: Array<{ id: BindingStep; label: string; description: string }> = [
    { id: "review", label: "Review", description: "Confirm authority and consent" },
    { id: "progress", label: "Progress", description: "Verify the delegation" },
    { id: "landing", label: "Landing", description: "View the bound subscription" }
  ];

  return (
    <nav aria-label="Azure subscription binding progress" className="rounded-ui border border-border bg-surface p-3">
      <ol className="grid gap-2 md:grid-cols-3">
        {items.map((item, index) => {
          const active = item.id === step;
          const complete = step === "landing" || (step === "progress" && index === 0);
          return (
            <li key={item.id} className={cn("flex gap-3 rounded-ui px-3 py-2.5", active && "bg-muted/55")} aria-current={active ? "step" : undefined}>
              <span className={cn(
                "grid h-7 w-7 shrink-0 place-items-center rounded-full border text-xs font-semibold",
                complete ? "border-primary/50 bg-primary/15 text-primary" : active ? "border-primary bg-primary text-primary-foreground" : "border-border text-muted-foreground"
              )}>
                {complete ? <Check aria-hidden className="h-3.5 w-3.5" /> : index + 1}
              </span>
              <span className="min-w-0">
                <span className={cn("block text-sm font-semibold", !active && !complete && "text-muted-foreground")}>{item.label}</span>
                <span className="block text-xs text-muted-foreground">{item.description}</span>
              </span>
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

function ReviewStep({
  offer,
  subscriptionId,
  customerTenantId,
  subscriptionIdError,
  customerTenantIdError,
  consentConfirmed,
  isSubmitting,
  error,
  onSubscriptionIdChange,
  onCustomerTenantIdChange,
  onConsentChange,
  onContinue
}: {
  offer: AzureSubscriptionBindingView["offer"];
  subscriptionId: string;
  customerTenantId: string;
  subscriptionIdError: string | null;
  customerTenantIdError: string | null;
  consentConfirmed: boolean;
  isSubmitting: boolean;
  error: unknown;
  onSubscriptionIdChange: (value: string) => void;
  onCustomerTenantIdChange: (value: string) => void;
  onConsentChange: (value: boolean) => void;
  onContinue: () => void;
}) {
  return (
    <div className="grid gap-4 xl:grid-cols-[minmax(0,1.3fr)_minmax(18rem,0.7fr)]">
      <div className="space-y-4">
        <div className="rounded-ui border border-border bg-surface p-5">
          <div className="flex items-start gap-3">
            <div className="grid h-9 w-9 shrink-0 place-items-center rounded-full border border-primary/30 bg-primary/10 text-primary"><ShieldCheck aria-hidden className="h-4 w-4" /></div>
            <div>
              <h2 className="font-display text-lg font-semibold">Review the delegation</h2>
              <p className="mt-1 text-sm leading-6 text-muted-foreground">A subscription Owner deploys the versioned Lighthouse artifact. Control only verifies the delegated authority after deployment.</p>
            </div>
          </div>

          <div className="mt-5 rounded-ui border border-border bg-muted/25 p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <p className="text-sm font-semibold">Deploy the ARM artifact with your subscription Owner</p>
                <p className="mt-1 text-xs leading-5 text-muted-foreground">The customer deploys this subscription-scoped offer in Azure. There is no one-click Marketplace deployment from Control.</p>
              </div>
              <a href={offer.artifactUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-2 text-sm font-medium text-primary underline-offset-4 hover:underline">
                {offer.artifactLabel ?? `Azure Lighthouse ARM artifact v${offer.version}`} <ExternalLink aria-hidden className="h-3.5 w-3.5" />
              </a>
            </div>
            <ol className="mt-4 grid gap-2 text-xs text-muted-foreground sm:grid-cols-3">
              <li><span className="font-semibold text-foreground">1.</span> Open the artifact in Azure.</li>
              <li><span className="font-semibold text-foreground">2.</span> Select the target subscription.</li>
              <li><span className="font-semibold text-foreground">3.</span> Deploy, then return here.</li>
            </ol>
          </div>

          <div className="mt-5 grid gap-4 md:grid-cols-2">
            <label className="text-sm font-medium">
              Azure subscription ID
              <Input aria-label="Azure subscription ID" className="mt-1.5" value={subscriptionId} onChange={(event) => onSubscriptionIdChange(event.target.value)} placeholder="00000000-0000-0000-0000-000000000000" aria-invalid={Boolean(subscriptionIdError)} />
              {subscriptionIdError ? <span className="mt-1 block text-xs text-destructive">{subscriptionIdError}</span> : <span className="mt-1 block text-xs text-muted-foreground">The customer-owned subscription where managed Elsa will operate.</span>}
            </label>
            <label className="text-sm font-medium">
              Customer tenant ID
              <Input aria-label="Customer tenant ID" className="mt-1.5" value={customerTenantId} onChange={(event) => onCustomerTenantIdChange(event.target.value)} placeholder="00000000-0000-0000-0000-000000000000" aria-invalid={Boolean(customerTenantIdError)} />
              {customerTenantIdError ? <span className="mt-1 block text-xs text-destructive">{customerTenantIdError}</span> : <span className="mt-1 block text-xs text-muted-foreground">The Microsoft Entra tenant that owns the subscription.</span>}
            </label>
          </div>
        </div>

        <div className="rounded-ui border border-warning/35 bg-warning/5 p-5">
          <div className="flex items-start gap-3">
            <TriangleAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-warning" />
            <div>
              <h2 className="font-display text-base font-semibold">Minimum operate roles</h2>
              <p className="mt-1 text-sm leading-6 text-muted-foreground">The delegation needs both mutation authority and narrowly delegated role-assignment authority at customer subscription scope. Contributor alone is not enough.</p>
              <ul className="mt-3 grid gap-2 text-sm">
                <li className="flex items-start gap-2"><CheckCircle2 aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-primary" /><span><strong>Contributor</strong> — required mutation authority.</span></li>
                <li className="flex items-start gap-2"><CheckCircle2 aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-primary" /><span><strong>User Access Administrator</strong> — the Lighthouse v1 artifact limits it to the reviewed managed-identity role.</span></li>
                <li className="flex items-start gap-2"><CircleAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-warning" /><span><strong>Role Based Access Control Administrator or Owner</strong> — direct customer-side alternatives only; Azure Lighthouse cannot delegate either role.</span></li>
              </ul>
            </div>
          </div>
        </div>

        <div className="rounded-ui border border-border bg-surface p-5">
          <label className="flex items-start gap-3 text-sm leading-6">
            <input type="checkbox" className="mt-1 h-4 w-4 accent-primary" checked={consentConfirmed} onChange={(event) => onConsentChange(event.target.checked)} />
            <span aria-label="I understand Valence will operate">
              I understand that Valence will operate managed Elsa in the selected subscription within the granted roles. This is not an Azure Marketplace or Logic Apps replacement, and the Azure bill is separate from any Elsa fee.
            </span>
          </label>
          {error ? <p role="alert" className="mt-3 text-sm text-destructive">{errorMessage(error, "The bind could not be started.")}</p> : null}
          <div className="mt-4 flex justify-end">
            <Button type="button" onClick={onContinue} disabled={!consentConfirmed || !guidPattern.test(subscriptionId.trim()) || !guidPattern.test(customerTenantId.trim()) || isSubmitting}>
              {isSubmitting ? <LoaderCircle aria-hidden className="h-4 w-4 animate-spin" /> : null}
              {isSubmitting ? "Starting verification…" : "Continue to verification"}
            </Button>
          </div>
        </div>
      </div>

      <aside className="h-fit rounded-ui border border-border bg-surface p-5">
        <p className="text-xs font-medium uppercase tracking-[0.14em] text-muted-foreground">What happens next</p>
        <ol className="mt-4 grid gap-4 text-sm">
          <li className="flex gap-3"><span className="grid h-6 w-6 shrink-0 place-items-center rounded-full border border-primary/35 text-xs text-primary">1</span><span>Confirm your subscription and customer tenant.</span></li>
          <li className="flex gap-3"><span className="grid h-6 w-6 shrink-0 place-items-center rounded-full border border-primary/35 text-xs text-primary">2</span><span>Deploy the ARM artifact with the honest role set.</span></li>
          <li className="flex gap-3"><span className="grid h-6 w-6 shrink-0 place-items-center rounded-full border border-primary/35 text-xs text-primary">3</span><span>Ask Control to verify the delegation. Active is a prerequisite signal, not an instance-ready claim.</span></li>
        </ol>
      </aside>
    </div>
  );
}

function ProgressStep({
  bind,
  offer,
  failureCode,
  isVerifying,
  error,
  onVerify
}: {
  bind: AzureSubscriptionBindingView["bind"];
  offer: AzureSubscriptionBindingView["offer"];
  failureCode: string | null;
  isVerifying: boolean;
  error: unknown;
  onVerify: () => void;
}) {
  const nonActive = bind?.state !== "Active";
  return (
    <div className="grid gap-4 xl:grid-cols-[minmax(0,1.2fr)_minmax(18rem,0.8fr)]">
      <div className="rounded-ui border border-border bg-surface p-5">
        <div className="flex items-start gap-3">
          <div className="grid h-9 w-9 shrink-0 place-items-center rounded-full border border-primary/30 bg-primary/10 text-primary">
            {isVerifying ? <LoaderCircle aria-hidden className="h-4 w-4 animate-spin" /> : <ShieldCheck aria-hidden className="h-4 w-4" />}
          </div>
          <div>
            <h2 className="font-display text-lg font-semibold">Verify the Lighthouse delegation</h2>
            <p className="mt-1 text-sm leading-6 text-muted-foreground">Control checks that the delegated managing principal can see this tenant and subscription and that the required operate roles are present.</p>
          </div>
        </div>

        <div className="mt-5 grid gap-3 sm:grid-cols-2">
          <DataTile label="Subscription ID" value={bind?.subscriptionId ?? "Waiting for bind record"} mono />
          <DataTile label="Offer version" value={`v${offer.version}`} />
          <DataTile label="Bind state" value={bind?.state ?? "Verifying"} />
          <DataTile label="Role check" value={bind?.state === "Degraded" ? "Needs attention" : "Not yet verified"} />
        </div>

        {nonActive ? (
          <div role="status" className={cn("mt-5 rounded-ui border p-4", failureCode ? "border-destructive/40 bg-destructive/5" : "border-warning/35 bg-warning/5")}>
            <div className="flex items-start gap-3">
              {failureCode ? <CircleAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-destructive" /> : <LoaderCircle aria-hidden className={cn("mt-0.5 h-4 w-4 shrink-0 text-warning", isVerifying && "animate-spin")} />}
              <div>
                <p className="text-sm font-semibold">{failureCode ? "Verification needs attention" : "Waiting for customer deployment"}</p>
                <p className="mt-1 text-sm leading-6 text-muted-foreground">
                  {failureCode ? "The subscription does not have Active status. Confirm the Lighthouse deployment and the complete role set, then verify again." : "Control has not simulated Ready. The bind remains non-Active until an operator-triggered preflight succeeds."}
                </p>
                {failureCode ? <p className="mt-2 font-mono text-xs text-destructive">Preflight code: {failureCode}</p> : null}
                {error ? <p className="mt-2 text-xs text-destructive">{errorMessage(error, "The verification request failed.")}</p> : null}
              </div>
            </div>
          </div>
        ) : null}

        <div className="mt-5 flex flex-wrap items-center justify-between gap-3">
          <p className="text-xs text-muted-foreground">Need help? This is a concierge-guided design-partner step; Valence can review the failed preflight code.</p>
          <Button type="button" onClick={onVerify} disabled={isVerifying}>
            {isVerifying ? <LoaderCircle aria-hidden className="h-4 w-4 animate-spin" /> : <ShieldCheck aria-hidden className="h-4 w-4" />}
            {isVerifying ? "Verifying…" : "Verify delegation"}
          </Button>
        </div>
      </div>

      <aside className="h-fit rounded-ui border border-border bg-surface p-5">
        <p className="text-xs font-medium uppercase tracking-[0.14em] text-muted-foreground">Verification scope</p>
        <ul className="mt-4 grid gap-3 text-sm text-muted-foreground">
          <li className="flex gap-2"><CheckCircle2 aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-primary" />Tenant and subscription are visible to the managing principal.</li>
          <li className="flex gap-2"><CheckCircle2 aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-primary" />Contributor is present at subscription scope.</li>
          <li className="flex gap-2"><CheckCircle2 aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-primary" />Lighthouse v1 has limited UAA; RBAC Administrator or Owner are direct-grant alternatives only.</li>
          <li className="flex gap-2"><CircleAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-warning" />Cross-tenant Key Vault data-plane checks remain a separate readiness concern.</li>
        </ul>
      </aside>
    </div>
  );
}

function LandingStep({ bind }: { bind: AzureSubscriptionBindingView["bind"] }) {
  return (
    <div className="max-w-3xl rounded-ui border border-primary/35 bg-primary/5 p-6">
      <div className="flex items-start gap-3">
        <div className="grid h-10 w-10 shrink-0 place-items-center rounded-full border border-primary/40 bg-primary/15 text-primary"><CheckCircle2 aria-hidden className="h-5 w-5" /></div>
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-display text-xl font-semibold">Subscription bound</h2>
            <Badge className="border-primary/30 bg-primary/10 text-primary">Active</Badge>
          </div>
          <p className="mt-2 text-sm leading-6 text-muted-foreground">Valence can operate through the delegated Lighthouse authority in this subscription within the granted roles.</p>
        </div>
      </div>
      <div className="mt-5 grid gap-3 sm:grid-cols-2">
        <DataTile label="Subscription ID" value={bind?.subscriptionId ?? "Not reported"} mono />
        <DataTile label="Verified" value={bind?.verifiedAt ? formatDate(bind.verifiedAt) : "Not reported"} />
      </div>
      <div className="mt-5 rounded-ui border border-warning/35 bg-warning/5 p-4">
        <p className="text-sm font-semibold">Bind Active is only a prerequisite signal</p>
        <p className="mt-1 text-sm leading-6 text-muted-foreground">This bind does not mint the Azure-bound entitlement or enable customer-subscription targeting. Azure-bound entitlement and customer-subscription targeting are separate follow-on steps. No managed instance is Ready to create from this screen. Cross-tenant Key Vault data-plane checks remain separate.</p>
      </div>
      <p className="mt-4 text-xs leading-5 text-muted-foreground">Azure infrastructure billing remains with the customer. Any Elsa fee is separate and is not represented by this bind status.</p>
    </div>
  );
}

function DataTile({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return <div className="rounded-ui border border-border bg-muted/20 px-3 py-2.5"><p className="text-[10px] font-semibold uppercase tracking-[0.12em] text-muted-foreground">{label}</p><p className={cn("mt-1 break-all text-sm", mono && "font-mono text-xs")}>{value}</p></div>;
}

function stepFor(state: AzureSubscriptionBindState | undefined): BindingStep {
  if (state === "Active") return "landing";
  if (state === "Verifying" || state === "Degraded") return "progress";
  return "review";
}

function problemCode(error: unknown): string | null {
  if (!(error instanceof ApiError) || !error.details || typeof error.details !== "object") return null;
  if ("code" in error.details && typeof error.details.code === "string") return error.details.code;
  if ("errorCode" in error.details && typeof error.details.errorCode === "string") return error.details.errorCode;
  return null;
}

function errorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message.trim().length > 0 ? error.message : fallback;
}

function formatDate(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.valueOf())) return value;
  return new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" }).format(date);
}
