import type { JsonValue } from "./types";

interface DiffEntry {
  path: string;
  kind: "added" | "removed" | "changed";
  before?: JsonValue;
  after?: JsonValue;
}

export function jsonDiff(before: JsonValue | undefined, after: JsonValue | undefined, path = ""): DiffEntry[] {
  if (before === undefined && after === undefined) return [];
  if (before === undefined) return [{ path: path || "$", kind: "added", after }];
  if (after === undefined) return [{ path: path || "$", kind: "removed", before }];
  const bothObjects = before !== null && after !== null && typeof before === "object" && typeof after === "object";
  if (bothObjects && Array.isArray(before) === Array.isArray(after)) {
    const out: DiffEntry[] = [];
    if (Array.isArray(before) && Array.isArray(after)) {
      const n = Math.max(before.length, after.length);
      for (let i = 0; i < n; i++) out.push(...jsonDiff(before[i], after[i], `${path}[${i}]`));
    } else {
      const b = before as Record<string, JsonValue>;
      const a = after as Record<string, JsonValue>;
      const keys = [...new Set([...Object.keys(b), ...Object.keys(a)])].sort();
      for (const k of keys) out.push(...jsonDiff(b[k], a[k], path ? `${path}.${k}` : k));
    }
    return out;
  }
  return JSON.stringify(before) === JSON.stringify(after) ? [] : [{ path: path || "$", kind: "changed", before, after }];
}
