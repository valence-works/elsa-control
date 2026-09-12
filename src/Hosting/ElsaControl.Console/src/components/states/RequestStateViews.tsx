import { FileQuestion, Inbox, KeyRound, LoaderCircle, RefreshCw, TriangleAlert } from "lucide-react";
import type { ReactNode } from "react";
import { EmptyState } from "@/components/ui";

export type RequestState = "loading" | "empty" | "stale" | "unauthorized" | "not-found" | "unexpected";

const defaultText: Record<RequestState, { title: string; description: string }> = {
  loading: { title: "Loading", description: "Fetching the latest catalog data." },
  empty: { title: "Nothing here yet", description: "There are no records for this view." },
  stale: { title: "Showing stale data", description: "The last refresh failed. Try again when the API is available." },
  unauthorized: { title: "Access problem", description: "Your console session is missing or no longer valid." },
  "not-found": { title: "Not found", description: "This record may have been removed or changed." },
  unexpected: { title: "Something went wrong", description: "The console could not complete the request." }
};

const statePresentation: Record<RequestState, { icon: ReactNode; role: "status" | "alert"; tone: "neutral" | "warning" | "destructive" }> = {
  loading: { icon: <LoaderCircle aria-hidden className="h-5 w-5 animate-spin" />, role: "status", tone: "neutral" },
  empty: { icon: <Inbox aria-hidden className="h-5 w-5" />, role: "status", tone: "neutral" },
  stale: { icon: <RefreshCw aria-hidden className="h-5 w-5" />, role: "status", tone: "warning" },
  unauthorized: { icon: <KeyRound aria-hidden className="h-5 w-5" />, role: "alert", tone: "destructive" },
  "not-found": { icon: <FileQuestion aria-hidden className="h-5 w-5" />, role: "status", tone: "neutral" },
  unexpected: { icon: <TriangleAlert aria-hidden className="h-5 w-5" />, role: "alert", tone: "destructive" }
};

export function RequestStateView({
  state,
  title,
  description
}: {
  state: RequestState;
  title?: string;
  description?: string;
}) {
  const copy = defaultText[state];
  const presentation = statePresentation[state];
  return (
    <EmptyState
      title={title ?? copy.title}
      description={description ?? copy.description}
      icon={presentation.icon}
      role={presentation.role}
      tone={presentation.tone}
    />
  );
}
