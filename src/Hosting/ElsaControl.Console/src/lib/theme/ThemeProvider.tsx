import { createContext, useCallback, useContext, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { DEFAULT_THEME_PREFERENCES, getTheme, type ResolvedThemeMode, type ThemeAccent, type ThemeDefinition, type ThemeId, type ThemeMode, type ThemePreferences } from "./themes";
import { appearanceStorageKey, legacyThemeStorageKey, legacyAccentStorageKey, applyThemePreferences, normalizePreferences, readStoredPreferences, parseVersionedPreferences, persistPreferences, resolveMode, getColorSchemeMediaQuery, getStorage, readStorageValue, parseAccent, samePreferences, type StoredPreferences } from "./themeRuntime";
export { initializeTheme, applyThemePreferences, appearanceStorageKey, legacyThemeStorageKey, legacyAccentStorageKey, appearanceStorageVersion } from "./themeRuntime";

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

type ThemeContextValue = {
  preferences: ThemePreferences;
  theme: ThemeDefinition;
  resolvedMode: ResolvedThemeMode;
  setThemeId: (themeId: ThemeId) => void;
  setMode: (mode: ThemeMode) => void;
  setAccent: (accent: ThemeAccent) => void;
};

const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);

export interface ThemeProviderProps {
  children: ReactNode;
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
