import type { ManagedElsaInstanceIntent, ManagedElsaOnboardingOptions, ManagedElsaReleaseOption } from "@/features/managed-elsa/managedElsaModels";

export type OnboardingChoice = ManagedElsaReleaseOption & {
  previewManifestDigest: string | null;
  recommended: boolean;
};

export function onboardingChoices(
  options: ManagedElsaOnboardingOptions | undefined,
  { includePreview = false }: { includePreview?: boolean } = {}
): OnboardingChoice[] {
  const entries: OnboardingChoice[] = [
    ...(options?.releases ?? []).map((entry) => ({
      ...entry,
      previewManifestDigest: null as string | null,
      recommended: Boolean(entry.recommended)
    })),
    ...(includePreview ? options?.previewReleases ?? [] : [])
      .filter((entry) => /^sha256:[0-9a-f]{64}$/.test(entry.manifestDigest))
      .map(({ manifestDigest, ...entry }) => ({
        ...entry,
        previewManifestDigest: manifestDigest,
        recommended: false
      }))
  ];
  const groups = new Map<string, typeof entries>();
  for (const entry of entries) {
    const key = choiceIdentityKey(entry);
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

export function preferredOnboardingChoice(choices: OnboardingChoice[], requestedVersion = "") {
  if (requestedVersion) {
    const requested = choices.find((choice) => choice.version === requestedVersion);
    if (requested) return requested;
  }
  return choices.find((choice) => choice.recommended) ?? null;
}

export function onboardingChoiceKey(choice: OnboardingChoice) {
  return `${choice.distributionId}|${choice.releaseLine}|${choice.version}|${choice.channel}|${choice.topologyId}|${choice.previewManifestDigest ?? ""}`;
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

function choiceIdentityKey(choice: Pick<OnboardingChoice, "distributionId" | "releaseLine" | "version" | "channel" | "topologyId">) {
  return `${choice.distributionId}|${choice.releaseLine}|${choice.version}|${choice.channel}|${choice.topologyId}`;
}

export function slugify(value: string) {
  return value.toLowerCase().trim().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 63);
}
