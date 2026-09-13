import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";
import { WorkspaceContextProvider } from "@/app/WorkspaceContextProvider";
import { AuthProvider } from "@/lib/auth/AuthProvider";
import { ReleasesPage } from "@/features/release-catalog/ReleasesPage";
import { releaseCatalogCopy, releaseCatalogIdentityConflictCode } from "@/features/release-catalog/releaseCatalogModels";

const organizationId = "00000000-0000-0000-0000-000000000001";
const workspaceId = "00000000-0000-0000-0000-000000000010";
const digestA = `sha256:${"a".repeat(64)}`;
const digestB = `sha256:${"b".repeat(64)}`;

describe("ReleasesPage", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("admits a release and shows Stored, then Unchanged on identical replay", async () => {
    const fetchMock = installFetch({
      admitResponses: [
        { status: "Stored", entries: [catalogEntry({ manifestDigest: digestA })] },
        { status: "Unchanged", entries: [catalogEntry({ manifestDigest: digestA })] }
      ]
    });
    const user = userEvent.setup();
    renderPage();

    await screen.findByRole("heading", { name: "Admit release" });
    await fillAdmitForm(user);
    await user.click(screen.getByRole("button", { name: "Admit" }));

    expect(await screen.findByText("Stored")).toBeInTheDocument();
    expect(screen.getByText(/The signed manifest was admitted/)).toBeInTheDocument();
    expect(screen.getAllByText(releaseCatalogCopy.admitBlockedUntilAdmitted).length).toBeGreaterThan(0);

    const admitCall = fetchMock.mock.calls.find(([input, init]) =>
      String(input).endsWith("/api/admin/release-catalog/manifests") && String(init?.method ?? "GET").toUpperCase() === "POST"
    );
    expect(JSON.parse(String(admitCall?.[1]?.body))).toEqual({
      reference: "oci://example/manifest@" + digestA,
      digest: digestA,
      payload: producerPayload("3.8.0-preview.5567-build.160", digestA)
    });

    await user.click(screen.getByRole("button", { name: "Admit" }));
    expect(await screen.findByText("Unchanged")).toBeInTheDocument();
    expect(screen.getByText(/already exists/)).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: releaseCatalogCopy.conflictTitle })).not.toBeInTheDocument();
    expect(screen.getByText(/Happy path: admit a unique preview releaseVersion/)).toBeInTheDocument();
  });

  it("tells operators Admit requires control_admin or an admin API key", async () => {
    installFetch({
      admitError: { status: 403, body: { title: "Forbidden" } }
    });
    const user = userEvent.setup();
    renderPage();
    await fillAdmitForm(user);
    await user.click(screen.getByRole("button", { name: "Admit" }));

    expect(await screen.findByText("Admit is not authorized")).toBeInTheDocument();
    expect(screen.getByText(releaseCatalogCopy.admitRequiresAdmin)).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: releaseCatalogCopy.conflictTitle })).not.toBeInTheDocument();
  });

  it("opens the identity.conflict drawer as recovery UX with A primary, B disabled, and C hidden", async () => {
    installFetch({
      catalog: [catalogEntry({
        // Historical 149 vs 151 collision shape: same catalog identity, different fingerprint.
        // Recovery UX fixture only — not a live Open / dogfood blocker (#393 closed on build.160).
        distribution: {
          ...catalogEntry().distribution,
          releaseVersion: "3.8.0-preview.5567",
          source: { repository: "https://example", commit: "abc", runId: "149" }
        },
        manifestDigest: digestA
      })],
      admitError: {
        status: 409,
        body: {
          title: "Release catalog identity conflict.",
          code: releaseCatalogIdentityConflictCode,
          existing: [catalogEntry({
            distribution: {
              ...catalogEntry().distribution,
              releaseVersion: "3.8.0-preview.5567",
              source: { repository: "https://example", commit: "abc", runId: "149" }
            },
            manifestDigest: digestA
          })],
          incoming: [catalogEntry({
            distribution: {
              ...catalogEntry().distribution,
              releaseVersion: "3.8.0-preview.5567",
              source: { repository: "https://example", commit: "abc", runId: "151" }
            },
            manifestDigest: digestB
          })]
        }
      }
    });
    const user = userEvent.setup();
    renderPage();
    await fillAdmitForm(user, "3.8.0-preview.5567", digestB);
    await user.click(screen.getByRole("button", { name: "Admit" }));

    const drawer = await screen.findByRole("dialog", { name: releaseCatalogCopy.conflictTitle });
    expect(drawer).toHaveTextContent(releaseCatalogCopy.conflictBody);
    expect(drawer).toHaveTextContent(releaseCatalogCopy.happyPath);
    expect(drawer).toHaveTextContent(releaseCatalogCopy.admitBlockedUntilAdmitted);
    expect(drawer).toHaveTextContent("Existing (owns identity)");
    expect(drawer).toHaveTextContent("Incoming (blocked)");
    expect(drawer).toHaveTextContent("3.8.0-preview.5567");
    expect(drawer).toHaveTextContent(digestA);
    expect(drawer).toHaveTextContent(digestB);
    const existingCard = within(drawer).getByRole("heading", { name: "Existing (owns identity)" }).closest("section");
    const incomingCard = within(drawer).getByRole("heading", { name: "Incoming (blocked)" }).closest("section");
    expect(existingCard?.querySelector('[data-fact="build"]')).toHaveTextContent("build.149");
    expect(incomingCard?.querySelector('[data-fact="build"]')).toHaveTextContent("build.151");
    expect(within(drawer).getByRole("link", { name: "Open existing" })).toHaveAttribute(
      "href",
      "/admin/releases?existing=valence-runtime%7C3.8%7C3.8.0-preview.5567"
    );
    expect(within(drawer).getByText(/A — Publish a new catalog identity/)).toBeInTheDocument();
    expect(within(drawer).getByText("PRIMARY")).toBeInTheDocument();
    expect(within(drawer).getByText(releaseCatalogCopy.primaryRecovery)).toBeInTheDocument();
    expect(within(drawer).getByRole("button", { name: "Retry identical admit" })).toBeDisabled();
    expect(drawer).toHaveTextContent(releaseCatalogCopy.idempotentRetry);
    expect(drawer).not.toHaveTextContent(/supersede/i);
    expect(drawer).not.toHaveTextContent(/overwrite/i);
    expect(drawer).not.toHaveTextContent(/V3 Pass/i);
    expect(within(drawer).queryByRole("button", { name: /supersede/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("status", { name: /toast/i })).not.toBeInTheDocument();
  });

  it("enables B only when incoming fingerprints match the existing identity", async () => {
    installFetch({
      admitError: {
        status: 409,
        body: {
          title: "Release catalog identity conflict.",
          code: releaseCatalogIdentityConflictCode,
          existing: [catalogEntry({ manifestDigest: digestA })],
          incoming: [catalogEntry({ manifestDigest: digestA })]
        }
      }
    });
    const user = userEvent.setup();
    renderPage();
    await fillAdmitForm(user);
    await user.click(screen.getByRole("button", { name: "Admit" }));

    const drawer = await screen.findByRole("dialog", { name: releaseCatalogCopy.conflictTitle });
    expect(within(drawer).getByRole("button", { name: "Retry identical admit" })).toBeEnabled();
    expect(drawer).toHaveTextContent("Fingerprints match");
  });

  it("focuses the matching catalog row when Open existing deep-links with existing facts", async () => {
    installFetch({
      catalog: [catalogEntry({
        distribution: {
          ...catalogEntry().distribution,
          releaseVersion: "3.8.0-preview.5567",
          source: { repository: "https://example", commit: "abc", runId: "149" }
        },
        manifestDigest: digestA
      })],
      admitError: {
        status: 409,
        body: {
          title: "Release catalog identity conflict.",
          code: releaseCatalogIdentityConflictCode,
          existing: [catalogEntry({
            distribution: {
              ...catalogEntry().distribution,
              releaseVersion: "3.8.0-preview.5567",
              source: { repository: "https://example", commit: "abc", runId: "149" }
            },
            manifestDigest: digestA
          })],
          incoming: [catalogEntry({
            distribution: {
              ...catalogEntry().distribution,
              releaseVersion: "3.8.0-preview.5567",
              source: { repository: "https://example", commit: "abc", runId: "151" }
            },
            manifestDigest: digestB
          })]
        }
      }
    });
    const user = userEvent.setup();
    renderPage();
    await fillAdmitForm(user, "3.8.0-preview.5567", digestB);
    await user.click(screen.getByRole("button", { name: "Admit" }));

    const drawer = await screen.findByRole("dialog", { name: releaseCatalogCopy.conflictTitle });
    await user.click(within(drawer).getByRole("link", { name: "Open existing" }));

    expect(screen.queryByRole("dialog", { name: releaseCatalogCopy.conflictTitle })).not.toBeInTheDocument();
    const focusedRow = await screen.findByRole("row", { current: true });
    expect(focusedRow).toHaveAttribute("data-existing-focus", "true");
    expect(focusedRow).toHaveTextContent("3.8.0-preview.5567");
    expect(focusedRow).toHaveTextContent("Focused existing catalog identity");
    expect(focusedRow).toHaveFocus();
    expect(screen.queryByRole("button", { name: /supersede/i })).not.toBeInTheDocument();
  });

  it("highlights the existing catalog row from a deep-link without opening the conflict drawer", async () => {
    installFetch({
      catalog: [
        catalogEntry(),
        catalogEntry({
          distribution: {
            ...catalogEntry().distribution,
            releaseVersion: "3.8.0-preview.5567",
            source: { repository: "https://example", commit: "abc", runId: "149" }
          },
          manifestDigest: digestA
        })
      ]
    });
    renderPage("/admin/releases?existing=valence-runtime%7C3.8%7C3.8.0-preview.5567");

    const focusedRow = await screen.findByRole("row", { current: true });
    expect(focusedRow).toHaveAttribute("data-existing-focus", "true");
    expect(focusedRow).toHaveTextContent("3.8.0-preview.5567");
    expect(focusedRow).toHaveFocus();
    expect(screen.queryByRole("dialog", { name: releaseCatalogCopy.conflictTitle })).not.toBeInTheDocument();
    expect(screen.getByText("3.8.0-preview.5567-build.160")).toBeInTheDocument();
  });

  it("does not invent a catalog-row deep-link when existing facts are absent", async () => {
    installFetch({
      catalog: [catalogEntry()],
      admitError: {
        status: 409,
        body: {
          title: "Release catalog identity conflict.",
          code: releaseCatalogIdentityConflictCode
        }
      }
    });
    const user = userEvent.setup();
    renderPage();
    await fillAdmitForm(user, "9.9.9-preview.1", digestB);
    await user.click(screen.getByRole("button", { name: "Admit" }));

    const drawer = await screen.findByRole("dialog", { name: releaseCatalogCopy.conflictTitle });
    expect(within(drawer).getByRole("link", { name: "Open existing" })).toHaveAttribute("href", "/admin/releases");
    await user.click(within(drawer).getByRole("link", { name: "Open existing" }));
    expect(screen.queryByRole("row", { current: true })).not.toBeInTheDocument();
  });

  it("lists admitted catalog identities and filters them", async () => {
    const fetchMock = installFetch({
      catalog: [
        catalogEntry(),
        catalogEntry({
          distribution: { ...catalogEntry().distribution, releaseVersion: "3.8.0-preview.5567-build.161", catalogLifecycle: "supported" },
          topology: { ...catalogEntry().topology, id: "server" }
        })
      ]
    });
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByText("3.8.0-preview.5567-build.160")).toBeInTheDocument();
    expect(screen.getByText("3.8.0-preview.5567-build.161")).toBeInTheDocument();
    await user.selectOptions(screen.getByRole("combobox", { name: "Filter catalog lifecycle" }), "preview");
    await waitFor(() => expect(fetchMock.mock.calls.some(([input]) => String(input).includes("lifecycle=preview"))).toBe(true));
  });
});

async function fillAdmitForm(user: ReturnType<typeof userEvent.setup>, version = "3.8.0-preview.5567-build.160", digest = digestA) {
    await screen.findByRole("heading", { name: "Admit release" });
    await user.click(screen.getByRole("textbox", { name: "OCI reference" }));
    await user.paste(`oci://example/manifest@${digest}`);
    await user.click(screen.getByRole("textbox", { name: "Manifest digest" }));
    await user.paste(digest);
    await user.click(screen.getByRole("textbox", { name: "Signed manifest payload" }));
    await user.paste(producerPayload(version, digest));
}

function producerPayload(version: string, digest: string) {
  return JSON.stringify({
    release: {
      distributionId: "valence-runtime",
      generation: "producer-2.0.0",
      releaseLine: "3.8",
      version,
      channel: "preview",
      lifecycle: "preview",
      source: { workflow: { runId: "160" } }
    },
    distributions: [{ topology: "combined", capabilities: ["workflow-runtime"] }]
  }) + (digest ? "" : "");
}

function catalogEntry(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: "2.0.0",
    manifestReference: `oci://example/manifest@${digestA}`,
    manifestDigest: digestA,
    payloadDigest: digestA,
    signatureEvidenceReference: "oci://example/sig",
    signatureEvidenceDigest: digestA,
    registryClass: "paid",
    distribution: {
      id: "valence-runtime",
      generation: "producer-2.0.0",
      releaseLine: "3.8",
      releaseVersion: "3.8.0-preview.5567-build.160",
      channel: "preview",
      producerLifecycle: "preview",
      catalogLifecycle: "preview",
      edition: "commercial",
      source: { repository: "https://example", commit: "abc", runId: "160" }
    },
    topology: { id: "combined", packageManifestSchema: "1", runtimeKinds: ["combined"], capabilities: ["workflow-runtime"] },
    admittedAt: "2026-09-13T00:00:00Z",
    ...overrides
  };
}

function renderPage(path = "/admin/releases") {
  render(
    <TestQueryProvider>
      <MemoryRouter initialEntries={[path]}>
        <AuthProvider>
          <WorkspaceContextProvider>
            <ReleasesPage />
          </WorkspaceContextProvider>
        </AuthProvider>
      </MemoryRouter>
    </TestQueryProvider>
  );
}

function installFetch({
  catalog = [catalogEntry()],
  admitResponses = [],
  admitError
}: {
  catalog?: unknown[];
  admitResponses?: Array<{ status: string; entries: unknown[] }>;
  admitError?: { status: number; body: unknown };
} = {}) {
  const remaining = [...admitResponses];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = input instanceof Request ? input.url : input.toString();
    const method = (input instanceof Request ? input.method : init?.method ?? "GET").toUpperCase();
    if (url.endsWith("/api/auth/session"))
      return Response.json({ loginEnabled: true, authenticated: true, displayName: "Ops", email: "ops@example.com" });
    if (url.endsWith("/api/me/organizations"))
      return Response.json({
        account: { id: "account-1", displayName: "Ops", email: "ops@example.com" },
        organizations: [{ id: organizationId, name: "Acme", role: "Owner" }],
        workspaces: [{ id: workspaceId, name: "Dogfood", kind: "Shared", role: "Owner", organizationId, organizationName: "Acme", organizationRole: "Owner" }]
      });
    if (url.includes(`/api/workspaces/${workspaceId}/release-catalog`))
      return Response.json(catalog);
    if (method === "POST" && url.endsWith("/api/admin/release-catalog/manifests")) {
      if (admitError)
        return Response.json(admitError.body, { status: admitError.status });
      const next = remaining.shift() ?? { status: "Stored", entries: catalog };
      return Response.json(next, { status: next.status === "Stored" ? 201 : 200 });
    }
    return Response.json({ title: "Not found" }, { status: 404 });
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function TestQueryProvider({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
