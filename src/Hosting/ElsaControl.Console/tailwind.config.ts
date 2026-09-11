import type { Config } from "tailwindcss";

export default {
  darkMode: ["class"],
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        border: "hsl(var(--border))",
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        muted: "hsl(var(--muted))",
        "muted-foreground": "hsl(var(--muted-foreground))",
        surface: "hsl(var(--surface))",
        primary: "hsl(var(--primary))",
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
