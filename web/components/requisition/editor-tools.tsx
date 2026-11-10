"use client";

import { useMemo, useState, useSyncExternalStore } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, Download, FileJson, GitCompare, Trash2, Upload } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { api, qk } from "@/lib/api";
import { fmtDateTime } from "@/lib/format";
import type { Template } from "@/lib/requisition-form";
import { parseFixtures, readFixturesText, storeFixtures, subscribeFixtures, type SavedFixture } from "./fixtures";

export function TemplatesMenu({ templates, onPick }: { templates: Template[]; onPick: (t: Template) => void }) {
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="outline" size="sm" data-testid="templates-menu">
          <FileJson /> Templates <ChevronDown />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-80">
        <DropdownMenuLabel>Load a template (replaces the form)</DropdownMenuLabel>
        <DropdownMenuSeparator />
        {templates.map((t) => (
          <DropdownMenuItem key={t.id} onSelect={() => onPick(t)} data-template={t.id} className="flex-col items-start gap-0.5">
            <span className="font-medium">{t.name}</span>
            <span className="text-xs text-muted-foreground">{t.description}</span>
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

export function GeneratorTools({
  busy,
  onClone,
  onDuplicate,
  onConflict,
  lockedId,
}: {
  busy: boolean;
  onClone: (id: string) => void;
  onDuplicate: (id: string) => void;
  onConflict: (id: string) => void;
  lockedId?: string;
}) {
  const [cloneId, setCloneId] = useState("");
  const [existing, setExisting] = useState("");
  const rows = useQuery({ queryKey: qk.requisitions, queryFn: api.requisitions, enabled: !lockedId });
  const ids = (rows.data ?? []).map((r) => r.id);
  const selected = existing || ids[0] || "";

  if (lockedId) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Generate</CardTitle>
          <CardDescription>Builds test variants of {lockedId} from the lab source.</CardDescription>
        </CardHeader>
        <CardContent>
          <Button variant="outline" size="sm" disabled={busy} onClick={() => onConflict(lockedId)}>
            Generate conflicting record (same version, different amount)
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Clone and generate</CardTitle>
        <CardDescription>Start from a record that is in the lab source now.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (cloneId.trim()) onClone(cloneId.trim());
          }}
        >
          <div className="flex min-w-40 flex-1 flex-col gap-1.5">
            <Label htmlFor="clone-id">Existing ID to clone</Label>
            <Input id="clone-id" value={cloneId} onChange={(e) => setCloneId(e.target.value)} placeholder="REQ-7001" list="testlab-existing-ids" autoComplete="off" />
          </div>
          <Button type="submit" variant="outline" size="sm" disabled={busy || !cloneId.trim()}>
            Clone existing
          </Button>
        </form>
        <datalist id="testlab-existing-ids">
          {ids.slice(0, 500).map((id) => (
            <option key={id} value={id} />
          ))}
        </datalist>
        <div className="flex flex-wrap items-end gap-2">
          <div className="flex min-w-40 flex-1 flex-col gap-1.5">
            <Label htmlFor="existing-id">Existing requisition</Label>
            <NativeSelect id="existing-id" value={selected} onChange={(e) => setExisting(e.target.value)} disabled={ids.length === 0}>
              {ids.length === 0 ? <option value="">{rows.isPending ? "Loading..." : "No requisitions in the lab"}</option> : null}
              {ids.map((id) => (
                <option key={id} value={id}>
                  {id}
                </option>
              ))}
            </NativeSelect>
          </div>
          <Button variant="outline" size="sm" disabled={busy || !selected} onClick={() => onDuplicate(selected)}>
            Generate duplicate ID
          </Button>
          <Button variant="outline" size="sm" disabled={busy || !selected} onClick={() => onConflict(selected)}>
            Generate conflicting record
          </Button>
        </div>
        <p className="text-xs text-muted-foreground">
          Duplicate ID resends the stored record unchanged under the same ID. Conflicting record keeps the ID and version but changes the amount, which the integration must never
          silently overwrite in SAP.
        </p>
      </CardContent>
    </Card>
  );
}

export function FixturesPanel({ currentName, currentText, onLoad }: { currentName: string; currentText: () => string; onLoad: (f: SavedFixture) => void }) {
  const stored = useSyncExternalStore(subscribeFixtures, readFixturesText, () => "");
  const list = useMemo(() => parseFixtures(stored), [stored]);
  const [note, setNote] = useState<string | null>(null);

  const save = async () => {
    const text = currentText();
    const name = currentName.trim() || "requisition";
    const { downloadText } = await import("@/lib/download");
    downloadText(text, `${name}.fixture.json`, "application/json");
    const next = [{ name, savedAt: new Date().toISOString(), raw: text }, ...list.filter((f) => f.name !== name)];
    setNote(storeFixtures(next) ? `Saved "${name}" in this browser and downloaded ${name}.fixture.json.` : "Downloaded, but this browser blocked local storage, so it is not in the list.");
  };

  const remove = (name: string) => {
    storeFixtures(list.filter((f) => f.name !== name));
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Fixtures</CardTitle>
        <CardDescription>Saved in this browser only; the JSON is exactly what the editor holds.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <Button variant="outline" size="sm" onClick={() => void save()}>
          <Download /> Save fixture
        </Button>
        {note ? (
          <p className="text-xs text-muted-foreground" role="status">
            {note}
          </p>
        ) : null}
        {list.length === 0 ? (
          <p className="text-xs text-muted-foreground">No saved fixtures.</p>
        ) : (
          <ul className="space-y-1" data-testid="fixtures-list">
            {list.map((f) => (
              <li key={f.name} className="flex items-center gap-2 rounded border px-2 py-1 text-sm">
                <span className="min-w-0 flex-1 truncate font-mono text-xs" title={f.name}>
                  {f.name}
                </span>
                <span className="hidden text-xs text-muted-foreground sm:inline">{fmtDateTime(f.savedAt)}</span>
                <Button variant="ghost" size="sm" onClick={() => onLoad(f)} aria-label={`Load fixture ${f.name}`}>
                  <Upload /> Load
                </Button>
                <Button variant="ghost" size="sm" onClick={() => remove(f.name)} aria-label={`Delete fixture ${f.name}`}>
                  <Trash2 />
                </Button>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

export function CompareButton({ enabled, open, onToggle }: { enabled: boolean; open: boolean; onToggle: () => void }) {
  return (
    <Button variant="outline" size="sm" onClick={onToggle} disabled={!enabled} aria-expanded={open} title={enabled ? undefined : "Load a record, template or clone first"}>
      <GitCompare /> {open ? "Hide comparison" : "Compare corrected vs original"}
    </Button>
  );
}
