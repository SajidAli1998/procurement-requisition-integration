"use client";

import { Search, X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { PRIORITIES, STATUSES } from "@/lib/scenarios";
import { scenarioStatus } from "@/lib/status";
import type { Mode, Priority, ScenarioStatus } from "@/lib/types";
import { cn } from "@/lib/utils";

export interface FilterState {
  search: string;
  category: string | null;
  priorities: Priority[];
  severities: string[];
  statuses: ScenarioStatus[];
  modes: Mode[];
}

export const EMPTY_FILTER: FilterState = { search: "", category: null, priorities: [], severities: [], statuses: [], modes: [] };

function toggle<T>(list: T[], v: T): T[] {
  return list.includes(v) ? list.filter((x) => x !== v) : [...list, v];
}

function Chips<T extends string>({ label, options, selected, onChange, display }: { label: string; options: readonly T[]; selected: T[]; onChange: (v: T[]) => void; display?: (v: T) => string }) {
  return (
    <fieldset className="min-w-0">
      <legend className="mb-1 text-xs font-medium text-muted-foreground">{label}</legend>
      <div className="flex flex-wrap gap-1">
        {options.map((o) => {
          const on = selected.includes(o);
          return (
            <button
              key={o}
              type="button"
              aria-pressed={on}
              onClick={() => onChange(toggle(selected, o))}
              className={cn(
                "rounded-md border px-2 py-0.5 text-xs transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                on ? "border-primary bg-primary text-primary-foreground" : "bg-background hover:bg-accent",
              )}
            >
              {display ? display(o) : o}
            </button>
          );
        })}
      </div>
    </fieldset>
  );
}

export function ScenarioFilters({ value, onChange, modes, severities }: { value: FilterState; onChange: (v: FilterState) => void; modes: Mode[]; severities: string[] }) {
  const active = value.search || value.priorities.length || value.severities.length || value.statuses.length || value.modes.length;
  return (
    <div className="flex flex-col gap-3">
      <div className="relative">
        <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
        <Input
          aria-label="Search scenarios"
          placeholder="Search id, title, steps"
          value={value.search}
          onChange={(e) => onChange({ ...value, search: e.target.value })}
          className="pl-8"
          data-testid="scenario-search"
        />
      </div>
      <Chips label="Priority" options={PRIORITIES} selected={value.priorities} onChange={(priorities) => onChange({ ...value, priorities })} />
      {severities.length > 1 ? <Chips label="Severity" options={severities} selected={value.severities} onChange={(sv) => onChange({ ...value, severities: sv })} /> : null}
      <Chips label="Status" options={STATUSES} selected={value.statuses} onChange={(statuses) => onChange({ ...value, statuses })} display={(s) => scenarioStatus(s).label} />
      <Chips label="Mode" options={modes} selected={value.modes} onChange={(m) => onChange({ ...value, modes: m })} />
      {active ? (
        <Button variant="ghost" size="sm" className="self-start" onClick={() => onChange({ ...EMPTY_FILTER, category: value.category })}>
          <X /> Clear filters
        </Button>
      ) : null}
    </div>
  );
}

export function CategoryList({ categories, counts, total, value, onChange }: { categories: string[]; counts: Record<string, number>; total: number; value: string | null; onChange: (c: string | null) => void }) {
  const item = (key: string | null, label: string, count: number) => {
    const on = value === key;
    return (
      <li key={key ?? "__all"}>
        <button
          type="button"
          aria-pressed={on}
          onClick={() => onChange(key)}
          className={cn(
            "flex w-full items-center justify-between gap-2 rounded-md px-2 py-1 text-left text-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
            on ? "bg-primary/10 font-medium text-primary" : "hover:bg-accent",
          )}
        >
          <span className="min-w-0 break-anywhere">{label}</span>
          <span className="shrink-0 text-xs tabular-nums text-muted-foreground">{count}</span>
        </button>
      </li>
    );
  };
  return (
    <nav aria-label="Scenario categories">
      <ul className="flex flex-col gap-0.5">
        {item(null, "All categories", total)}
        {categories.map((c) => item(c, c, counts[c] ?? 0))}
      </ul>
    </nav>
  );
}
