// Excerpt of elsa-cloud AppShell used by calm-sand dashboard screens.
// Pinned source: elsa-cloud main b8718da7. There is no account menu.
// "Billing and plans" is a sidebar NavLink (role=link). "Sign out" is a
// sidebar button. Mobile opens the sidebar drawer via "Open navigation".
// The account display name is .acct-name next to .acct-mail.
export const APP_SHELL_NAV = [
  { to: "/dashboard", label: "Overview", end: true },
  { to: "/dashboard/managed-engines", label: "Managed engines" },
  { to: "/dashboard/existing-engines", label: "Existing engines" },
  { to: "/dashboard/billing", label: "Billing and plans" },
  { to: "/dashboard/deployments", label: "Deployments" },
] as const;

export function AppShellSidebar() {
  return (
    <div className="ec-app">
      <button type="button" aria-label="Open navigation" aria-controls="app-sidebar">
        Open navigation
      </button>
      <aside className="sidebar" id="app-sidebar" aria-label="Workspace navigation">
        <nav className="nav" aria-label="Workspace">
          {APP_SHELL_NAV.map((item) => (
            <a key={item.to} href={item.to}>
              {item.label}
            </a>
          ))}
        </nav>
        <div className="side-foot">
          <div className="account">
            <p className="acct-name">{/* account.displayName */}</p>
            <p className="acct-mail">{/* account.email */}</p>
          </div>
          <button className="icon-btn" type="button">
            Sign out
          </button>
        </div>
      </aside>
    </div>
  );
}
