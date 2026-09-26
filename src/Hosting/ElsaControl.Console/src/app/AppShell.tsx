import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Aperture, Bot, ChevronRight, Menu, Palette, Search, X } from "lucide-react";
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
    .filter(item => location.pathname === item.to || location.pathname.startsWith(item.to + "/"))
    .sort((a, b) => b.to.length - a.to.length)[0]?.label ?? (location.pathname.includes("engines/connect") ? "Connect engine" : "Console");
  const isWorkspacePage = location.pathname === "/admin/overview";
  const isConnectionPage = location.pathname === "/admin/engines/connect";

  useEffect(() => setNavigationOpen(false), [location.pathname]);

  return (
    <div className="console-shell">
      <a href="#console-content" className="console-skip-link">Skip to content</a>
      <div className="console-sidebar"><ConsoleSidebar /></div>
      <div className="console-main">
        <header className="console-topbar">
          <button className="console-icon-button console-menu-button" aria-label="Open navigation" aria-haspopup="dialog" aria-expanded={navigationOpen} aria-controls="console-navigation" onClick={() => setNavigationOpen(true)}>
            <Menu aria-hidden size={18} />
          </button>
          <Link to="/admin/overview" className="console-brand console-topbar-brand" aria-label="Elsa Control home">
            <span className="console-brand-mark"><Aperture aria-hidden size={18} strokeWidth={1.75} /></span>
            <span>elsa<span className="console-brand-product">control</span></span>
          </Link>
          <button className="console-search-trigger" aria-label="Search console" title="Search console (⌘/Ctrl K)" aria-haspopup="dialog" onClick={() => setSearchOpen(true)}>
            <Search aria-hidden size={16} />
            <span className="console-search-trigger-label">Search or jump to…</span>
            <kbd aria-hidden>⌘K</kbd>
          </button>
          <div className="console-topbar-actions">
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
              <summary className="console-avatar" aria-label="Account menu">{(auth.session.displayName ?? "User").split(/\s+/).slice(0, 2).map(name => name[0]).join("").toUpperCase()}</summary>
              <div className="console-account-menu">
                <p className="text-sm font-medium">{auth.session.displayName}</p>
                <p className="mt-1 truncate text-xs text-muted-foreground">{auth.session.email}</p>
                <button className="mt-4 w-full rounded-ui border border-border px-3 py-2 text-left text-sm hover:bg-muted" onClick={() => auth.signOut()}>Sign out</button>
              </div>
            </details>}
          </div>
        </header>
        <div className="console-workspace">
          {!isWorkspacePage && !isConnectionPage && <div className="console-breadcrumb"><span>{selectedWorkspace?.name ?? "Console"}</span><ChevronRight aria-hidden size={12} /><span>{currentPage}</span></div>}
          <main id="console-content" tabIndex={-1} className="console-content"><Outlet /></main>
        </div>
      </div>
      <NavigationDialog open={navigationOpen} onClose={() => setNavigationOpen(false)} />
      <QuickNavigate open={searchOpen} onOpen={() => setSearchOpen(true)} onClose={() => setSearchOpen(false)} />
      <WeaverAssistantPanel open={weaverOpen} onClose={() => setWeaverOpen(false)} />
      <AppearanceDialog open={appearanceOpen} onClose={() => setAppearanceOpen(false)} />
    </div>
  );
}

function ConsoleSidebar({ onNavigate }: { onNavigate?: () => void }) {
  const auth = useAuth();
  const linkClass = ({ isActive }: { isActive: boolean }) => "console-nav-link" + (isActive ? " is-active" : "");
  return <>
    <Link to="/admin/overview" className="console-brand" aria-label="Elsa Control home" onClick={onNavigate}>
      <span className="console-brand-mark"><Aperture aria-hidden size={18} strokeWidth={1.75} /></span>
      <span>elsa<span className="console-brand-product">control</span></span>
    </Link>
    <OrganizationWorkspaceSwitcher />
    <nav aria-label="Primary" className="console-sidebar-nav">
      {consoleNavigation.map(section => <div className="console-nav-group" role="group" aria-label={section.label} key={section.label}>
        <span className="console-nav-heading" aria-hidden>{section.label}</span>
        {section.items.map(item => <NavLink key={item.to} to={item.to} end={item.end} onClick={onNavigate} className={linkClass}><item.icon aria-hidden size={16} /><span>{item.label}</span></NavLink>)}
      </div>)}
      {auth.session?.isAdmin ? <div className="console-nav-group" role="group" aria-label="Administration"><span className="console-nav-heading" aria-hidden>Administration</span><NavLink to="/admin/organizations" onClick={onNavigate} className={linkClass}><Aperture aria-hidden size={16} /><span>Organizations</span></NavLink></div> : null}
    </nav>
    <ApplicationBuildNumber />
  </>;
}

// Narrow viewports hide the sidebar; the same content opens as a modal drawer.
function NavigationDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    if (open && !dialog.current?.open) dialog.current?.showModal();
    if (!open && dialog.current?.open) dialog.current.close();
  }, [open]);
  return <dialog ref={dialog} id="console-navigation" className="console-navigation-dialog" aria-label="Console navigation" onCancel={onClose} onClose={onClose} onClick={event => {
    if (event.target === event.currentTarget) onClose();
  }}>
    {open && <div className="console-navigation-drawer">
      <button className="console-icon-button console-navigation-close" aria-label="Close navigation" onClick={onClose}><X aria-hidden size={18} /></button>
      <ConsoleSidebar onNavigate={onClose} />
    </div>}
  </dialog>;
}

function OrganizationWorkspaceSwitcher() {
  const auth = useAuth();
  const context = useWorkspaceContext();
  if (!auth.session?.authenticated) return null;
  if (context.isLoading) return <span className="console-context-placeholder text-muted-foreground text-xs">Loading workspace…</span>;
  if (context.isError || !context.organizations.length) return <span className="console-context-placeholder text-muted-foreground text-xs">No workspace available</span>;
  return <div className="console-context">
    <label>Organization<select value={context.selectedOrganizationId} onChange={event => context.setSelectedOrganizationId(event.target.value)}>
      {context.organizations.map(org => <option key={org.id} value={org.id}>{org.name}</option>)}
    </select></label>
    <label>Workspace<select value={context.selectedWorkspaceId} onChange={event => context.setSelectedWorkspaceId(event.target.value)}>
      {context.organizationWorkspaces.map(workspace => <option key={workspace.id} value={workspace.id}>{workspace.name}</option>)}
    </select></label>
  </div>;
}

function ApplicationBuildNumber() {
  const { data } = useQuery({ queryKey: queryKeys.application, queryFn: getApplicationInfo, staleTime: 300_000 });
  return data?.buildNumber ? <p aria-label="Application build number" className="console-build">Build {data.buildNumber}</p> : null;
}
