import { createBrowserRouter, Link, Navigate, Outlet, useParams, useSearchParams } from "react-router-dom";
import { AppShell } from "@/app/AppShell";
import { OverviewPage } from "@/app/OverviewPage";
import { RequestStateView } from "@/components/states/RequestStateViews";
import { Button, EmptyState, buttonClassName } from "@/components/ui";
import {
  DeploymentApplicationEditPage,
  DeploymentApplicationsPage,
  DeploymentApplicationPage,
  DeploymentApplicationRevisionsPage,
  DeploymentCredentialReferenceCreatePage,
  DeploymentCredentialReferenceEditPage,
  DeploymentCredentialReferencePage,
  DeploymentCredentialStoreCreatePage,
  DeploymentCredentialStoreEditPage,
  DeploymentEnginePage,
  DeploymentEngineEditPage,
  DeploymentCredentialsPage,
  DeploymentEnvironmentCreatePage,
  DeploymentEnvironmentEditPage,
  DeploymentEnvironmentPage,
  DeploymentRevisionDetailPage,
  DeploymentRevisionCreatePage,
  DeploymentsPage,
  NewDeploymentSetupPage
} from "@/features/deployments/DeploymentsPage";
import { DeploymentTierCreatePage, DeploymentTierEditPage, DeploymentTiersPage } from "@/features/deployments/DeploymentTiersPage";
import { ArtifactCreatePage, ArtifactDetailsPage, ArtifactsPage } from "@/features/artifacts/ArtifactsPage";
import { RequireCustomerAuth, safeReturnUrl, useAuth } from "@/lib/auth/AuthProvider";
import { NewSourcePage, EditSourcePage } from "@/features/sources/SourceFormPage";
import { SourceDetailsPage } from "@/features/sources/SourceDetailsPage";
import { SourcesPage } from "@/features/sources/SourcesPage";
import { PackageDetailsPage } from "@/features/packages/PackageDetailsPage";
import { PackagesPage } from "@/features/packages/PackagesPage";
import { EditRuntimeBuilderPage, NewRuntimeBuilderPage, RuntimeBuilderPage } from "@/features/runtime-builder/RuntimeBuilderPage";
import { SyncRunDetailsPage } from "@/features/sync-runs/SyncRunDetailsPage";
import { SyncRunsPage } from "@/features/sync-runs/SyncRunsPage";
import { WeaverSessionPage } from "@/features/weaver/WeaverSessionPage";
import { ConsoleLogsPage } from "@/features/console/ConsoleLogsPage";
import { ManagedElsaInstancesPage } from "@/features/managed-elsa/ManagedElsaInstancesPage";
import { ApplyReleasePage } from "@/features/managed-elsa/ApplyReleasePage";
import { ManagedElsaOperationsPage } from "@/features/managed-elsa/ManagedElsaOperationsPage";
import { ReleasesPage } from "@/features/release-catalog/ReleasesPage";
import { OrganizationBillingPage } from "@/features/billing/OrganizationBillingPage";
import { ConnectEnginePage } from "@/features/deployments/ConnectEnginePage";
import { EngineProvisioningPage } from "@/features/engine-provisioning/EngineProvisioningPage";

function PlaceholderPage({ title }: { title: string }) {
  return (
    <section className="space-y-2">
      <h1 className="font-display text-xl font-semibold">{title}</h1>
      <p className="text-sm text-muted-foreground">This operational view is ready for feature implementation.</p>
    </section>
  );
}

function LegacyEngineConnectionRoute() {
  const { environmentId = "" } = useParams();
  return <Navigate to={"/admin/engines/connect?environmentId=" + encodeURIComponent(environmentId)} replace />;
}

export function AdminLoginPage() {
  const auth = useAuth();
  const [searchParams] = useSearchParams();
  const requestedReturnUrl = searchParams.get("returnUrl");
  const returnUrl = safeReturnUrl(requestedReturnUrl);

  if (auth.isLoading)
    return <RequestStateView state="loading" title="Checking customer session" />;

  if (auth.session?.authenticated) {
    // Arriving here with a return URL means the guard bounced a signed-out visitor and the session
    // has since become valid, so send them on to the page they originally asked for.
    if (requestedReturnUrl)
      return <Navigate to={returnUrl} replace />;

    return (
      <EmptyState
        title="You are already signed in"
        description="Your customer session is active. Continue to the Elsa Control overview or use the account menu when you need to sign out."
        action={<Link to="/admin/overview" className={buttonClassName()}>Open overview</Link>}
      />
    );
  }

  if (!auth.session?.loginEnabled) {
    return (
      <RequestStateView
        state="unauthorized"
        title="Customer login is not configured"
        description="Workspace features require a configured Elsa Control identity provider."
      />
    );
  }

  return (
    <EmptyState
      title="Sign in to Elsa Control"
      description="Use your configured Elsa Control identity provider to continue to the console."
      action={<Button type="button" onClick={() => auth.signIn(returnUrl)}>Continue to sign in</Button>}
    />
  );
}

export function ConsoleNotFoundPage() {
  return (
    <EmptyState
      title="Console page not found"
      description="The requested console page does not exist or is no longer available."
      action={<Link to="/admin/overview" className={buttonClassName()}>Open overview</Link>}
    />
  );
}

export const router = createBrowserRouter([
  {
    path: "/",
    element: <Navigate to="/admin/overview" replace />
  },
  {
    path: "/admin",
    element: <AppShell />,
    errorElement: <RequestStateView state="unexpected" title="The console could not load." />,
    children: [
      { index: true, element: <Navigate to="/admin/overview" replace /> },
      { path: "login", element: <AdminLoginPage /> },
      {
        // Every console route below this layout requires a signed-in customer session. The guard
        // lives here, once, so no page has to decide for itself what an unauthenticated visitor sees.
        element: <RequireCustomerAuth><Outlet /></RequireCustomerAuth>,
        children: [
          { path: "overview", element: <OverviewPage /> },
          { path: "billing", element: <OrganizationBillingPage /> },
          { path: "engines/connect", element: <ConnectEnginePage /> },
          { path: "engines/provision", element: <EngineProvisioningPage /> },
          { path: "sources", element: <SourcesPage /> },
          { path: "sources/new", element: <NewSourcePage /> },
          { path: "sources/:sourceId", element: <SourceDetailsPage /> },
          { path: "sources/:sourceId/edit", element: <EditSourcePage /> },
          { path: "packages", element: <PackagesPage /> },
          { path: "packages/:packageId", element: <PackageDetailsPage /> },
          { path: "packages/:packageId/versions/:version", element: <PackageDetailsPage /> },
          { path: "packages/:packageId/versions/:version/:section", element: <PackageDetailsPage /> },
          { path: "sync-runs", element: <SyncRunsPage /> },
          { path: "sync-runs/:runId", element: <SyncRunDetailsPage /> },
          { path: "deployments", element: <DeploymentsPage /> },
          { path: "deployments/new", element: <NewDeploymentSetupPage /> },
          { path: "deployments/credentials", element: <DeploymentCredentialsPage /> },
          { path: "deployments/credentials/stores/new", element: <DeploymentCredentialStoreCreatePage /> },
          { path: "deployments/credentials/stores/:secretStoreId/edit", element: <DeploymentCredentialStoreEditPage /> },
          { path: "deployments/credentials/references/new", element: <DeploymentCredentialReferenceCreatePage /> },
          { path: "deployments/credentials/references/:credentialReferenceId", element: <DeploymentCredentialReferencePage /> },
          { path: "deployments/credentials/references/:credentialReferenceId/edit", element: <DeploymentCredentialReferenceEditPage /> },
          { path: "deployments/applications", element: <DeploymentApplicationsPage /> },
          { path: "deployments/applications/:applicationId", element: <DeploymentApplicationPage /> },
          { path: "deployments/applications/:applicationId/edit", element: <DeploymentApplicationEditPage /> },
          { path: "deployments/applications/:applicationId/revisions", element: <DeploymentApplicationRevisionsPage /> },
          { path: "deployments/applications/:applicationId/revisions/:revisionId", element: <DeploymentRevisionDetailPage /> },
          { path: "deployments/applications/:applicationId/environments/new", element: <DeploymentEnvironmentCreatePage /> },
          { path: "deployments/applications/:applicationId/environments/:environmentId", element: <DeploymentEnvironmentPage /> },
          { path: "deployments/applications/:applicationId/environments/:environmentId/edit", element: <DeploymentEnvironmentEditPage /> },
          { path: "deployments/applications/:applicationId/environments/:environmentId/revisions/new", element: <DeploymentRevisionCreatePage /> },
          { path: "deployments/applications/:applicationId/environments/:environmentId/engines/new", element: <LegacyEngineConnectionRoute /> },
          { path: "deployments/applications/:applicationId/environments/:environmentId/engines/:engineId", element: <DeploymentEnginePage /> },
          { path: "deployments/applications/:applicationId/environments/:environmentId/engines/:engineId/edit", element: <DeploymentEngineEditPage /> },
          { path: "deployments/tiers", element: <DeploymentTiersPage /> },
          { path: "deployments/tiers/new", element: <DeploymentTierCreatePage /> },
          { path: "deployments/tiers/:tierId/edit", element: <DeploymentTierEditPage /> },
          { path: "artifacts", element: <ArtifactsPage /> },
          { path: "artifacts/new", element: <ArtifactCreatePage /> },
          { path: "artifacts/:artifactId", element: <ArtifactDetailsPage /> },
          { path: "runtime-builder", element: <RuntimeBuilderPage /> },
          { path: "runtime-builder/new", element: <NewRuntimeBuilderPage /> },
          { path: "runtime-builder/:configurationId/edit", element: <EditRuntimeBuilderPage /> },
          { path: "console", element: <ConsoleLogsPage /> },
          { path: "targets", element: <PlaceholderPage title="Targets" /> },
          { path: "releases", element: <ReleasesPage /> },
          { path: "runtimes", element: <ManagedElsaInstancesPage /> },
          { path: "runtimes/:instanceId/apply", element: <ApplyReleasePage /> },
          { path: "operations", element: <ManagedElsaOperationsPage /> },
          { path: "weaver/sessions/:sessionId", element: <WeaverSessionPage /> },
          { path: "audit", element: <PlaceholderPage title="Audit" /> }
        ]
      },
      { path: "*", element: <ConsoleNotFoundPage /> }
    ]
  }
]);
