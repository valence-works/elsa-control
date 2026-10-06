// @vitest-environment node
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { build } from "vite";
import { consoleDependencyBoundary } from "./vite.config";

describe("production dependency boundary", () => {
  let directory: string;
  beforeEach(() => { directory = mkdtempSync(path.join(tmpdir(), "console-boundary-")); });
  afterEach(() => { rmSync(directory, { recursive: true, force: true }); });

  async function buildFixture(entry: string, external: string[] = []) {
    return build({
      configFile: false,
      root: directory,
      logLevel: "silent",
      plugins: [consoleDependencyBoundary()],
      build: { write: false, minify: false, rollupOptions: { input: entry, external } }
    });
  }

  it("accepts an actual clean browser chunk", async () => {
    const entry = path.join(directory, "index.js");
    writeFileSync(entry, 'console.log("clean-browser-chunk");');
    expect(await buildFixture(entry)).toMatchObject({
      output: expect.arrayContaining([expect.objectContaining({ type: "chunk", code: expect.stringContaining("clean-browser-chunk") })])
    });
  });

  it.each(["vitest", "@vitest/mocker", "tinypool", "braces", "micromatch", "fast-glob", "postcss-selector-parser", "source-map-js"])(
    "rejects %s in an actual emitted chunk", async (dependency) => {
      const entry = path.join(directory, "node_modules", dependency, "index.js");
      mkdirSync(path.dirname(entry), { recursive: true });
      writeFileSync(entry, 'console.log("build-only-module");');
      await expect(buildFixture(entry)).rejects.toThrow(`Production console bundle contains a build/test dependency: ${dependency}`);
    }
  );

  it.each([
    ['import { expand } from "braces"; console.log(expand("a"));', "braces"],
    ['import { expand } from "braces?raw"; console.log(expand("a"));', "braces?raw"],
    ['import("braces?raw").then(console.log);', "braces?raw"],
    ['import("braces#fragment").then(console.log);', "braces#fragment"]
  ])("rejects an external import including decorated/dynamic IDs: %s", async (source, dependency) => {
    const entry = path.join(directory, "index.js");
    writeFileSync(entry, source);
    await expect(buildFixture(entry, [dependency])).rejects.toThrow("Production console bundle contains a build/test dependency: braces");
  });
});
