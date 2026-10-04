#!/usr/bin/env node
// Capture calm-sand customer screens for the staging compatibility fixture.
// Never writes Playwright traces, HAR captures, videos, or browser storage files.

import { chromium, devices } from "playwright";
import { mkdir, writeFile } from "node:fs/promises";
import path from "node:path";

const ALLOWED_ORIGIN = "https://calm-sand-03964eb03.2.azurestaticapps.net";
const UPDATE_BANNER = "Service update in progress.";
const HOSTED_PAUSED = "Managed engine actions are temporarily paused";
const SIDE_SURFACES = "Billing, sign-out, and support remain available";
const UUID_PATTERN = /[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}/i;

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
    page.getByText(UUID_PATTERN)
  ];
}

async function assertArmed(page, email) {
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
  await openAccountMenu(page, email);
  await page.getByRole("menuitem", { name: "Billing and plans" }).waitFor();
  await page.getByRole("menuitem", { name: "Sign out" }).waitFor();
  await page.getByRole("link", { name: /hello@valence.works/i }).first().waitFor();
}

async function assertRestored(page) {
  await page.waitForTimeout(1_000);
  if (await page.getByText(UPDATE_BANNER, { exact: true }).count()) {
    throw new Error("The update-in-progress banner was still visible after restore.");
  }
  if (await page.getByText(HOSTED_PAUSED, { exact: false }).count()) {
    throw new Error("Hosted engine actions were still paused after restore.");
  }
}

async function openAccountMenu(page, email) {
  if (await page.getByRole("menu").count()) {
    return;
  }
  const candidates = [
    page.getByRole("button", { name: email, exact: false }),
    page.getByRole("button", { name: /signed in|account|menu/i })
  ];
  for (const candidate of candidates) {
    if (await candidate.count()) {
      await candidate.first().click();
      if (await page.getByRole("menu").count()) {
        return;
      }
    }
  }
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

async function runPhase({ phase, origin, email, password, directory }) {
  const browser = await chromium.launch();
  const files = [];
  try {
    for (const viewport of viewports()) {
      const context = await browser.newContext({
        viewport: viewport.viewport,
        isMobile: Boolean(viewport.isMobile),
        hasTouch: Boolean(viewport.hasTouch),
        ignoreHTTPSErrors: false
      });
      context.setDefaultNavigationTimeout(45_000);
      const page = await context.newPage();
      await signIn(page, origin, email, password);
      if (phase === "armed") {
        await assertArmed(page, email);
        files.push(await capture(page, directory, `${viewport.name}-armed`, email));
        await visitBillingAndSupport(page, origin);
        await page.goto(`${origin}/dashboard`, { waitUntil: "domcontentloaded" });
        await page.reload({ waitUntil: "domcontentloaded" });
        await assertArmed(page, email);
        files.push(await capture(page, directory, `${viewport.name}-armed-reload`, email));
      } else if (phase === "restored") {
        await page.reload({ waitUntil: "domcontentloaded" });
        await assertRestored(page);
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
  const origin = originFromEnv();
  refuseProduction(email, "STAGING_E2E_COMPAT_EMAIL");
  await mkdir(directory, { recursive: true });
  const files = await runPhase({ phase, origin, email, password, directory });
  await writeFile(
    path.join(directory, "manifest.txt"),
    files.map((file) => path.basename(file)).join("\n") + "\n",
    "utf8"
  );
  console.log(`Captured ${files.length} ${phase} screens. Email and org ids were masked.`);
}

await main();
