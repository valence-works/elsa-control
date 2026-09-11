import { useEffect, useRef } from "react";
import type { CSSProperties } from "react";
import { Check, X } from "lucide-react";
import { themes } from "@/lib/theme/themes";
import type { ThemeAccent, ThemeDefinition, ThemeMode } from "@/lib/theme/themes";
import { useTheme } from "@/lib/theme/ThemeProvider";

export function AppearanceDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const { preferences, resolvedMode, setThemeId, setMode, setAccent } = useTheme();

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return (
    <dialog
      ref={dialogRef}
      className="appearance-dialog rounded-ui border border-border bg-surface p-0 text-foreground"
      aria-labelledby="appearance-title"
      onClose={onClose}
      onCancel={onClose}
    >
      <header className="flex items-center justify-between gap-4 border-b border-border px-5 py-4">
        <h2 id="appearance-title" className="font-display text-lg font-semibold">Appearance</h2>
        <button type="button" aria-label="Close appearance" onClick={onClose} className="rounded-ui p-2 text-muted-foreground hover:bg-muted">
          <X aria-hidden className="h-4 w-4" />
        </button>
      </header>
      {open ? (
        <div className="space-y-6 p-5">
          <fieldset>
            <legend className="mb-3 text-sm font-medium">Theme</legend>
            <div className="grid grid-cols-2 gap-3">
              {themes.map((theme) => (
                <label key={theme.id} title={`${theme.name} · v${theme.version}`} className="relative min-w-0 cursor-pointer">
                  <input
                    type="radio" name="console-theme" value={theme.id}
                    checked={preferences.themeId === theme.id}
                    onChange={() => setThemeId(theme.id)}
                    className="peer sr-only"
                  />
                  <span className="block h-full overflow-hidden rounded-ui border border-border peer-checked:border-primary peer-checked:ring-1 peer-checked:ring-primary peer-focus-visible:outline peer-focus-visible:outline-2 peer-focus-visible:outline-offset-4 peer-focus-visible:outline-primary">
                    <ThemePreview theme={theme} mode={resolvedMode} />
                    <span className="flex items-center justify-between gap-1 px-3 py-2.5 text-sm font-medium">
                      {theme.name}
                      {preferences.themeId === theme.id ? <Check aria-hidden className="h-4 w-4 shrink-0 text-primary" /> : null}
                    </span>
                  </span>
                </label>
              ))}
            </div>
          </fieldset>

          <fieldset>
            <legend className="mb-2 text-sm font-medium">Color mode</legend>
            <div className="flex rounded-ui border border-border p-1">
              {([['light', 'Light'], ['dark', 'Dark'], ['system', 'System']] satisfies [ThemeMode, string][]).map(([mode, label]) => (
                <label key={mode} className="min-w-0 flex-1 cursor-pointer">
                  <input type="radio" name="console-color-mode" className="peer sr-only" value={mode} checked={preferences.mode === mode} onChange={() => setMode(mode)} />
                  <span className="block rounded-ui px-2 py-2 text-center text-sm text-muted-foreground peer-checked:bg-muted peer-checked:text-foreground peer-focus-visible:outline peer-focus-visible:outline-2 peer-focus-visible:outline-primary">{label}</span>
                </label>
              ))}
            </div>
          </fieldset>

          {preferences.themeId === 'classic' ? (
            <label className="flex items-center justify-between gap-3 text-sm font-medium">
              Accent
              <select aria-label="Theme accent" className="rounded-ui border border-border bg-background px-3 py-2 text-sm" value={preferences.accent} onChange={(event) => setAccent(event.target.value as ThemeAccent)}>
                <option value="teal">Teal</option><option value="blue">Blue</option><option value="violet">Violet</option><option value="amber">Amber</option><option value="rose">Rose</option>
              </select>
            </label>
          ) : null}
        </div>
      ) : null}
    </dialog>
  );
}

function ThemePreview({ theme, mode }: { theme: ThemeDefinition; mode: "light" | "dark" }) {
  const palette = theme.palettes[mode];
  const style = {
    '--preview-background': `hsl(${palette.background})`,
    '--preview-surface': `hsl(${palette.surface})`,
    '--preview-ink': `hsl(${palette.foreground})`,
    '--preview-muted': `hsl(${palette.muted})`,
    '--preview-accent': `hsl(${palette.primary})`,
    borderRadius: theme.radius,
    fontFamily: theme.fontDisplay
  } as CSSProperties;
  return (
    <span aria-hidden="true" className="theme-preview" style={style}>
      <span className="theme-preview-rail"><i /><i /><i /></span>
      <span className="theme-preview-page"><b>Engines</b><span className="theme-preview-line" /><span className="theme-preview-row"><i /><span /></span><span className="theme-preview-row"><i /><span /></span></span>
    </span>
  );
}
