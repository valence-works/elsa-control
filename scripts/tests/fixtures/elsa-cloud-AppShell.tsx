// Pinned AppShell source for the staging-compat screen drift guard.
// Upstream: valence-works/elsa-cloud src/components/app/AppShell.tsx @ b8718da7
// (last change 6bbd4249). SHA-256 of this exact file is in
// elsa-cloud-AppShell.tsx.sha256. Re-hash after any vendor refresh.
import { useEffect, useState, type ReactNode } from "react";
import { Link, NavLink, useLocation } from "react-router-dom";
import { Icon } from "../ui/Icon";
import { ThemeToggle } from "../ui/ThemeToggle";
import { accountInitials } from "../../lib/accountPresentation";
import { existingEnginesAvailable } from "../../lib/existingEngines";

export const workspaceNav = [
  { to: "/dashboard", label: "Overview", icon: "grid" as const, end: true },
  { to: "/dashboard/managed-engines", label: "Managed engines", icon: "server" as const },
  { to: "/dashboard/existing-engines", label: "Existing engines", icon: "plug" as const },
  { to: "/dashboard/billing", label: "Billing and plans", icon: "card" as const },
  { to: "/dashboard/deployments", label: "Deployments", icon: "list" as const }
];

type AppShellProps = {
  account: { name: string; email: string };
  guide?: { complete: boolean; done: number; total: number } | null;
  navMarkers?: Record<string, string>;
  onSignOut: () => void;
  children: ReactNode;
};

export function AppShell({ account, guide, navMarkers, onSignOut, children }: AppShellProps) {
  const [drawerOpen, setDrawerOpen] = useState(false);
  const { pathname } = useLocation();

  useEffect(() => {
    setDrawerOpen(false);
  }, [pathname]);

  useEffect(() => {
    if (guide) {
      persistGuideComplete(guide.complete);
    }
  }, [guide]);

  useEffect(() => {
    if (!drawerOpen) {
      return;
    }
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        setDrawerOpen(false);
      }
    };
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [drawerOpen]);

  return (
    <div className={`ec-app${drawerOpen ? " drawer-open" : ""}`}>
      <a className="skip" href="#main">Skip to content</a>
      <div className="app">
        <header className="topbar">
          <button className="menu-btn" type="button" aria-label="Open navigation" aria-expanded={drawerOpen} aria-controls="app-sidebar" onClick={() => setDrawerOpen(true)}>
            <Icon name="menu" />
          </button>
          <Link className="brand" to="/">
            <span className="brand-name">Elsa Cloud</span>
          </Link>
          {guide && !guide.complete ? (
            <Link className="top-progress" to="/dashboard">
              <span className="dot signal" aria-hidden="true" />
              {guide.done} of {guide.total} done
            </Link>
          ) : null}
        </header>
        <div className="scrim" aria-hidden="true" onClick={() => setDrawerOpen(false)} />
        <aside className="sidebar" id="app-sidebar" aria-label="Workspace navigation">
          <div className="brand-row">
            <Link className="brand" to="/" aria-label="Elsa Cloud home">
              <img src="/brand/elsa-cloud/elsa-cloud-mark-compact.svg" alt="" />
              <span className="brand-name">Elsa Cloud</span>
            </Link>
            <span className="early">
              <span className="dot" aria-hidden="true" />
              Early access
            </span>
          </div>
          <nav className="nav" aria-label="Workspace">
            {workspaceNav.map((item) => {
              const comingSoon = item.to === "/dashboard/existing-engines" && !existingEnginesAvailable() ? "Coming soon" : undefined;
              return (
                <NavLink key={item.to} to={item.to} end={item.end}>
                  <Icon name={item.icon} />
                  {item.label}
                  {(comingSoon || navMarkers?.[item.to]) && (
                    <span className="nav-meta">{comingSoon}{navMarkers?.[item.to]}</span>
                  )}
                </NavLink>
              );
            })}
          </nav>
          {guide ? <GuidePanel guide={guide} /> : null}
          <div className="side-foot">
            <div className="account">
              <span className="avatar" aria-hidden="true">{accountInitials(account.name)}</span>
              <div className="acct-text">
                <p className="acct-name">{account.name}</p>
                <p className="acct-mail">{account.email}</p>
              </div>
            </div>
            <div className="acct-actions">
              <ThemeToggle className="icon-btn" />
              <button className="icon-btn" type="button" onClick={onSignOut}>
                <Icon name="logout" size="sm" />
                Sign out
              </button>
            </div>
          </div>
        </aside>
        <main id="main">{children}</main>
      </div>
    </div>
  );
}

function persistGuideComplete(_complete: boolean) {}
function GuidePanel(_props: { guide: { complete: boolean; done: number; total: number } }) {
  return null;
}
