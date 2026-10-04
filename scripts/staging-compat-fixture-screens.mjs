#!/usr/bin/env node
// Capture calm-sand customer screens for the staging compatibility fixture.
// Never writes Playwright traces, HAR captures, videos, or browser storage files
// into uploaded artifacts. Session state stays in PLAYWRIGHT_STATE_PATH only.

import { createRequire } from "node:module";
import { access, mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";

const require = createRequire(import.meta.url);
const { chromium, devices } = require("playwright");

const ALLOWED_ORIGIN = "https://calm-sand-03964eb03.2.azurestaticapps.net";
const UPDATE_BANNER = "Service update in progress.";
const HOSTED_PAUSED = "Managed engine actions are temporarily paused";
const SIDE_SURFACES = "Billing, sign-out, and support remain available";
// Restored-only hosted copy inside #main. Do not match /Managed engine/ —
// that also matches the armed paused text. Do not match "Existing engines"
// — that is a sidebar NavLink and would settle (or hang on mobile) before
// the dashboard content. "Hosted subscription" and "Engine details" cover
// an entitled Owner and a user who already has an engine; neither renders
// while armed.
const RESTORED_HOSTED = /No managed engines|Confirm managed engine|Create your first engine|Confirm and create engine|Start Hosted|Hosted subscription|Engine details/i;
const UUID_PATTERN = /[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/i;
const PII_HIDE_SELECTORS = [".acct-name", ".acct-mail", ".avatar", "#cloud-workspace", ".vh h1"];
const PII_HIDE_STYLE = `${PII_HIDE_SELECTORS.join(", ")} { visibility: hidden !important; transition: none !important; animation: none !important; } #cloud-workspace * { transition: none !important; }`;

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
  await page.locator("#main").getByText(RESTORED_HOSTED).first().waitFor({ timeout: 45_000 });
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

async function collectIdentitySecrets(page, email) {
  const trimmedEmail = (email ?? "").trim();
  if (!trimmedEmail) {
    throw new Error("Identity values were not collected before capture.");
  }
  const secrets = [trimmedEmail];
  for (const selector of [".acct-name", ".acct-mail"]) {
    const locators = page.locator(selector);
    const count = await locators.count();
    let found = false;
    for (let index = 0; index < count; index += 1) {
      const text = ((await locators.nth(index).textContent()) ?? "").trim();
      if (text) {
        secrets.push(text);
        found = true;
      }
    }
    if (!found) {
      throw new Error("Identity values were not collected before capture.");
    }
  }
  return secrets;
}

async function hidePiiInDom(page) {
  await page.addStyleTag({ content: PII_HIDE_STYLE });
}

async function assertNoVisibleIdentity(page, secrets) {
  const expected = Array.isArray(secrets) ? secrets.map((secret) => (secret ?? "").trim()).filter(Boolean) : [];
  if (expected.length === 0) {
    throw new Error("Identity values were not collected before capture.");
  }
  for (const secret of expected) {
    for (const locator of await page.getByText(secret, { exact: false }).all()) {
      if (await locator.isVisible()) {
        throw new Error("A customer identity string was still visible at capture time.");
      }
    }
  }
}

async function redactPiiText(page) {
  await page.evaluate((selectors) => {
    for (const selector of selectors) {
      for (const element of document.querySelectorAll(selector)) {
        element.textContent = "REDACTED";
      }
    }
  }, PII_HIDE_SELECTORS);
}

async function capture(page, directory, stem, email, viewport) {
  const file = path.join(directory, `${stem}.png`);
  await page.evaluate(() => {
    const active = document.activeElement;
    if (active && active !== document.body && typeof active.blur === "function") {
      active.blur();
    }
  });
  const secrets = await collectIdentitySecrets(page, email);
  await hidePiiInDom(page);
  await assertNoVisibleIdentity(page, secrets);
  await redactPiiText(page);
  await page.screenshot({
    path: file,
    fullPage: Boolean(viewport?.isMobile),
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
        files.push(await capture(page, directory, `${viewport.name}-armed`, email, viewport));
        await visitBillingAndSupport(page, origin);
        await page.goto(`${origin}/dashboard`, { waitUntil: "domcontentloaded" });
        await page.reload({ waitUntil: "domcontentloaded" });
        await assertArmed(page, email, viewport);
        files.push(await capture(page, directory, `${viewport.name}-armed-reload`, email, viewport));
      } else if (phase === "restored") {
        await page.goto(`${origin}/dashboard`, { waitUntil: "domcontentloaded" });
        await page.reload({ waitUntil: "domcontentloaded" });
        await assertRestored(page, email, viewport);
        files.push(await capture(page, directory, `${viewport.name}-restored-reload`, email, viewport));
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
  console.log(`Captured ${files.length} ${phase} screens. Identity hidden in DOM and masked.`);
}

const PROVE_EMAIL = "compat-user@example.test";
const PROVE_DISPLAY_NAME = "Compat Display Name";
const IDENTITY_LEAK = /compat-user@example\.test|Compat Display Name|@example\.test/i;

function functionBody(source, name) {
  const start = source.indexOf(`async function ${name}`);
  if (start < 0) {
    throw new Error(`Missing ${name}.`);
  }
  const rest = source.slice(start);
  const next = rest.indexOf("\nasync function ", 1);
  return next < 0 ? rest : rest.slice(0, next);
}

function assertNoIdentityLeak(text) {
  if (IDENTITY_LEAK.test(String(text ?? ""))) {
    throw new Error("An identity path leaked a secret.");
  }
}

function identityLocatorPage(textsBySelector) {
  return {
    locator(selector) {
      const texts = textsBySelector[selector] ?? [];
      return {
        async count() {
          return texts.length;
        },
        nth(index) {
          return {
            async textContent() {
              return texts[index] ?? "";
            }
          };
        }
      };
    }
  };
}

async function withIdentityIoGuard(work) {
  const originals = {
    log: console.log,
    info: console.info,
    warn: console.warn,
    error: console.error,
    debug: console.debug,
    stdout: process.stdout.write.bind(process.stdout),
    stderr: process.stderr.write.bind(process.stderr)
  };
  const writes = [];
  const tapConsole = (...args) => {
    writes.push(args.map((value) => String(value)).join(" "));
  };
  const tapStream = () => (chunk, encoding, callback) => {
    writes.push(String(chunk));
    if (typeof encoding === "function") {
      encoding();
    } else if (typeof callback === "function") {
      callback();
    }
    return true;
  };
  console.log = tapConsole;
  console.info = tapConsole;
  console.warn = tapConsole;
  console.error = tapConsole;
  console.debug = tapConsole;
  process.stdout.write = tapStream();
  process.stderr.write = tapStream();
  try {
    const result = await work();
    for (const write of writes) {
      assertNoIdentityLeak(write);
    }
    return result;
  } catch (error) {
    for (const write of writes) {
      assertNoIdentityLeak(write);
    }
    assertNoIdentityLeak(error?.message);
    throw error;
  } finally {
    console.log = originals.log;
    console.info = originals.info;
    console.warn = originals.warn;
    console.error = originals.error;
    console.debug = originals.debug;
    process.stdout.write = originals.stdout;
    process.stderr.write = originals.stderr;
  }
}

function assertIdentityPathsDoNotInterpolate(source) {
  for (const name of ["collectIdentitySecrets", "assertNoVisibleIdentity", "capture"]) {
    const body = functionBody(source, name);
    if (/\bconsole\./.test(body) || /\bprocess\.std/.test(body)) {
      throw new Error("An identity path writes to the console or a std stream.");
    }
    for (const match of body.matchAll(/throw new Error\((.*)\);/g)) {
      if (!/^"[^"\\]*"$/.test(match[1].trim())) {
        throw new Error("An identity path interpolates a value in an error.");
      }
    }
  }
  const collector = functionBody(source, "collectIdentitySecrets");
  if (!/return secrets;/.test(collector)) {
    throw new Error("collectIdentitySecrets does not return secrets.");
  }
  if (!/if\s*\(\s*!trimmedEmail\s*\)/.test(collector)) {
    throw new Error("The blank-email collector check is missing.");
  }
  if (!/if\s*\(\s*!found\s*\)/.test(collector)) {
    throw new Error("The missing-identity collector check is missing.");
  }
}

async function proveIdentityCollector() {
  const source = await readFile(new URL(import.meta.url), "utf8");
  assertIdentityPathsDoNotInterpolate(source);
  const page = identityLocatorPage({
    ".acct-name": [PROVE_DISPLAY_NAME],
    ".acct-mail": [PROVE_EMAIL]
  });
  const collected = await withIdentityIoGuard(() => collectIdentitySecrets(page, PROVE_EMAIL));
  if (!Array.isArray(collected) || collected.length === 0) {
    throw new Error("collectIdentitySecrets did not return secrets.");
  }
  if (!collected.includes(PROVE_EMAIL) || !collected.includes(PROVE_DISPLAY_NAME)) {
    throw new Error("collectIdentitySecrets omitted a collected identity value.");
  }
  try {
    await withIdentityIoGuard(() => collectIdentitySecrets(page, "   "));
    throw new Error("blank-email-miss");
  } catch (error) {
    if (error.message === "blank-email-miss") {
      throw error;
    }
    if (!/not collected/.test(error.message)) {
      throw error;
    }
  }
  const emptyPage = identityLocatorPage({ ".acct-name": [], ".acct-mail": [] });
  try {
    await withIdentityIoGuard(() => collectIdentitySecrets(emptyPage, PROVE_EMAIL));
    throw new Error("not-found-miss");
  } catch (error) {
    if (error.message === "not-found-miss") {
      throw error;
    }
    if (!/not collected/.test(error.message)) {
      throw error;
    }
  }
  const blankNamePage = identityLocatorPage({
    ".acct-name": ["   "],
    ".acct-mail": [PROVE_EMAIL]
  });
  try {
    await withIdentityIoGuard(() => collectIdentitySecrets(blankNamePage, PROVE_EMAIL));
    throw new Error("blank-name-miss");
  } catch (error) {
    if (error.message === "blank-name-miss") {
      throw error;
    }
    if (!/not collected/.test(error.message)) {
      throw error;
    }
  }
}

async function provePiiGuard() {
  await proveIdentityCollector();
  const visiblePage = {
    getByText() {
      return {
        async all() {
          return [{ async isVisible() { return true; } }];
        }
      };
    }
  };
  const hiddenPage = {
    getByText() {
      return {
        async all() {
          return [{ async isVisible() { return false; } }];
        }
      };
    }
  };
  let failed = false;
  try {
    await withIdentityIoGuard(() => assertNoVisibleIdentity(visiblePage, [PROVE_EMAIL]));
  } catch (error) {
    failed = true;
    if (IDENTITY_LEAK.test(error.message)) {
      throw new Error("The identity guard leaked a secret in its error.");
    }
    if (!/identity string was still visible/.test(error.message)) {
      throw error;
    }
  }
  if (!failed) {
    throw new Error("The identity guard accepted a visible secret.");
  }
  await withIdentityIoGuard(() => assertNoVisibleIdentity(hiddenPage, [PROVE_EMAIL]));
  try {
    await withIdentityIoGuard(() => assertNoVisibleIdentity(hiddenPage, []));
    throw new Error("empty-secrets-miss");
  } catch (error) {
    if (error.message === "empty-secrets-miss") {
      throw error;
    }
    if (!/not collected/.test(error.message)) {
      throw error;
    }
  }
  for (const selector of [".acct-name", ".acct-mail", ".avatar", "#cloud-workspace", ".vh h1"]) {
    if (!PII_HIDE_SELECTORS.includes(selector) || !PII_HIDE_STYLE.includes(selector)) {
      throw new Error("The hide style is missing a target selector.");
    }
  }
  if (!/visibility:\s*hidden/i.test(PII_HIDE_STYLE)) {
    throw new Error("The hide style does not set visibility hidden.");
  }
  if (!/transition:\s*none/i.test(PII_HIDE_STYLE) || !/animation:\s*none/i.test(PII_HIDE_STYLE)) {
    throw new Error("The hide style still allows transitions or animations.");
  }
  if (!/#cloud-workspace \*\s*\{[^}]*transition:\s*none/.test(PII_HIDE_STYLE)) {
    throw new Error("The hide style does not disable transitions on workspace descendants.");
  }
  console.log("pii-guard-ok");
}

if (process.argv.includes("--prove-pii-guard")) {
  await provePiiGuard();
  process.exit(0);
}

await main();
