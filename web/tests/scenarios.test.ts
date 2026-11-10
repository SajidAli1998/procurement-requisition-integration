import { describe, expect, it } from "vitest";
import { compareBatches, effectiveStatus, filterScenarios, parseRunConfiguration, tallyBy } from "@/lib/scenarios";
import type { BatchResult, ScenarioDefinition, ScenarioResult } from "@/lib/types";

function sc(p: Partial<ScenarioDefinition>): ScenarioDefinition {
  return {
    id: "X-1",
    category: "1. Valid requisitions",
    title: "t",
    description: "",
    priority: "Medium",
    severity: "Major",
    modes: ["Functional"],
    preconditions: "",
    fixture: "",
    faults: "None",
    steps: [],
    expected: { summary: "" },
    invariants: [],
    implemented: true,
    ...p,
  };
}

const catalogue = [
  sc({ id: "VAL-001", title: "Single valid approved requisition", priority: "Critical", modes: ["Functional", "Baseline"] }),
  sc({ id: "DUP-001", category: "2. Duplicates & idempotency", title: "Resend same id", priority: "High", modes: ["DuplicateStress"], severity: "Critical" }),
  sc({ id: "SEC-009", category: "13. Security & authorization", title: "Needs production", blockedReason: "no prod", modes: ["Security"] }),
  sc({ id: "UX-001", category: "16. Frontend & user experience", title: "Empty dashboard", implemented: false, modes: ["Functional"] }),
];

const result = (id: string, status: ScenarioResult["status"]): ScenarioResult =>
  ({ id, title: id, category: "c", priority: "High", status, startedAt: "", durationMs: 1, expected: "", actual: "", log: [], assertions: [], invariants: [], runs: [], http: [], evidence: {}, seed: 1 }) as ScenarioResult;

describe("scenario filter", () => {
  it("filters by category, priority, severity and mode", () => {
    expect(filterScenarios(catalogue, { category: "2. Duplicates & idempotency" }).map((s) => s.id)).toEqual(["DUP-001"]);
    expect(filterScenarios(catalogue, { priorities: ["Critical", "High"] }).map((s) => s.id)).toEqual(["VAL-001", "DUP-001"]);
    expect(filterScenarios(catalogue, { severities: ["Critical"] }).map((s) => s.id)).toEqual(["DUP-001"]);
    expect(filterScenarios(catalogue, { modes: ["Baseline"] }).map((s) => s.id)).toEqual(["VAL-001"]);
  });

  it("searches id, title and description with every term", () => {
    expect(filterScenarios(catalogue, { search: "val-001" }).map((s) => s.id)).toEqual(["VAL-001"]);
    expect(filterScenarios(catalogue, { search: "resend id" }).map((s) => s.id)).toEqual(["DUP-001"]);
    expect(filterScenarios(catalogue, { search: "nothing-matches" })).toEqual([]);
  });

  it("derives status from the latest result, else from the catalogue (blocked/not implemented are never passed)", () => {
    expect(effectiveStatus(catalogue[2]!)).toBe("Blocked");
    expect(effectiveStatus(catalogue[3]!)).toBe("NotImplemented");
    expect(effectiveStatus(catalogue[0]!)).toBe("NotRun");
    expect(effectiveStatus(catalogue[0]!, { status: "Pass" })).toBe("Pass");
    const latest = { "VAL-001": { status: "Fail" as const } };
    expect(filterScenarios(catalogue, { statuses: ["Fail"] }, latest).map((s) => s.id)).toEqual(["VAL-001"]);
    expect(filterScenarios(catalogue, { statuses: ["Pass"] }, latest)).toEqual([]);
  });

  it("tallies per mode without counting blocked as executed", () => {
    const t = tallyBy(catalogue, (s) => s.modes, { "VAL-001": { status: "Pass" } });
    expect(t.Functional).toMatchObject({ total: 2, executed: 1, passed: 1, notImplemented: 1 });
    expect(t.Security).toMatchObject({ total: 1, executed: 0, blocked: 1, passed: 0 });
  });
});

describe("batch comparison", () => {
  it("labels fixed, regressed, new and removed scenarios", () => {
    const prev = { id: "b1", results: [result("A", "Fail"), result("B", "Pass"), result("C", "Pass")] } as BatchResult;
    const cur = { id: "b2", results: [result("A", "Pass"), result("B", "Fail"), result("D", "Blocked")] } as BatchResult;
    const kinds = Object.fromEntries(compareBatches(cur, prev).map((c) => [c.id, c.kind]));
    expect(kinds).toEqual({ A: "fixed", B: "regressed", C: "removed", D: "new" });
  });
});

describe("run configuration", () => {
  it("parses a saved configuration and rejects other files", () => {
    const cfg = parseRunConfiguration(JSON.stringify({ kind: "procurement-test-lab/run-configuration", version: 1, name: "n", ids: ["VAL-001"], parallelism: 99 }));
    expect(cfg.ids).toEqual(["VAL-001"]);
    expect(cfg.parallelism).toBe(16);
    expect(() => parseRunConfiguration('{"ids":[1]}')).toThrow();
  });
});
