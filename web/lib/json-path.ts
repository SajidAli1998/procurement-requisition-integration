import type { JsonObject, JsonValue } from "./types";

export function parsePath(path: string): (string | number)[] {
  const out: (string | number)[] = [];
  for (const part of path.split(".")) {
    const m = /^([^[\]]*)((?:\[\d+\])*)$/.exec(part);
    if (!m) {
      out.push(part);
      continue;
    }
    if (m[1]) out.push(m[1]);
    for (const idx of (m[2] ?? "").matchAll(/\[(\d+)\]/g)) out.push(Number(idx[1]));
  }
  return out;
}

// The backend reports "TotalAmount" for "totalAmount".
function keyFor(obj: Record<string, unknown>, key: string): string {
  if (key in obj) return key;
  return Object.keys(obj).find((k) => k.toLowerCase() === key.toLowerCase()) ?? key;
}

export function getPath(root: JsonValue | undefined, path: string): JsonValue | undefined {
  let cur: JsonValue | undefined = root;
  for (const seg of parsePath(path)) {
    if (cur === null || cur === undefined) return undefined;
    if (typeof seg === "number") cur = Array.isArray(cur) ? cur[seg] : undefined;
    else cur = typeof cur === "object" && !Array.isArray(cur) ? (cur as JsonObject)[keyFor(cur as JsonObject, seg)] : undefined;
  }
  return cur;
}

export function setPath(root: JsonObject, path: string, value: JsonValue): JsonObject {
  const copy = JSON.parse(JSON.stringify(root)) as JsonObject;
  const segs = parsePath(path);
  let cur: JsonValue = copy;
  segs.forEach((seg, i) => {
    const last = i === segs.length - 1;
    const nextIsIndex = typeof segs[i + 1] === "number";
    if (typeof seg === "number") {
      const arr = cur as JsonValue[];
      if (last) arr[seg] = value;
      else {
        if (arr[seg] === null || typeof arr[seg] !== "object") arr[seg] = nextIsIndex ? [] : {};
        cur = arr[seg];
      }
    } else {
      const obj = cur as JsonObject;
      const k = keyFor(obj, seg);
      if (last) obj[k] = value;
      else {
        if (obj[k] === null || typeof obj[k] !== "object") obj[k] = nextIsIndex ? [] : {};
        cur = obj[k];
      }
    }
  });
  return copy;
}

export function mergePatchFor(before: JsonObject, after: JsonObject, paths: string[]): JsonObject {
  const patch: JsonObject = {};
  for (const p of paths) {
    const top = parsePath(p)[0];
    if (typeof top !== "string") continue;
    const k = Object.keys(after).find((x) => x.toLowerCase() === top.toLowerCase()) ?? top;
    if (JSON.stringify(before[k]) !== JSON.stringify(after[k])) patch[k] = after[k] ?? null;
  }
  return patch;
}

export function parseTyped(input: string): JsonValue {
  const t = input.trim();
  if (t === "") return null;
  if (/^-?\d+(\.\d+)?([eE][-+]?\d+)?$/.test(t) || t === "true" || t === "false" || t === "null" || (t.startsWith('"') && t.endsWith('"')) || t.startsWith("{") || t.startsWith("[")) {
    try {
      return JSON.parse(t) as JsonValue;
    } catch {
      return input;
    }
  }
  return input;
}
