import { ApiError } from "@/lib/api/httpClient";
import type { ManagedElsaInstance } from "@/features/managed-elsa/managedElsaModels";

/**
 * Operator-facing Open failure taxonomy for #404.
 *
 * Historical evidence only — do not treat these as a live scorecard:
 * - #383: missing handoff (404 / handoff-unavailable). Not a Studio crash.
 * - #397: runtime/auth 500 with handoff present. Image/runtime wiring; not #383.
 * - #393: closed live-proof issue. Historical evidence only.
 *
 * Launch Ops owns verification scoring. This module never claims a pass.
 */
export type OpenFailureMode = "handoff-missing" | "runtime-auth" | "can-open-false" | "honesty-gap";

export type OpenFailureSource = "continuation" | "issue" | "preflight" | "open";

export type OpenFailureSignal = {
  httpStatus?: number | null;
  diagnosticCode?: string | null;
  identityBindingState?: string | null;
  canOpen?: boolean;
  unavailableReason?: string | null;
  source?: OpenFailureSource;
};

export type OpenFailureHistoricalIssue = {
  number: 383 | 393 | 397;
  href: string;
  label: string;
  note: string;
};

export type OpenFailureClassification = {
  mode: OpenFailureMode;
  title: string;
  shortLabel: string;
  guidance: string;
  diagnosticCode: string;
  identityBindingState: string | null;
  httpStatus: number | null;
  classLabel: string | null;
  honestyGap: boolean;
  relatedIssues: readonly OpenFailureHistoricalIssue[];
};

export const openFailureHistoricalIssues: readonly OpenFailureHistoricalIssue[] = [
  {
    number: 383,
    href: "https://github.com/valence-works/elsa-control/issues/383",
    label: "#383",
    note: "Missing handoff (404). Historical #383-class signal — the provider did not apply handoff."
  },
  {
    number: 397,
    href: "https://github.com/valence-works/elsa-control/issues/397",
    label: "#397",
    note: "Runtime 500 with handoff present. Historical #397-class signal — image/runtime wiring, not missing handoff."
  },
  {
    number: 393,
    href: "https://github.com/valence-works/elsa-control/issues/393",
    label: "#393",
    note: "Closed live-proof issue. Historical evidence only — do not treat as an open live AC."
  }
];

export const openFailureScorecardNote =
  "Launch Ops owns the Open scorecard. This console does not mark verification or claim a pass.";

export const openFailureModeHelp = [
  {
    mode: "handoff-missing" as const,
    signal: "404 / handoff-unavailable",
    guidance: "Provider did not apply handoff. This is not a Studio crash (#383-class)."
  },
  {
    mode: "runtime-auth" as const,
    signal: "500 (or similar) with handoff configured",
    guidance: "Image/runtime wiring (#397-class). Do not reopen #383 as the same bug."
  },
  {
    mode: "can-open-false" as const,
    signal: "IdentityBindingState reasons",
    guidance: "Fix permission, health, handoff, or identity before Open."
  },
  {
    mode: "honesty-gap" as const,
    signal: "canOpen true but Open fails",
    guidance: "Surface the mismatch. Do not claim the Open path worked."
  }
] as const;

const honestyGapGuidance =
  "canOpen was true, but Open did not succeed. Surface this mismatch; do not claim the Open path worked.";

export function classifyInstanceOpenFailure(
  instance: Pick<ManagedElsaInstance, "canOpen" | "identityBindingState" | "unavailableReason" | "health" | "observedLifecycle">
): OpenFailureClassification {
  return classifyOpenFailure({
    canOpen: instance.canOpen,
    identityBindingState: instance.identityBindingState ?? inferIdentityBindingState(instance),
    unavailableReason: instance.unavailableReason,
    source: "preflight"
  });
}

export function classifyOpenFailure(signal: OpenFailureSignal): OpenFailureClassification {
  const httpStatus = signal.httpStatus ?? null;
  const identityBindingState = normalizeBindingState(signal.identityBindingState);
  const diagnostic = normalizeDiagnostic(signal.diagnosticCode);
  const handoffMissing = isHandoffMissingSignal(httpStatus, identityBindingState, diagnostic);

  if (handoffMissing) {
    return withHonestyGap(handoffMissingClassification(signal, identityBindingState, httpStatus), signal.canOpen);
  }

  if (isRuntimeFailureStatus(httpStatus) && isHandoffConfigured(signal, identityBindingState)) {
    return withHonestyGap(runtimeAuthClassification(httpStatus, identityBindingState), signal.canOpen);
  }

  if (signal.canOpen === false) {
    return canOpenFalseClassification(signal, identityBindingState);
  }

  return honestyGapClassification(signal, identityBindingState, httpStatus);
}

export function classifyOpenFailureFromError(
  error: unknown,
  instance: Pick<ManagedElsaInstance, "canOpen" | "identityBindingState" | "unavailableReason">
): OpenFailureClassification {
  if (error instanceof ApiError) {
    return classifyOpenFailure({
      httpStatus: error.status ?? null,
      canOpen: instance.canOpen,
      identityBindingState: instance.identityBindingState,
      unavailableReason: instance.unavailableReason,
      source: "issue"
    });
  }

  if (isManagedElsaOpenError(error)) {
    return classifyOpenFailure({
      diagnosticCode: error.reason,
      canOpen: instance.canOpen,
      identityBindingState: instance.identityBindingState,
      unavailableReason: instance.unavailableReason,
      source: "open"
    });
  }

  return classifyOpenFailure({
    canOpen: instance.canOpen,
    identityBindingState: instance.identityBindingState,
    unavailableReason: instance.unavailableReason,
    source: "open"
  });
}

export function isManagedElsaOpenError(error: unknown): error is { reason: string } {
  return error instanceof Error && error.name === "ManagedElsaOpenError";
}

export function parseHandoffFailureSignal(rawStatus: string | null): {
  failureStatus: number | null;
  failureCode: string | null;
} {
  if (!rawStatus)
    return { failureStatus: null, failureCode: null };
  if (/^(401|403|404|409|500|502|503|504)$/.test(rawStatus))
    return { failureStatus: Number(rawStatus), failureCode: rawStatus === "404" ? "handoff-unavailable" : null };
  if (/^[A-Za-z0-9._-]{3,64}$/.test(rawStatus))
    return { failureStatus: null, failureCode: rawStatus };
  return { failureStatus: null, failureCode: null };
}

function handoffMissingClassification(
  signal: OpenFailureSignal,
  identityBindingState: string | null,
  httpStatus: number | null
): OpenFailureClassification {
  return {
    mode: "handoff-missing",
    title: "Handoff missing",
    shortLabel: "Handoff missing",
    guidance: [
      "The provider did not apply managed handoff. This is not a Studio crash. Confirm the current deployment received ManagedElsa handoff settings before retrying Open.",
      signal.unavailableReason
    ].filter(Boolean).join(" "),
    diagnosticCode: "open.handoff-missing",
    identityBindingState: identityBindingState ?? "handoff-unavailable",
    httpStatus,
    classLabel: "#383-class",
    honestyGap: false,
    relatedIssues: openFailureHistoricalIssues
  };
}

function runtimeAuthClassification(
  httpStatus: number | null,
  identityBindingState: string | null
): OpenFailureClassification {
  return {
    mode: "runtime-auth",
    title: "Runtime or auth failure",
    shortLabel: "Runtime failure",
    guidance:
      "Handoff is configured, but the runtime returned a server error. This is image or runtime wiring, not the missing-handoff case. Do not reopen #383 as the same bug.",
    diagnosticCode: "open.runtime-auth",
    identityBindingState,
    httpStatus,
    classLabel: "#397-class",
    honestyGap: false,
    relatedIssues: openFailureHistoricalIssues
  };
}

function canOpenFalseClassification(
  signal: OpenFailureSignal,
  identityBindingState: string | null
): OpenFailureClassification {
  const reason = identityBindingState ?? "identity-unavailable";
  return {
    mode: "can-open-false",
    title: "Open is blocked",
    shortLabel: canOpenFalseShortLabel(reason),
    guidance: [canOpenFalseGuidance(reason), signal.unavailableReason].filter(Boolean).join(" "),
    diagnosticCode: `open.can-open-false.${reason}`,
    identityBindingState: reason,
    httpStatus: signal.httpStatus ?? null,
    classLabel: reason === "handoff-unavailable" ? "#383-class" : null,
    honestyGap: false,
    relatedIssues: openFailureHistoricalIssues
  };
}

function honestyGapClassification(
  signal: OpenFailureSignal,
  identityBindingState: string | null,
  httpStatus: number | null
): OpenFailureClassification {
  return {
    mode: "honesty-gap",
    title: "Open failed after Control offered it",
    shortLabel: "Open mismatch",
    guidance: sessionOrFallbackGuidance(httpStatus, signal.unavailableReason, signal.source),
    diagnosticCode: diagnosticForStatus(httpStatus, signal.source) ?? "open.honesty-gap",
    identityBindingState: identityBindingState ?? "available",
    httpStatus,
    classLabel: null,
    honestyGap: true,
    relatedIssues: openFailureHistoricalIssues
  };
}

function withHonestyGap(classification: OpenFailureClassification, canOpen: boolean | undefined): OpenFailureClassification {
  if (canOpen !== true)
    return classification;
  return {
    ...classification,
    honestyGap: true,
    guidance: `${classification.guidance} ${honestyGapGuidance}`
  };
}

function sessionOrFallbackGuidance(
  httpStatus: number | null,
  unavailableReason: string | null | undefined,
  source?: OpenFailureSource
) {
  switch (httpStatus) {
    case 401:
      return source === "continuation"
        ? "Your managed-instance link has expired. Open Elsa again to create a new link."
        : "Your Control session could not authorize this handoff. Sign in again and retry.";
    case 403:
      return "This managed instance is no longer available to your account.";
    case 409:
      return "This handoff has already been used. Open the instance again to create a new link.";
    default:
      return unavailableReason
        ?? (httpStatus == null
          ? "This managed instance is no longer available. Refresh the page and try again."
          : honestyGapGuidance);
  }
}

function canOpenFalseGuidance(state: string) {
  switch (state) {
    case "not-authorized":
      return "Fix permission before Open. Your account is not authorized to open this instance.";
    case "instance-unavailable":
      return "Fix health before Open. The instance is not currently Healthy and Ready.";
    case "handoff-unavailable":
      return "Fix handoff before Open. The provider did not apply managed handoff for the current deployment.";
    case "identity-unavailable":
      return "Fix identity before Open. The current identity binding is unavailable.";
    default:
      return "Fix permission, health, handoff, or identity before Open.";
  }
}

function canOpenFalseShortLabel(state: string) {
  switch (state) {
    case "not-authorized":
      return "Permission blocked";
    case "instance-unavailable":
      return "Health blocked";
    case "handoff-unavailable":
      return "Handoff missing";
    case "identity-unavailable":
      return "Identity blocked";
    default:
      return "Open blocked";
  }
}

function isHandoffMissingSignal(
  httpStatus: number | null,
  identityBindingState: string | null,
  diagnostic: string | null
) {
  return httpStatus === 404
    || identityBindingState === "handoff-unavailable"
    || diagnostic === "handoff-unavailable"
    || diagnostic === "open.handoff-missing";
}

function isRuntimeFailureStatus(status: number | null) {
  return status === 500 || status === 502 || status === 503 || status === 504;
}

function isHandoffConfigured(signal: OpenFailureSignal, identityBindingState: string | null) {
  return signal.canOpen === true || identityBindingState === "available";
}

function diagnosticForStatus(status: number | null, source?: OpenFailureSource) {
  switch (status) {
    case 401:
      return source === "continuation" ? "open.handoff-expired" : "open.session-unauthorized";
    case 403:
      return "open.forbidden";
    case 409:
      return "open.handoff-replay";
    default:
      return null;
  }
}

function normalizeBindingState(value: string | null | undefined) {
  if (!value)
    return null;
  const normalized = value.trim().toLowerCase();
  return normalized.length > 0 ? normalized : null;
}

function normalizeDiagnostic(value: string | null | undefined) {
  if (!value)
    return null;
  return value.trim().toLowerCase();
}

export function inferIdentityBindingState(
  instance: Pick<ManagedElsaInstance, "canOpen" | "identityBindingState" | "unavailableReason" | "health" | "observedLifecycle">
) {
  const explicit = normalizeBindingState(instance.identityBindingState);
  if (explicit)
    return explicit;
  const reason = instance.unavailableReason?.toLowerCase() ?? "";
  if (reason.includes("not authorized"))
    return "not-authorized";
  if (reason.includes("sign-in is not configured") || reason.includes("handoff"))
    return "handoff-unavailable";
  if (reason.includes("binding is unavailable"))
    return "identity-unavailable";
  if (reason.includes("not currently available") || instance.health !== "Healthy" || instance.observedLifecycle !== "Ready")
    return "instance-unavailable";
  return null;
}
