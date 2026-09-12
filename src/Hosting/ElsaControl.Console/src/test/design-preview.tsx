import React from "react";
import ReactDOM from "react-dom/client";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { installDesignPreviewFixtures } from "./designPreviewFixtures";

const root = document.getElementById("root");

async function startDesignPreview() {
  if (!root) throw new Error("Design preview root is missing.");

  if (!import.meta.env.DEV) {
    root.innerHTML = '<main style="max-width:42rem;margin:4rem auto;padding:0 1.5rem;font:16px system-ui;color:#24302f"><h1>Design preview is development-only</h1><p>Run the console development server to open this sample-data harness.</p></main>';
    return;
  }

  // Keep fixture installation ahead of every application import. No preview request can reach a
  // real API, and production main.tsx is never imported by this entrypoint.
  installDesignPreviewFixtures();

  const [{ router }, { AuthProvider }, { QueryProvider }, { initializeTheme }] = await Promise.all([
    import("@/app/routes"),
    import("@/lib/auth/AuthProvider"),
    import("@/lib/query/queryClient"),
    import("@/lib/theme/ThemeProvider"),
    import("@/styles.css"),
    import("@/console-layout.css")
  ]);

  initializeTheme();
  const routes = router.routes;
  router.dispose();
  const previewRouter = createMemoryRouter(routes, { initialEntries: ["/admin/overview"] });
  ReactDOM.createRoot(root).render(
    <React.StrictMode>
      <QueryProvider>
        <AuthProvider>
          <RouterProvider router={previewRouter} />
          <PreviewBanner />
        </AuthProvider>
      </QueryProvider>
    </React.StrictMode>
  );
}

function PreviewBanner() {
  return (
    <aside className="fixed bottom-3 right-3 z-50 rounded-full border border-primary/30 bg-surface/95 px-2.5 py-1.5 text-[11px] font-medium text-foreground shadow-md shadow-black/10 backdrop-blur" aria-label="Design preview notice">
      <span className="flex items-center gap-2"><span className="h-1.5 w-1.5 rounded-full bg-primary" aria-hidden />Design preview · Sample data</span>
    </aside>
  );
}

void startDesignPreview().catch((error: unknown) => {
  if (!root) return;
  const message = error instanceof Error ? error.message : String(error);
  root.innerHTML = `<main style="max-width:52rem;margin:4rem auto;padding:0 1.5rem;font:16px system-ui;color:#7f1d1d"><h1>Design preview failed to start</h1><p>${escapeHtml(message)}</p></main>`;
  console.error(error);
});

function escapeHtml(value: string) {
  return value.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#039;");
}
