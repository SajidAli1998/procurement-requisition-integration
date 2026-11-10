import { describe, expect, it } from "vitest";
import { httpStatusTone, isSynchronised, outcomeCategory, outcomeStyle, scenarioStatus, stateCategory } from "@/lib/status";

describe("outcome badge mapping", () => {
  it.each(["Created", "Linked", "Updated"])("%s is synchronised", (o) => {
    expect(isSynchronised(o)).toBe(true);
  });

  it.each(["Quarantined", "Skipped", "Pending", "Retrying", "Unresolved (verifying in SAP)", "Unresolved (mid-send)", "Resolved by operator", "Not processed", "", null, undefined, "Synced?"])(
    "%j is never shown as synchronised",
    (o) => {
      expect(isSynchronised(o)).toBe(false);
    },
  );

  it("gives quarantined and skipped distinct non-success tones", () => {
    expect(outcomeStyle("Quarantined").tone).toBe("danger");
    expect(outcomeStyle("Skipped").tone).toBe("muted");
    expect(outcomeStyle("Created").tone).toBe("success");
    expect(outcomeStyle("Unresolved (mid-send)").tone).toBe("critical");
  });

  it("maps the reconciliation vocabulary too", () => {
    expect(outcomeCategory("Duplicate")).toBe("linked");
    expect(outcomeCategory("Ignored")).toBe("skipped");
    expect(stateCategory("InDoubt")).toBe("unresolved");
    expect(stateCategory("Ignored")).toBe("skipped");
  });

  it("maps scenario statuses and HTTP codes", () => {
    expect(scenarioStatus("NotImplemented").label).toBe("Not implemented");
    expect(scenarioStatus(undefined).label).toBe("Not run");
    expect(scenarioStatus("Blocked").tone).toBe("warning");
    expect(httpStatusTone(201)).toBe("success");
    expect(httpStatusTone(429)).toBe("warning");
    expect(httpStatusTone(null)).toBe("critical");
    expect(httpStatusTone(503)).toBe("danger");
  });
});
