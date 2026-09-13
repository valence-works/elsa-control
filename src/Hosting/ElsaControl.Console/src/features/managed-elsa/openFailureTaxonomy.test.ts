import { describe, expect, it } from "vitest";
import { ApiError } from "@/lib/api/httpClient";
import {
  classifyInstanceOpenFailure,
  classifyOpenFailure,
  classifyOpenFailureFromError,
  openFailureHistoricalIssues,
  openFailureScorecardNote,
  parseHandoffFailureSignal
} from "@/features/managed-elsa/openFailureTaxonomy";
import type { ManagedElsaInstance } from "@/features/managed-elsa/managedElsaModels";

describe("openFailureTaxonomy", () => {
  it("maps 404 and handoff-unavailable to the #383-class missing-handoff mode", () => {
    const fromStatus = classifyOpenFailure({
      httpStatus: 404,
      canOpen: false,
      identityBindingState: "handoff-unavailable",
      source: "continuation"
    });
    const fromState = classifyInstanceOpenFailure(instance({
      canOpen: false,
      identityBindingState: "handoff-unavailable",
      unavailableReason: "Managed sign-in is not configured for this instance's current deployment."
    }));

    expect(fromStatus.mode).toBe("handoff-missing");
    expect(fromStatus.classLabel).toBe("#383-class");
    expect(fromStatus.diagnosticCode).toBe("open.handoff-missing");
    expect(fromStatus.guidance).toMatch(/not a Studio crash/i);
    expect(fromState.mode).toBe("handoff-missing");
    expect(fromState.identityBindingState).toBe("handoff-unavailable");
  });

  it("maps a 500 with handoff configured to the #397-class runtime mode", () => {
    const failure = classifyOpenFailure({
      httpStatus: 500,
      canOpen: true,
      identityBindingState: "available",
      source: "continuation"
    });

    expect(failure.mode).toBe("runtime-auth");
    expect(failure.classLabel).toBe("#397-class");
    expect(failure.diagnosticCode).toBe("open.runtime-auth");
    expect(failure.honestyGap).toBe(true);
    expect(failure.guidance).toMatch(/Do not reopen #383/);
    expect(failure.guidance).toMatch(/canOpen was true/);
  });

  it("maps canOpen false IdentityBindingState reasons before Open", () => {
    expect(classifyInstanceOpenFailure(instance({
      canOpen: false,
      identityBindingState: "not-authorized",
      unavailableReason: "Not authorized to open this instance."
    })).shortLabel).toBe("Permission blocked");
    expect(classifyInstanceOpenFailure(instance({
      canOpen: false,
      health: "Unknown",
      observedLifecycle: "Deleting",
      unavailableReason: "This instance is not currently available."
    })).shortLabel).toBe("Health blocked");
    expect(classifyInstanceOpenFailure(instance({
      canOpen: false,
      audience: null,
      redirectUri: null,
      unavailableReason: "The current instance binding is unavailable."
    })).shortLabel).toBe("Identity blocked");
  });

  it("surfaces an honesty gap when canOpen is true but Open fails", () => {
    const failure = classifyOpenFailureFromError(
      new ApiError("Unexpected", "expired Control session", 401),
      instance({ canOpen: true, identityBindingState: "available" })
    );

    expect(failure.mode).toBe("honesty-gap");
    expect(failure.honestyGap).toBe(true);
    expect(failure.guidance).toMatch(/Sign in again and retry/);
    expect(failure.title).toMatch(/Open failed after Control offered it/);
  });

  it("parses runtime 404, 500, and handoff-unavailable continuation signals", () => {
    expect(parseHandoffFailureSignal("404")).toEqual({ failureStatus: 404, failureCode: "handoff-unavailable" });
    expect(parseHandoffFailureSignal("500")).toEqual({ failureStatus: 500, failureCode: null });
    expect(parseHandoffFailureSignal("handoff-unavailable")).toEqual({
      failureStatus: null,
      failureCode: "handoff-unavailable"
    });
  });

  it("links historical evidence without scorecard language", () => {
    const failure = classifyOpenFailure({ httpStatus: 404, source: "continuation" });
    expect(failure.relatedIssues.map((issue) => issue.number)).toEqual([383, 397, 393]);
    expect(openFailureHistoricalIssues.find((issue) => issue.number === 393)?.note).toMatch(/Historical evidence only/);
    expect(openFailureScorecardNote).not.toMatch(/Verification=Passed|V3 Pass/i);
    expect(JSON.stringify(failure)).not.toMatch(/live AC still open|#393 is still open/i);
    expect(JSON.stringify(failure)).not.toMatch(/Verification=Passed|V3 Pass/i);
  });
});

function instance(overrides: Partial<ManagedElsaInstance> = {}): ManagedElsaInstance {
  return {
    organizationId: "org",
    instanceId: "instance",
    name: "Dogfood2",
    slug: "dogfood2",
    desiredLifecycle: "Running",
    observedLifecycle: "Ready",
    health: "Healthy",
    canOpen: false,
    audience: null,
    redirectUri: null,
    unavailableReason: null,
    ...overrides
  };
}
