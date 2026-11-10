"use client";

import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { RefreshCcw } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk, WORKSPACE_KEY } from "@/lib/api";
import type { Dataset } from "@/lib/types";

const schema = z.object({
  dataset: z.enum(["Workbook", "Generated", "Empty"]),
  seed: z.coerce.number<string>().int("Seed must be a whole number").min(0, "Seed must be 0 or more"),
  count: z.coerce.number<string>().int("Count must be a whole number").min(1, "Count must be at least 1").max(20000, "Count must be at most 20000"),
});
type Values = z.input<typeof schema>;
type Parsed = z.output<typeof schema>;

export function WorkspaceSwitcher({ current }: { current?: string }) {
  const [open, setOpen] = useState(false);
  const form = useForm<Values, unknown, Parsed>({ resolver: zodResolver(schema), defaultValues: { dataset: (current as Dataset) ?? "Workbook", seed: "42", count: "250" } });
  const action = useLabAction<Parsed, void>({
    name: "Reset workspace",
    run: (v) => api.resetWorkspace({ dataset: v.dataset, seed: v.seed, count: v.count }),
    invalidate: [WORKSPACE_KEY, qk.batches],
    confirm: async () => {
      const ws = await api.workspace();
      return `the lab now reports dataset "${ws.dataset}" (created ${new Date(ws.createdAt).toLocaleTimeString()}).`;
    },
    onConfirmed: () => setOpen(false),
  });
  const dataset = useWatch({ control: form.control, name: "dataset" });
  const errors = form.formState.errors;
  return (
    <Dialog open={open} onOpenChange={(o) => (!action.pending ? setOpen(o) : undefined)}>
      <DialogTrigger asChild>
        <Button variant="outline" size="sm">
          <RefreshCcw /> Switch / reset workspace
        </Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Reset the lab workspace</DialogTitle>
          <DialogDescription>
            Replaces the isolated in-memory lab (source, SAP mock and integration database) with a fresh dataset. Run history and faults are cleared. Nothing outside the lab is touched.
          </DialogDescription>
        </DialogHeader>
        <form className="grid gap-3" onSubmit={form.handleSubmit((v) => void action.execute(v))} noValidate>
          <div className="grid gap-1.5">
            <Label htmlFor="ws-dataset">Dataset</Label>
            <NativeSelect id="ws-dataset" {...form.register("dataset")}>
              <option value="Workbook">Workbook (client&apos;s 120 requisitions)</option>
              <option value="Generated">Generated (synthetic, seeded)</option>
              <option value="Empty">Empty</option>
            </NativeSelect>
          </div>
          {dataset === "Generated" ? (
            <div className="grid grid-cols-2 gap-3">
              <div className="grid gap-1.5">
                <Label htmlFor="ws-seed">Seed</Label>
                <Input id="ws-seed" inputMode="numeric" aria-invalid={!!errors.seed} {...form.register("seed")} />
                {errors.seed ? <p className="text-xs text-destructive" role="alert">{errors.seed.message}</p> : null}
              </div>
              <div className="grid gap-1.5">
                <Label htmlFor="ws-count">Count</Label>
                <Input id="ws-count" inputMode="numeric" aria-invalid={!!errors.count} {...form.register("count")} />
                {errors.count ? <p className="text-xs text-destructive" role="alert">{errors.count.message}</p> : null}
              </div>
            </div>
          ) : null}
          <ActionStatus phase={action.phase} message={action.message} />
          <DialogFooter>
            <Button type="submit" disabled={action.pending}>
              {action.pending ? "Resetting..." : "Reset workspace"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
