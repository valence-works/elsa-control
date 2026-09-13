import { describe, expect, it } from "vitest";
import {
  extractBuild,
  factsFromCatalogEntries,
  fingerprintsUnchanged,
  identityTuple,
  parseProducerFacts
} from "@/features/release-catalog/releaseManifestFacts";
import type { ReleaseCatalogEntry } from "@/features/release-catalog/releaseCatalogModels";

const digestA = `sha256:${"a".repeat(64)}`;
const digestB = `sha256:${"b".repeat(64)}`;

describe("releaseManifestFacts", () => {
  it("parses producer facts from a schema 2 payload", () => {
    const facts = parseProducerFacts(JSON.stringify({
      release: {
        distributionId: "valence-runtime",
        generation: "producer-2.0.0",
        releaseLine: "3.8",
        version: "3.8.0-preview.5567-build.160",
        channel: "preview",
        lifecycle: "preview",
        source: { workflow: { runId: "987" } }
      },
      distributions: [
        { topology: "combined", capabilities: ["workflow-runtime"] },
        { topology: "server" }
      ]
    }), digestA);

    expect(facts).toMatchObject({
      distributionId: "valence-runtime",
      generation: "producer-2.0.0",
      releaseLine: "3.8",
      releaseVersion: "3.8.0-preview.5567-build.160",
      channel: "preview",
      producerLifecycle: "preview",
      manifestDigest: digestA,
      sourceRunId: "987",
      topologies: ["combined", "server"],
      build: "160"
    });
    expect(identityTuple(facts!)).toContain("3.8.0-preview.5567-build.160");
  });

  it("treats matching digests and identity as Unchanged fingerprints", () => {
    const incoming = parseProducerFacts(JSON.stringify({
      release: { distributionId: "valence-runtime", releaseLine: "3.8", version: "3.8.0-preview.5567-build.160", channel: "preview" },
      distributions: [{ topology: "combined" }]
    }), digestA);
    const existing = factsFromCatalogEntries([entryFixture({ manifestDigest: digestA })]);

    expect(fingerprintsUnchanged(existing, incoming)).toBe(true);
    expect(fingerprintsUnchanged(existing, parseProducerFacts(JSON.stringify({
      release: { distributionId: "valence-runtime", releaseLine: "3.8", version: "3.8.0-preview.5567-build.160", channel: "preview" },
      distributions: [{ topology: "combined" }]
    }), digestB))).toBe(false);
  });

  it("extracts build.N from preview versions", () => {
    expect(extractBuild("3.8.0-preview.5567-build.160")).toBe("160");
    expect(extractBuild("3.8.0-preview.5567-build.151")).toBe("151");
    expect(extractBuild("3.8.0-preview.5567")).toBeNull();
  });
});

function entryFixture(overrides: Partial<ReleaseCatalogEntry> = {}): ReleaseCatalogEntry {
  return {
    schemaVersion: "2.0.0",
    manifestReference: "oci://example/manifest@sha256:aaa",
    manifestDigest: digestA,
    payloadDigest: digestA,
    signatureEvidenceReference: "oci://example/sig",
    signatureEvidenceDigest: digestA,
    registryClass: "paid",
    distribution: {
      id: "valence-runtime",
      generation: "producer-2.0.0",
      releaseLine: "3.8",
      releaseVersion: "3.8.0-preview.5567-build.160",
      channel: "preview",
      producerLifecycle: "preview",
      catalogLifecycle: "preview",
      edition: "commercial",
      source: { repository: "https://example", commit: "abc", runId: "1" }
    },
    topology: {
      id: "combined",
      packageManifestSchema: "1",
      runtimeKinds: ["combined"],
      capabilities: ["workflow-runtime"]
    },
    admittedAt: "2026-09-13T00:00:00Z",
    ...overrides
  };
}
