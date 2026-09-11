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
  destructive: string;
  warning: string;
  success: string;
}

export type ThemeId = "classic" | "operations-canvas" | "command-deck" | "topology-atlas";
export type ThemeMode = "light" | "dark" | "system";
export type ThemeAccent = "teal" | "blue" | "violet" | "amber" | "rose";
export type ResolvedThemeMode = Exclude<ThemeMode, "system">;

export interface ThemeDefinition {
  id: ThemeId;
  name: string;
  description: string;
  version: number;
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
  themeId: "classic",
  mode: "light",
  accent: "teal"
};

const classicLight: ThemePalette = {
  background: "210 33% 98%",
  foreground: "222 32% 12%",
  surface: "0 0% 100%",
  muted: "210 24% 94%",
  mutedForeground: "218 13% 43%",
  border: "214 24% 88%",
  primary: "170 78% 33%",
  primaryForeground: "0 0% 100%",
  destructive: "0 72% 45%",
  warning: "36 90% 42%",
  success: "160 64% 34%"
};

const classicDark: ThemePalette = {
  background: "222 32% 7%",
  foreground: "210 30% 96%",
  surface: "222 28% 10%",
  muted: "222 22% 14%",
  mutedForeground: "215 16% 62%",
  border: "220 18% 18%",
  primary: "168 78% 52%",
  primaryForeground: "222 40% 8%",
  destructive: "0 72% 55%",
  warning: "38 92% 60%",
  success: "168 78% 52%"
};

const operationsCanvasLight: ThemePalette = {
  background: "50 25% 95%",
  foreground: "180 18% 14%",
  surface: "48 100% 99%",
  muted: "51 18% 92%",
  mutedForeground: "176 6% 40%",
  border: "100 9% 87%",
  primary: "175 88% 26%",
  primaryForeground: "48 100% 99%",
  destructive: "5 48% 48%",
  warning: "33 82% 40%",
  success: "176 86% 19%"
};

const operationsCanvasDark: ThemePalette = {
  background: "180 18% 8%",
  foreground: "50 25% 95%",
  surface: "180 18% 11%",
  muted: "180 15% 16%",
  mutedForeground: "176 12% 66%",
  border: "180 12% 24%",
  primary: "175 73% 51%",
  primaryForeground: "180 18% 8%",
  destructive: "5 62% 62%",
  warning: "33 85% 66%",
  success: "156 70% 58%"
};

const commandDeckLight: ThemePalette = {
  background: "195 20% 96%",
  foreground: "195 27% 10%",
  surface: "0 0% 100%",
  muted: "192 20% 91%",
  mutedForeground: "190 15% 38%",
  border: "190 18% 80%",
  primary: "188 70% 31%",
  primaryForeground: "0 0% 100%",
  destructive: "0 68% 43%",
  warning: "34 78% 39%",
  success: "86 70% 32%"
};

const commandDeckDark: ThemePalette = {
  background: "195 27% 6%",
  foreground: "173 23% 92%",
  surface: "194 28% 9%",
  muted: "192 25% 12%",
  mutedForeground: "185 11% 56%",
  border: "189 21% 18%",
  primary: "188 79% 60%",
  primaryForeground: "195 27% 6%",
  destructive: "0 100% 75%",
  warning: "34 85% 67%",
  success: "86 85% 69%"
};

const topologyAtlasLight: ThemePalette = {
  background: "230 50% 98%",
  foreground: "233 61% 10%",
  surface: "230 45% 100%",
  muted: "230 35% 93%",
  mutedForeground: "230 26% 40%",
  border: "230 32% 82%",
  primary: "255 65% 45%",
  primaryForeground: "0 0% 100%",
  destructive: "0 72% 45%",
  warning: "39 75% 40%",
  success: "156 60% 32%"
};

const topologyAtlasDark: ThemePalette = {
  background: "233 61% 8%",
  foreground: "230 100% 98%",
  surface: "230 55% 15%",
  muted: "230 53% 18%",
  mutedForeground: "229 36% 67%",
  border: "230 32% 42%",
  primary: "255 100% 81%",
  primaryForeground: "233 61% 8%",
  destructive: "0 100% 78%",
  warning: "39 82% 71%",
  success: "156 83% 71%"
};

export const themes: ThemeDefinition[] = [
  {
    id: "classic",
    name: "Classic",
    description: "The familiar Elsa Control console with a calm teal accent.",
    version: 1,
    palettes: { light: classicLight, dark: classicDark },
    radius: "0.5rem",
    fontDisplay: '"Space Grotesk", Inter, ui-sans-serif, system-ui, sans-serif',
    fontBody: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontMono: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace'
  },
  {
    id: "operations-canvas",
    name: "Operations Canvas",
    description: "A cool, spacious canvas for watching systems and deployment flow.",
    version: 1,
    palettes: { light: operationsCanvasLight, dark: operationsCanvasDark },
    radius: "0.75rem",
    fontDisplay: 'Georgia, "Times New Roman", serif',
    fontBody: 'Inter, ui-sans-serif, system-ui, sans-serif',
    fontMono: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace'
  },
  {
    id: "command-deck",
    name: "Command Deck",
    description: "High-signal amber and charcoal treatment for decisive operations.",
    version: 1,
    palettes: { light: commandDeckLight, dark: commandDeckDark },
    radius: "0.5rem",
    fontDisplay: '"Space Grotesk", Inter, ui-sans-serif, system-ui, sans-serif',
    fontBody: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontMono: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, monospace'
  },
  {
    id: "topology-atlas",
    name: "Topology Atlas",
    description: "A layered violet palette for navigating complex runtime topology.",
    version: 1,
    palettes: { light: topologyAtlasLight, dark: topologyAtlasDark },
    radius: "0.6875rem",
    fontDisplay: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontBody: 'Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif',
    fontMono: '"SFMono-Regular", Consolas, "Liberation Mono", monospace'
  }
];

export function getTheme(themeId: ThemeId): ThemeDefinition {
  return themes.find((theme) => theme.id === themeId) ?? themes[0];
}
