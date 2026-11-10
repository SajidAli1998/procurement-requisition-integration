import type { WorkspaceRun } from "@/lib/types";

const STOPPED_EARLY = new Set(["Crashed", "Failed", "Stopped"]);

function count(n: number, one: string, many: string): string {
  return `${n} ${n === 1 ? one : many}`;
}

export function lastFinishedRun(runs: WorkspaceRun[] | undefined): WorkspaceRun | null {
  return runs?.find((r) => r.state === "Finished") ?? null;
}

export function describeLastRun(run: WorkspaceRun, formatTime: (iso: string) => string = (iso) => new Date(iso).toLocaleTimeString()): string {
  const e = run.events ?? {};
  const n = (key: string) => e[key] ?? 0;
  const created = n("Created");
  const updated = n("Amended");
  const linked = n("AlreadyInSap") + n("InDoubtResolved");
  const heldBack = n("Quarantined");
  const retrying = n("RetryScheduled");

  const duration = run.durationMs === null ? "" : ` (${(run.durationMs / 1000).toFixed(1)} s)`;
  const head = `Last run at ${formatTime(run.startedAt)}${duration}:`;

  const parts = [
    created > 0 ? `${created} created` : null,
    updated > 0 ? `${updated} updated` : null,
    linked > 0 ? count(linked, "linked to a document already in SAP", "linked to documents already in SAP") : null,
    heldBack > 0 ? `${heldBack} held back for bad data` : null,
    retrying > 0 ? `${retrying} waiting for a retry` : null,
  ].filter((p) => p !== null);

  let sentence: string;
  if (parts.length > 0) sentence = `${head} ${parts.join(", ")}.`;
  else if (run.outcome === "Completed" || run.outcome === "CompletedWithIssues")
    sentence = `${head} nothing new to send. Everything was already in SAP, so nothing was created or changed.`;
  else sentence = `${head} nothing was sent.`;

  if (run.outcome && STOPPED_EARLY.has(run.outcome)) {
    const message = run.message?.trim().replace(/\.$/, "");
    sentence += message ? ` It stopped early: ${message}.` : " It stopped early.";
  }
  return sentence;
}
