import type { ManagedElsaInstanceIntent, ManagedElsaOnboardingOptions, ManagedElsaReleaseOption } from "@/features/managed-elsa/managedElsaModels";

export function onboardingChoices(options: ManagedElsaOnboardingOptions | undefined) {
  const entries = [
    ...(options?.releases ?? []).map((entry) => ({ ...entry, previewManifestDigest: null as string | null })),
    ...(options?.previewReleases ?? [])
      .filter((entry) => /^sha256:[0-9a-f]{64}$/.test(entry.manifestDigest))
      .map(({ manifestDigest, ...entry }) => ({ ...entry, previewManifestDigest: manifestDigest }))
  ];
  const groups = new Map<string, typeof entries>();
  for (const entry of entries) {
    const key = choiceKey(entry.distributionId, entry.releaseLine, entry.version, entry.channel, entry.topologyId);
    const group = groups.get(key) ?? [];
    group.push(entry);
    groups.set(key, group);
  }
  // Supported discovery retains its existing semantics. A stale Preview row must
  // not hide a Supported choice or grant Preview consent for an ambiguous identity.
  return [...groups.values()].flatMap((group) => {
    const supported = group.find((entry) => entry.previewManifestDigest === null);
    if (supported) return [supported];
    return group.every((entry) => entry.previewManifestDigest === group[0].previewManifestDigest) ? [group[0]] : [];
  });
}

export function buildManagedElsaIntent(
  selected: ManagedElsaReleaseOption & { previewManifestDigest?: string | null },
  options: ManagedElsaOnboardingOptions
): ManagedElsaInstanceIntent {
  return {
    release: {
      distributionId: selected.distributionId,
      releaseLine: selected.releaseLine,
      requestedVersion: selected.version,
      channel: selected.channel,
      patchUpdates: "automatic-within-minor",
      minorUpdates: "explicit-approval",
      majorMigrations: "explicit-migration",
      ...(selected.previewManifestDigest ? { previewManifestDigest: selected.previewManifestDigest } : {})
    },
    application: {
      topologyId: selected.topologyId,
      featurePresetId: null,
      featureOverrides: {},
      packagePolicy: null,
      configurationShapeRevisionId: null
    },
    placement: {
      targetMode: options.launchProfile.targetMode,
      regionCode: options.launchProfile.regionCode,
      isolationProfile: options.launchProfile.isolationProfile,
      capacityProfile: options.launchProfile.capacityProfile,
      networkOutcome: options.launchProfile.networkOutcome,
      domainOutcome: options.launchProfile.domainOutcome
    },
    desiredLifecycle: "Running"
  };
}

function choiceKey(distributionId: string, releaseLine: string, version: string, channel: string, topologyId: string) {
  return `${distributionId}|${releaseLine}|${version}|${channel}|${topologyId}`;
}

export function slugify(value: string) {
  return value.toLowerCase().trim().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 63);
}
