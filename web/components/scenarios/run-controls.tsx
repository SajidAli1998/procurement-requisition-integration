"use client";

import { useRef, useState } from "react";
import { FileDown, FileUp, Play, PlayCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { parseRunConfiguration, type RunConfiguration } from "@/lib/scenarios";
import type { Mode } from "@/lib/types";

export function RunControls({
  selectedCount,
  category,
  modes,
  disabled,
  parallelism,
  onParallelism,
  name,
  onName,
  onRunSelected,
  onRunCategory,
  onRunMode,
  onRunAll,
  onSaveConfig,
  onLoadConfig,
  loadedConfig,
  onRunConfig,
}: {
  selectedCount: number;
  category: string | null;
  modes: Mode[];
  disabled: boolean;
  parallelism: number;
  onParallelism: (n: number) => void;
  name: string;
  onName: (s: string) => void;
  onRunSelected: () => void;
  onRunCategory: () => void;
  onRunMode: (m: Mode) => void;
  onRunAll: () => void;
  onSaveConfig: () => void;
  onLoadConfig: (c: RunConfiguration) => void;
  loadedConfig: RunConfiguration | null;
  onRunConfig: () => void;
}) {
  const [mode, setMode] = useState<Mode>(modes[0] ?? "Functional");
  const [loadError, setLoadError] = useState<string | null>(null);
  const file = useRef<HTMLInputElement>(null);

  return (
    <div className="flex flex-col gap-3">
      <div className="grid gap-2 sm:grid-cols-[1fr_8rem]">
        <div className="flex flex-col gap-1">
          <Label htmlFor="batch-name">Batch name (optional)</Label>
          <Input id="batch-name" value={name} onChange={(e) => onName(e.target.value)} placeholder="Defaults to the selection" maxLength={120} />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="batch-parallelism">Parallelism</Label>
          <Input
            id="batch-parallelism"
            type="number"
            min={1}
            max={16}
            value={parallelism}
            onChange={(e) => onParallelism(Math.min(16, Math.max(1, Math.floor(Number(e.target.value) || 1))))}
          />
        </div>
      </div>
      <div className="flex flex-wrap items-end gap-2">
        <Button onClick={onRunSelected} disabled={disabled || selectedCount === 0} data-testid="run-selected">
          <Play /> Run selected ({selectedCount})
        </Button>
        <Button variant="outline" onClick={onRunCategory} disabled={disabled || !category} title={category ? `Run every scenario in ${category}` : "Pick a category first"}>
          <Play /> Run category
        </Button>
        <div className="flex items-end gap-1">
          <div className="flex flex-col gap-1">
            <Label htmlFor="run-mode">Mode</Label>
            <NativeSelect id="run-mode" value={mode} onChange={(e) => setMode(e.target.value as Mode)} className="w-44">
              {modes.map((m) => (
                <option key={m} value={m}>
                  {m}
                </option>
              ))}
            </NativeSelect>
          </div>
          <Button variant="outline" onClick={() => onRunMode(mode)} disabled={disabled} aria-label={`Run mode: ${mode}`}>
            <Play /> Run mode
          </Button>
        </div>
        <Button variant="secondary" onClick={onRunAll} disabled={disabled} data-testid="run-all">
          <PlayCircle /> Full regression
        </Button>
      </div>
      <div className="flex flex-wrap items-center gap-2 border-t pt-3">
        <Button size="sm" variant="outline" onClick={onSaveConfig} title="Download the selection, parallelism and seed as a reproducible run configuration">
          <FileDown /> Save configuration
        </Button>
        <Button size="sm" variant="outline" onClick={() => file.current?.click()}>
          <FileUp /> Load configuration
        </Button>
        <input
          ref={file}
          type="file"
          accept="application/json,.json"
          className="sr-only"
          aria-label="Run configuration file"
          onChange={async (e) => {
            const f = e.target.files?.[0];
            e.target.value = "";
            if (!f) return;
            try {
              onLoadConfig(parseRunConfiguration(await f.text()));
              setLoadError(null);
            } catch (err) {
              setLoadError(err instanceof Error ? err.message : String(err));
            }
          }}
        />
        {loadedConfig ? (
          <Button size="sm" onClick={onRunConfig} disabled={disabled}>
            <Play /> Re-run &ldquo;{loadedConfig.name}&rdquo; ({loadedConfig.ids.length})
          </Button>
        ) : null}
      </div>
      {loadError ? (
        <Alert variant="destructive">
          <AlertTitle>Could not load the configuration</AlertTitle>
          <AlertDescription>{loadError}</AlertDescription>
        </Alert>
      ) : null}
      {loadedConfig ? (
        <p className="text-xs text-muted-foreground break-anywhere">
          Loaded configuration from {loadedConfig.createdAt}: {loadedConfig.ids.length} scenarios, parallelism {loadedConfig.parallelism}
          {loadedConfig.seed !== undefined ? `, recorded seed ${loadedConfig.seed} (scenarios use their catalogue seed)` : ""}. Its selection has been applied.
        </p>
      ) : null}
    </div>
  );
}
