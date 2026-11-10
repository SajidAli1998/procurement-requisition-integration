"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { FlaskConical, Menu, ServerCrash } from "lucide-react";
import { Sheet, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { IsolatedBadge } from "@/components/isolated-badge";
import { ThemeToggle } from "@/components/theme-toggle";
import { useLiveStatus } from "@/components/live-provider";
import { NAV, NAV_GROUPS, isActive } from "@/components/nav";
import { api, qk } from "@/lib/api";
import { cn } from "@/lib/utils";

function NavLinks({ onNavigate }: { onNavigate?: () => void }) {
  const pathname = usePathname() ?? "/";
  return (
    <nav aria-label="Main" className="flex flex-col gap-4">
      {NAV_GROUPS.map((group) => (
        <div key={group}>
          <div className="px-3 pb-1 text-[11px] font-semibold uppercase tracking-wider text-muted-foreground">{group}</div>
          <ul className="flex flex-col gap-0.5">
            {NAV.filter((n) => n.group === group).map((n) => {
              const active = isActive(pathname, n.href);
              const Icon = n.icon;
              return (
                <li key={n.href}>
                  <Link
                    href={n.href}
                    onClick={onNavigate}
                    aria-current={active ? "page" : undefined}
                    className={cn(
                      "flex items-center gap-2.5 rounded-md px-3 py-1.5 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                      active ? "bg-primary/10 font-medium text-primary" : "text-foreground/80 hover:bg-accent hover:text-accent-foreground",
                    )}
                  >
                    <Icon className="size-4 shrink-0" aria-hidden />
                    {n.label}
                  </Link>
                </li>
              );
            })}
          </ul>
        </div>
      ))}
    </nav>
  );
}

function LiveIndicator() {
  const status = useLiveStatus();
  const label = status === "open" ? "Live" : status === "connecting" ? "Connecting" : "Offline";
  return (
    <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground" title={`Live event stream: ${status}`} data-testid="live-indicator" data-status={status}>
      <span className={cn("size-2 rounded-full", status === "open" ? "bg-emerald-500" : status === "connecting" ? "bg-amber-500" : "bg-red-500")} aria-hidden />
      {label}
    </span>
  );
}

function ApiBanner() {
  const health = useQuery({ queryKey: qk.health, queryFn: api.health, refetchInterval: 15_000, retry: 0 });
  if (!health.isError) return null;
  return (
    <div role="alert" data-testid="api-banner" className="flex items-center gap-2 border-b border-red-600/30 bg-red-50 px-4 py-2 text-sm text-red-900 dark:bg-red-950/50 dark:text-red-100">
      <ServerCrash className="size-4 shrink-0" aria-hidden />
      <span className="min-w-0 break-anywhere">
        <strong>Test Lab API unavailable.</strong> Data on this page may be stale and actions are not confirmed until it answers again.
      </span>
      <Button size="sm" variant="outline" className="ml-auto" onClick={() => void health.refetch()}>
        Retry
      </Button>
    </div>
  );
}

function DatasetBadge() {
  const ws = useQuery({ queryKey: qk.workspace, queryFn: api.workspace, refetchInterval: 20_000 });
  if (!ws.data) return null;
  return (
    <Badge tone="neutral" className="hidden sm:inline-flex" title="Dataset loaded in the lab workspace">
      Dataset: {ws.data.dataset}
    </Badge>
  );
}

export function AppShell({ children }: { children: React.ReactNode }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="flex min-h-dvh">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:left-2 focus:top-2 focus:z-[60] focus:rounded focus:bg-background focus:px-3 focus:py-2 focus:shadow">
        Skip to content
      </a>
      <aside className="sticky top-0 hidden h-dvh w-60 shrink-0 flex-col gap-4 overflow-y-auto border-r bg-sidebar px-2 py-4 lg:flex">
        <Link href="/" className="flex items-center gap-2 px-3 text-sm font-semibold">
          <FlaskConical className="size-5 text-primary" aria-hidden />
          Procurement Test Lab
        </Link>
        <NavLinks />
      </aside>
      <Sheet open={open} onOpenChange={setOpen}>
        <SheetContent side="left" className="bg-sidebar">
          <SheetTitle className="px-3">Procurement Test Lab</SheetTitle>
          <SheetDescription className="sr-only">Navigation</SheetDescription>
          <NavLinks onNavigate={() => setOpen(false)} />
        </SheetContent>
      </Sheet>
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-40 flex h-14 items-center gap-2 border-b bg-background/95 px-3 backdrop-blur sm:px-4">
          <Button variant="ghost" size="icon" className="lg:hidden" aria-label="Open navigation" onClick={() => setOpen(true)}>
            <Menu />
          </Button>
          <span className="truncate text-sm font-semibold lg:hidden">Test Lab</span>
          <div className="ml-auto flex items-center gap-2 sm:gap-3">
            <DatasetBadge />
            <LiveIndicator />
            <IsolatedBadge />
            <ThemeToggle />
          </div>
        </header>
        <ApiBanner />
        <main id="main" className="min-w-0 flex-1 px-4 py-4 sm:px-6 sm:py-6">
          {children}
        </main>
      </div>
    </div>
  );
}
