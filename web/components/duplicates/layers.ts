export type LayerKey = "mapping" | "idempotency" | "lookup" | "ambiguous" | "created" | "not-reached" | "other";

export function classifyLayer(layer: string | null | undefined): LayerKey {
  const l = (layer ?? "").toLowerCase();
  if (l.includes("ambiguous")) return "ambiguous";
  if (l.includes("layer 1") || l.startsWith("local mapping")) return "mapping";
  if (l.includes("layer 2") || l.startsWith("idempotency")) return "idempotency";
  if (l.includes("layer 3") || l.startsWith("lookup")) return "lookup";
  if (l.startsWith("no existing document")) return "created";
  if (l.startsWith("not reached")) return "not-reached";
  return "other";
}

export const LAYER_LABEL: Record<LayerKey, string> = {
  mapping: "1 · Local mapping",
  idempotency: "2 · Idempotency-Key",
  lookup: "3 · SAP lookup before create",
  ambiguous: "3 · Lookup: ambiguous (nothing linked)",
  created: "Created once (no existing document)",
  "not-reached": "Not reached",
  other: "Other",
};
