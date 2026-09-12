import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Bot, Building2, Boxes, ChevronRight, Menu, Palette, Search, X } from "lucide-react";
import { Link, NavLink, Outlet, useLocation } from "react-router-dom";
import { getApplicationInfo } from "@/app/applicationApi";
import { WorkspaceContextProvider, useWorkspaceContext } from "@/app/WorkspaceContextProvider";
import { WeaverAssistantPanel } from "@/features/weaver/WeaverAssistantPanel";
import { useAuth } from "@/lib/auth/AuthProvider";
import { queryKeys } from "@/lib/query/queryClient";
import { ThemeProvider } from "@/lib/theme/ThemeProvider";
import { AppearanceDialog } from "@/components/appearance/AppearanceDialog";
import { QuickNavigate } from "@/app/QuickNavigate";
import { consoleNavigation } from "@/app/consoleNavigation";

export function AppShell() {
  return <ThemeProvider><WorkspaceContextProvider><AppShellLayout /></WorkspaceContextProvider></ThemeProvider>;
}

function AppShellLayout() {
  const [weaverOpen, setWeaverOpen] = useState(false);
  const [appearanceOpen, setAppearanceOpen] = useState(false);
  const [navigationOpen, setNavigationOpen] = useState(false);
  const [searchOpen, setSearchOpen] = useState(false);
  const auth = useAuth();
  const location = useLocation();
  const { selectedWorkspace } = useWorkspaceContext();
  const currentPage = consoleNavigation.flatMap(section => section.items)
    .filter(item => location.pathname === item.to || (!item.end && location.pathname.startsWith(item.to + "/")))
    .sort((a, b) => b.to.length - a.to.length)[0]?.label ?? (location.pathname.includes("engines/connect") ? "Connect engine" : "Console");

  return (
    <div className="console-shell">
      <a href="#console-content" className="console-skip-link">Skip to content</a>
      <header className="console-topbar">
        <Link to="/admin/overview" className="console-brand" aria-label="Elsa Control home">
          <span className="console-brand-mark"><Boxes aria-hidden size={20} /></span>
          <span>elsa<span className="console-brand-product">control</span></span>
        </Link>
        <OrganizationWorkspaceSwitcher />
        <div className="console-topbar-actions">
          <button className="console-search-trigger" aria-label="Search console" aria-haspopup="dialog" onClick={() => setSearchOpen(true)}>
            <Search aria-hidden size={16} /><span>Go to…</span><kbd>⌘ K</kbd>
          </button>
          <button className="console-icon-button" aria-label="Appearance" title="Appearance" aria-haspopup="dialog" onClick={() => setAppearanceOpen(true)}><Palette aria-hidden size={18} /></button>
          <button className="console-icon-button" aria-label="Open Weaver assistant" title="Weaver" onClick={() => setWeaverOpen(true)}><Bot aria-hidden size={18} /></button>
          {auth.session?.authenticated && <details className="console-account" onKeyDown={event => {
            if (event.key === "Escape") {
              event.currentTarget.open = false;
              event.currentTarget.querySelector("summary")?.focus();
            }
          }} onBlur={event => {
            if (!event.currentTarget.contains(event.relatedTarget)) event.currentTarget.open = false;
          }}>
            <summary className="console-avatar" aria-label="Account menu">{(auth.session.displayName ?? "U").slice(0, 1).toUpperCase()}</summary>
            <div className="console-account-menu">
              <p className="text-sm font-medium">{auth.session.displayName}</p>
              <p className="mt-1 truncate text-xs text-muted-foreground">{auth.session.email}</p>
              <button className="mt-4 w-full rounded-ui border border-border px-3 py-2 text-left text-sm hover:bg-muted" onClick={() => auth.signOut()}>Sign out</button>
            </div>
          </details>}
          <button className="console-icon-button console-menu-button" aria-label={navigationOpen ? "Close navigation" : "Open navigation"} aria-expanded={navigationOpen} aria-controls="console-navigation" onClick={() => setNavigationOpen(!navigationOpen)}>{navigationOpen ? <X aria-hidden size={18} /> : <Menu aria-hidden size={18} />}</button>
        </div>
      </header>
      <aside id="console-navigation" className={"console-sidebar " + (navigationOpen ? "is-open" : "")}>
        <nav aria-label="Primary">
          {consoleNavigation.map(section => <div className="console-nav-group" key={section.label}>
            <p className="console-nav-heading">{section.label}</p>
            {section.items.map(item => <NavLink key={item.to} to={item.to} end={item.end} title={item.label} onClick={() => setNavigationOpen(false)} className={({ isActive }) => "console-nav-link " + (isActive ? "is-active" : "")}>
              <item.icon aria-hidden size={18} /><span>{item.label}</span>
            </NavLink>)}
          </div>)}
        </nav>
        <div className="console-sidebar-footer"><span className="console-footer-label">Elsa Control</span><ApplicationBuildNumber /></div>
      </aside>
      <div className="console-workspace">
        <div className="console-breadcrumb"><span>{selectedWorkspace?.name ?? "Console"}</span><ChevronRight aria-hidden size={12} /><span>{currentPage}</span></div>
        <main id="console-content" tabIndex={-1} className="console-content"><Outlet /></main>
      </div>
      <QuickNavigate open={searchOpen} onOpen={() => setSearchOpen(true)} onClose={() => setSearchOpen(false)} />
      <WeaverAssistantPanel open={weaverOpen} onClose={() => setWeaverOpen(false)} />
      <AppearanceDialog open={appearanceOpen} onClose={() => setAppearanceOpen(false)} />
    </div>
  );
}

function OrganizationWorkspaceSwitcher() {
  const auth = useAuth();
  const context = useWorkspaceContext();
  if (!auth.session?.authenticated) return <span className="console-context-placeholder" />;
  if (context.isLoading) return <span className="console-context-placeholder text-muted-foreground text-xs">Loading workspace…</span>;
  if (context.isError || !context.organizations.length) return <span className="console-context-placeholder text-muted-foreground text-xs">No workspace available</span>;
  return <div className="console-context">
    <Building2 aria-hidden size={15} />
    <select aria-label="Organization" value={context.selectedOrganizationId} onChange={event => context.setSelectedOrganizationId(event.target.value)}>
      {context.organizations.map(org => <option key={org.id} value={org.id}>{org.name}</option>)}
    </select>
    <span className="console-context-divider">/</span>
    <select aria-label="Workspace" value={context.selectedWorkspaceId} onChange={event => context.setSelectedWorkspaceId(event.target.value)}>
      {context.organizationWorkspaces.map(workspace => <option key={workspace.id} value={workspace.id}>{workspace.name}</option>)}
    </select>
  </div>;
}

function ApplicationBuildNumber() {
  const { data } = useQuery({ queryKey: queryKeys.application, queryFn: getApplicationInfo, staleTime: 300_000 });
  return data?.buildNumber ? <p aria-label="Application build number" className="console-build">Build {data.buildNumber}</p> : null;
}
