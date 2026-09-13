import { createContext, useCallback, useContext, useEffect, useMemo, useState } from "react";
import type { ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { useLocation } from "react-router-dom";
import { getOrganizationWorkspaceContext } from "@/app/workspaceContextApi";
import type { OrganizationContext, OrganizationWorkspaceContextResponse, WorkspaceContext } from "@/app/workspaceContextModels";
import { useAuth } from "@/lib/auth/AuthProvider";
import { queryKeys } from "@/lib/query/queryClient";

type WorkspaceContextValue = {
  context: OrganizationWorkspaceContextResponse | undefined;
  organizations: OrganizationContext[];
  workspaces: WorkspaceContext[];
  organizationWorkspaces: WorkspaceContext[];
  selectedOrganization: OrganizationContext | null;
  selectedWorkspace: WorkspaceContext | null;
  selectedOrganizationId: string;
  selectedWorkspaceId: string;
  isLoading: boolean;
  isError: boolean;
  error: Error | null;
  setSelectedOrganizationId: (organizationId: string) => void;
  setSelectedWorkspaceId: (workspaceId: string) => void;
};

const WorkspaceContext = createContext<WorkspaceContextValue | null>(null);
const organizationStorageKey = "elsa-control-console-selected-organization-id";
const workspaceStorageKey = "elsa-control-console-selected-workspace-id";

export function WorkspaceContextProvider({ children }: { children: ReactNode }) {
  const auth = useAuth();
  const location = useLocation();
  const [selectedOrganizationId, setSelectedOrganizationIdState] = useState(() => readStoredValue(organizationStorageKey));
  const [selectedWorkspaceId, setSelectedWorkspaceIdState] = useState(() => readStoredValue(workspaceStorageKey));
  const context = useQuery({
    queryKey: queryKeys.workspaceContext,
    queryFn: getOrganizationWorkspaceContext,
    enabled: Boolean(auth.session?.authenticated) && !isAdminOrganizationsRoute(location.pathname),
    retry: false
  });

  const organizations = context.data?.organizations ?? [];
  const workspaces = context.data?.workspaces ?? [];
  const selectedOrganization = useMemo(() => {
    const direct = organizations.find((organization) => organization.id === selectedOrganizationId);
    if (direct) return direct;

    const workspaceOrganizationId = workspaces.find((workspace) => workspace.id === selectedWorkspaceId)?.organizationId;
    return organizations.find((organization) => organization.id === workspaceOrganizationId) ?? organizations[0] ?? null;
  }, [organizations, selectedOrganizationId, selectedWorkspaceId, workspaces]);
  const organizationWorkspaces = useMemo(
    () => workspaces.filter((workspace) => workspace.organizationId === selectedOrganization?.id),
    [selectedOrganization?.id, workspaces]
  );
  const selectedWorkspace = useMemo(
    () => organizationWorkspaces.find((workspace) => workspace.id === selectedWorkspaceId) ?? organizationWorkspaces[0] ?? null,
    [organizationWorkspaces, selectedWorkspaceId]
  );

  useEffect(() => {
    if (selectedOrganization && selectedOrganization.id !== selectedOrganizationId) {
      setSelectedOrganizationIdState(selectedOrganization.id);
    }
  }, [selectedOrganization, selectedOrganizationId]);

  useEffect(() => {
    if (selectedWorkspace && selectedWorkspace.id !== selectedWorkspaceId) {
      setSelectedWorkspaceIdState(selectedWorkspace.id);
    }
  }, [selectedWorkspace, selectedWorkspaceId]);

  useEffect(() => {
    storeValue(organizationStorageKey, selectedOrganizationId);
  }, [selectedOrganizationId]);

  useEffect(() => {
    storeValue(workspaceStorageKey, selectedWorkspaceId);
  }, [selectedWorkspaceId]);

  const setSelectedOrganizationId = useCallback((organizationId: string) => {
    setSelectedOrganizationIdState(organizationId);
    const nextWorkspace = workspaces.find((workspace) => workspace.organizationId === organizationId);
    setSelectedWorkspaceIdState(nextWorkspace?.id ?? "");
  }, [workspaces]);

  const setSelectedWorkspaceId = useCallback((workspaceId: string) => {
    setSelectedWorkspaceIdState(workspaceId);
    const workspace = workspaces.find((item) => item.id === workspaceId);
    if (workspace) {
      setSelectedOrganizationIdState(workspace.organizationId);
    }
  }, [workspaces]);

  const value = useMemo<WorkspaceContextValue>(() => ({
    context: context.data,
    organizations,
    workspaces,
    organizationWorkspaces,
    selectedOrganization,
    selectedWorkspace,
    selectedOrganizationId: selectedOrganization?.id ?? "",
    selectedWorkspaceId: selectedWorkspace?.id ?? "",
    isLoading: auth.isLoading || context.isLoading,
    isError: context.isError,
    error: context.error instanceof Error ? context.error : null,
    setSelectedOrganizationId,
    setSelectedWorkspaceId
  }), [
    auth.isLoading,
    context.data,
    context.error,
    context.isError,
    context.isLoading,
    organizationWorkspaces,
    organizations,
    selectedOrganization,
    selectedWorkspace,
    setSelectedOrganizationId,
    setSelectedWorkspaceId,
    workspaces
  ]);

  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

function isAdminOrganizationsRoute(pathname: string) {
  return pathname === "/admin/organizations" || pathname.startsWith("/admin/organizations/");
}

export function useWorkspaceContext() {
  const value = useContext(WorkspaceContext);
  if (value === null) {
    throw new Error("useWorkspaceContext must be used inside WorkspaceContextProvider.");
  }

  return value;
}

function readStoredValue(key: string) {
  if (typeof window === "undefined" || typeof window.localStorage?.getItem !== "function") return "";
  try {
    return window.localStorage.getItem(key) ?? "";
  } catch {
    return "";
  }
}

function storeValue(key: string, value: string) {
  if (typeof window === "undefined" || typeof window.localStorage?.setItem !== "function") return;
  try {
    if (value) {
      window.localStorage.setItem(key, value);
    } else {
      window.localStorage.removeItem(key);
    }
  } catch {
    // Context persistence is optional; selection still works for the current session.
  }
}
