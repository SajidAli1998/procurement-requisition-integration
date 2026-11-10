import type { BatchResult, Mode, Priority, ScenarioDefinition, ScenarioResult, ScenarioStatus, Tally } from "./types";

export const PRIORITIES: Priority[] = ["Critical", "High", "Medium", "Low"];
export const STATUSES: ScenarioStatus[] = ["Pass", "Fail", "Blocked", "NotImplemented", "NotRun"];

interface ScenarioFilter {
  search?: string;
  category?: string | null;
  priorities?: Priority[];
  severities?: string[];
  statuses?: ScenarioStatus[];
  modes?: Mode[];
}

export function effectiveStatus(s: ScenarioDefinition, latest?: Pick<ScenarioResult, "status"> | null): ScenarioStatus {
  if (latest) return latest.status;
  if (!s.implemented) return "NotImplemented";
  if (s.blockedReason) return "Blocked";
  return "NotRun";
}

function matchesSearch(s: ScenarioDefinition, q: string): boolean {
  const needle = q.trim().toLowerCase();
  if (!needle) return true;
  const hay = [s.id, s.title, s.description, s.category, s.fixture, s.faults, s.expected?.summary, s.severity, ...(s.steps ?? [])]
    .filter(Boolean)
    .join("\n")
    .toLowerCase();
  return needle.split(/\s+/).every((t) => hay.includes(t));
}

export function filterScenarios(
  scenarios: ScenarioDefinition[],
  filter: ScenarioFilter,
  latest: Record<string, Pick<ScenarioResult, "status">> = {},
): ScenarioDefinition[] {
  return scenarios.filter((s) => {
    if (filter.category && s.category !== filter.category) return false;
    if (filter.priorities?.length && !filter.priorities.includes(s.priority)) return false;
    if (filter.severities?.length && !filter.severities.includes(s.severity)) return false;
    if (filter.modes?.length && !s.modes.some((m) => filter.modes!.includes(m))) return false;
    if (filter.statuses?.length && !filter.statuses.includes(effectiveStatus(s, latest[s.id]))) return false;
    if (filter.search && !matchesSearch(s, filter.search)) return false;
    return true;
  });
}

export interface StatusChange {
  id: string;
  title: string;
  previous: ScenarioStatus | null;
  current: ScenarioStatus | null;
  kind: "fixed" | "regressed" | "changed" | "new" | "removed" | "same";
}

export function compareBatches(current: BatchResult, previous: BatchResult): StatusChange[] {
  const prev = new Map(previous.results.map((r) => [r.id, r]));
  const cur = new Map(current.results.map((r) => [r.id, r]));
  const ids = [...new Set([...cur.keys(), ...prev.keys()])].sort();
  return ids.map((id) => {
    const c = cur.get(id);
    const p = prev.get(id);
    const title = c?.title ?? p?.title ?? id;
    let kind: StatusChange["kind"];
    if (!p) kind = "new";
    else if (!c) kind = "removed";
    else if (p.status === c.status) kind = "same";
    else if (c.status === "Pass" && p.status === "Fail") kind = "fixed";
    else if (c.status === "Fail" && p.status === "Pass") kind = "regressed";
    else kind = "changed";
    return { id, title, previous: p?.status ?? null, current: c?.status ?? null, kind };
  });
}

export function emptyTally(): Tally {
  return { total: 0, executed: 0, passed: 0, failed: 0, blocked: 0, notImplemented: 0, notRun: 0 };
}

export function tallyBy(
  scenarios: ScenarioDefinition[],
  key: (s: ScenarioDefinition) => string[],
  latest: Record<string, Pick<ScenarioResult, "status">>,
): Record<string, Tally> {
  const out: Record<string, Tally> = {};
  for (const s of scenarios) {
    const status = effectiveStatus(s, latest[s.id]);
    for (const k of key(s)) {
      const t = (out[k] ??= emptyTally());
      t.total++;
      if (status === "Pass") {
        t.passed++;
        t.executed++;
      } else if (status === "Fail") {
        t.failed++;
        t.executed++;
      } else if (status === "Blocked") t.blocked++;
      else if (status === "NotImplemented") t.notImplemented++;
      else t.notRun++;
    }
  }
  return out;
}

export interface RunConfiguration {
  kind: "procurement-test-lab/run-configuration";
  version: 1;
  name: string;
  ids: string[];
  parallelism: number;
  seed?: number;
  createdAt: string;
  catalogueSize: number;
}

export function parseRunConfiguration(text: string): RunConfiguration {
  const v = JSON.parse(text) as Partial<RunConfiguration>;
  if (v.kind !== "procurement-test-lab/run-configuration" || !Array.isArray(v.ids) || v.ids.some((x) => typeof x !== "string")) {
    throw new Error("Not a run configuration file (expected kind procurement-test-lab/run-configuration with an ids list)");
  }
  return {
    kind: v.kind,
    version: 1,
    name: typeof v.name === "string" ? v.name : "Saved configuration",
    ids: v.ids,
    parallelism: typeof v.parallelism === "number" && v.parallelism > 0 ? Math.min(16, Math.floor(v.parallelism)) : 4,
    seed: typeof v.seed === "number" ? v.seed : undefined,
    createdAt: typeof v.createdAt === "string" ? v.createdAt : new Date().toISOString(),
    catalogueSize: typeof v.catalogueSize === "number" ? v.catalogueSize : 0,
  };
}
