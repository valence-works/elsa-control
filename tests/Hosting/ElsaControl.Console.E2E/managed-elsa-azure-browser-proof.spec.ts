import { expect, type Page, test } from "@playwright/test";
import { assertAuthenticatedStudioSession } from "./helpers/assertAuthenticatedStudioSession";

const proofEnabled = process.env.MANAGED_ELSA_AZURE_BROWSER_PROOF === "1";
const controlOrigin = (process.env.ADMIN_UI_BASE_URL ?? "").replace(/\/+$/, "");
const runtimeOrigin = (process.env.MANAGED_ELSA_PROOF_RUNTIME_ORIGIN ?? "").replace(/\/+$/, "");
const stateLifetimeSeconds = Number(process.env.MANAGED_ELSA_PROOF_STATE_LIFETIME_SECONDS ?? "");
const interactiveSignInTimeoutMs = 300_000;
const postSignInBudgetMs = 120_000;

test.use({ ignoreHTTPSErrors: false, trace: "off", screenshot: "off", video: "off" });

test.describe("managed Elsa Azure browser proof", () => {
  test.skip(!proofEnabled, "Requires an explicit Azure proof run and interactive Entra sign-in.");
  test.describe.configure({ mode: "serial" });

  test("completes the public-TLS handoff and fails closed on replay and expiry", async ({ page }) => {
    requirePublicHttpsOrigin(controlOrigin, "ADMIN_UI_BASE_URL");
    requirePublicHttpsOrigin(runtimeOrigin, "MANAGED_ELSA_PROOF_RUNTIME_ORIGIN");
    requireBoundedStateLifetime(stateLifetimeSeconds);
    test.setTimeout(interactiveSignInTimeoutMs + ((stateLifetimeSeconds + 5) * 1_000) + postSignInBudgetMs);

    await signInInteractively(page);

    let callbackForm: URLSearchParams | undefined;
    const runtimeResponses: string[] = [];
    page.on("request", (request) => {
      if (request.method() === "POST" && request.url() === `${runtimeOrigin}/managed-elsa/handoff/callback`)
        callbackForm = new URLSearchParams(request.postData() ?? "");
    });
    page.on("response", (response) => {
      const uri = new URL(response.url());
      if (uri.origin === runtimeOrigin && runtimeResponses.length < 12)
        runtimeResponses.push(`${response.request().method()} ${uri.pathname} ${response.status()}`);
    });

    const callbackResponsePromise = page.waitForResponse((response) =>
      response.request().method() === "POST" && response.url() === `${runtimeOrigin}/managed-elsa/handoff/callback`);
    await openHealthyInstance(page);
    const callbackResponse = await callbackResponsePromise;
    const callbackLocation = safeRedirectPath(callbackResponse.headers().location);
    try {
      await assertAuthenticatedStudioSession(page, runtimeOrigin, {
        callbackStatus: callbackResponse.status(),
        callbackLocation
      });
    }
    catch (error) {
      throw new Error(
        `${error instanceof Error ? error.message : String(error)} ` +
        `responses: ${runtimeResponses.join(" | ") || "none"}.`);
    }
    expect(await protectedOperationStatus(page)).toBe(200);

    expect(callbackForm).toBeDefined();
    expect([...callbackForm!.keys()].sort()).toEqual(["code", "state"]);
    const replay = await page.request.post(`${runtimeOrigin}/managed-elsa/handoff/callback`, {
      form: Object.fromEntries(callbackForm!)
    });
    expect(replay.status()).toBe(400);

    const logoutStatus = await page.evaluate(async () =>
      fetch("/managed-elsa/logout", { method: "POST", credentials: "include" }).then((response) => response.status));
    expect(logoutStatus).toBe(204);
    expect(await protectedOperationStatus(page)).toBe(401);

    await page.goto(`${controlOrigin}/admin/runtimes`);
    await expect(page.getByRole("heading", { name: "Managed Elsa", exact: true })).toBeVisible();
    await page.route("**/api/managed-elsa/handoff/issue", async (route) => {
      await new Promise((resolve) => setTimeout(resolve, (stateLifetimeSeconds + 5) * 1_000));
      await route.continue();
    }, { times: 1 });

    const expiredCallback = page.waitForResponse((response) =>
      response.request().method() === "POST" && response.url() === `${runtimeOrigin}/managed-elsa/handoff/callback`);
    await managedInstanceRow(page).getByRole("button", { name: "Open" }).click();
    expect((await expiredCallback).status()).toBe(400);
  });
});

async function signInInteractively(page: Page) {
  await page.goto(`${controlOrigin}/admin/runtimes`);
  const managedElsaHeading = page.getByRole("heading", { name: "Managed Elsa", exact: true });
  if (await managedElsaHeading.isVisible())
    return;

  await expect(page.getByRole("heading", { name: "Sign in to Elsa Control" })).toBeVisible();
  await page.getByRole("button", { name: "Continue to sign in" }).click();
  await waitForControlReturn(page);
  await expect(managedElsaHeading).toBeVisible();
}

async function waitForControlReturn(page: Page) {
  const deadline = Date.now() + interactiveSignInTimeoutMs;
  while (Date.now() < deadline) {
    const current = new URL(page.url());
    const isAdminRoute = current.pathname === "/admin" || current.pathname.startsWith("/admin/");
    if (current.origin === controlOrigin && isAdminRoute) {
      if (current.pathname !== "/admin/runtimes")
        await page.goto(`${controlOrigin}/admin/runtimes`);
      return;
    }
    await page.waitForTimeout(500);
  }

  throw new Error("Interactive sign-in did not return to Elsa Control within the bounded window.");
}

async function openHealthyInstance(page: Page) {
  const instanceRow = managedInstanceRow(page);
  await expect(instanceRow).toContainText("Healthy", { timeout: 30_000 });
  await instanceRow.getByRole("button", { name: "Open" }).click();
}

function managedInstanceRow(page: Page) {
  return page.getByRole("row").filter({ hasText: "Managed Elsa browser proof" });
}

function protectedOperationStatus(page: Page) {
  return page.evaluate(async () =>
    fetch("/elsa/api/workflow-definitions?skip=0&take=1", { credentials: "include" }).then((response) => response.status));
}

function requirePublicHttpsOrigin(value: string, variableName: string) {
  const message = `${variableName} must be a public HTTPS origin without credentials, query, or fragment.`;
  let uri: URL;
  try {
    uri = new URL(value);
  }
  catch {
    throw new Error(message);
  }
  if (uri.protocol !== "https:" || uri.username || uri.password || uri.pathname !== "/" || uri.search || uri.hash)
    throw new Error(message);
}

function safeRedirectPath(value: string | undefined) {
  if (!value)
    return "none";

  try {
    const uri = new URL(value, runtimeOrigin);
    return uri.origin === runtimeOrigin ? uri.pathname : "external";
  }
  catch {
    return "invalid";
  }
}

function requireBoundedStateLifetime(value: number) {
  if (!Number.isInteger(value) || value < 5 || value > 300)
    throw new Error("MANAGED_ELSA_PROOF_STATE_LIFETIME_SECONDS must be an integer from 5 through 300.");
}
