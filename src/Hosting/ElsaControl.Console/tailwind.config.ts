import type { Config } from "tailwindcss";

export default {
  darkMode: ["class"],
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      // Pastel accent fills need a darker foreground treatment on light surfaces.
      textColor: {
        primary: "hsl(var(--primary-text))",
        "primary-text": "hsl(var(--primary-text))"
      },
      colors: {
        border: "hsl(var(--border))",
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        muted: "hsl(var(--muted))",
        "muted-foreground": "hsl(var(--muted-foreground))",
        surface: "hsl(var(--surface))",
        primary: "hsl(var(--primary))",
        "primary-text": "hsl(var(--primary-text))",
        "primary-foreground": "hsl(var(--primary-foreground))",
        destructive: "hsl(var(--destructive))",
        warning: "hsl(var(--warning))",
        success: "hsl(var(--success))"
      },
      borderRadius: {
        ui: "var(--radius-ui, 8px)"
      },
      fontFamily: {
        display: ["var(--font-display)"],
        mono: ["var(--font-mono)"]
      }
    }
  },
  plugins: []
} satisfies Config;
