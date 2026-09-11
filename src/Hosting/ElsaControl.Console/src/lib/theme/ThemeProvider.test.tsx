import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  appearanceStorageKey,
  ThemeProvider,
  initializeTheme,
  legacyAccentStorageKey,
  legacyThemeStorageKey,
  useTheme
} from "./ThemeProvider";
import { themes } from "./themes";

function ThemeProbe() {
  const { preferences, resolvedMode, theme, setAccent, setMode, setThemeId } = useTheme();

  return (
    <div>
      <output data-testid="preferences">{JSON.stringify(preferences)}</output>
      <output data-testid="resolved-mode">{resolvedMode}</output>
      <output data-testid="theme-name">{theme.name}</output>
      <button onClick={() => setThemeId("operations-canvas")}>operations</button>
      <button onClick={() => setMode("system")}>system</button>
      <button onClick={() => setMode("light")}>light</button>
      <button onClick={() => setAccent("rose")}>rose</button>
    </div>
  );
}

function renderTheme() {
  return render(
    <ThemeProvider>
      <ThemeProbe />
    </ThemeProvider>
  );
}

const defaultMatchMedia = Object.getOwnPropertyDescriptor(window, "matchMedia");
const defaultLocalStorage = Object.getOwnPropertyDescriptor(window, "localStorage");

class MemoryStorage implements Storage {
  private readonly values = new Map<string, string>();

  get length() {
    return this.values.size;
  }

  clear() {
    this.values.clear();
  }

  getItem(key: string) {
    return this.values.get(key) ?? null;
  }

  key(index: number) {
    return [...this.values.keys()][index] ?? null;
  }

  removeItem(key: string) {
    this.values.delete(key);
  }

  setItem(key: string, value: string) {
    this.values.set(key, String(value));
  }
}

beforeEach(() => {
  cleanup();
  Object.defineProperty(window, "localStorage", {
    configurable: true,
    value: new MemoryStorage()
  });
  window.localStorage.clear();
  document.documentElement.removeAttribute("data-console-theme");
  document.documentElement.removeAttribute("data-theme-accent");
  document.documentElement.classList.remove("dark");
  document.documentElement.removeAttribute("style");
});

afterEach(() => {
  cleanup();
  if (defaultMatchMedia) {
    Object.defineProperty(window, "matchMedia", defaultMatchMedia);
  } else {
    Reflect.deleteProperty(window, "matchMedia");
  }
  if (defaultLocalStorage) {
    Object.defineProperty(window, "localStorage", defaultLocalStorage);
  }
  vi.restoreAllMocks();
});

describe("theme registry", () => {
  it("contains complete semantic palettes and preview swatches", () => {
    expect(themes.map((theme) => theme.id)).toEqual(["classic", "operations-canvas", "command-deck", "topology-atlas"]);
    for (const theme of themes) {
      expect(theme.version).toBeGreaterThan(0);
      expect(Object.keys(theme.palettes.light)).toEqual(Object.keys(theme.palettes.dark));
      expect(theme.palettes.light.primary).toMatch(/^\d+ \d+% \d+%$/);
      expect(theme.palettes.dark.primary).toMatch(/^\d+ \d+% \d+%$/);
    }
  });

  it("keeps normal text and primary labels readable in curated palettes", () => {
    for (const theme of themes.filter((item) => item.id !== "classic")) {
      for (const palette of [theme.palettes.light, theme.palettes.dark]) {
        expect(contrast(palette.foreground, palette.background)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.foreground, palette.surface)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.foreground, palette.muted)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.mutedForeground, palette.background)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.mutedForeground, palette.surface)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.mutedForeground, palette.muted)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.primaryForeground, palette.primary)).toBeGreaterThanOrEqual(4.5);
      }
    }
  });
});

describe("ThemeProvider", () => {
  it("migrates the legacy light/dark and accent keys into versioned preferences", () => {
    window.localStorage.setItem(legacyThemeStorageKey, "dark");
    window.localStorage.setItem(legacyAccentStorageKey, "violet");

    expect(initializeTheme()).toEqual({ themeId: "classic", mode: "dark", accent: "violet" });
    expect(JSON.parse(window.localStorage.getItem(appearanceStorageKey)!)).toEqual({
      version: 1,
      themeId: "classic",
      mode: "dark",
      accent: "violet"
    });
    expect(document.documentElement).toHaveClass("dark");
    expect(document.documentElement).toHaveAttribute("data-console-theme", "classic");
    expect(document.documentElement).toHaveAttribute("data-theme-accent", "violet");
  });

  it("loads valid versioned preferences on a later render", () => {
    window.localStorage.setItem(
      appearanceStorageKey,
      JSON.stringify({ version: 1, themeId: "topology-atlas", mode: "dark", accent: "amber" })
    );

    renderTheme();

    expect(screen.getByTestId("preferences")).toHaveTextContent("topology-atlas");
    expect(screen.getByTestId("theme-name")).toHaveTextContent("Topology Atlas");
    expect(document.documentElement).toHaveAttribute("data-console-theme", "topology-atlas");
    expect(document.documentElement.style.getPropertyValue("--primary")).toBe("255 100% 81%");
    expect(document.documentElement.style.getPropertyValue("--radius-ui")).toBe("0.6875rem");
  });

  it("falls back safely from malformed or invalid versioned input", () => {
    window.localStorage.setItem(appearanceStorageKey, "{not-json");
    window.localStorage.setItem(legacyThemeStorageKey, "not-a-mode");
    window.localStorage.setItem(legacyAccentStorageKey, "not-an-accent");

    renderTheme();

    expect(screen.getByTestId("preferences")).toHaveTextContent('{"themeId":"classic","mode":"light","accent":"teal"}');
    expect(document.documentElement).not.toHaveClass("dark");
    expect(JSON.parse(window.localStorage.getItem(appearanceStorageKey)!)).toEqual({
      version: 1,
      themeId: "classic",
      mode: "light",
      accent: "teal"
    });
  });

  it("continues with defaults when browser storage is blocked", () => {
    Object.defineProperty(window, "localStorage", {
      configurable: true,
      get: () => {
        throw new DOMException("Storage is blocked", "SecurityError");
      }
    });

    expect(() => initializeTheme()).not.toThrow();
    expect(() => renderTheme()).not.toThrow();
    expect(screen.getByTestId("preferences")).toHaveTextContent('{"themeId":"classic","mode":"light","accent":"teal"}');
    fireEvent.click(screen.getByRole("button", { name: "operations" }));
    expect(document.documentElement).toHaveAttribute("data-console-theme", "operations-canvas");
  });

  it("responds to system color scheme changes", () => {
    let matches = true;
    let listener: (() => void) | undefined;
    const media = {
      get matches() {
        return matches;
      },
      addEventListener: (_type: string, callback: () => void) => {
        listener = callback;
      },
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn()
    } as unknown as MediaQueryList;
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: vi.fn(() => media)
    });
    window.localStorage.setItem(appearanceStorageKey, JSON.stringify({ version: 1, themeId: "classic", mode: "system", accent: "teal" }));

    renderTheme();
    expect(screen.getByTestId("resolved-mode")).toHaveTextContent("dark");
    expect(document.documentElement).toHaveClass("dark");

    act(() => {
      matches = false;
      listener?.();
    });

    expect(screen.getByTestId("resolved-mode")).toHaveTextContent("light");
    expect(document.documentElement).not.toHaveClass("dark");

    fireEvent.click(screen.getByRole("button", { name: "light" }));
    matches = true;
    act(() => listener?.());
    expect(screen.getByTestId("resolved-mode")).toHaveTextContent("light");
    expect(document.documentElement).not.toHaveClass("dark");
  });

  it("applies valid cross-tab updates without writing them back", () => {
    renderTheme();
    const setItem = vi.spyOn(window.localStorage, "setItem");
    const remote = JSON.stringify({ version: 1, themeId: "command-deck", mode: "dark", accent: "rose" });

    act(() => {
      window.dispatchEvent(
        new StorageEvent("storage", {
          key: appearanceStorageKey,
          newValue: remote
        })
      );
    });

    expect(screen.getByTestId("preferences")).toHaveTextContent("command-deck");
    expect(document.documentElement).toHaveClass("dark");
    expect(setItem).not.toHaveBeenCalled();
  });

  it("keeps the canonical cross-tab theme when compatibility keys arrive afterward", () => {
    renderTheme();
    const remote = JSON.stringify({ version: 1, themeId: "command-deck", mode: "dark", accent: "rose" });
    window.localStorage.setItem(appearanceStorageKey, remote);
    const setItem = vi.spyOn(window.localStorage, "setItem");

    act(() => {
      window.dispatchEvent(
        new StorageEvent("storage", {
          key: appearanceStorageKey,
          newValue: remote
        })
      );
      window.dispatchEvent(
        new StorageEvent("storage", {
          key: legacyThemeStorageKey,
          newValue: "dark"
        })
      );
      window.dispatchEvent(
        new StorageEvent("storage", {
          key: legacyAccentStorageKey,
          newValue: "rose"
        })
      );
    });

    expect(screen.getByTestId("preferences")).toHaveTextContent("command-deck");
    expect(setItem).not.toHaveBeenCalled();
  });

  it("keeps curated palettes when an accent changes outside Classic", () => {
    renderTheme();
    fireEvent.click(screen.getByRole("button", { name: "operations" }));
    const curatedPrimary = document.documentElement.style.getPropertyValue("--primary");

    fireEvent.click(screen.getByRole("button", { name: "rose" }));

    expect(document.documentElement.style.getPropertyValue("--primary")).toBe(curatedPrimary);
    expect(document.documentElement).toHaveAttribute("data-theme-accent", "rose");
  });

  it("removes previous theme styling when switching themes", () => {
    renderTheme();
    const classicRadius = document.documentElement.style.getPropertyValue("--radius-ui");
    const classicFont = document.documentElement.style.getPropertyValue("--font-display");

    fireEvent.click(screen.getByRole("button", { name: "operations" }));

    expect(document.documentElement.style.getPropertyValue("--radius-ui")).not.toBe(classicRadius);
    expect(document.documentElement.style.getPropertyValue("--font-display")).not.toBe(classicFont);
    expect(document.documentElement.style.getPropertyValue("--primary")).toBe("175 88% 26%");
  });
});

function contrast(foreground: string, background: string): number {
  const foregroundLuminance = luminance(foreground);
  const backgroundLuminance = luminance(background);
  return (
    (Math.max(foregroundLuminance, backgroundLuminance) + 0.05) /
    (Math.min(foregroundLuminance, backgroundLuminance) + 0.05)
  );
}

function luminance(value: string): number {
  const [hue, saturation, lightness] = value.match(/[\d.]+/g)!.map(Number);
  const s = saturation / 100;
  const l = lightness / 100;
  const chroma = (1 - Math.abs(2 * l - 1)) * s;
  const x = chroma * (1 - Math.abs(((hue / 60) % 2) - 1));
  const m = l - chroma / 2;
  const [red, green, blue] =
    hue < 60
      ? [chroma, x, 0]
      : hue < 120
        ? [x, chroma, 0]
        : hue < 180
          ? [0, chroma, x]
          : hue < 240
            ? [0, x, chroma]
            : hue < 300
              ? [x, 0, chroma]
              : [chroma, 0, x];
  return [red + m, green + m, blue + m]
    .map((channel) => (channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4))
    .reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
}
