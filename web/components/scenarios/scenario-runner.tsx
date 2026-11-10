"use client";

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQueries, useQuery, type UseQueryResult } from "@tanstack/react-query";
import { ChevronDown, SlidersHorizontal } from "lucide-react";
import { PageHeader } from "@/components/page-header";
import { ErrorPanel } from "@/components/error-panel";
import { QueryState } from "@/components/query-state";
import { ActionStatus } from "@/components/action-status";
import { EmptyState } from "@/components/empty-state";
import { StatCard } from "@/components/stat-card";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { api, qk } from "@/lib/api";
import { downloadJson } from "@/lib/download";
import { emptyTally, filterScenarios, tallyBy, type RunConfiguration } from "@/lib/scenarios";
import type { BatchResult, Catalogue, LatestResult, Mode, RunScenariosRequest, ScenarioResult, Tally } from "@/lib/types";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { cn } from "@/lib/utils";
import { BatchProgress } from "./batch-progress";
import { HistoryPanel } from "./history-panel";
import { ModesView } from "./modes-view";
import { ResultDrawer } from "./result-drawer";
import { RunControls } from "./run-controls";
import { ScenarioCard, type CardResult } from "./scenario-card";
import { CategoryList, EMPTY_FILTER, ScenarioFilters, type FilterState } from "./scenario-filters";
import { useBatchRun } from "./use-batch-run";

interface Located {
  result: ScenarioResult;
  batchId: string;
  at: string;
}

// NotRun entries from a cancelled batch must never hide an earlier real result.
function locateResults(batches: BatchResult[], latest: Record<string, LatestResult>, catalogue: Catalogue): Record<string, Located> {
  const out: Record<string, Located> = {};
  const ordered = [...batches].sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1));
  for (const b of ordered)
    for (const r of b.results) if (!out[r.id] && r.status !== "NotRun") out[r.id] = { result: r, batchId: b.id, at: b.startedAt };
  const byId = new Map(catalogue.scenarios.map((s) => [s.id, s]));
  for (const [id, l] of Object.entries(latest)) {
    const current = out[id];
    if (current && current.at >= l.at) continue;
    if (current?.batchId === l.batchId) continue;
    const s = byId.get(id);
    out[id] = {
      batchId: l.batchId,
      at: l.at,
      result: {
        id,
        title: s?.title ?? id,
        category: s?.category ?? "",
        priority: s?.priority ?? "Medium",
        status: l.status,
        startedAt: l.at,
        durationMs: l.durationMs ?? 0,
        expected: s?.expected.summary ?? "",
        actual: l.actual ?? "",
        log: [],
        assertions: [],
        invariants: [],
        runs: [],
        http: [],
        evidence: {},
        seed: 0,
      },
    };
  }
  return out;
}

function sumTally(ts: Tally[]): Tally {
  return ts.reduce(
    (a, t) => ({
      total: a.total + t.total,
      executed: a.executed + t.executed,
      passed: a.passed + t.passed,
      failed: a.failed + t.failed,
      blocked: a.blocked + t.blocked,
      notImplemented: a.notImplemented + t.notImplemented,
      notRun: a.notRun + t.notRun,
    }),
    emptyTally(),
  );
}

export function ScenarioRunner() {
  const catalogue = useQuery({ queryKey: qk.catalogue, queryFn: api.catalogue, staleTime: 60_000 });
  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        title="Scenario test runner"
        description="Run the scenario catalogue against fresh, isolated labs. Every Pass or Fail shown here comes from a backend result; blocked and not-implemented scenarios are never counted as passed."
      />
      <QueryState
        query={catalogue}
        title="Could not load the scenario catalogue"
        skeleton={
          <div className="grid gap-3 lg:grid-cols-[16rem_1fr]">
            <Skeleton className="h-96" />
            <div className="space-y-2">
              <Skeleton className="h-24" />
              <Skeleton className="h-24" />
              <Skeleton className="h-24" />
            </div>
          </div>
        }
      >
        {(cat) => <RunnerBody catalogue={cat} />}
      </QueryState>
    </div>
  );
}

// Module-level so useQueries can keep the combined result stable between renders.
function withLoadedBatches(results: UseQueryResult<BatchResult>[]) {
  return { results, loadedBatches: results.map((q) => q.data).filter((b): b is BatchResult => !!b) };
}

function RunnerBody({ catalogue }: { catalogue: Catalogue }) {
  const batches = useQuery({ queryKey: qk.batches, queryFn: api.batches, refetchInterval: 10_000 });
  const history = useMemo(() => [...(batches.data?.history ?? [])].sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1)), [batches.data]);

  const [selectedBatchId, setSelectedBatchId] = useState<string | null>(null);
  const [sessionIds, setSessionIds] = useState<string[]>([]);
  const viewingId = selectedBatchId ?? history[0]?.id ?? null;
  const loadedIds = useMemo(() => [...new Set([viewingId, ...sessionIds].filter((x): x is string => !!x))], [viewingId, sessionIds]);
  // Summaries only (no HTTP logs or snapshots): full results are fetched one at a time for the detail drawer.
  const batchQueries = useQueries({
    queries: loadedIds.map((id) => ({ queryKey: qk.batchSummary(id), queryFn: () => api.batchSummary(id), staleTime: Infinity })),
    combine: withLoadedBatches,
  });
  const latestQuery = useQuery({ queryKey: qk.scenariosLatest, queryFn: api.scenariosLatest, refetchInterval: 15_000 });
  const { loadedBatches } = batchQueries;
  const latestData = latestQuery.data;
  const located = useMemo(() => locateResults(loadedBatches, latestData ?? {}, catalogue), [loadedBatches, latestData, catalogue]);
  const viewing = loadedBatches.find((b) => b.id === viewingId);
  const viewingQuery = batchQueries.results[loadedIds.indexOf(viewingId ?? "")];

  const onSaved = useCallback((id: string) => {
    setSessionIds((prev) => (prev.includes(id) ? prev : [...prev, id]));
    setSelectedBatchId(id);
  }, []);
  const { live, stream, start, cancel, attach, dismiss, running } = useBatchRun(onSaved);

  // After a refresh, pick up a batch that is still running on the server.
  const attached = useRef(false);
  useEffect(() => {
    const active = batches.data?.active?.[0];
    if (!attached.current && active && !live) {
      attached.current = true;
      attach({ id: active.id, name: active.name, total: active.total }, active.tally ?? null);
    }
  }, [batches.data, live, attach]);

  const savedLoaded = !!live?.savedId && loadedBatches.some((b) => b.id === live.savedId);
  const liveApplies = !!live && !savedLoaded;

  const [filter, setFilter] = useState<FilterState>(EMPTY_FILTER);
  const [selection, setSelection] = useState<Set<string>>(new Set());
  const [parallelism, setParallelism] = useState(4);
  const [name, setName] = useState("");
  const [openId, setOpenId] = useState<string | null>(null);
  const [loadedConfig, setLoadedConfig] = useState<RunConfiguration | null>(null);
  const [filtersOpen, setFiltersOpen] = useState(false);
  const [seedText, setSeedText] = useState("");
  const seedValue = /^\d+$/.test(seedText.trim()) ? Number(seedText.trim()) : undefined;
  const seedInvalid = seedText.trim() !== "" && seedValue === undefined;

  const statusMap = useMemo(() => Object.fromEntries(Object.entries(located).map(([id, l]) => [id, { status: l.result.status }])), [located]);
  const visible = useMemo(
    () => filterScenarios(catalogue.scenarios, { ...filter, category: filter.category }, statusMap),
    [catalogue.scenarios, filter, statusMap],
  );
  const categoryCounts = useMemo(() => {
    const c: Record<string, number> = {};
    for (const s of catalogue.scenarios) c[s.category] = (c[s.category] ?? 0) + 1;
    return c;
  }, [catalogue.scenarios]);
  const severities = useMemo(() => [...new Set(catalogue.scenarios.map((s) => s.severity))].sort(), [catalogue.scenarios]);
  const knownIds = useMemo(() => new Set(catalogue.scenarios.map((s) => s.id)), [catalogue.scenarios]);

  const infoFor = useCallback(
    (id: string): CardResult => {
      const l = located[id];
      const liveStatus = liveApplies ? live?.statuses[id] : undefined;
      return { result: l?.result, batchId: l?.batchId, live: liveStatus, liveActual: liveApplies ? live?.actual[id] : undefined };
    },
    [located, live, liveApplies],
  );

  const runBusy = running;
  const run = useCallback(
    (req: RunScenariosRequest) => {
      if (seedInvalid) return;
      const seed = req.seed ?? seedValue;
      void start.execute({ ...req, name: req.name ?? (name.trim() || undefined), parallelism: req.parallelism ?? parallelism, ...(seed !== undefined ? { seed } : {}) });
    },
    [start, name, parallelism, seedValue, seedInvalid],
  );
  const runOne = useCallback((id: string) => run({ ids: [id], name: name.trim() || id }), [run, name]);
  const onSelect = useCallback((id: string, on: boolean) => {
    setSelection((prev) => {
      const next = new Set(prev);
      if (on) next.add(id);
      else next.delete(id);
      return next;
    });
  }, []);
  const onOpen = useCallback((id: string) => setOpenId(id), []);

  const allVisibleSelected = visible.length > 0 && visible.every((s) => selection.has(s.id));
  const someVisibleSelected = visible.some((s) => selection.has(s.id));
  const selectedIds = catalogue.scenarios.filter((s) => selection.has(s.id)).map((s) => s.id);

  const saveConfig = () => {
    const ids = selectedIds.length > 0 ? selectedIds : visible.map((s) => s.id);
    const seed = seedValue ?? ids.map((id) => located[id]?.result.seed).find((s) => typeof s === "number" && s > 0);
    const config: RunConfiguration = {
      kind: "procurement-test-lab/run-configuration",
      version: 1,
      name: name.trim() || (selectedIds.length > 0 ? `${ids.length} selected scenarios` : `Filtered: ${ids.length} scenarios`),
      ids,
      parallelism,
      seed,
      createdAt: new Date().toISOString(),
      catalogueSize: catalogue.scenarios.length,
    };
    downloadJson(config, `scenario-run-config-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.json`);
  };
  const loadConfig = (c: RunConfiguration) => {
    setLoadedConfig(c);
    setSelection(new Set(c.ids.filter((id) => knownIds.has(id))));
    setParallelism(c.parallelism);
    setName(c.name);
    setSeedText(c.seed !== undefined ? String(c.seed) : "");
  };
  const missingFromConfig = loadedConfig ? loadedConfig.ids.filter((id) => !knownIds.has(id)) : [];

  const catalogueByMode = useMemo(() => tallyBy(catalogue.scenarios, (s) => s.modes, statusMap), [catalogue.scenarios, statusMap]);
  const catalogueByCategory = useMemo(() => tallyBy(catalogue.scenarios, (s) => [s.category], statusMap), [catalogue.scenarios, statusMap]);
  const modesSource = viewing ? `batch ${viewing.name} (${viewing.id})` : "catalogue + latest loaded results";
  const byMode = viewing?.byMode ?? catalogueByMode;
  const byCategory = viewing?.byCategory ?? catalogueByCategory;
  const overall = viewing?.tally ?? sumTally(Object.values(catalogueByCategory));

  const catalogueTally = sumTally(Object.values(catalogueByCategory));
  const openBatchId = openId ? located[openId]?.batchId : undefined;
  const fullResult = useQuery({
    queryKey: qk.batchResult(openBatchId ?? "", openId ?? ""),
    queryFn: () => api.batchResult(openBatchId!, openId!),
    enabled: !!openId && !!openBatchId,
    staleTime: Infinity,
  });
  const openScenario = catalogue.scenarios.find((s) => s.id === openId) ?? null;

  const filters = (
    <div className="flex flex-col gap-4">
      <CategoryList categories={catalogue.categories} counts={categoryCounts} total={catalogue.scenarios.length} value={filter.category} onChange={(c) => setFilter((f) => ({ ...f, category: c }))} />
      <ScenarioFilters value={filter} onChange={setFilter} modes={catalogue.modes} severities={severities} />
    </div>
  );

  return (
    <>
      <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-6">
        <StatCard label="Scenarios" value={catalogue.scenarios.length} hint={`${catalogue.categories.length} categories`} />
        <StatCard label="Passed" value={catalogueTally.passed} tone="success" hint="latest loaded results" />
        <StatCard label="Failed" value={catalogueTally.failed} tone="danger" />
        <StatCard label="Blocked" value={catalogueTally.blocked} tone="warning" hint="not executed" />
        <StatCard label="Not implemented" value={catalogueTally.notImplemented} tone="muted" />
        <StatCard label="Not run" value={catalogueTally.notRun} tone="neutral" />
      </div>

      {start.phase !== "idle" && start.phase !== "confirmed" ? <ActionStatus phase={start.phase} message={start.message} /> : null}
      {live ? (
        <BatchProgress live={live} stream={stream} onCancel={() => void cancel.execute(live.id)} cancelPhase={cancel.phase} cancelMessage={cancel.message} onDismiss={dismiss} />
      ) : null}
      {batches.isError ? <ErrorPanel error={batches.error} title="Could not load batch history" onRetry={() => void batches.refetch()} compact /> : null}

      <Tabs defaultValue="scenarios">
        <TabsList aria-label="Runner views">
          <TabsTrigger value="scenarios">Scenarios</TabsTrigger>
          <TabsTrigger value="modes">Execution modes</TabsTrigger>
          <TabsTrigger value="history">History{history.length ? ` (${history.length})` : ""}</TabsTrigger>
        </TabsList>

        <TabsContent value="scenarios">
          <div className="grid gap-4 lg:grid-cols-[16rem_minmax(0,1fr)]">
            <aside className="hidden lg:block" aria-label="Filters">
              <div className="sticky top-16">{filters}</div>
            </aside>
            <Collapsible open={filtersOpen} onOpenChange={setFiltersOpen} className="rounded-md border lg:hidden">
              <CollapsibleTrigger className="flex w-full items-center gap-2 px-3 py-2 text-sm font-medium">
                <SlidersHorizontal className="size-4" aria-hidden /> Categories &amp; filters
                {filter.category ? <Badge tone="info" className="max-w-40 truncate">{filter.category}</Badge> : null}
                <ChevronDown className={cn("ml-auto size-4 transition-transform", filtersOpen && "rotate-180")} aria-hidden />
              </CollapsibleTrigger>
              <CollapsibleContent className="border-t p-3">{filters}</CollapsibleContent>
            </Collapsible>

            <div className="flex min-w-0 flex-col gap-3">
              <Card>
                <CardHeader>
                  <CardTitle>Run</CardTitle>
                  <CardDescription>Each scenario runs in its own fresh lab; the workspace used by the operations pages is not touched.</CardDescription>
                </CardHeader>
                <CardContent>
                  <RunControls
                    selectedCount={selectedIds.length}
                    category={filter.category}
                    modes={catalogue.modes}
                    disabled={runBusy}
                    parallelism={parallelism}
                    onParallelism={setParallelism}
                    name={name}
                    onName={setName}
                    onRunSelected={() => run({ ids: selectedIds, name: name.trim() || (selectedIds.length <= 5 ? selectedIds.join(" ") : `${selectedIds.length} selected scenarios`) })}
                    onRunCategory={() => filter.category && run({ category: filter.category })}
                    onRunMode={(m: Mode) => run({ mode: m })}
                    onRunAll={() => run({ all: true, name: name.trim() || "Full regression" })}
                    onSaveConfig={saveConfig}
                    onLoadConfig={loadConfig}
                    loadedConfig={loadedConfig}
                    onRunConfig={() => loadedConfig && run({ ids: loadedConfig.ids.filter((id) => knownIds.has(id)), name: loadedConfig.name, parallelism: loadedConfig.parallelism, seed: loadedConfig.seed })}
                  />
                  <div className="mt-3 flex flex-wrap items-end gap-2">
                    <div className="grid gap-1">
                      <Label htmlFor="run-seed">Seed (optional)</Label>
                      <Input id="run-seed" inputMode="numeric" value={seedText} onChange={(e) => setSeedText(e.target.value)} placeholder="each scenario's own" className="w-40" aria-invalid={seedInvalid} aria-describedby="run-seed-help" />
                    </div>
                    <p id="run-seed-help" className={cn("pb-2 text-xs", seedInvalid ? "text-destructive" : "text-muted-foreground")} role={seedInvalid ? "alert" : undefined}>
                      {seedInvalid ? "The seed must be a whole number." : "Overrides every scenario's lab seed, so the run can be reproduced exactly. Saved with the configuration."}
                    </p>
                  </div>
                  {missingFromConfig.length > 0 ? (
                    <p className="mt-2 text-xs text-amber-800 dark:text-amber-300 break-anywhere">
                      Not in the current catalogue (skipped): {missingFromConfig.join(", ")}
                    </p>
                  ) : null}
                </CardContent>
              </Card>

              <div className="flex flex-wrap items-center gap-3 text-sm">
                <label className="flex items-center gap-2">
                  <Checkbox
                    checked={allVisibleSelected ? true : someVisibleSelected ? "indeterminate" : false}
                    onCheckedChange={(v) =>
                      setSelection((prev) => {
                        const next = new Set(prev);
                        for (const s of visible) {
                          if (v === true) next.add(s.id);
                          else next.delete(s.id);
                        }
                        return next;
                      })
                    }
                    aria-label="Select all visible scenarios"
                  />
                  Select all visible
                </label>
                <span className="text-muted-foreground" aria-live="polite" data-testid="scenario-count">
                  {visible.length} of {catalogue.scenarios.length} shown · {selectedIds.length} selected
                </span>
                {selectedIds.length > 0 ? (
                  <Button variant="ghost" size="sm" onClick={() => setSelection(new Set())}>
                    Clear selection
                  </Button>
                ) : null}
                <span className="ml-auto text-xs text-muted-foreground">
                  {viewingQuery?.isPending
                    ? "Loading results..."
                    : viewing
                      ? `Results: batch ${viewing.name} (${viewing.id})${loadedBatches.length > 1 ? ` + ${loadedBatches.length - 1} more from this session` : ""}`
                      : "No saved batch results yet"}
                </span>
              </div>
              {viewingQuery?.isError ? <ErrorPanel error={viewingQuery.error} title="Could not load the batch results" onRetry={() => void viewingQuery.refetch()} compact /> : null}

              {visible.length === 0 ? (
                <EmptyState title="No scenarios match" description="Change the search, category or filters." />
              ) : (
                <div className="grid gap-2 xl:grid-cols-2" data-testid="scenario-list">
                  {visible.map((s) => (
                    <ScenarioCard key={s.id} scenario={s} info={infoFor(s.id)} selected={selection.has(s.id)} onSelect={onSelect} onOpen={onOpen} onRun={runOne} runDisabled={runBusy} />
                  ))}
                </div>
              )}
            </div>
          </div>
        </TabsContent>

        <TabsContent value="modes">
          <ModesView
            modes={catalogue.modes}
            byMode={byMode}
            overall={overall}
            byCategory={byCategory}
            source={modesSource}
            onRunMode={(m) => run({ mode: m })}
            onRunAll={() => run({ all: true, name: name.trim() || "Full regression" })}
            disabled={runBusy}
          />
        </TabsContent>

        <TabsContent value="history">
          {batches.isPending ? (
            <Skeleton className="h-40" />
          ) : (
            <HistoryPanel history={history} selectedId={viewingId} onSelect={setSelectedBatchId} selected={viewing} />
          )}
        </TabsContent>
      </Tabs>

      <ResultDrawer
        scenario={openScenario}
        result={openId ? (fullResult.data ?? located[openId]?.result) : undefined}
        batchId={openId ? located[openId]?.batchId : undefined}
        invariants={catalogue.invariants}
        open={!!openId}
        onOpenChange={(v) => !v && setOpenId(null)}
        onRun={runOne}
        runDisabled={runBusy}
      />
    </>
  );
}
