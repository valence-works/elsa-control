/**
 * The semantic tokens consumed by the console stylesheet. Values are HSL
 * channels (without the `hsl(...)` wrapper) so Tailwind can keep using
 * `hsl(var(--token))` for the same tokens.
 */
export interface ThemePalette {
  background: string;
  foreground: string;
  surface: string;
  muted: string;
  mutedForeground: string;
  border: string;
  primary: string;
  primaryForeground: string;
  primaryText: string;
  band: string;
  bandForeground: string;
  bandMuted: string;
  bandBorder: string;
  destructive: string;
  warning: string;
  success: string;
}

export type ThemeId = "aperture";
/** Dim is a softer, blue-slate dark mode; System follows the OS light/dark preference. */
export type ThemeMode = "light" | "dim" | "dark" | "system";
export type ThemeAccent = "cobalt" | "lime" | "glacier" | "iris" | "ember";
export type ResolvedThemeMode = Exclude<ThemeMode, "system">;

export interface ThemeAccentPalette {
  primary: string;
  primaryForeground: string;
  primaryText: string;
}

export interface ThemeAccentDefinition {
  id: ThemeAccent;
  name: string;
  palettes: Record<ResolvedThemeMode, ThemeAccentPalette>;
}

/**
 * Accent metadata is shared by the appearance picker and ThemeProvider. The
 * primary colour stays soft enough to work as a surface fill; primaryText is
 * the mode-aware, darker/lighter counterpart for links and labels.
 */
export const accentDefinitions: readonly ThemeAccentDefinition[] = [
  {
    id: "cobalt",
    name: "Cobalt",
    palettes: {
      light: { primary: "226 81% 55%", primaryForeground: "0 0% 100%", primaryText: "226 70% 45%" },
      dim: { primary: "225 85% 68%", primaryForeground: "222 47% 11%", primaryText: "225 90% 76%" },
      dark: { primary: "225 90% 66%", primaryForeground: "222 47% 9%", primaryText: "225 95% 75%" }
    }
  },
  {
    id: "glacier",
    name: "Glacier",
    palettes: {
      light: { primary: "172 78% 27%", primaryForeground: "0 0% 100%", primaryText: "172 78% 24%" },
      dim: { primary: "170 50% 54%", primaryForeground: "172 60% 10%", primaryText: "170 55% 64%" },
      dark: { primary: "170 55% 55%", primaryForeground: "172 60% 9%", primaryText: "170 60% 65%" }
    }
  },
  {
    id: "iris",
    name: "Iris",
    palettes: {
      light: { primary: "258 67% 55%", primaryForeground: "0 0% 100%", primaryText: "258 60% 48%" },
      dim: { primary: "256 80% 76%", primaryForeground: "258 40% 14%", primaryText: "256 85% 82%" },
      dark: { primary: "256 80% 74%", primaryForeground: "258 40% 12%", primaryText: "256 85% 80%" }
    }
  },
  {
    id: "ember",
    name: "Ember",
    palettes: {
      light: { primary: "17 88% 40%", primaryForeground: "0 0% 100%", primaryText: "17 88% 37%" },
      dim: { primary: "22 80% 64%", primaryForeground: "20 50% 12%", primaryText: "24 85% 72%" },
      dark: { primary: "22 80% 62%", primaryForeground: "20 50% 10%", primaryText: "24 85% 70%" }
    }
  },
  {
    id: "lime",
    name: "Lime",
    palettes: {
      light: { primary: "72 72% 72%", primaryForeground: "79 46% 16%", primaryText: "79 46% 22%" },
      dim: { primary: "72 72% 72%", primaryForeground: "79 46% 16%", primaryText: "72 72% 72%" },
      dark: { primary: "72 72% 72%", primaryForeground: "79 46% 16%", primaryText: "72 72% 72%" }
    }
  }
];

/** Alias kept short for consumers such as the appearance picker. */
export const accents = accentDefinitions;

export interface ThemeDefinition {
  id: ThemeId;
  name: string;
  description: string;
  version: number;
  layout: "topbar";
  backgroundPattern: "none" | "grid";
  palettes: Record<ResolvedThemeMode, ThemePalette>;
  radius: string;
  fontDisplay: string;
  fontBody: string;
  fontMono: string;
}

export interface ThemePreferences {
  themeId: ThemeId;
  mode: ThemeMode;
  accent: ThemeAccent;
}

export const DEFAULT_THEME_PREFERENCES: ThemePreferences = {
  themeId: "aperture",
  mode: "dim",
  accent: "cobalt"
};

// Lanes: cool grey canvas, white cards, cobalt accent. Dim and Dark share its blue-slate hue.
const lanesLight: ThemePalette = {
  background: "216 26% 95%",
  foreground: "217 46% 10%",
  surface: "0 0% 100%",
  muted: "216 25% 92%",
  mutedForeground: "213 16% 38%",
  border: "215 25% 89%",
  primary: "226 81% 55%",
  primaryForeground: "0 0% 100%",
  primaryText: "226 70% 45%",
  band: "217 46% 10%",
  bandForeground: "214 30% 94%",
  bandMuted: "215 16% 70%",
  bandBorder: "217 22% 24%",
  destructive: "3 65% 45%",
  warning: "34 100% 27%",
  success: "145 61% 28%"
};

const lanesDim: ThemePalette = {
  background: "216 20% 16%",
  foreground: "213 25% 88%",
  surface: "216 18% 20%",
  muted: "216 16% 25%",
  mutedForeground: "214 13% 68%",
  border: "215 14% 30%",
  primary: "225 85% 68%",
  primaryForeground: "222 47% 11%",
  primaryText: "225 90% 76%",
  band: "216 22% 12%",
  bandForeground: "213 25% 90%",
  bandMuted: "214 13% 68%",
  bandBorder: "215 14% 30%",
  destructive: "4 80% 70%",
  warning: "38 75% 66%",
  success: "145 45% 62%"
};

const lanesDark: ThemePalette = {
  background: "222 28% 7%",
  foreground: "214 32% 93%",
  surface: "220 24% 10%",
  muted: "219 20% 15%",
  mutedForeground: "215 14% 66%",
  border: "218 17% 20%",
  primary: "225 90% 66%",
  primaryForeground: "222 47% 9%",
  primaryText: "225 95% 75%",
  band: "222 30% 5%",
  bandForeground: "214 32% 93%",
  bandMuted: "215 14% 66%",
  bandBorder: "218 17% 20%",
  destructive: "4 85% 70%",
  warning: "40 85% 62%",
  success: "142 55% 58%"
};

const sansFont = '"IBM Plex Sans", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
const monoFont = '"IBM Plex Mono", "SFMono-Regular", Consolas, monospace';

export const themes: ThemeDefinition[] = [
  {
    id: "aperture",
    name: "Lanes",
    description: "A cool, light workspace with cobalt accents; Dim and Dark keep the same structure.",
    version: 1,
    layout: "topbar",
    backgroundPattern: "none",
    palettes: { light: lanesLight, dim: lanesDim, dark: lanesDark },
    radius: "8px",
    fontDisplay: sansFont,
    fontBody: sansFont,
    fontMono: monoFont
  }
];

export function getTheme(themeId: ThemeId): ThemeDefinition {
  return themes.find((theme) => theme.id === themeId) ?? themes[0];
}
