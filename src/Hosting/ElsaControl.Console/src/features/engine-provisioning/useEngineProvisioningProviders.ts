import { useQuery } from "@tanstack/react-query";
import { listEngineProvisioningProviders } from "@/features/engine-provisioning/engineProvisioningApi";
import { queryKeys } from "@/lib/query/queryClient";

export function useEngineProvisioningProviders(
  workspaceId: string | null,
  enabled = true
) {
  const discoveryEnabled = Boolean(workspaceId) && enabled;
  const query = useQuery({
    queryKey: queryKeys.engineProvisioningProviders(workspaceId ?? ""),
    queryFn: () => listEngineProvisioningProviders(workspaceId!),
    enabled: discoveryEnabled,
    retry: false
  });

  return {
    ...query,
    providers: query.data ?? [],
    hasProvider: (providerId: string) =>
      discoveryEnabled && query.isSuccess && query.data?.some((provider) => provider.id === providerId) === true
  };
}
