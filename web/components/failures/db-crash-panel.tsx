"use client";

import { useState } from "react";
import { Bomb, Database } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { ActionStatus } from "@/components/action-status";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk } from "@/lib/api";

type DbKind = "SaveFails" | "DatabaseLocked" | "Crash";
type DbMatch = "mapping" | "checkpoint" | "sending";
type CrashPoint = "after-sap-create" | "mid-page";

const DB_KIND_HELP: Record<DbKind, string> = {
  SaveFails: "The save throws: the integration must not lose track of what SAP already holds.",
  DatabaseLocked: "SQLite reports the database is locked (busy).",
  Crash: "The process dies during the save: the next run must recover.",
};
const MATCH_HELP: Record<DbMatch, string> = {
  mapping: "saving the SAP number for a requisition",
  checkpoint: "saving the source page checkpoint",
  sending: "marking a record as being sent",
};
const CRASH_HELP: Record<CrashPoint, string> = {
  "after-sap-create": "SAP created the requisition, then the process dies before the mapping is saved. The next run must link it, not create it again.",
  "mid-page": "The process dies while saving a page checkpoint. The next run must resume without skipping records.",
};

export function DbCrashPanel() {
  const [kind, setKind] = useState<DbKind>("SaveFails");
  const [match, setMatch] = useState<DbMatch>("mapping");
  const [times, setTimes] = useState("1");
  const [point, setPoint] = useState<CrashPoint>("after-sap-create");
  const timesValid = /^\d+$/.test(times) && Number(times) >= 1 && Number(times) <= 1000;

  const db = useLabAction<{ kind: DbKind; match: DbMatch; times: number }, { armed: string }>({
    name: "Arm database fault",
    run: (b) => api.dbFault(b),
    invalidate: [qk.workspace],
    confirm: async (r, b) => {
      const ws = await api.workspace();
      return `The lab armed "${r.armed}" (${b.times}×) and now lists ${ws.dbFaultsArmed?.length ?? 0} armed DB fault(s). It fires on the next matching save.`;
    },
  });
  const crash = useLabAction<CrashPoint, { armed: string }>({
    name: "Arm crash",
    run: (p) => api.crash(p),
    invalidate: [qk.workspace],
    confirm: async (r) => {
      const ws = await api.workspace();
      return ws.crashArmed ? `The lab reports the crash point "${ws.crashArmed}" armed. It fires during the next run.` : `The lab accepted "${r.armed}" but does not list it as armed (it may be a DB-level crash: see database faults).`;
    },
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Database faults and crashes</CardTitle>
        <CardDescription>Faults inside the integration itself, not in the mocks. They are one-shot unless Times says otherwise; Clear all removes them.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <form
          className="space-y-3"
          aria-label="Database fault"
          onSubmit={(e) => {
            e.preventDefault();
            if (timesValid) void db.execute({ kind, match, times: Number(times) });
          }}
        >
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="space-y-1">
              <Label htmlFor="db-kind">Kind</Label>
              <NativeSelect id="db-kind" value={kind} onChange={(e) => setKind(e.target.value as DbKind)}>
                <option value="SaveFails">SaveFails</option>
                <option value="DatabaseLocked">DatabaseLocked</option>
                <option value="Crash">Crash</option>
              </NativeSelect>
            </div>
            <div className="space-y-1">
              <Label htmlFor="db-match">When saving</Label>
              <NativeSelect id="db-match" value={match} onChange={(e) => setMatch(e.target.value as DbMatch)}>
                <option value="mapping">mapping</option>
                <option value="checkpoint">checkpoint</option>
                <option value="sending">sending</option>
              </NativeSelect>
            </div>
            <div className="space-y-1">
              <Label htmlFor="db-times">Times</Label>
              <Input id="db-times" inputMode="numeric" value={times} onChange={(e) => setTimes(e.target.value)} aria-invalid={!timesValid} />
              {!timesValid ? <p className="text-xs text-destructive">Times must be a whole number from 1 to 1000</p> : null}
            </div>
          </div>
          <p className="text-xs text-muted-foreground">
            {DB_KIND_HELP[kind]} Fires when {MATCH_HELP[match]}.
          </p>
          <Button type="submit" variant="outline" disabled={db.pending || !timesValid}>
            <Database /> Arm database fault
          </Button>
          <ActionStatus phase={db.phase} message={db.message} />
        </form>
        <form
          className="space-y-3 border-t pt-4"
          aria-label="Crash point"
          onSubmit={(e) => {
            e.preventDefault();
            void crash.execute(point);
          }}
        >
          <div className="space-y-1 sm:max-w-xs">
            <Label htmlFor="crash-point">Crash point</Label>
            <NativeSelect id="crash-point" value={point} onChange={(e) => setPoint(e.target.value as CrashPoint)}>
              <option value="after-sap-create">after-sap-create</option>
              <option value="mid-page">mid-page</option>
            </NativeSelect>
          </div>
          <p className="text-xs text-muted-foreground">{CRASH_HELP[point]}</p>
          <Button type="submit" variant="outline" disabled={crash.pending}>
            <Bomb /> Arm crash
          </Button>
          <ActionStatus phase={crash.phase} message={crash.message} />
        </form>
      </CardContent>
    </Card>
  );
}
