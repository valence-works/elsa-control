import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ReactNode } from "react";
import { ApiError } from "@/lib/api/httpClient";

function isUnauthorized(error: unknown) {
  return error instanceof ApiError && error.kind === "Unauthorized";
}

/**
 * A 401 from any request means the console session is gone or expired. Refreshing the session query
 * is all this needs to do: RequireCustomerAuth observes it and redirects to the login route, so an
 * expired session lands in the same place as one that was never established.
 */
function refreshSessionOnUnauthorized(error: unknown) {
  if (isUnauthorized(error)) {
    void queryClient.invalidateQueries({ queryKey: queryKeys.authSession });
  }
}

export const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: refreshSessionOnUnauthorized }),
  mutationCache: new MutationCache({ onError: refreshSessionOnUnauthorized }),
  defaultOptions: {
    queries: {
      staleTime: 20_000,
      refetchOnWindowFocus: false,
      // Retrying an unauthenticated request only delays the redirect; it can never succeed.
      retry: (failureCount, error) => !isUnauthorized(error) && failureCount < 1
    },
    mutations: {
      retry: 0
    }
  }
});

export const queryKeys = {
  authSession: ["auth-session"] as const,
  application: ["application"] as const,
  sources: ["sources"] as const,
  packages: ["packages"] as const,
  packageDetails: (packageId: string) => ["packages", packageId] as const,
  packageValidation: (packageId: string, version: string) => ["packages", packageId, "versions", version, "validation"] as const,
  packageManifest: (packageId: string, version: string) => ["packages", packageId, "versions", version, "manifest"] as const,
  syncRuns: ["sync-runs"] as const,
  syncRun: (runId: string) => ["sync-runs", runId] as const,
  deploymentCockpit: (workspaceId: string) => ["deployments", workspaceId, "cockpit"] as const,
  deploymentPermissions: (workspaceId: string) => ["deployments", workspaceId, "permissions"] as const,
  deploymentTiers: (workspaceId: string) => ["deployments", workspaceId, "tiers"] as const,
  deploymentTierCapabilities: (workspaceId: string) => ["deployments", workspaceId, "tier-capabilities"] as const,
  deploymentApplicationRevisions: (workspaceId: string, applicationId: string) => ["deployments", workspaceId, "applications", applicationId, "revisions"] as const,
  deploymentRevision: (workspaceId: string, revisionId: string) => ["deployments", workspaceId, "revisions", revisionId] as const,
  deploymentRevisionDeployability: (workspaceId: string, revisionId: string, engineId: string) => ["deployments", workspaceId, "revisions", revisionId, "deployability", engineId] as const,
  deploymentDesiredStateRequirements: (workspaceId: string, environmentId: string) => ["deployments", workspaceId, "environments", environmentId, "desired-state-requirements"] as const,
  deploymentSecretStores: (workspaceId: string) => ["deployments", workspaceId, "secret-stores"] as const,
  deploymentCredentialReferences: (workspaceId: string) => ["deployments", workspaceId, "credential-references"] as const,
  deploymentCredentialReferenceUsage: (workspaceId: string, credentialReferenceId: string) => ["deployments", workspaceId, "credential-references", credentialReferenceId, "usage"] as const,
  artifacts: (workspaceId: string) => ["artifacts", workspaceId] as const,
  artifactDetails: (workspaceId: string, artifactId: string) => ["artifacts", workspaceId, artifactId] as const,
  overview: ["overview"] as const,
  workspaceContext: ["workspace-context"] as const,
  runtimeBuilderCatalog: (workspaceId: string) => ["runtime-builder", workspaceId, "catalog"] as const,
  runtimeConfigurations: (workspaceId: string) => ["runtime-builder", workspaceId, "configurations"] as const,
  runtimeConfiguration: (workspaceId: string, configurationId: string) => ["runtime-builder", workspaceId, "configurations", configurationId] as const,
  engineProvisioningProviders: (workspaceId: string) => ["engine-provisioning", workspaceId, "providers"] as const,
  engineProvisioningTargets: (workspaceId: string) => ["engine-provisioning", workspaceId, "targets"] as const,
  managedElsaOnboardingOptions: (workspaceId: string) => ["managed-elsa", workspaceId, "onboarding-options"] as const,
  managedElsaInstances: (workspaceId: string) => ["managed-elsa", workspaceId, "instances"] as const,
  managedElsaOperation: (workspaceId: string, instanceId: string, operationId: string) => ["managed-elsa", workspaceId, "operation", instanceId, operationId] as const,
  managedElsaInstanceHealth: (workspaceId: string, instanceId: string) => ["managed-elsa", workspaceId, "instances", instanceId, "health"] as const,
  managedElsaInstanceAudit: (workspaceId: string, instanceId: string) => ["managed-elsa", workspaceId, "instances", instanceId, "audit"] as const,
  organizationBilling: (organizationId: string) => ["organization-billing", organizationId] as const,
  weaverConfiguration: (workspaceId: string) => ["weaver", workspaceId, "configuration"] as const,
  weaverSession: (workspaceId: string, sessionId: string) => ["weaver", workspaceId, "sessions", sessionId] as const
};

export function QueryProvider({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}
