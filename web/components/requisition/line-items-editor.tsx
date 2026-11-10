"use client";

import { Plus, Trash2 } from "lucide-react";
import { useWatch, type FieldArrayWithId, type UseFormReturn } from "react-hook-form";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { KNOWN_PLANTS, KNOWN_UNITS, type RequisitionForm } from "@/lib/requisition-form";
import { Field, fieldAria } from "./field";

type LineKey = "lineNo" | "materialCode" | "description" | "materialGroup" | "quantity" | "uom" | "unitPrice" | "plant" | "taxCode";

const COLUMNS: { key: LineKey; label: string; numeric?: boolean; list?: string; className?: string }[] = [
  { key: "lineNo", label: "Line no.", numeric: true },
  { key: "materialCode", label: "Material code", className: "sm:col-span-2" },
  { key: "description", label: "Description", className: "sm:col-span-3" },
  { key: "materialGroup", label: "Material group" },
  { key: "quantity", label: "Quantity", numeric: true },
  { key: "uom", label: "Unit of measure", list: "testlab-units" },
  { key: "unitPrice", label: "Unit price", numeric: true },
  { key: "plant", label: "Plant", list: "testlab-plants" },
  { key: "taxCode", label: "Tax code" },
];

export function LineItemsEditor({
  form,
  fields,
  onAdd,
  onRemove,
  currency,
}: {
  form: UseFormReturn<RequisitionForm>;
  fields: FieldArrayWithId<RequisitionForm, "lines", "key">[];
  onAdd: () => void;
  onRemove: (index: number) => void;
  currency: string;
}) {
  const errors = form.formState.errors.lines;
  const lines = useWatch({ control: form.control, name: "lines" });
  return (
    <div className="space-y-3" data-testid="line-items-editor">
      <datalist id="testlab-units">
        {KNOWN_UNITS.map((u) => (
          <option key={u} value={u} />
        ))}
      </datalist>
      <datalist id="testlab-plants">
        {KNOWN_PLANTS.map((p) => (
          <option key={p} value={p} />
        ))}
      </datalist>
      {typeof errors?.message === "string" ? (
        <p role="alert" className="text-xs text-red-700 dark:text-red-300">
          {errors.message}
        </p>
      ) : null}
      {errors?.root?.message ? (
        <p role="alert" className="text-xs text-red-700 dark:text-red-300">
          {errors.root.message}
        </p>
      ) : null}
      {fields.map((f, i) => {
        const lineErrors = errors?.[i];
        const q = Number(lines?.[i]?.quantity);
        const p = Number(lines?.[i]?.unitPrice);
        const lineTotal = Number.isFinite(q) && Number.isFinite(p) ? Math.round(q * p * 100) / 100 : null;
        return (
          <div key={f.key} className="rounded-md border p-3" data-testid="line-item">
            <div className="mb-2 flex items-center gap-2">
              <h3 className="text-sm font-medium">Line {i + 1}</h3>
              <span className="text-xs text-muted-foreground tabular-nums">
                Line total: {lineTotal === null ? "-" : `${lineTotal} ${currency}`.trim()}
              </span>
              <Button
                variant="ghost"
                size="sm"
                className="ml-auto"
                onClick={() => onRemove(i)}
                disabled={fields.length <= 1}
                aria-label={`Remove line ${i + 1}`}
                title={fields.length <= 1 ? "A requisition needs at least one line in the form; use Custom JSON for a record without lines" : undefined}
              >
                <Trash2 /> Remove
              </Button>
            </div>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-6">
              {COLUMNS.map((c) => {
                const id = `lines-${i}-${c.key}`;
                const error = lineErrors?.[c.key]?.message;
                return (
                  <Field key={c.key} id={id} label={c.label} error={error} className={c.className}>
                    <Input
                      {...fieldAria(id, error)}
                      aria-label={`Line ${i + 1} ${c.label}`}
                      inputMode={c.numeric ? "decimal" : undefined}
                      list={c.list}
                      autoComplete="off"
                      {...form.register(`lines.${i}.${c.key}` as const)}
                    />
                  </Field>
                );
              })}
            </div>
          </div>
        );
      })}
      <Button variant="outline" size="sm" onClick={onAdd}>
        <Plus /> Add line
      </Button>
    </div>
  );
}
