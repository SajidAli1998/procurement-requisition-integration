import {
  Activity,
  Bot,
  ClipboardCheck,
  Copy,
  Database,
  FileSpreadsheet,
  FilePlus2,
  FlaskConical,
  Hand,
  LayoutDashboard,
  Lightbulb,
  ListChecks,
  Network,
  ShieldAlert,
  Zap,
} from "lucide-react";

interface NavItem {
  href: string;
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  group: "Start here" | "Results" | "Advanced";
}

export const NAV: NavItem[] = [
  { href: "/try", label: "Try it yourself", icon: Hand, group: "Start here" },
  { href: "/what-if", label: "What if", icon: Lightbulb, group: "Start here" },
  { href: "/", label: "Dashboard", icon: LayoutDashboard, group: "Results" },
  { href: "/requisitions", label: "Requisitions", icon: ListChecks, group: "Results" },
  { href: "/quarantine", label: "Quarantine", icon: ShieldAlert, group: "Results" },
  { href: "/reconciliation", label: "Reconciliation", icon: ClipboardCheck, group: "Results" },
  { href: "/scenarios", label: "Scenario runner", icon: FlaskConical, group: "Advanced" },
  { href: "/control", label: "Control center", icon: Activity, group: "Advanced" },
  { href: "/failures", label: "Failure injection", icon: Zap, group: "Advanced" },
  { href: "/duplicates", label: "Duplicate detection", icon: Copy, group: "Advanced" },
  { href: "/assistant", label: "AI assistant", icon: Bot, group: "Advanced" },
  { href: "/requisitions/new", label: "New requisition", icon: FilePlus2, group: "Advanced" },
  { href: "/generator", label: "Data generator", icon: Database, group: "Advanced" },
  { href: "/import", label: "Import", icon: FileSpreadsheet, group: "Advanced" },
  { href: "/http", label: "HTTP log", icon: Network, group: "Advanced" },
];

export const NAV_GROUPS: NavItem["group"][] = ["Start here", "Results", "Advanced"];

export function isActive(pathname: string, href: string): boolean {
  if (href === "/") return pathname === "/";
  if (href === "/requisitions") return pathname === "/requisitions" || (pathname.startsWith("/requisitions/") && !pathname.startsWith("/requisitions/new"));
  return pathname === href || pathname.startsWith(`${href}/`);
}
