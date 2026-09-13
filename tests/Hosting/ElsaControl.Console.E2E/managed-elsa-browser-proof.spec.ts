import { execFile } from "node:child_process";
import path from "node:path";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { expect, type Page, test } from "@playwright/test";
import { assertAuthenticatedStudioSession } from "./helpers/assertAuthenticatedStudioSession";

const execFileAsync = promisify(execFile);
const proofEnabled = process.env.MANAGED_ELSA_BROWSER_PROOF === "1";
const keycloakUsername = process.env.MANAGED_ELSA_PROOF_USERNAME ?? "ada";
const keycloakPassword = process.env.MANAGED_ELSA_PROOF_PASSWORD ?? "password";
const runtimeOrigin = (process.env.MANAGED_ELSA_PROOF_RUNTIME_ORIGIN ?? "https://runtime.localhost:7444")
  .replace(/\/+$/, "");
const fixtureDatabase = process.env.MANAGED_ELSA_PROOF_DATABASE;
const repositoryRoot = fileURLToPath(new URL("../../..", import.meta.url));
const fixtureProject = process.env.MANAGED_ELSA_PROOF_FIXTURE_PROJECT ??
  path.join(repositoryRoot, "src/Hosting/ElsaControl.ManagedBrowserProof/ElsaControl.ManagedBrowserProof.csproj");

test.use({ ignoreHTTPSErrors: true, trace: "off", screenshot: "off", video: "off" });

test.describe("managed Elsa browser proof", () => {
  test.skip(!proofEnabled, "Requires the isolated managed-Elsa proof fixture.");
  test.describe.configure({ mode: "serial" });

  test.beforeAll(() => {
    if (!fixtureDatabase)
      throw new Error("MANAGED_ELSA_PROOF_DATABASE is required for the live browser proof.");
  });

  test.afterEach(async () => {
    if (fixtureDatabase)
      await runFixture("restore");
  });

  test("opens a healthy instance, creates and runs a workflow, rejects replay, and revokes the runtime session", async ({ page }) => {
    test.setTimeout(90_000);
    await signInToControl(page);

    let callbackForm: URLSearchParams | undefined;
    page.on("request", (request) => {
      if (request.method() === "POST" && request.url() === `${runtimeOrigin}/managed-elsa/handoff/callback`)
        callbackForm = new URLSearchParams(request.postData() ?? "");
    });

    await openHealthyInstance(page);
    await assertAuthenticatedStudioSession(page, runtimeOrigin);
    const workflowProof = await createAndExecuteBasicWorkflow(page);
    expect(workflowProof.createStatus).toBe(200);
    expect(workflowProof.createdDefinitionId).toBe(workflowProof.definitionId);
    expect(workflowProof.createdDefinitionPublished).toBe(true);
    expect(workflowProof.executeStatus).toBe(200);
    expect(workflowProof.workflowInstanceId).toMatch(/^[A-Za-z0-9._-]{1,256}$/);
    expect(workflowProof.executionStatus).toBe("Finished");
    expect(workflowProof.executionSubStatus).toBe("Finished");

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
  });

  test("renders a verified but unavailable instance without an open action", async ({ page }) => {
    await runFixture("unavailable");
    await signInToControl(page);

    await expect(page.getByRole("heading", { name: "Managed Elsa", exact: true })).toBeVisible();
    const instanceRow = managedInstanceRow(page);
    await expect(instanceRow).toContainText("Unavailable");
    await expect(instanceRow.getByRole("button", { name: "Open" })).toHaveCount(0);
  });

  test("fails safely when organization membership is revoked before issuance", async ({ page }) => {
    await signInToControl(page);
    await expect(managedInstanceRow(page)).toContainText("Healthy");

    await page.route("**/api/managed-elsa/handoff/issue", async (route) => {
      await runFixture("revoke");
      await route.continue();
    }, { times: 1 });
    await managedInstanceRow(page).getByRole("button", { name: "Open" }).click();

    await expect(page.getByRole("alert")).toContainText(
      "This managed instance is no longer available to your account.",
      { timeout: 30_000 });
    await expect(page).toHaveURL(/\/admin\/runtimes$/);
  });

  test("rejects an expired browser-bound handoff state", async ({ page }) => {
    test.setTimeout(120_000);
    await signInToControl(page);
    await page.route("**/api/managed-elsa/handoff/issue", async (route) => {
      await new Promise((resolve) => setTimeout(resolve, 65_000));
      await route.continue();
    }, { times: 1 });

    const callbackResponse = page.waitForResponse((response) =>
      response.request().method() === "POST" && response.url() === `${runtimeOrigin}/managed-elsa/handoff/callback`);
    await managedInstanceRow(page).getByRole("button", { name: "Open" }).click();

    expect((await callbackResponse).status()).toBe(400);
  });
});

async function signInToControl(page: Page) {
  await page.goto("/admin/runtimes");
  await expect(page.getByRole("heading", { name: "Sign in to Elsa Control" })).toBeVisible();
  await page.getByRole("button", { name: "Continue to sign in" }).click();
  await page.getByLabel("Username or email").fill(keycloakUsername);
  await page.getByRole("textbox", { name: "Password" }).fill(keycloakPassword);
  await page.getByRole("button", { name: "Sign In" }).click();
  await expect(page).toHaveURL(/\/admin\/runtimes$/);
}

async function openHealthyInstance(page: Page) {
  await expect(page.getByRole("heading", { name: "Managed Elsa", exact: true })).toBeVisible();
  const instanceRow = managedInstanceRow(page);
  await expect(instanceRow).toContainText("Healthy");
  await instanceRow.getByRole("button", { name: "Open" }).click();
}

function managedInstanceRow(page: Page) {
  return page.getByRole("row").filter({ hasText: "Managed Elsa browser proof" });
}

function protectedOperationStatus(page: Page) {
  return page.evaluate(async () =>
    fetch("/elsa/api/workflow-definitions?skip=0&take=1", { credentials: "include" }).then((response) => response.status));
}

function createAndExecuteBasicWorkflow(page: Page) {
  const definitionId = "managed-elsa-browser-proof-v1";

  return page.evaluate(async ({ definitionId }) => {
    const knownWorkflowStatus = (value: unknown) => {
      const stringStatuses = ["Running", "Finished", "Faulted", "Cancelled", "Suspended", "Interrupted"];
      if (value === 0)
        return "Running";
      if (value === 1)
        return "Finished";
      return typeof value === "string" && stringStatuses.includes(value) ? value : undefined;
    };
    const knownWorkflowSubStatus = (value: unknown) => {
      const numericStatuses = ["Pending", "Executing", "Suspended", "Finished", "Cancelled", "Faulted", "Interrupted"];
      if (typeof value === "number" && Number.isInteger(value))
        return numericStatuses[value];
      return typeof value === "string" && numericStatuses.includes(value) ? value : undefined;
    };

    const createResponse = await fetch("/elsa/api/workflow-definitions", {
      method: "POST",
      credentials: "include",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        model: {
          definitionId,
          name: "Managed Elsa browser proof",
          description: "Synthetic managed runtime workflow smoke proof",
          variables: [],
          inputs: [],
          outputs: [],
          outcomes: [],
          customProperties: {},
          root: {
            id: "managed-elsa-browser-proof-root",
            type: "Elsa.Flowchart",
            version: 1,
            metadata: {},
            customProperties: {
              canStartWorkflow: false,
              runAsynchronously: false
            },
            activities: [
              {
                id: "managed-elsa-browser-proof-write-line",
                name: "WriteLine",
                type: "Elsa.WriteLine",
                version: 1,
                metadata: {},
                customProperties: {
                  canStartWorkflow: false,
                  runAsynchronously: false
                },
                text: {
                  typeName: "String",
                  expression: {
                    type: "Literal",
                    value: "managed-elsa-browser-proof"
                  }
                }
              }
            ],
            connections: []
          }
        },
        publish: true
      })
    });

    let createdDefinitionId: string | undefined;
    let createdDefinitionPublished: boolean | undefined;
    if (createResponse.ok) {
      const response = await createResponse.json() as {
        workflowDefinition?: { definitionId?: string; isPublished?: boolean };
      };
      createdDefinitionId = response.workflowDefinition?.definitionId;
      createdDefinitionPublished = response.workflowDefinition?.isPublished;
    }

    let executeStatus: number | undefined;
    let workflowInstanceId: string | undefined;
    let executionStatus: string | undefined;
    let executionSubStatus: string | undefined;
    if (createResponse.ok && createdDefinitionId === definitionId && createdDefinitionPublished === true) {
      const executeResponse = await fetch(
        `/elsa/api/workflow-definitions/${encodeURIComponent(definitionId)}/execute`,
        {
          method: "POST",
          credentials: "include",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            correlationId: "managed-elsa-browser-proof-v1",
            name: "Managed Elsa browser proof execution"
          })
        });
      executeStatus = executeResponse.status;

      const headerValue = executeResponse.headers.get("x-elsa-workflow-instance-id")?.trim();
      if (executeResponse.ok && headerValue && /^[A-Za-z0-9._-]{1,256}$/.test(headerValue)) {
        workflowInstanceId = headerValue;

        const deadline = Date.now() + 30_000;
        while (Date.now() < deadline) {
          const instanceResponse = await fetch(
            `/elsa/api/workflow-instances/${encodeURIComponent(workflowInstanceId)}`,
            { credentials: "include" });
          if (!instanceResponse.ok)
            break;

          const response = await instanceResponse.json() as {
            status?: unknown;
            subStatus?: unknown;
            workflowState?: { status?: unknown; subStatus?: unknown };
          };
          const state = response.workflowState ?? response;
          executionStatus = knownWorkflowStatus(response.status) ?? knownWorkflowStatus(state.status);
          executionSubStatus = knownWorkflowSubStatus(response.subStatus) ?? knownWorkflowSubStatus(state.subStatus);
          if ((executionStatus === "Finished" && executionSubStatus === "Finished") ||
            [executionStatus, executionSubStatus].some((value) =>
              ["Faulted", "Cancelled", "Suspended", "Interrupted"].includes(value ?? "")))
            break;

          await new Promise((resolve) => setTimeout(resolve, 500));
        }
      }
    }

    return {
      definitionId,
      createStatus: createResponse.status,
      createdDefinitionId,
      createdDefinitionPublished,
      executeStatus,
      workflowInstanceId,
      executionStatus,
      executionSubStatus
    };
  }, { definitionId });
}

async function runFixture(command: "restore" | "revoke" | "unavailable") {
  if (!fixtureDatabase)
    throw new Error("MANAGED_ELSA_PROOF_DATABASE is required for the live browser proof.");

  await execFileAsync("dotnet", [
    "run",
    "--no-build",
    "--project",
    fixtureProject,
    "--",
    command,
    fixtureDatabase,
    runtimeOrigin
  ], { cwd: repositoryRoot });
}
