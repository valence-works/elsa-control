import { describe, expect, it } from "vitest";
import {
  hasStudioSessionCookie,
  isAuthenticatedStudioSurface,
  isStudioSignInUrl,
  studioExternalAuthenticationCookieName
} from "@/features/managed-elsa/studioHandoffSession";

const runtimeOrigin = "https://ee6ef57016238437-app.example.test";

describe("studioHandoffSession", () => {
  it("treats Studio /login as V3 Fail even when the origin matches", () => {
    expect(isStudioSignInUrl(`${runtimeOrigin}/login`, runtimeOrigin)).toBe(true);
    expect(isStudioSignInUrl(`${runtimeOrigin}/login?choose=true`, runtimeOrigin)).toBe(true);
    expect(isAuthenticatedStudioSurface(`${runtimeOrigin}/login`, runtimeOrigin)).toBe(false);
    expect(studioExternalAuthenticationCookieName).toBe("ElsaStudio.ExternalAuthentication");
    expect(hasStudioSessionCookie(["ManagedElsaRuntimeSession"])).toBe(true);
    expect(hasStudioSessionCookie([studioExternalAuthenticationCookieName])).toBe(true);
    expect(hasStudioSessionCookie(["__Secure-ElsaManagedHandoffState"])).toBe(false);
  });

  it("accepts only authenticated Studio surfaces after a successful handoff", () => {
    expect(isAuthenticatedStudioSurface(`${runtimeOrigin}/`, runtimeOrigin)).toBe(true);
    expect(isAuthenticatedStudioSurface(`${runtimeOrigin}/workflows`, runtimeOrigin)).toBe(true);
    expect(isAuthenticatedStudioSurface(`${runtimeOrigin}/workflows/`, runtimeOrigin)).toBe(true);
    expect(isStudioSignInUrl(`${runtimeOrigin}/`, runtimeOrigin)).toBe(false);
    expect(isStudioSignInUrl(`${runtimeOrigin}/workflows`, runtimeOrigin)).toBe(false);
  });

  it("does not treat Control login or another origin as Studio success", () => {
    expect(isAuthenticatedStudioSurface("https://api.example.test/admin/login", runtimeOrigin)).toBe(false);
    expect(isStudioSignInUrl("https://api.example.test/admin/login", runtimeOrigin)).toBe(false);
    expect(isAuthenticatedStudioSurface(`${runtimeOrigin}/authentication/external/callback`, runtimeOrigin)).toBe(false);
  });
});
