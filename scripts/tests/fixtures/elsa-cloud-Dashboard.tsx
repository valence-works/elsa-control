// Vendored markers from valence-works/elsa-cloud Dashboard.tsx @ b8718da7
// (compiled calm-sand bundle). Heading is .vh h1; workspace select is
// #cloud-workspace with "{organizationName} · {workspace name} ({role})".
export function dashboardPersonalDetails() {
  const firstName = "Ada";
  const organizationName = "Example Org";
  const workspaceName = "Example Workspace";
  const role = "Owner";
  return (
    <>
      <header className="vh">
        <h1>{`Welcome to Elsa Cloud${firstName ? `, ${firstName}` : ""}`}</h1>
      </header>
      <label htmlFor="cloud-workspace">Workspace</label>
      <select id="cloud-workspace" aria-label="Workspace">
        <option>{`${organizationName} · ${workspaceName} (${role})`}</option>
      </select>
    </>
  );
}
