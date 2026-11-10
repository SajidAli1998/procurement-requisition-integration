import { describe, expect, it } from "vitest";
import { buildTemplates, businessWarnings, emptyForm, formToRecord, linesTotal, recordToForm, requisitionFormSchema } from "@/lib/requisition-form";
import type { SourceLine } from "@/lib/types";

const NOW = new Date("2026-11-09T12:00:00Z");

describe("requisition form schema (structural, blocking)", () => {
  it("requires an id and at least one line", () => {
    const r = requisitionFormSchema.safeParse({ ...emptyForm(), lines: [] });
    expect(r.success).toBe(false);
    const messages = r.error!.issues.map((i) => i.message);
    expect(messages).toContain("Requisition ID is required");
    expect(messages).toContain("Add at least one line item");
  });

  it("rejects non-numeric quantities and impossible dates", () => {
    const f = { ...emptyForm(), id: "REQ-1", neededBy: "2026-02-30", lines: [{ ...emptyForm().lines[0], quantity: "abc" }] };
    const r = requisitionFormSchema.safeParse(f);
    expect(r.success).toBe(false);
    const msgs = r.error!.issues.map((i) => i.message).join("\n");
    expect(msgs).toMatch(/Quantity must be a number/);
    expect(msgs).toMatch(/Needed by must be a real date/);
  });

  it("rejects ids with unsafe characters", () => {
    const r = requisitionFormSchema.safeParse({ ...emptyForm(), id: "../etc" });
    expect(r.success).toBe(false);
  });

  it("accepts business-rule violations (negative qty, zero total): they are warnings, not blockers", () => {
    const f = { ...emptyForm(), id: "REQ-1", overrideTotal: true, totalAmount: "0", lines: [{ ...emptyForm().lines[0], quantity: "-2" }] };
    expect(requisitionFormSchema.safeParse(f).success).toBe(true);
  });

  it("requires a total when the override is on", () => {
    const r = requisitionFormSchema.safeParse({ ...emptyForm(), id: "REQ-1", overrideTotal: true, totalAmount: "" });
    expect(r.success).toBe(false);
  });
});

describe("business warnings (advisory)", () => {
  it("flags each template's deliberate problem", () => {
    const t = Object.fromEntries(buildTemplates("REQ-T", NOW).map((x) => [x.id, x]));
    const warn = (id: string) => businessWarnings(recordToForm(t[id]!.record!).form, NOW).map((w) => w.message).join("\n");
    expect(businessWarnings(recordToForm(t.valid!.record!).form, NOW)).toEqual([]);
    expect(warn("missing-requester")).toMatch(/Requester is missing/);
    expect(warn("zero-total")).toMatch(/AmountNotPositive/);
    expect(warn("past-date")).toMatch(/NeededByInPast/);
    expect(warn("unknown-plant")).toMatch(/UnknownPlant/);
    expect(warn("negative-qty")).toMatch(/InvalidQuantity/);
    expect(warn("decimal-ea")).toMatch(/whole number/);
  });

  it("keeps the duplicate-keys template as raw text", () => {
    const dup = buildTemplates("REQ-T", NOW).find((x) => x.id === "duplicate-json")!;
    expect(dup.record).toBeUndefined();
    expect(dup.raw!.match(/"totalAmount"/g)).toHaveLength(2);
  });
});

describe("record <-> form conversion", () => {
  it("round-trips a record and keeps fields the form does not edit", () => {
    const record = { ...buildTemplates("REQ-R", NOW)[0]!.record!, version: 7, erpSync: { reference: "X" }, injected: "<b>keep</b>" };
    const { form, extras } = recordToForm(record);
    const back = formToRecord(form, extras);
    expect(back.version).toBe(7);
    expect(back.injected).toBe("<b>keep</b>");
    expect(back.erpSync).toEqual({ reference: "X" });
    expect(back.totalAmount).toBe(1570);
    expect((back.lines as SourceLine[])[0]).toMatchObject({ quantity: 2, unitPrice: 387, lineTotal: 774, plant: "3100" });
  });

  it("auto-sums the total unless overridden; empty text becomes null", () => {
    const f = { ...emptyForm(), id: "REQ-2", lines: [{ ...emptyForm().lines[0]!, quantity: "3", unitPrice: "1.10" }] };
    expect(linesTotal(f.lines)).toBe(3.3);
    const rec = formToRecord(f);
    expect(rec.totalAmount).toBe(3.3);
    expect(rec.requester).toBeNull();
    expect(formToRecord({ ...f, overrideTotal: true, totalAmount: "99" }).totalAmount).toBe(99);
  });

  it("keeps XSS text as plain data", () => {
    const xss = buildTemplates("REQ-X", NOW).find((t) => t.id === "xss")!.record!;
    expect(formToRecord(recordToForm(xss).form).justification).toBe(`<script>alert("xss")</script><img src=x onerror=alert(1)>`);
  });
});
