import { ShieldAlert } from "lucide-react";
import {
  openFailureHistoricalIssues,
  openFailureModeHelp,
  openFailureScorecardNote,
  type OpenFailureClassification
} from "@/features/managed-elsa/openFailureTaxonomy";
import { cn } from "@/lib/utils";

export function OpenFailureNotice({
  failure,
  compact = false
}: {
  failure: OpenFailureClassification;
  compact?: boolean;
}) {
  if (compact) {
    return (
      <div className="max-w-xs space-y-1 text-left">
        <p className="text-xs font-medium text-foreground">{failure.shortLabel}</p>
        <OpenFailureDiagnostic failure={failure} summary="Why Open is blocked" />
      </div>
    );
  }

  return (
    <div role="alert" className="flex items-start gap-3 rounded-ui border border-warning/30 bg-warning/10 p-4 text-sm">
      <ShieldAlert aria-hidden className="mt-0.5 h-4 w-4 shrink-0 text-warning" />
      <div className="min-w-0 space-y-2">
        <div className="space-y-1">
          <p className="font-medium">{failure.title}</p>
          {failure.classLabel ? <p className="text-xs font-medium text-foreground">{failure.classLabel}</p> : null}
          <p className="text-muted-foreground">{failure.guidance}</p>
          {failure.honestyGap ? (
            <p className="text-muted-foreground">
              Control offered Open (`canOpen` true) but the attempt failed. Do not claim the Open path worked.
            </p>
          ) : null}
        </div>
        <OpenFailureDiagnostic failure={failure} summary="Diagnostic" />
      </div>
    </div>
  );
}

export function OpenFailureModeHelp({ className }: { className?: string }) {
  return (
    <details className={cn("rounded-ui border border-border bg-surface p-4 text-sm", className)}>
      <summary className="cursor-pointer font-medium">Open failure modes</summary>
      <p className="mt-3 text-xs text-muted-foreground">
        Use this taxonomy when Open fails or `canOpen` is false. {openFailureScorecardNote}
      </p>
      <ul className="mt-3 space-y-2 text-xs text-muted-foreground">
        {openFailureModeHelp.map((item) => (
          <li key={item.mode}>
            <span className="font-medium text-foreground">{item.mode}</span>
            {" — "}
            <span>{item.signal}. {item.guidance}</span>
          </li>
        ))}
      </ul>
      <HistoricalIssueList />
    </details>
  );
}

function OpenFailureDiagnostic({
  failure,
  summary
}: {
  failure: OpenFailureClassification;
  summary: string;
}) {
  return (
    <details className="rounded-ui border border-border/70 bg-background/60 p-3 text-xs">
      <summary className="cursor-pointer font-medium text-foreground">{summary}</summary>
      <dl className="mt-3 grid gap-2 text-muted-foreground">
        <DiagnosticRow label="Mode" value={failure.mode} />
        <DiagnosticRow label="Code" value={failure.diagnosticCode} mono />
        {failure.httpStatus != null ? <DiagnosticRow label="HTTP" value={String(failure.httpStatus)} mono /> : null}
        {failure.identityBindingState ? <DiagnosticRow label="IdentityBindingState" value={failure.identityBindingState} mono /> : null}
        {failure.classLabel ? <DiagnosticRow label="Class" value={failure.classLabel} /> : null}
      </dl>
      <p className="mt-3 text-muted-foreground">{failure.guidance}</p>
      <HistoricalIssueList />
      <p className="mt-3 text-muted-foreground">{openFailureScorecardNote}</p>
    </details>
  );
}

function HistoricalIssueList() {
  return (
    <ul className="mt-3 space-y-1 text-muted-foreground">
      {openFailureHistoricalIssues.map((issue) => (
        <li key={issue.number}>
          <a href={issue.href} target="_blank" rel="noreferrer" className="underline underline-offset-2">
            {issue.label}
          </a>
          {" — "}
          {issue.note}
        </li>
      ))}
    </ul>
  );
}

function DiagnosticRow({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <dt className="uppercase tracking-wide">{label}</dt>
      <dd className={cn("mt-0.5", mono && "font-mono")}>{value}</dd>
    </div>
  );
}
