import { describe, expect, it } from "vitest";
import type { ManagedElsaOnboardingOptions } from "@/features/managed-elsa/managedElsaModels";
import {
  onboardingChoices,
  preferredOnboardingChoice
} from "@/features/managed-elsa/managedElsaOnboarding";

const digest = `sha256:${"b".repeat(64)}`;

describe("onboardingChoices", () => {
  it("keeps Preview out of the picker until includePreview is requested", () => {
    const options = optionsFixture();
    const hidden = onboardingChoices(options);
    expect(hidden.map((choice) => choice.version)).toEqual(["3.10.2", "3.9.1", "3.8.2"]);
    expect(hidden.every((choice) => !choice.previewManifestDigest)).toBe(true);

    const revealed = onboardingChoices(options, { includePreview: true });
    expect(revealed.map((choice) => choice.version)).toEqual(["3.10.2", "3.9.1", "3.8.2", "3.8.0-preview.5413"]);
  });

  it("prefers the recommended flag instead of array order", () => {
    const choices = onboardingChoices({
      ...optionsFixture(),
      releases: [
        release("3.8.2"),
        release("3.10.2", true),
        release("3.9.1")
      ]
    });
    expect(preferredOnboardingChoice(choices)?.version).toBe("3.10.2");
    expect(preferredOnboardingChoice(choices)?.recommended).toBe(true);
  });

  it("does not fall back to a Preview when nothing is recommended", () => {
    const choices = onboardingChoices({
      ...optionsFixture(),
      releases: []
    }, { includePreview: true });
    expect(preferredOnboardingChoice(choices)).toBeNull();
  });

  it("tolerates a Supported manifestDigest without treating it as Preview consent", () => {
    const choices = onboardingChoices({
      ...optionsFixture(),
      releases: [{ ...release("3.10.2", true), manifestDigest: digest }]
    });
    expect(choices).toHaveLength(1);
    expect(choices[0].manifestDigest).toBe(digest);
    expect(choices[0].previewManifestDigest).toBeNull();
    expect(preferredOnboardingChoice(choices)?.recommended).toBe(true);
  });
});

function optionsFixture(): ManagedElsaOnboardingOptions {
  return {
    releases: [
      release("3.10.2", true),
      release("3.9.1"),
      release("3.8.2")
    ],
    previewReleases: [{
      distributionId: "elsa",
      releaseLine: "3.8",
      version: "3.8.0-preview.5413",
      channel: "preview",
      topologyId: "managed",
      manifestDigest: digest
    }],
    launchProfile: {
      name: "West Europe Dedicated",
      description: "Managed hosting in West Europe.",
      targetMode: "managed",
      regionCode: "westeurope",
      isolationProfile: "dedicated",
      capacityProfile: "standard-small",
      networkOutcome: "public",
      domainOutcome: "managed"
    }
  };
}

function release(version: string, recommended = false) {
  return {
    distributionId: "elsa",
    releaseLine: "3.x",
    version,
    channel: "stable",
    topologyId: "managed",
    recommended
  };
}
