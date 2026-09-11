import { createContext, useCallback, useContext, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import {
  DEFAULT_THEME_PREFERENCES,
  getTheme,
  themes,
  type ResolvedThemeMode,
  type ThemeAccent,
  type ThemeDefinition,
  type ThemeId,
  type ThemeMode,
  type ThemePalette,
  type ThemePreferences
} from "./themes";

export { DEFAULT_THEME_PREFERENCES, themes } from "./themes";
export type {
  ResolvedThemeMode,
  ThemeAccent,
  ThemeDefinition,
  ThemeId,
  ThemeMode,
  ThemePalette,
  ThemePreferences
} from "./themes";

export const appearanceStorageKey = "elsa-control-console-appearance";
export const legacyThemeStorageKey = "elsa-control-console-theme";
export const legacyAccentStorageKey = "elsa-control-console-theme-accent";
export const appearanceStorageVersion = 1;

type ThemeContextValue = {
  preferences: ThemePreferences;
  theme: ThemeDefinition;
  resolvedMode: ResolvedThemeMode;
  setThemeId: (themeId: ThemeId) => void;
  setMode: (mode: ThemeMode) => void;
  setAccent: (accent: ThemeAccent) => void;
};

type StoredPreferences = {
  preferences: ThemePreferences;
  shouldPersist: boolean;
};

const semanticVariables: Record<keyof ThemePalette, string> = {
  background: "--background",
  foreground: "--foreground",
  surface: "--surface",
  muted: "--muted",
  mutedForeground: "--muted-foreground",
  border: "--border",
  primary: "--primary",
  primaryForeground: "--primary-foreground",
  destructive: "--destructive",
  warning: "--warning",
  success: "--success"
};

const accentPrimary: Record<ThemeAccent, { light: { primary: string; primaryForeground: string }; dark: { primary: string; primaryForeground: string } }> = {
  teal: {
    light: { primary: "170 78% 33%", primaryForeground: "0 0% 100%" },
    dark: { primary: "168 78% 52%", primaryForeground: "222 40% 8%" }
  },
  blue: {
    light: { primary: "211 85% 44%", primaryForeground: "0 0% 100%" },
    dark: { primary: "207 90% 64%", primaryForeground: "222 40% 8%" }
  },
  violet: {
    light: { primary: "262 72% 52%", primaryForeground: "0 0% 100%" },
    dark: { primary: "262 84% 70%", primaryForeground: "222 40% 8%" }
  },
  amber: {
    light: { primary: "38 86% 42%", primaryForeground: "222 32% 10%" },
    dark: { primary: "41 92% 62%", primaryForeground: "222 40% 8%" }
  },
  rose: {
    light: { primary: "347 72% 46%", primaryForeground: "0 0% 100%" },
    dark: { primary: "347 86% 68%", primaryForeground: "222 40% 8%" }
  }
};

const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

export interface ThemeProviderProps {
  children: ReactNode;
}

/**
 * Reads the persisted preference, applies it before the first React paint,
 * and returns the normalized value for callers that bootstrap the app.
 */
export function initializeTheme(): ThemePreferences {
  const stored = readStoredPreferences();
  applyThemePreferences(stored.preferences);
  if (stored.shouldPersist) {
    persistPreferences(stored.preferences);
  }
  return stored.preferences;
}

/**
 * Applies the complete theme surface to the document root. The optional root
 * is useful for tests and for hosts that render the console into a document
 * other than the global one.
 */
export function applyThemePreferences(
  preferences: ThemePreferences,
  root: HTMLElement | undefined = getDocument()?.documentElement
): ResolvedThemeMode {
  if (!root) {
    return resolveMode(preferences.mode);
  }

  const normalized = normalizePreferences(preferences);
  const theme = getTheme(normalized.themeId);
  const resolvedMode = resolveMode(normalized.mode, root.ownerDocument.defaultView);
  const palette = getPalette(theme, resolvedMode, normalized.accent);

  for (const [name, variable] of Object.entries(semanticVariables) as Array<[keyof ThemePalette, string]>) {
    root.style.setProperty(variable, palette[name]);
  }
  root.style.setProperty("--radius-ui", theme.radius);
  root.style.setProperty("--font-display", theme.fontDisplay);
  root.style.setProperty("--font-body", theme.fontBody);
  root.style.setProperty("--font-mono", theme.fontMono);

  root.dataset.consoleTheme = theme.id;
  root.dataset.themeAccent = normalized.accent;
  root.classList.toggle("dark", resolvedMode === "dark");
  root.style.colorScheme = resolvedMode;

  return resolvedMode;
}

export function useTheme(): ThemeContextValue {
  const context = useContext(ThemeContext);
  if (!context) {
    throw new Error("useTheme must be used within a ThemeProvider");
  }
  return context;
}

export function ThemeProvider({ children }: ThemeProviderProps) {
  const initial = useState<StoredPreferences>(() => readStoredPreferences())[0];
  const [preferences, setPreferences] = useState<ThemePreferences>(initial.preferences);
  const preferencesRef = useRef(preferences);
  const shouldMigrateRef = useRef(initial.shouldPersist);
  const [resolvedMode, setResolvedMode] = useState<ResolvedThemeMode>(() => resolveMode(preferences.mode));

  const applyCurrentPreferences = useCallback((next: ThemePreferences) => {
    preferencesRef.current = next;
    const nextResolvedMode = applyThemePreferences(next);
    setResolvedMode((current) => (current === nextResolvedMode ? current : nextResolvedMode));
  }, []);

  const updatePreferences = useCallback(
    (update: (current: ThemePreferences) => ThemePreferences) => {
      const next = normalizePreferences(update(preferencesRef.current));
      if (samePreferences(preferencesRef.current, next)) {
        return;
      }
      applyCurrentPreferences(next);
      setPreferences(next);
      persistPreferences(next);
    },
    [applyCurrentPreferences]
  );

  useLayoutEffect(() => {
    applyCurrentPreferences(preferences);
  }, [applyCurrentPreferences, preferences]);

  useLayoutEffect(() => {
    if (shouldMigrateRef.current) {
      shouldMigrateRef.current = false;
      persistPreferences(preferences);
    }
  }, [preferences]);

  useLayoutEffect(() => {
    const media = getColorSchemeMediaQuery();
    if (!media) {
      return;
    }

    const updateSystemMode = () => {
      const nextResolvedMode = resolveMode(preferencesRef.current.mode);
      setResolvedMode((current) => (current === nextResolvedMode ? current : nextResolvedMode));
      if (preferencesRef.current.mode === "system") {
        applyThemePreferences(preferencesRef.current);
      }
    };

    if (media.addEventListener) {
      media.addEventListener("change", updateSystemMode);
    } else {
      media.addListener?.(updateSystemMode);
    }
    return () => {
      if (media.removeEventListener) {
        media.removeEventListener("change", updateSystemMode);
      } else {
        media.removeListener?.(updateSystemMode);
      }
    };
  }, []);

  useLayoutEffect(() => {
    const onStorage = (event: StorageEvent) => {
      const storage = getStorage();
      if (event.storageArea && storage && event.storageArea !== storage) {
        return;
      }

      const current = preferencesRef.current;
      let next: ThemePreferences | undefined;

      if (event.key === appearanceStorageKey) {
        const parsed = parseVersionedPreferences(event.newValue);
        // Ignore malformed remote values. A malformed value must not make one
        // tab erase a valid choice made in another tab.
        if (!parsed) {
          return;
        }
        next = parsed;
      } else if (event.key === legacyThemeStorageKey || event.key === legacyAccentStorageKey) {
        // New-format preferences are authoritative. Legacy writes happen
        // after the new record for compatibility and must not reset a custom
        // theme in another tab back to Classic.
        if (parseVersionedPreferences(readStorageValue(appearanceStorageKey))) {
          return;
        }
        const legacyTheme = event.key === legacyThemeStorageKey ? event.newValue : readStorageValue(legacyThemeStorageKey);
        const legacyAccent = event.key === legacyAccentStorageKey ? event.newValue : readStorageValue(legacyAccentStorageKey);
        next = {
          themeId: "classic",
          mode: legacyTheme === "dark" ? "dark" : "light",
          accent: parseAccent(legacyAccent)
        };
      }

      if (!next || samePreferences(current, next)) {
        return;
      }
      applyCurrentPreferences(next);
      setPreferences(next);
    };

    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, [applyCurrentPreferences]);

  const contextValue: ThemeContextValue = {
    preferences,
    theme: getTheme(preferences.themeId),
    resolvedMode,
    setThemeId: (themeId) => updatePreferences((current) => ({ ...current, themeId })),
    setMode: (mode) => updatePreferences((current) => ({ ...current, mode })),
    setAccent: (accent) => updatePreferences((current) => ({ ...current, accent }))
  };

  return <ThemeContext.Provider value={contextValue}>{children}</ThemeContext.Provider>;
}

function getPalette(theme: ThemeDefinition, mode: ResolvedThemeMode, accent: ThemeAccent): ThemePalette {
  const palette = theme.palettes[mode];
  if (theme.id !== "classic") {
    return palette;
  }

  return {
    ...palette,
    ...accentPrimary[accent][mode]
  };
}

function normalizePreferences(value: ThemePreferences): ThemePreferences {
  return {
    themeId: isThemeId(value?.themeId) ? value.themeId : DEFAULT_THEME_PREFERENCES.themeId,
    mode: isThemeMode(value?.mode) ? value.mode : DEFAULT_THEME_PREFERENCES.mode,
    accent: isThemeAccent(value?.accent) ? value.accent : DEFAULT_THEME_PREFERENCES.accent
  };
}

function readStoredPreferences(): StoredPreferences {
  const storage = getStorage();
  if (!storage) {
    return { preferences: DEFAULT_THEME_PREFERENCES, shouldPersist: false };
  }

  const versioned = parseVersionedPreferences(readStorageValue(appearanceStorageKey, storage));
  if (versioned) {
    return { preferences: versioned, shouldPersist: false };
  }

  const legacyTheme = readStorageValue(legacyThemeStorageKey, storage);
  const legacyAccent = readStorageValue(legacyAccentStorageKey, storage);
  return {
    preferences: {
      themeId: "classic",
      mode: legacyTheme === "dark" ? "dark" : "light",
      accent: parseAccent(legacyAccent)
    },
    shouldPersist: true
  };
}

function parseVersionedPreferences(raw: string | null): ThemePreferences | undefined {
  if (!raw) {
    return undefined;
  }

  try {
    const value: unknown = JSON.parse(raw);
    if (!isRecord(value) || value.version !== appearanceStorageVersion) {
      return undefined;
    }

    if (!isThemeId(value.themeId) || !isThemeMode(value.mode) || !isThemeAccent(value.accent)) {
      return undefined;
    }
    return {
      themeId: value.themeId,
      mode: value.mode,
      accent: value.accent
    };
  } catch {
    return undefined;
  }
}

function persistPreferences(preferences: ThemePreferences) {
  const storage = getStorage();
  if (!storage) {
    return;
  }

  const normalized = normalizePreferences(preferences);
  const versioned = JSON.stringify({
    version: appearanceStorageVersion,
    themeId: normalized.themeId,
    mode: normalized.mode,
    accent: normalized.accent
  });
  const resolvedMode = resolveMode(normalized.mode);
  try {
    storage.setItem(appearanceStorageKey, versioned);
    // Keep the old keys readable for older console bundles. `system` has no
    // legacy representation, so its current resolved value is used there.
    storage.setItem(legacyThemeStorageKey, resolvedMode);
    storage.setItem(legacyAccentStorageKey, normalized.accent);
  } catch {
    // Browser storage can be unavailable in private browsing or embedded
    // documents. Theme selection remains an in-memory feature in that case.
  }
}

function resolveMode(mode: ThemeMode, view: Window | null | undefined = getDocument()?.defaultView): ResolvedThemeMode {
  if (mode !== "system") {
    return mode;
  }
  try {
    return view?.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  } catch {
    return "light";
  }
}

function getColorSchemeMediaQuery(): MediaQueryList | undefined {
  try {
    return typeof window !== "undefined" && typeof window.matchMedia === "function"
      ? window.matchMedia("(prefers-color-scheme: dark)")
      : undefined;
  } catch {
    return undefined;
  }
}

function getDocument(): Document | undefined {
  return typeof document === "undefined" ? undefined : document;
}

function getStorage(): Storage | undefined {
  try {
    return typeof window !== "undefined" && window.localStorage ? window.localStorage : undefined;
  } catch {
    return undefined;
  }
}

function readStorageValue(key: string, storage: Storage | undefined = getStorage()): string | null {
  if (!storage) {
    return null;
  }
  try {
    return storage.getItem(key);
  } catch {
    return null;
  }
}

function parseAccent(value: string | null): ThemeAccent {
  return isThemeAccent(value) ? value : DEFAULT_THEME_PREFERENCES.accent;
}

function isThemeId(value: unknown): value is ThemeId {
  return themes.some((theme) => theme.id === value);
}

function isThemeMode(value: unknown): value is ThemeMode {
  return value === "light" || value === "dark" || value === "system";
}

function isThemeAccent(value: unknown): value is ThemeAccent {
  return value === "teal" || value === "blue" || value === "violet" || value === "amber" || value === "rose";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function samePreferences(left: ThemePreferences, right: ThemePreferences): boolean {
  return left.themeId === right.themeId && left.mode === right.mode && left.accent === right.accent;
}
