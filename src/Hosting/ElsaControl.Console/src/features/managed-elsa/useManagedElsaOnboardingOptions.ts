import { useQuery } from "@tanstack/react-query";
import { getManagedElsaOnboardingOptions } from "@/features/managed-elsa/managedElsaApi";
import { onboardingChoices } from "@/features/managed-elsa/managedElsaOnboarding";
import { queryKeys } from "@/lib/query/queryClient";

export const previewOptionsLoadError =
  "Preview releases could not load. Turn off Show Preview releases to keep using Supported releases.";

export function useManagedElsaOnboardingOptions(workspaceId: string, includePreview: boolean, enabled = true) {
  const supported = useQuery({
    queryKey: queryKeys.managedElsaOnboardingOptions(workspaceId, false),
    queryFn: () => getManagedElsaOnboardingOptions(workspaceId),
    enabled: Boolean(workspaceId) && enabled,
    retry: false
  });
  const preview = useQuery({
    queryKey: queryKeys.managedElsaOnboardingOptions(workspaceId, true),
    queryFn: () => getManagedElsaOnboardingOptions(workspaceId, { includePreview: true }),
    enabled: Boolean(workspaceId) && enabled && includePreview,
    retry: false
  });
  const showPreviewEntries = includePreview && preview.isSuccess;
  const data = supported.data
    ? {
        ...supported.data,
        previewReleases: showPreviewEntries ? preview.data?.previewReleases ?? null : null
      }
    : undefined;

  return {
    data,
    choices: onboardingChoices(data, { includePreview: showPreviewEntries }),
    isLoading: supported.isLoading,
    isError: supported.isError,
    error: supported.error,
    previewError: includePreview && preview.isError ? previewOptionsLoadError : null
  };
}
