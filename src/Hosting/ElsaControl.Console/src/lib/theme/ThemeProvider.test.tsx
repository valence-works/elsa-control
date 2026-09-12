import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  accentDefinitions,
  appearanceStorageKey,
  appearanceStorageVersion,
  ThemeProvider,
  initializeTheme,
  legacyAccentStorageKey,
  legacyThemeStorageKey,
  useTheme
} from "./ThemeProvider";
import { themes } from "./themes";

function ThemeProbe() {
  const { preferences, resolvedMode, theme, setAccent, setMode } = useTheme();

  return (
    <div>
      <output data-testid="preferences">{JSON.stringify(preferences)}</output>
      <output data-testid="resolved-mode">{resolvedMode}</output>
      <output data-testid="theme-name">{theme.name}</output>
      <button onClick={() => setMode("system")}>system</button>
      <button onClick={() => setMode("light")}>light</button>
      <button onClick={() => setAccent("lime")}>lime</button>
      <button onClick={() => setAccent("glacier")}>glacier</button>
      <button onClick={() => setAccent("iris")}>iris</button>
      <button onClick={() => setAccent("ember")}>ember</button>
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
  document.documentElement.removeAttribute("data-console-layout");
  document.documentElement.removeAttribute("data-console-pattern");
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
  it("contains the Aperture base and future-ready accent metadata", () => {
    expect(themes.map((theme) => theme.id)).toEqual(["aperture"]);
    expect(accentDefinitions.map((accent) => accent.id)).toEqual(["lime", "glacier", "iris", "ember"]);

    expect(themes[0].palettes.light).toMatchObject({
      background: "80 18% 97%",
      foreground: "193 11% 16%",
      surface: "0 0% 100%",
      band: "193 12% 14%",
      bandForeground: "120 13% 94%",
      bandMuted: "160 9% 73%",
      bandBorder: "174 6% 31%"
    });
    expect(themes[0].palettes.dark).toMatchObject({
      background: "200 10% 11%",
      foreground: "144 13% 92%",
      surface: "202 11% 15%",
      band: "189 17% 8%",
      bandForeground: "120 13% 94%",
      bandMuted: "160 9% 73%",
      bandBorder: "174 6% 31%"
    });

    for (const theme of themes) {
      expect(theme.layout).toBe("topbar");
      expect(theme.version).toBeGreaterThan(0);
      expect(Object.keys(theme.palettes.light)).toEqual(Object.keys(theme.palettes.dark));
      expect(theme.fontDisplay).toBe('-apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif');
      expect(theme.fontMono).toBe('"SFMono-Regular", Consolas, monospace');
    }
  });

  it("keeps accent fills and text readable in both modes", () => {
    const theme = themes[0];
    for (const accent of accentDefinitions) {
      for (const mode of ["light", "dark"] as const) {
        const palette = accent.palettes[mode];
        expect(contrast(palette.primaryForeground, palette.primary)).toBeGreaterThanOrEqual(4.5);
        expect(contrast(palette.primaryText, theme.palettes[mode].background)).toBeGreaterThanOrEqual(4.5);
      }
    }
  });
});

describe("ThemeProvider", () => {
  it("uses Aperture dark and Lime for fresh preferences", () => {
    renderTheme();

    expect(screen.getByTestId("preferences")).toHaveTextContent(
      '{"themeId":"aperture","mode":"dark","accent":"lime"}'
    );
    expect(screen.getByTestId("theme-name")).toHaveTextContent("Aperture");
    expect(document.documentElement).toHaveClass("dark");
    expect(document.documentElement).toHaveAttribute("data-console-theme", "aperture");
    expect(document.documentElement).toHaveAttribute("data-console-layout", "topbar");
    expect(JSON.parse(window.localStorage.getItem(appearanceStorageKey)!)).toEqual({
      version: appearanceStorageVersion,
      themeId: "aperture",
      mode: "dark",
      accent: "lime"
    });
  });

  it("migrates legacy keys to Aperture accents while preserving mode", () => {
    window.localStorage.setItem(legacyThemeStorageKey, "dark");
    window.localStorage.setItem(legacyAccentStorageKey, "violet");

    expect(initializeTheme()).toEqual({ themeId: "aperture", mode: "dark", accent: "iris" });
    expect(JSON.parse(window.localStorage.getItem(appearanceStorageKey)!)).toEqual({
      version: appearanceStorageVersion,
      themeId: "aperture",
      mode: "dark",
      accent: "iris"
    });
    expect(window.localStorage.getItem(legacyAccentStorageKey)).toBe("violet");
    expect(document.documentElement).toHaveClass("dark");
    expect(document.documentElement).toHaveAttribute("data-theme-accent", "iris");
  });

  it("migrates v1 theme and accent values without losing system mode", () => {
    let matches = true;
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      value: vi.fn(() => ({ matches, addEventListener: vi.fn(), removeEventListener: vi.fn() }))
    });
    window.localStorage.setItem(
      appearanceStorageKey,
      JSON.stringify({ version: 1, themeId: "topology-atlas", mode: "system", accent: "rose" })
    );

    expect(initializeTheme()).toEqual({ themeId: "aperture", mode: "system", accent: "ember" });
    expect(document.documentElement).toHaveClass("dark");
    expect(JSON.parse(window.localStorage.getItem(appearanceStorageKey)!)).toEqual({
      version: appearanceStorageVersion,
      themeId: "aperture",
      mode: "system",
      accent: "ember"
    });

    matches = false;
    expect(initializeTheme()).toEqual({ themeId: "aperture", mode: "system", accent: "ember" });
    expect(document.documentElement).not.toHaveClass("dark");
  });

  it("loads valid current preferences on a later render", () => {
    window.localStorage.setItem(
      appearanceStorageKey,
      JSON.stringify({ version: appearanceStorageVersion, themeId: "aperture", mode: "light", accent: "glacier" })
    );

    renderTheme();

    expect(screen.getByTestId("preferences")).toHaveTextContent("glacier");
    expect(screen.getByTestId("resolved-mode")).toHaveTextContent("light");
    expect(document.documentElement).not.toHaveClass("dark");
    expect(document.documentElement.style.getPropertyValue("--primary")).toBe("191 64% 74%");
    expect(document.documentElement.style.getPropertyValue("--primary-text")).toBe("193 45% 16%");
    expect(document.documentElement.style.getPropertyValue("--radius-ui")).toBe("4px");
  });

  it.each(["not-an-accent", "__proto__", "constructor"])("falls back safely from malformed storage and invalid accent %s", (accent) => {
    window.localStorage.setItem(appearanceStorageKey, "{not-json");
    window.localStorage.setItem(legacyThemeStorageKey, "not-a-mode");
    window.localStorage.setItem(legacyAccentStorageKey, accent);

    renderTheme();

    expect(screen.getByTestId("preferences")).toHaveTextContent(
      '{"themeId":"aperture","mode":"dark","accent":"lime"}'
    );
    expect(document.documentElement).toHaveClass("dark");
    expect(JSON.parse(window.localStorage.getItem(appearanceStorageKey)!)).toEqual({
      version: appearanceStorageVersion,
      themeId: "aperture",
      mode: "dark",
      accent: "lime"
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
    expect(screen.getByTestId("preferences")).toHaveTextContent(
      '{"themeId":"aperture","mode":"dark","accent":"lime"}'
    );
    expect(document.documentElement).toHaveClass("dark");
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
    window.localStorage.setItem(
      appearanceStorageKey,
      JSON.stringify({ version: appearanceStorageVersion, themeId: "aperture", mode: "system", accent: "lime" })
    );

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
    const remote = JSON.stringify({ version: appearanceStorageVersion, themeId: "aperture", mode: "light", accent: "iris" });

    act(() => {
      window.dispatchEvent(
        new StorageEvent("storage", {
          key: appearanceStorageKey,
          newValue: remote
        })
      );
    });

    expect(screen.getByTestId("preferences")).toHaveTextContent("iris");
    expect(document.documentElement).not.toHaveClass("dark");
    expect(setItem).not.toHaveBeenCalled();
  });

  it("keeps the canonical cross-tab theme when compatibility keys arrive afterward", () => {
    renderTheme();
    const remote = JSON.stringify({ version: appearanceStorageVersion, themeId: "aperture", mode: "light", accent: "iris" });
    window.localStorage.setItem(appearanceStorageKey, remote);
    const setItem = vi.spyOn(window.localStorage, "setItem");

    act(() => {
      window.dispatchEvent(new StorageEvent("storage", { key: appearanceStorageKey, newValue: remote }));
      window.dispatchEvent(new StorageEvent("storage", { key: legacyThemeStorageKey, newValue: "dark" }));
      window.dispatchEvent(new StorageEvent("storage", { key: legacyAccentStorageKey, newValue: "violet" }));
    });

    expect(screen.getByTestId("preferences")).toHaveTextContent("iris");
    expect(setItem).not.toHaveBeenCalled();
  });

  it("changes only primary tokens when the accent changes", () => {
    renderTheme();
    const statuses = ["--destructive", "--warning", "--success"].map((name) =>
      document.documentElement.style.getPropertyValue(name)
    );
    const bandTokens = ["--band", "--band-foreground", "--band-muted", "--band-border"].map((name) =>
      document.documentElement.style.getPropertyValue(name)
    );
    const limePrimary = document.documentElement.style.getPropertyValue("--primary");

    fireEvent.click(screen.getByRole("button", { name: "iris" }));

    expect(document.documentElement.style.getPropertyValue("--primary")).not.toBe(limePrimary);
    expect(document.documentElement.style.getPropertyValue("--primary-text")).toBe("254 62% 82%");
    expect(["--destructive", "--warning", "--success"].map((name) => document.documentElement.style.getPropertyValue(name))).toEqual(
      statuses
    );
    expect(["--band", "--band-foreground", "--band-muted", "--band-border"].map((name) =>
      document.documentElement.style.getPropertyValue(name)
    )).toEqual(bandTokens);
    expect(document.documentElement).toHaveAttribute("data-theme-accent", "iris");
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
