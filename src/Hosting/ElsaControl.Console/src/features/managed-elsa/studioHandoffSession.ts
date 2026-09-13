/**
 * Commercial V3 Open success is an authenticated Studio surface, not the
 * External Authentication Sign-in chooser. Control continuation GET can only
 * auto-POST to the bound runtime callback; the runtime must then mint
 * `ElsaStudio.ExternalAuthentication` so `/` does not 302 to `/login`.
 */
export const studioExternalAuthenticationCookieName = "ElsaStudio.ExternalAuthentication";
export const managedElsaRuntimeSessionCookieName = "ManagedElsaRuntimeSession";

export function hasStudioSessionCookie(cookieNames: readonly string[]): boolean {
  return cookieNames.includes(studioExternalAuthenticationCookieName) ||
    cookieNames.includes(managedElsaRuntimeSessionCookieName);
}

export function isStudioSignInUrl(url: string, runtimeOrigin: string): boolean {
  return studioPath(url, runtimeOrigin) === "/login";
}

export function isAuthenticatedStudioSurface(url: string, runtimeOrigin: string): boolean {
  const path = studioPath(url, runtimeOrigin);
  return path === "/" || path === "/workflows";
}

function studioPath(url: string, runtimeOrigin: string): string | null {
  let uri: URL;
  let origin: URL;
  try {
    uri = new URL(url);
    origin = new URL(runtimeOrigin);
  } catch {
    return null;
  }
  if (uri.origin !== origin.origin)
    return null;
  return uri.pathname.replace(/\/+$/, "") || "/";
}
