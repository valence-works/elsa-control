import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { ReleasePicker, releaseOptionLabel, releasePickerCopy } from "@/features/managed-elsa/ReleasePicker";
import { onboardingChoiceKey, type OnboardingChoice } from "@/features/managed-elsa/managedElsaOnboarding";

const digest = `sha256:${"a".repeat(64)}`;

describe("ReleasePicker", () => {
  it("preselects the recommended release and badges it Recommended", () => {
    const choices = [
      supported("3.10.2", true),
      supported("3.9.1"),
      supported("3.8.2")
    ];
    renderPicker({ choices, value: onboardingChoiceKey(choices[0]) });

    const picker = screen.getByRole("combobox", { name: "Release" });
    expect(picker).toHaveValue(onboardingChoiceKey(choices[0]));
    expect(screen.getAllByText(releasePickerCopy.recommended).length).toBeGreaterThanOrEqual(1);
    expect(within(picker).getByRole("option", { name: releaseOptionLabel(choices[0]) })).toBeInTheDocument();
    expect(releaseOptionLabel(choices[0])).toContain(releasePickerCopy.recommended);
  });

  it("renders only the curated list the API returned and does not invent extra versions", () => {
    const choices = [
      supported("3.10.2", true),
      supported("3.9.1"),
      supported("3.8.2")
    ];
    renderPicker({ choices, value: onboardingChoiceKey(choices[0]) });

    const picker = screen.getByRole("combobox", { name: "Release" });
    const options = within(picker).getAllByRole("option");
    expect(options).toHaveLength(3);
    expect(options.map((option) => option.textContent)).toEqual(choices.map(releaseOptionLabel));
    expect(screen.queryByRole("option", { name: /3.7/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /preview/i })).not.toBeInTheDocument();
  });

  it("does not auto-select a Preview when nothing is recommended", () => {
    const preview = previewChoice("3.8.0-preview.5413");
    renderPicker({
      choices: [preview],
      value: "",
      canShowPreview: true,
      showPreview: true
    });

    const picker = screen.getByRole("combobox", { name: "Release" });
    expect(picker).toHaveValue("");
    expect(within(picker).getByRole("option", { name: "Select a release" })).toBeInTheDocument();
    expect(within(picker).getByRole("option", { name: /3.8.0-preview.5413/ })).toBeInTheDocument();
  });

  it("keeps Preview off by default and hides Preview entries until an Owner opts in", async () => {
    const supportedChoices = [supported("3.10.2", true)];
    const preview = previewChoice("3.8.0-preview.5413");
    const user = userEvent.setup();
    render(<Harness supported={supportedChoices} preview={[preview]} canShowPreview />);

    const picker = screen.getByRole("combobox", { name: "Release" });
    expect(screen.getByRole("checkbox", { name: /Show Preview releases/ })).not.toBeChecked();
    expect(within(picker).queryByRole("option", { name: /3.8.0-preview.5413/ })).not.toBeInTheDocument();
    expect(within(picker).getAllByRole("option")).toHaveLength(1);

    await user.click(screen.getByRole("checkbox", { name: /Show Preview releases/ }));
    expect(within(screen.getByRole("combobox", { name: "Release" })).getByRole("option", { name: /3.8.0-preview.5413/ })).toBeInTheDocument();
    expect(screen.getByText(releasePickerCopy.previewWarning)).toBeInTheDocument();
  });

  it("does not offer Preview to callers who are not workspace Owners", () => {
    renderPicker({
      choices: [supported("3.10.2", true)],
      value: onboardingChoiceKey(supported("3.10.2", true)),
      canShowPreview: false
    });

    expect(screen.queryByRole("checkbox", { name: /Show Preview releases/ })).not.toBeInTheDocument();
    expect(screen.queryByRole("option", { name: /Preview/ })).not.toBeInTheDocument();
  });
});

function Harness({
  supported,
  preview,
  canShowPreview
}: {
  supported: OnboardingChoice[];
  preview: OnboardingChoice[];
  canShowPreview: boolean;
}) {
  const [showPreview, setShowPreview] = useState(false);
  const choices = showPreview ? [...supported, ...preview] : supported;
  const [value, setValue] = useState(onboardingChoiceKey(supported[0]));
  return (
    <ReleasePicker
      label="Release"
      choices={choices}
      value={value}
      onChange={setValue}
      canShowPreview={canShowPreview}
      showPreview={showPreview}
      onShowPreviewChange={setShowPreview}
    />
  );
}

function renderPicker({
  choices,
  value,
  canShowPreview = false,
  showPreview = false
}: {
  choices: OnboardingChoice[];
  value: string;
  canShowPreview?: boolean;
  showPreview?: boolean;
}) {
  return render(
    <ReleasePicker
      label="Release"
      choices={choices}
      value={value}
      onChange={() => undefined}
      canShowPreview={canShowPreview}
      showPreview={showPreview}
      onShowPreviewChange={() => undefined}
    />
  );
}

function supported(version: string, recommended = false): OnboardingChoice {
  return {
    distributionId: "elsa",
    releaseLine: "3.x",
    version,
    channel: "stable",
    topologyId: "managed",
    previewManifestDigest: null,
    recommended
  };
}

function previewChoice(version: string): OnboardingChoice {
  return {
    distributionId: "elsa",
    releaseLine: "3.8",
    version,
    channel: "preview",
    topologyId: "managed",
    previewManifestDigest: digest,
    recommended: false
  };
}
