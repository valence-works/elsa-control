import { Badge, Select } from "@/components/ui";
import { onboardingChoiceKey, type OnboardingChoice } from "@/features/managed-elsa/managedElsaOnboarding";

export const releasePickerCopy = {
  recommended: "Recommended",
  preview: "Preview",
  showPreview: "Show Preview releases",
  previewWarning: "Preview releases are for evaluation and have no availability SLO.",
  empty: "No admitted releases"
} as const;

export function releaseOptionLabel(choice: OnboardingChoice) {
  const labels = [`Elsa ${choice.version}`, choice.releaseLine, choice.channel, choice.topologyId];
  if (choice.recommended) labels.push(releasePickerCopy.recommended);
  if (choice.previewManifestDigest) labels.push(releasePickerCopy.preview);
  return labels.join(" · ");
}

export function ReleasePicker({
  label,
  ariaLabel,
  choices,
  value,
  onChange,
  disabled = false,
  canShowPreview,
  showPreview,
  onShowPreviewChange,
  previewError = null
}: {
  label: string;
  ariaLabel?: string;
  choices: OnboardingChoice[];
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  canShowPreview: boolean;
  showPreview: boolean;
  onShowPreviewChange: (value: boolean) => void;
  previewError?: string | null;
}) {
  const selected = choices.find((choice) => onboardingChoiceKey(choice) === value) ?? null;
  const comboboxLabel = ariaLabel ?? label;

  return (
    <div className="space-y-2">
      <label className="block space-y-1.5 text-sm">
        <span className="flex flex-wrap items-center gap-2">
          <span className="font-medium">{label}</span>
          {selected?.recommended ? <Badge>{releasePickerCopy.recommended}</Badge> : null}
          {selected?.previewManifestDigest ? (
            <Badge className="border-warning/40 bg-warning/10 text-warning">{releasePickerCopy.preview}</Badge>
          ) : null}
        </span>
        <Select
          aria-label={comboboxLabel}
          className="w-full"
          value={value}
          onChange={(event) => onChange(event.target.value)}
          disabled={disabled || choices.length === 0}
        >
          {choices.length === 0 || !value ? <option value="">{choices.length === 0 ? releasePickerCopy.empty : "Select a release"}</option> : null}
          {choices.map((choice) => (
            <option key={onboardingChoiceKey(choice)} value={onboardingChoiceKey(choice)}>
              {releaseOptionLabel(choice)}
            </option>
          ))}
        </Select>
      </label>
      {canShowPreview ? (
        <label className="flex items-start gap-3 rounded-ui border border-warning/40 bg-warning/10 p-3 text-sm">
          <input
            type="checkbox"
            className="mt-1 h-4 w-4 accent-primary"
            checked={showPreview}
            onChange={(event) => onShowPreviewChange(event.target.checked)}
            disabled={disabled}
          />
          <span>
            <span className="font-medium">{releasePickerCopy.showPreview}</span>
            <span className="mt-1 block text-muted-foreground">{releasePickerCopy.previewWarning}</span>
          </span>
        </label>
      ) : null}
      {previewError ? <p role="alert" className="text-sm text-warning">{previewError}</p> : null}
    </div>
  );
}
