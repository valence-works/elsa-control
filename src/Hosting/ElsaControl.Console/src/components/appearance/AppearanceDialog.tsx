import { useEffect, useRef } from "react";
import type { CSSProperties } from "react";
import { Check, Monitor, Moon, Sun, X } from "lucide-react";
import { accentDefinitions, themes } from "@/lib/theme/themes";
import type { ThemeMode } from "@/lib/theme/themes";
import { useTheme } from "@/lib/theme/ThemeProvider";

const modes = [
  { id: "light", name: "Light", icon: Sun },
  { id: "dark", name: "Dark", icon: Moon },
  { id: "system", name: "System", icon: Monitor }
] satisfies Array<{ id: ThemeMode; name: string; icon: typeof Sun }>;

export function AppearanceDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const dialogRef = useRef<HTMLDialogElement>(null);
  const { preferences, theme, resolvedMode, setThemeId, setMode, setAccent } = useTheme();
  const accent = accentDefinitions.find(item => item.id === preferences.accent) ?? accentDefinitions[0];
  const palette = theme.palettes[resolvedMode];

  useEffect(() => {
    const dialog = dialogRef.current;
    if (!dialog) return;
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
  }, [open]);

  return <dialog ref={dialogRef} className="appearance-dialog" aria-labelledby="appearance-title" onClose={onClose} onCancel={onClose}>
    <header className="console-dialog-heading"><h2 id="appearance-title">Appearance</h2><button type="button" aria-label="Close appearance" onClick={onClose} className="console-icon-button"><X aria-hidden size={18} /></button></header>
    {open && <div className="appearance-options">
      <div className="appearance-preview" style={{
        '--preview-background': `hsl(${palette.background})`,
        '--preview-surface': `hsl(${palette.surface})`,
        '--preview-ink': `hsl(${palette.foreground})`,
        '--preview-muted': `hsl(${palette.border})`,
        '--preview-accent': `hsl(${accent.palettes[resolvedMode].primary})`,
        '--preview-band': `hsl(${palette.band})`
      } as CSSProperties}>
        <div className="appearance-preview-canvas" aria-hidden="true"><div className="appearance-preview-band"><i /><i /><i /></div><div className="appearance-preview-body"><b>Workspace.</b><div className="appearance-preview-action" /><div className="appearance-preview-inventory"><i /><i /><i /></div><div className="appearance-preview-inspector" /></div></div>
        <div className="appearance-preview-caption"><span>{theme.name}</span><span>{accent.name}</span></div>
      </div>
      {themes.length > 1 && <fieldset><legend>Style</legend><div className="appearance-style-options">{themes.map(item => <label key={item.id}><input type="radio" name="console-style" value={item.id} checked={preferences.themeId === item.id} onChange={() => setThemeId(item.id)} />{item.name}</label>)}</div></fieldset>}
      <fieldset><legend>Color mode</legend><div className="appearance-modes">{modes.map(mode => <label key={mode.id}><input type="radio" name="console-color-mode" className="peer sr-only" value={mode.id} checked={preferences.mode === mode.id} onChange={() => setMode(mode.id)} /><span><mode.icon aria-hidden size={15} />{mode.name}</span></label>)}</div></fieldset>
      <fieldset><legend>Accent</legend><div className="appearance-accents">{accentDefinitions.map(item => <label key={item.id}><input type="radio" name="console-accent" className="peer sr-only" value={item.id} checked={preferences.accent === item.id} onChange={() => setAccent(item.id)} /><span className="appearance-accent-option"><span className="appearance-swatch" style={{ background: `hsl(${item.palettes[resolvedMode].primary})`, color: `hsl(${item.palettes[resolvedMode].primaryForeground})` }}>{preferences.accent === item.id && <Check aria-hidden size={15} />}</span>{item.name}</span></label>)}</div></fieldset>
    </div>}
  </dialog>;
}
