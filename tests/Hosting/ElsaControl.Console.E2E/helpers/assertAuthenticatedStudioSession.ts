import { expect, type Page } from "@playwright/test";
import {
  isAuthenticatedStudioSurface,
  isStudioSignInUrl,
  studioExternalAuthenticationCookieName
} from "../../../../src/Hosting/ElsaControl.Console/src/features/managed-elsa/studioHandoffSession";

export async function assertAuthenticatedStudioSession(
  page: Page,
  runtimeOrigin: string,
  details: { callbackStatus?: number; callbackLocation?: string } = {}
) {
  await expect.poll(() => page.url(), { timeout: 30_000 }).not.toBe("");
  const url = page.url();
  const cookieNames = (await page.context().cookies(runtimeOrigin)).map(cookie => cookie.name).sort();
  const callback = details.callbackStatus == null
    ? "callback unobserved"
    : `callback ${details.callbackStatus} -> ${details.callbackLocation ?? "none"}`;

  if (isStudioSignInUrl(url, runtimeOrigin) || !isAuthenticatedStudioSurface(url, runtimeOrigin)) {
    throw new Error(
      `Runtime handoff did not establish an authenticated Studio session (${callback}; ` +
      `url: ${safeRuntimePath(url, runtimeOrigin)}; cookies: ${cookieNames.join(",") || "none"}). ` +
      `Landing on /login is V3 Fail. After Control posts code+state, the runtime must mint ` +
      `${studioExternalAuthenticationCookieName} (or the runtime session cookie) so SuccessPath / stays signed in.`);
  }

  await expect(page.getByRole("heading", { name: /^sign in$/i })).toHaveCount(0);
}

function safeRuntimePath(url: string, runtimeOrigin: string) {
  try {
    const uri = new URL(url);
    return new URL(runtimeOrigin).origin === uri.origin ? uri.pathname : "external";
  } catch {
    return "invalid";
  }
}
