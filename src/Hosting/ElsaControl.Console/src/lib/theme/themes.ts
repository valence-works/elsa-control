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
export type ThemeMode = "light" | "dark" | "system";
export type ThemeAccent = "lime" | "glacier" | "iris" | "ember";
export type ResolvedThemeMode = Exclude<ThemeMode, "system">;

export interface ThemeAccentPalette {
  primary: string;
  primaryForeground: string;
  primaryText: string;
}

export interface ThemeAccentDefinition {
  id: ThemeAccent;
  name: string;
  palettes: {
    light: ThemeAccentPalette;
    dark: ThemeAccentPalette;
  };
}

/**
 * Accent metadata is shared by the appearance picker and ThemeProvider. The
 * primary colour stays soft enough to work as a surface fill; primaryText is
 * the mode-aware, darker/lighter counterpart for links and labels.
 */
export const accentDefinitions: readonly ThemeAccentDefinition[] = [
  {
    id: "lime",
    name: "Lime",
    palettes: {
      light: { primary: "72 72% 72%", primaryForeground: "79 46% 16%", primaryText: "79 46% 16%" },
      dark: { primary: "72 72% 72%", primaryForeground: "79 46% 16%", primaryText: "72 72% 72%" }
    }
  },
  {
    id: "glacier",
    name: "Glacier",
    palettes: {
      light: { primary: "191 64% 74%", primaryForeground: "193 45% 16%", primaryText: "193 45% 16%" },
      dark: { primary: "191 64% 74%", primaryForeground: "193 45% 16%", primaryText: "191 64% 74%" }
    }
  },
  {
    id: "iris",
    name: "Iris",
    palettes: {
      light: { primary: "254 62% 82%", primaryForeground: "259 30% 20%", primaryText: "259 30% 20%" },
      dark: { primary: "254 62% 82%", primaryForeground: "259 30% 20%", primaryText: "254 62% 82%" }
    }
  },
  {
    id: "ember",
    name: "Ember",
    palettes: {
      light: { primary: "23 72% 75%", primaryForeground: "22 35% 18%", primaryText: "22 35% 18%" },
      dark: { primary: "23 72% 75%", primaryForeground: "22 35% 18%", primaryText: "23 72% 75%" }
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
  palettes: {
    light: ThemePalette;
    dark: ThemePalette;
  };
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
  mode: "dark",
  accent: "lime"
};

const apertureLight: ThemePalette = {
  background: "80 18% 97%",
  foreground: "193 11% 16%",
  surface: "0 0% 100%",
  muted: "96 15% 94%",
  mutedForeground: "185 5% 42%",
  border: "130 9% 87%",
  primary: "72 72% 72%",
  primaryForeground: "79 46% 16%",
  primaryText: "79 46% 16%",
  band: "193 12% 14%",
  bandForeground: "120 13% 94%",
  bandMuted: "160 9% 73%",
  bandBorder: "174 6% 31%",
  destructive: "5 62% 45%",
  warning: "38 55% 34%",
  success: "130 17% 35%"
};

const apertureDark: ThemePalette = {
  background: "200 10% 11%",
  foreground: "144 13% 92%",
  surface: "202 11% 15%",
  muted: "187 10% 18%",
  mutedForeground: "175 8% 69%",
  border: "191 9% 25%",
  primary: "72 72% 72%",
  primaryForeground: "79 46% 16%",
  primaryText: "72 72% 72%",
  band: "189 17% 8%",
  bandForeground: "120 13% 94%",
  bandMuted: "160 9% 73%",
  bandBorder: "174 6% 31%",
  destructive: "5 72% 68%",
  warning: "39 48% 73%",
  success: "114 23% 74%"
};

const nativeFont = '-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif';
const monoFont = '"SFMono-Regular", Consolas, monospace';

export const themes: ThemeDefinition[] = [
  {
    id: "aperture",
    name: "Aperture",
    description: "A calm charcoal workspace for finding engines and connecting fast.",
    version: 1,
    layout: "topbar",
    backgroundPattern: "none",
    palettes: { light: apertureLight, dark: apertureDark },
    radius: "4px",
    fontDisplay: nativeFont,
    fontBody: nativeFont,
    fontMono: monoFont
  }
];

export function getTheme(themeId: ThemeId): ThemeDefinition {
  return themes.find((theme) => theme.id === themeId) ?? themes[0];
}
