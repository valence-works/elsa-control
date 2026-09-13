import { Activity, Cloud, WalletCards, Archive, DatabaseZap, FileClock, Gauge, Home, KeyRound, Layers3, PackageSearch, Rocket, ShieldCheck, Tag, Terminal } from "lucide-react";
import type { LucideIcon } from "lucide-react";

type ConsoleDestination = { to: string; label: string; icon: LucideIcon; end?: boolean };
export const consoleNavigation: Array<{ label: string; items: ConsoleDestination[] }> = [
  { label: "Workspace", items: [
    { to: "/admin/overview", label: "Overview", icon: Home },
    { to: "/admin/deployments/applications", label: "Applications", icon: Rocket },
    { to: "/admin/deployments", label: "Deployments", icon: Gauge, end: true },
    { to: "/admin/releases", label: "Releases", icon: Tag },
    { to: "/admin/runtimes", label: "Managed runtimes", icon: Cloud },
    { to: "/admin/operations", label: "Runtime operations", icon: Activity }
  ] },
  { label: "Library", items: [
    { to: "/admin/artifacts", label: "Artifacts", icon: Archive },
    { to: "/admin/packages", label: "Packages", icon: PackageSearch },
    { to: "/admin/runtime-builder", label: "Runtime builder", icon: Layers3 }
  ] },
  { label: "Manage", items: [
    { to: "/admin/sources", label: "Sources", icon: DatabaseZap },
    { to: "/admin/sync-runs", label: "Sync runs", icon: FileClock },
    { to: "/admin/deployments/credentials", label: "Engine credentials", icon: KeyRound },
    { to: "/admin/deployments/tiers", label: "Tiers", icon: ShieldCheck },
    { to: "/admin/console", label: "Logs", icon: Terminal },
    { to: "/admin/billing", label: "Billing", icon: WalletCards }
  ] }
];
