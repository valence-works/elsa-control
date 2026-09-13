import type {
  ReleaseCatalogEntry,
  ReleaseCatalogIdentityFacts
} from "@/features/release-catalog/releaseCatalogModels";

const digestPattern = /^sha256:[0-9a-f]{64}$/i;

export function parseProducerFacts(payload: string, manifestDigest = ""): ReleaseCatalogIdentityFacts | null {
  const root = parseObject(payload);
  if (!root) return null;
  const release = asObject(root.release);
  if (!release) return null;

  const releaseVersion = firstString(release, ["version", "exactVersion", "releaseVersion"]);
  const releaseLine = firstString(release, ["releaseLine"]);
  const channel = firstString(release, ["channel"]);
  if (!releaseVersion || !releaseLine || !channel) return null;

  const source = asObject(release.source);
  const workflow = asObject(source?.workflow);
  const topologies = parseTopologyIds(root);
  const capabilities = parseCapabilities(root);
  const sourceRunId = firstString(workflow ?? {}, ["runId", "id"]) ?? firstString(source ?? {}, ["runId"]) ?? "";

  return {
    distributionId: firstString(release, ["distributionId", "id"]) ?? "",
    generation: firstString(release, ["generation"]) ?? "producer-2.0.0",
    releaseLine,
    releaseVersion,
    channel,
    producerLifecycle: firstString(release, ["lifecycle"]) ?? "",
    catalogLifecycle: null,
    registryClass: "paid",
    manifestDigest: digestPattern.test(manifestDigest.trim()) ? manifestDigest.trim() : "",
    sourceRunId,
    topologies,
    capabilities,
    build: resolveBuildIdentity(releaseVersion, sourceRunId)
  };
}

export function factsFromCatalogEntries(entries: ReleaseCatalogEntry[] | undefined | null): ReleaseCatalogIdentityFacts | null {
  if (!entries?.length) return null;
  const first = entries[0];
  const topologies = unique(entries.map((entry) => entry.topology.id));
  const capabilities = unique(entries.flatMap((entry) => entry.topology.capabilities));
  return {
    distributionId: first.distribution.id,
    generation: first.distribution.generation,
    releaseLine: first.distribution.releaseLine,
    releaseVersion: first.distribution.releaseVersion,
    channel: first.distribution.channel,
    producerLifecycle: first.distribution.producerLifecycle,
    catalogLifecycle: first.distribution.catalogLifecycle,
    registryClass: first.registryClass,
    manifestDigest: first.manifestDigest,
    sourceRunId: first.distribution.source.runId,
    topologies,
    capabilities,
    build: resolveBuildIdentity(first.distribution.releaseVersion, first.distribution.source.runId)
  };
}

export function fingerprintsUnchanged(existing: ReleaseCatalogIdentityFacts | null, incoming: ReleaseCatalogIdentityFacts | null) {
  if (!existing || !incoming) return false;
  return existing.manifestDigest.length > 0
    && incoming.manifestDigest.length > 0
    && existing.manifestDigest.toLowerCase() === incoming.manifestDigest.toLowerCase()
    && sameSet(existing.topologies, incoming.topologies)
    && existing.releaseVersion.toLowerCase() === incoming.releaseVersion.toLowerCase()
    && existing.releaseLine.toLowerCase() === incoming.releaseLine.toLowerCase()
    && existing.channel.toLowerCase() === incoming.channel.toLowerCase()
    && existing.generation.toLowerCase() === incoming.generation.toLowerCase()
    && existing.distributionId.toLowerCase() === incoming.distributionId.toLowerCase()
    && existing.registryClass.toLowerCase() === incoming.registryClass.toLowerCase();
}

export function identityTuple(facts: ReleaseCatalogIdentityFacts) {
  return [
    facts.distributionId,
    facts.generation,
    facts.releaseLine,
    facts.releaseVersion,
    facts.registryClass
  ].filter(Boolean).join(" · ");
}

export function extractBuild(version: string) {
  const match = version.match(/build\.(\d+)/i);
  return match?.[1] ?? null;
}

export function resolveBuildIdentity(version: string, sourceRunId = "") {
  const fromVersion = extractBuild(version);
  if (fromVersion) return fromVersion;
  const run = sourceRunId.trim();
  return /^\d+$/.test(run) ? run : null;
}

export function formatBuildIdentity(facts: Pick<ReleaseCatalogIdentityFacts, "build" | "releaseVersion" | "sourceRunId">) {
  const build = facts.build ?? resolveBuildIdentity(facts.releaseVersion, facts.sourceRunId);
  return build ? `build.${build.replace(/^build\./i, "")}` : "—";
}

export function existingCatalogFocusKey(facts: Pick<ReleaseCatalogIdentityFacts, "distributionId" | "releaseLine" | "releaseVersion">) {
  return [facts.distributionId, facts.releaseLine, facts.releaseVersion]
    .map((part) => part.trim())
    .filter(Boolean)
    .join("|");
}

export function existingCatalogHref(facts: Pick<ReleaseCatalogIdentityFacts, "distributionId" | "releaseLine" | "releaseVersion"> | null) {
  if (!facts?.releaseVersion.trim()) return "/admin/releases";
  const params = new URLSearchParams({ existing: existingCatalogFocusKey(facts) });
  return `/admin/releases?${params.toString()}`;
}

export function catalogEntryMatchesExistingFocus(entry: ReleaseCatalogEntry, focusKey: string) {
  if (!focusKey.trim()) return false;
  return existingCatalogFocusKey({
    distributionId: entry.distribution.id,
    releaseLine: entry.distribution.releaseLine,
    releaseVersion: entry.distribution.releaseVersion
  }).toLowerCase() === focusKey.trim().toLowerCase();
}

export function catalogIdentityKey(entry: ReleaseCatalogEntry) {
  return [
    entry.distribution.id,
    entry.distribution.generation,
    entry.distribution.releaseLine,
    entry.distribution.releaseVersion,
    entry.registryClass
  ].join("|").toLowerCase();
}

function parseTopologyIds(root: Record<string, unknown>) {
  const distributions = Array.isArray(root.distributions) ? root.distributions : [];
  return unique(distributions.flatMap((item) => {
    const distribution = asObject(item);
    if (!distribution) return [];
    const topology = firstString(distribution, ["topology", "topologyId"]);
    if (topology) return [topology];
    return firstString(distribution, ["distributionId"]) ? [] : firstString(distribution, ["id"]) ? [firstString(distribution, ["id"])!] : [];
  }));
}

function parseCapabilities(root: Record<string, unknown>) {
  const distributions = Array.isArray(root.distributions) ? root.distributions : [];
  return unique(distributions.flatMap((item) => {
    const distribution = asObject(item);
    return Array.isArray(distribution?.capabilities)
      ? distribution.capabilities.filter((value): value is string => typeof value === "string" && value.trim().length > 0)
      : [];
  }));
}

function parseObject(payload: string) {
  try {
    const parsed: unknown = JSON.parse(payload);
    return asObject(parsed);
  } catch {
    return null;
  }
}

function asObject(value: unknown): Record<string, unknown> | null {
  return value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : null;
}

function firstString(record: Record<string, unknown>, keys: string[]) {
  for (const key of keys) {
    const value = record[key];
    if (typeof value === "string" && value.trim().length > 0) return value.trim();
  }
  return null;
}

function unique(values: string[]) {
  return [...new Set(values.map((value) => value.trim()).filter(Boolean))];
}

function sameSet(left: string[], right: string[]) {
  if (left.length !== right.length) return false;
  const normalized = new Set(left.map((value) => value.toLowerCase()));
  return right.every((value) => normalized.has(value.toLowerCase()));
}
