import { createContext, useCallback, useContext, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import {
  DEFAULT_THEME_PREFERENCES,
  accentDefinitions,
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
export { accentDefinitions, accents } from "./themes";
export type {
  ResolvedThemeMode,
  ThemeAccentDefinition,
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
export const appearanceStorageVersion = 2;
const previousAppearanceStorageVersion = 1;

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
  primaryText: "--primary-text",
  band: "--band",
  bandForeground: "--band-foreground",
  bandMuted: "--band-muted",
  bandBorder: "--band-border",
  destructive: "--destructive",
  warning: "--warning",
  success: "--success"
};

const legacyThemeIds = new Set(["classic", "operations-canvas", "command-deck", "topology-atlas"]);
const legacyAccentMap: Record<string, ThemeAccent> = {
  teal: "lime",
  blue: "glacier",
  violet: "iris",
  amber: "ember",
  rose: "ember",
  lime: "lime",
  glacier: "glacier",
  iris: "iris",
  ember: "ember"
};
const legacyAccentByAccent: Record<ThemeAccent, string> = {
  lime: "teal",
  glacier: "blue",
  iris: "violet",
  ember: "amber"
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
  root.dataset.consoleLayout = theme.layout;
  root.dataset.consolePattern = theme.backgroundPattern;
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
        // choice in another tab back to the Aperture defaults.
        if (parseVersionedPreferences(readStorageValue(appearanceStorageKey))) {
          return;
        }
        const legacyTheme = event.key === legacyThemeStorageKey ? event.newValue : readStorageValue(legacyThemeStorageKey);
        const legacyAccent = event.key === legacyAccentStorageKey ? event.newValue : readStorageValue(legacyAccentStorageKey);
        next = {
          themeId: "aperture",
          mode: legacyTheme === "dark" || legacyTheme === "light" ? legacyTheme : DEFAULT_THEME_PREFERENCES.mode,
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
  const accentPalette = accentDefinitions.find((definition) => definition.id === accent)?.palettes[mode];
  return {
    ...palette,
    ...(accentPalette ?? {})
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

  const versioned = parseStoredPreferences(readStorageValue(appearanceStorageKey, storage));
  if (versioned) {
    return { preferences: versioned.preferences, shouldPersist: versioned.version !== appearanceStorageVersion };
  }

  const legacyTheme = readStorageValue(legacyThemeStorageKey, storage);
  const legacyAccent = readStorageValue(legacyAccentStorageKey, storage);
  return {
    preferences: {
      themeId: "aperture",
      mode: legacyTheme === "dark" || legacyTheme === "light" ? legacyTheme : DEFAULT_THEME_PREFERENCES.mode,
      accent: parseAccent(legacyAccent)
    },
    shouldPersist: true
  };
}

function parseVersionedPreferences(raw: string | null): ThemePreferences | undefined {
  return parseStoredPreferences(raw)?.preferences;
}

function parseStoredPreferences(raw: string | null): { preferences: ThemePreferences; version: number } | undefined {
  if (!raw) {
    return undefined;
  }

  try {
    const value: unknown = JSON.parse(raw);
    if (!isRecord(value) || typeof value.version !== "number") {
      return undefined;
    }

    if (value.version === appearanceStorageVersion) {
      if (!isThemeId(value.themeId) || !isThemeMode(value.mode) || !isThemeAccent(value.accent)) {
        return undefined;
      }
      return {
        preferences: {
          themeId: value.themeId,
          mode: value.mode,
          accent: value.accent
        },
        version: value.version
      };
    }

    if (value.version !== previousAppearanceStorageVersion) {
      return undefined;
    }

    const migratedThemeId = migrateThemeId(value.themeId);
    const migratedAccent = migrateAccent(value.accent);
    if (!migratedThemeId || !isThemeMode(value.mode) || !migratedAccent) {
      return undefined;
    }

    return {
      preferences: {
        themeId: migratedThemeId,
        mode: value.mode,
        accent: migratedAccent
      },
      version: value.version
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
    storage.setItem(legacyAccentStorageKey, legacyAccentByAccent[normalized.accent]);
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
  return migrateAccent(value) ?? DEFAULT_THEME_PREFERENCES.accent;
}

function isThemeId(value: unknown): value is ThemeId {
  return themes.some((theme) => theme.id === value);
}

function isThemeMode(value: unknown): value is ThemeMode {
  return value === "light" || value === "dark" || value === "system";
}

function isThemeAccent(value: unknown): value is ThemeAccent {
  return accentDefinitions.some(accent => accent.id === value);
}

function migrateThemeId(value: unknown): ThemeId | undefined {
  if (isThemeId(value)) {
    return value;
  }
  return typeof value === "string" && legacyThemeIds.has(value) ? DEFAULT_THEME_PREFERENCES.themeId : undefined;
}

function migrateAccent(value: unknown): ThemeAccent | undefined {
  return typeof value === "string" && Object.hasOwn(legacyAccentMap, value) ? legacyAccentMap[value] : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function samePreferences(left: ThemePreferences, right: ThemePreferences): boolean {
  return left.themeId === right.themeId && left.mode === right.mode && left.accent === right.accent;
}
