import path from "node:path";
import react from "@vitejs/plugin-react";
import { build, defineConfig, loadEnv, normalizePath } from "vite";

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, __dirname, "");
  const catalogApiProxyTarget = env.CATALOG_API_PROXY_TARGET ?? "http://localhost:5220";
  const portValue = process.env.PORT ?? env.PORT ?? "5173";
  const port = Number(portValue);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error(`Invalid PORT "${portValue}": expected a TCP port between 1 and 65535`);
  }

  let catalogApiProxyOrigin: string;
  try {
    catalogApiProxyOrigin = new URL(catalogApiProxyTarget).origin;
    if (catalogApiProxyOrigin === "null") {
      throw new Error("include a URL scheme such as http:// or https://");
    }
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    throw new Error(`Invalid CATALOG_API_PROXY_TARGET "${catalogApiProxyTarget}": ${message}`);
  }
  const devIdentityIssuer = env.CATALOG_DEV_IDENTITY_ISSUER ?? "https://elsaworkflows.io";
  const devIdentitySubject = env.CATALOG_DEV_IDENTITY_SUBJECT ?? "local-admin";
  const devIdentityEmail = env.CATALOG_DEV_IDENTITY_EMAIL ?? "local-admin@example.test";
  const devIdentityName = env.CATALOG_DEV_IDENTITY_NAME ?? "Local Admin";
  const devAdminApiKey = env.CATALOG_DEV_ADMIN_API_KEY ?? "local-dev-key";
  let themeBootstrap: Promise<string> | undefined;

  return {
    base: "/admin/",
    plugins: [react(), {
      name: "console-theme-bootstrap",
      transformIndexHtml: {
        order: "pre",
        async handler() {
          themeBootstrap ??= buildThemeBootstrap();
          return [{ tag: "script", attrs: { id: "console-theme-bootstrap" }, children: await themeBootstrap, injectTo: "head-prepend" }];
        }
      },
      handleHotUpdate(context) {
        if (normalizePath(context.file).includes("/lib/theme/")) themeBootstrap = undefined;
      }
    }, {
      name: "console-design-preview",
      configureServer(server) {
        server.middlewares.use((request, response, next) => {
          // The sample session has no real identity to sign out. Keep its native
          // logout form inside the preview, then restart it with fresh sample data.
          if (request.method === "POST" && request.url?.split("?")[0] === "/admin/design-preview.html") {
            response.writeHead(303, { Location: "/admin/design-preview.html" });
            response.end();
            return;
          }
          next();
        });
      }
    }],
    resolve: {
      alias: {
        "@": path.resolve(__dirname, "./src")
      }
    },
    server: {
      port,
      strictPort: true,
      proxy: {
        "/api": {
          target: catalogApiProxyTarget,
          changeOrigin: true,
          ws: true,
          configure: (proxy) => {
            proxy.on("proxyReq", (proxyReq, req) => {
              proxyReq.setHeader("Origin", catalogApiProxyOrigin);
              proxyReq.setHeader("X-Catalog-Identity-Issuer", devIdentityIssuer);
              proxyReq.setHeader("X-Catalog-Identity-Subject", devIdentitySubject);
              proxyReq.setHeader("X-Catalog-Identity-Email", devIdentityEmail);
              proxyReq.setHeader("X-Catalog-Identity-Name", devIdentityName);
              if (requestTargetsAdminConsoleLogs(req.url))
                proxyReq.setHeader("X-Api-Key", devAdminApiKey);
            });
            proxy.on("proxyReqWs", (proxyReq, req) => {
              proxyReq.setHeader("Origin", catalogApiProxyOrigin);
              if (requestTargetsAdminConsoleLogs(req.url))
                proxyReq.setHeader("X-Api-Key", devAdminApiKey);
            });
          }
        }
      }
    }
  };
});

async function buildThemeBootstrap(): Promise<string> {
  const result = await build({
    configFile: false,
    logLevel: "silent",
    build: {
      write: false,
      minify: true,
      lib: { entry: path.resolve(__dirname, "src/lib/theme/bootstrap.ts"), name: "ElsaThemeBootstrap", formats: ["iife"] }
    }
  });
  const outputs = Array.isArray(result) ? result : [result];
  const chunks = outputs.flatMap(output => "output" in output ? output.output : []).filter(output => output.type === "chunk");
  if (chunks.length !== 1) throw new Error("Expected one standalone theme bootstrap script.");
  return chunks[0].code;
}

function requestTargetsAdminConsoleLogs(url: string | undefined) {
  if (!url)
    return false;

  return url === "/api/admin/console-logs" || url.startsWith("/api/admin/console-logs/");
}
