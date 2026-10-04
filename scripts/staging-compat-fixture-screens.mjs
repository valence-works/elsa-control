#!/usr/bin/env node
// Capture calm-sand customer screens for the staging compatibility fixture.
// Never writes Playwright traces, HAR captures, videos, or browser storage files
// into uploaded artifacts. Session state stays in PLAYWRIGHT_STATE_PATH only.

import { createRequire } from "node:module";
import { access, mkdir, writeFile } from "node:fs/promises";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium, devices } = require("playwright");

const ALLOWED_ORIGIN = "https://calm-sand-03964eb03.2.azurestaticapps.net";
const UPDATE_BANNER = "Service update in progress.";
const HOSTED_PAUSED = "Managed engine actions are temporarily paused";
const SIDE_SURFACES = "Billing, sign-out, and support remain available";
// Restored-only hosted copy. Do not match /Managed engine/ — that also
// matches the armed paused text and would settle the wait too early.
const RESTORED_HOSTED = /No managed engines|Confirm managed engine|Create your first engine|Confirm and create engine|Existing engines|Start Hosted/i;
const UUID_PATTERN = /[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/i;

if (process.argv.includes("--prove-load")) {
  if (!chromium || !devices) {
    throw new Error("playwright did not load.");
  }
  console.log("playwright-ok");
  process.exit(0);
}

function requiredEnv(name) {
  const value = process.env[name] ?? "";
  if (!value) {
    throw new Error(`${name} is required.`);
  }
  return value;
}

function refuseProduction(value, what) {
  if (/jhrcnclyydzngnyvhdht/i.test(value)) {
    throw new Error(`${what} uses the production Supabase project ref; the fixture refuses to run.`);
  }
}

function originFromEnv() {
  const origin = (process.env.STAGING_CLOUD_ORIGIN || ALLOWED_ORIGIN).replace(/\/+$/, "");
  if (origin !== ALLOWED_ORIGIN) {
    throw new Error("STAGING_CLOUD_ORIGIN must be the pinned calm-sand staging Cloud origin.");
  }
  refuseProduction(origin, "STAGING_CLOUD_ORIGIN");
  return origin;
}

function viewports() {
  return [
    { name: "desktop", viewport: { width: 1280, height: 800 } },
    { name: "mobile", viewport: devices["iPhone 14"].viewport, isMobile: true, hasTouch: true }
  ];
}

async function signIn(page, origin, email, password) {
  await page.goto(`${origin}/dashboard`, { waitUntil: "domcontentloaded" });
  const emailBox = page.getByLabel("Email", { exact: false });
  if (await emailBox.count()) {
    await emailBox.first().fill(email);
    await page.getByLabel("Password", { exact: false }).first().fill(password);
    await page.getByRole("button", { name: "Sign in", exact: true }).click();
  }
  await page.waitForURL(/\/dashboard/, { timeout: 45_000 });
}

function maskLocators(page, email) {
  return [
    page.getByText(email, { exact: false }),
    page.locator(".acct-name"),
    page.locator(".acct-mail"),
    page.locator(".avatar"),
    page.locator("#cloud-workspace"),
    page.locator(".vh h1").filter({ hasText: /^Welcome/ }),
    page.getByText(UUID_PATTERN)
  ];
}

async function openWorkspaceNavigation(page, viewport) {
  // AppShell (elsa-cloud src/components/app/AppShell.tsx @ b8718da7) has no
  // account menu. The sidebar is a drawer on mobile, behind the
  // "Open navigation" button.
  if (!viewport?.isMobile) {
    return;
  }
  const toggle = page.getByRole("button", { name: "Open navigation" });
  await toggle.waitFor({ timeout: 45_000 });
  if ((await toggle.getAttribute("aria-expanded")) !== "true") {
    await toggle.click();
  }
  await page.locator("#app-sidebar").waitFor({ state: "visible", timeout: 45_000 });
}

async function closeWorkspaceNavigation(page, viewport) {
  // Close the drawer before capture so masks land on settled layout and the
  // PNG shows the dashboard, not the sliding 320px sidebar (app.css 0.25s).
  if (!viewport?.isMobile) {
    return;
  }
  const sidebar = page.locator("#app-sidebar");
  if (await sidebar.isVisible()) {
    await page.keyboard.press("Escape");
    await sidebar.waitFor({ state: "hidden", timeout: 45_000 });
  }
}

async function assertSideSurfaces(page) {
  const sidebar = page.locator("#app-sidebar");
  await sidebar.getByRole("link", { name: "Billing and plans", exact: true }).waitFor();
  await sidebar.getByRole("button", { name: "Sign out", exact: true }).waitFor();
}

async function assertArmed(page, email, viewport) {
  await page.getByText(UPDATE_BANNER, { exact: false }).waitFor({ timeout: 45_000 });
  await page.getByText(HOSTED_PAUSED, { exact: false }).waitFor();
  await page.getByText(SIDE_SURFACES, { exact: false }).waitFor();
  const create = page.getByRole("button", { name: /create engine|Confirm and create engine/i });
  if (await create.count()) {
    for (const button of await create.all()) {
      if (await button.isVisible()) {
        const disabled = await button.isDisabled();
        if (!disabled) {
          throw new Error("A Hosted engine action stayed enabled while the fixture was armed.");
        }
      }
    }
  }
  await openWorkspaceNavigation(page, viewport);
  await assertSideSurfaces(page);
  await page.getByRole("link", { name: /hello@valence.works/i }).first().waitFor();
  await closeWorkspaceNavigation(page, viewport);
}

async function assertRestored(page, email, viewport) {
  // Wait until the armed copy has cleared so the absence checks do not race
  // the compatibility poll. RESTORED_HOSTED must not match the paused text.
  await page.getByText(UPDATE_BANNER, { exact: true }).waitFor({ state: "hidden", timeout: 45_000 });
  await page.getByText(HOSTED_PAUSED, { exact: false }).waitFor({ state: "hidden", timeout: 45_000 });
  await page.getByText(RESTORED_HOSTED).first().waitFor({ timeout: 45_000 });
  if (await page.getByText(UPDATE_BANNER, { exact: true }).count()) {
    throw new Error("The update-in-progress banner was still visible after restore.");
  }
  if (await page.getByText(HOSTED_PAUSED, { exact: false }).count()) {
    throw new Error("Hosted engine actions were still paused after restore.");
  }
  if (await page.getByText(SIDE_SURFACES, { exact: false }).count()) {
    throw new Error("The update-in-progress side-surface copy was still visible after restore.");
  }
  const create = page.getByRole("button", { name: /Confirm and create engine|Start Hosted/i });
  if (await create.count()) {
    for (const button of await create.all()) {
      if (await button.isVisible() && await button.isDisabled()) {
        throw new Error("A Hosted engine action stayed disabled after restore.");
      }
    }
  }
  await openWorkspaceNavigation(page, viewport);
  await assertSideSurfaces(page);
  await closeWorkspaceNavigation(page, viewport);
}

async function visitBillingAndSupport(page, origin) {
  await page.goto(`${origin}/dashboard/billing`, { waitUntil: "domcontentloaded" });
  await page.waitForTimeout(500);
  if (await page.getByRole("heading", { name: /doesn’t exist|does not exist/i }).count()) {
    throw new Error("Billing did not stay available.");
  }
}

async function capture(page, directory, stem, email) {
  const file = path.join(directory, `${stem}.png`);
  await page.screenshot({
    path: file,
    fullPage: true,
    mask: maskLocators(page, email),
    maskColor: "#6b7280"
  });
  return file;
}

async function fileExists(file) {
  try {
    await access(file);
    return true;
  } catch {
    return false;
  }
}

async function runPhase({ phase, origin, email, password, directory, statePath }) {
  const browser = await chromium.launch();
  const files = [];
  try {
    for (const viewport of viewports()) {
      const reuseSession = phase === "restored" && statePath && (await fileExists(statePath));
      if (phase === "restored" && !reuseSession) {
        throw new Error("Restored screens must reload the armed session; PLAYWRIGHT_STATE_PATH is missing.");
      }
      const context = await browser.newContext({
        viewport: viewport.viewport,
        isMobile: Boolean(viewport.isMobile),
        hasTouch: Boolean(viewport.hasTouch),
        reducedMotion: "reduce",
        ignoreHTTPSErrors: false,
        ...(reuseSession ? { storageState: statePath } : {})
      });
      context.setDefaultNavigationTimeout(45_000);
      const page = await context.newPage();
      if (phase === "armed") {
        await signIn(page, origin, email, password);
        if (statePath) {
          await context.storageState({ path: statePath });
        }
        await assertArmed(page, email, viewport);
        files.push(await capture(page, directory, `${viewport.name}-armed`, email));
        await visitBillingAndSupport(page, origin);
        await page.goto(`${origin}/dashboard`, { waitUntil: "domcontentloaded" });
        await page.reload({ waitUntil: "domcontentloaded" });
        await assertArmed(page, email, viewport);
        files.push(await capture(page, directory, `${viewport.name}-armed-reload`, email));
      } else if (phase === "restored") {
        await page.goto(`${origin}/dashboard`, { waitUntil: "domcontentloaded" });
        await page.reload({ waitUntil: "domcontentloaded" });
        await assertRestored(page, email, viewport);
        files.push(await capture(page, directory, `${viewport.name}-restored-reload`, email));
      } else {
        throw new Error("SCREENSHOT_PHASE must be armed or restored.");
      }
      await context.close();
    }
  } finally {
    await browser.close();
  }
  return files;
}

async function main() {
  const phase = requiredEnv("SCREENSHOT_PHASE");
  const email = requiredEnv("STAGING_E2E_COMPAT_EMAIL");
  const password = requiredEnv("STAGING_E2E_COMPAT_PASSWORD");
  const directory = requiredEnv("SCREENSHOT_DIR");
  const statePath = process.env.PLAYWRIGHT_STATE_PATH || "";
  const origin = originFromEnv();
  refuseProduction(email, "STAGING_E2E_COMPAT_EMAIL");
  if (statePath && /storage-state|trace|video/i.test(statePath)) {
    throw new Error("PLAYWRIGHT_STATE_PATH must not look like an uploaded browser artifact.");
  }
  await mkdir(directory, { recursive: true });
  const files = await runPhase({ phase, origin, email, password, directory, statePath });
  await writeFile(
    path.join(directory, "manifest.txt"),
    files.map((file) => path.basename(file)).join("\n") + "\n",
    "utf8"
  );
  console.log(`Captured ${files.length} ${phase} screens. Email, display name, and org ids were masked.`);
}

await main();
