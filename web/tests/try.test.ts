import { describe as suite, expect, it } from "vitest";
import { collapse, describe } from "@/components/try/activity";
import { describeLastRun, lastFinishedRun } from "@/components/try/last-run";
import type { HttpExchange, WorkspaceRun } from "@/lib/types";

const ex = (p: Partial<HttpExchange>): HttpExchange => ({ seq: 1, at: "2026-11-11T10:00:00Z", channel: "integration→sap", host: "sap.lab", method: "POST", url: "/x/A_PurchaseRequisitionHeader", durationMs: 1, ...p });

suite("activity feed", () => {
  it("explains a create, a lost reply and a lookup in plain words", () => {
    expect(describe(ex({ status: 201, requestBody: '{"ExternalReference":"REQ-1"}' }))?.text).toBe("REQ-1 created in SAP");
    expect(describe(ex({ status: null, requestBody: '{"ExternalReference":"REQ-1"}' }))?.text).toContain("no answer came back");
    expect(describe(ex({ method: "GET", url: "/x?$filter=ExternalReference%20eq%20'REQ-2'", status: 200, responseBody: '{"value":[{}]}' }))?.text).toContain("already there");
    expect(describe(ex({ method: "GET", url: "/x?$filter=ExternalReference%20eq%20'REQ-2'", status: 200, responseBody: '{"value":[]}' }))?.text).toContain("not there yet");
  });

  it("merges repeated lines", () => {
    const a = { seq: 1, at: "", text: "same", tone: "plain" as const };
    expect(collapse([a, { ...a, seq: 2 }, { ...a, seq: 3, text: "other" }]).map((l) => l.times)).toEqual([2, 1]);
  });
});

const run = (p: Partial<WorkspaceRun>): WorkspaceRun => ({
  id: "r1", kind: "Sync", startedAt: "2026-11-11T12:15:57Z", finishedAt: "2026-11-11T12:15:59Z", state: "Finished", outcome: "Completed",
  message: null, durationMs: 1600, events: {}, transport: null, pending: 0, openQuarantine: 0, retryWaitSeconds: null, steps: [], ...p,
});
const at = () => "12:15:57";

suite("last run", () => {
  it("says when there was nothing new to send", () => {
    expect(describeLastRun(run({ events: { Created: 0 } }), at)).toBe(
      "Last run at 12:15:57 (1.6 s): nothing new to send. Everything was already in SAP, so nothing was created or changed.",
    );
  });

  it("lists only the non-zero counts, with correct plurals", () => {
    expect(describeLastRun(run({ durationMs: 8200, outcome: "CompletedWithIssues", events: { Created: 13, AlreadyInSap: 1, InDoubtResolved: 1, RetryScheduled: 48, Quarantined: 0 } }), at)).toBe(
      "Last run at 12:15:57 (8.2 s): 13 created, 2 linked to documents already in SAP, 48 waiting for a retry.",
    );
    expect(describeLastRun(run({ events: { Created: 1, Amended: 1, AlreadyInSap: 1, Quarantined: 1, RetryScheduled: 1 } }), at)).toBe(
      "Last run at 12:15:57 (1.6 s): 1 created, 1 updated, 1 linked to a document already in SAP, 1 held back for bad data, 1 waiting for a retry.",
    );
  });

  it("explains a run that stopped early", () => {
    expect(describeLastRun(run({ outcome: "Crashed", message: "Process killed at SapCreate", events: { Created: 3 } }), at)).toBe(
      "Last run at 12:15:57 (1.6 s): 3 created. It stopped early: Process killed at SapCreate.",
    );
    expect(describeLastRun(run({ outcome: "Stopped", message: "", events: { Created: 2 } }), at)).toBe("Last run at 12:15:57 (1.6 s): 2 created. It stopped early.");
  });

  it("uses the newest finished run and ignores one still running", () => {
    expect(lastFinishedRun([run({ id: "a", state: "Running" }), run({ id: "b" }), run({ id: "c" })])?.id).toBe("b");
    expect(lastFinishedRun([])).toBeNull();
    expect(lastFinishedRun(undefined)).toBeNull();
  });
});
