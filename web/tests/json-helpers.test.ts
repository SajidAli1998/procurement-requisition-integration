import { describe, expect, it } from "vitest";
import { jsonDiff } from "@/lib/json-diff";
import { getPath, mergePatchFor, parsePath, parseTyped, setPath } from "@/lib/json-path";
import { camelKeys, faultsActive, normalizeFaultState } from "@/lib/faults";

describe("json path helpers", () => {
  it("parses, reads and writes nested paths case-insensitively", () => {
    expect(parsePath("lines[0].quantity")).toEqual(["lines", 0, "quantity"]);
    const rec = { id: "R", totalAmount: 0, lines: [{ quantity: 1 }] };
    expect(getPath(rec, "TotalAmount")).toBe(0);
    const after = setPath(rec, "lines[0].quantity", 5);
    expect(getPath(after, "lines[0].quantity")).toBe(5);
    expect(rec.lines[0]?.quantity).toBe(1);
    expect(mergePatchFor(rec, setPath(rec, "totalAmount", 25452), ["totalAmount"])).toEqual({ totalAmount: 25452 });
    expect(mergePatchFor(rec, after, ["lines[0].quantity"])).toEqual({ lines: [{ quantity: 5 }] });
  });

  it("keeps typed input types", () => {
    expect(parseTyped("25452")).toBe(25452);
    expect(parseTyped("null")).toBeNull();
    expect(parseTyped("")).toBeNull();
    expect(parseTyped("E1071")).toBe("E1071");
    expect(parseTyped('"007"')).toBe("007");
  });
});

describe("jsonDiff", () => {
  it("reports changed, added and removed leaves", () => {
    const d = jsonDiff({ a: 1, b: { c: [1, 2] }, gone: true }, { a: 2, b: { c: [1, 3, 4] }, added: "x" });
    expect(d).toEqual([
      { path: "a", kind: "changed", before: 1, after: 2 },
      { path: "added", kind: "added", after: "x" },
      { path: "b.c[1]", kind: "changed", before: 2, after: 3 },
      { path: "b.c[2]", kind: "added", after: 4 },
      { path: "gone", kind: "removed", before: true },
    ]);
  });
});

describe("fault state normalisation", () => {
  it("camel-cases keys and maps numeric enums", () => {
    expect(camelKeys({ A: { BCd: 1 } })).toEqual({ a: { bCd: 1 } });
    const s = normalizeFaultState({ Settings: { ThrottleRate: 0.2 }, PendingScript: [{ Kind: 2, Transform: 1 }], Injected: {} })!;
    expect(s.settings.throttleRate).toBe(0.2);
    expect(s.pendingScript[0]?.kind).toBe("Throttle");
    expect(s.pendingScript[0]?.transform).toBe("SetField");
    expect(faultsActive(s)).toBe(true);
    expect(faultsActive(normalizeFaultState({ settings: {}, pendingScript: [], injected: {} }))).toBe(false);
    expect(normalizeFaultState(null)).toBeNull();
  });
});
