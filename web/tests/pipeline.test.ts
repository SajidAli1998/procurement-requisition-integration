import { describe, expect, it } from "vitest";
import { eventsOfLatestRun, groupByStage, requisitionsTouched, stagesFor } from "@/lib/pipeline";
import type { HttpExchange, LiveEvent } from "@/lib/types";

const ex = (p: Partial<HttpExchange>): HttpExchange => ({ seq: 1, at: "2026-01-01T00:00:00Z", channel: "integration→sap", host: "sap.lab", method: "GET", url: "/", durationMs: 1, status: 200, ...p });
const SAP = "/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/A_PurchaseRequisitionHeader";

describe("pipeline stage classification", () => {
  it("maps source pages, pagination, lookup, create and write-back", () => {
    expect(stagesFor(ex({ channel: "integration→source", url: "/api/requisitions?limit=40" }))).toEqual(["source"]);
    expect(stagesFor(ex({ channel: "integration→source", url: "/api/requisitions?limit=40&cursor=abc" }))).toEqual(["pagination"]);
    expect(stagesFor(ex({ url: `${SAP}?$filter=ExternalReference%20eq%20%27REQ-1%27` }))).toEqual(["duplicate"]);
    expect(stagesFor(ex({ method: "POST", url: SAP }))).toEqual(["sap-request", "sap-response"]);
    expect(stagesFor(ex({ channel: "integration→source", method: "PUT", url: "/api/requisitions/REQ-1/erp-status" }))).toEqual(["writeback"]);
    expect(stagesFor(ex({ channel: "setup→sap", url: "/_admin/requisitions" }))).toEqual([]);
  });

  it("counts requisitions that reached SAP from lookups and create bodies", () => {
    const touched = requisitionsTouched([
      ex({ url: `${SAP}?$filter=ExternalReference%20eq%20%27REQ-7002%27` }),
      ex({ method: "POST", url: SAP, requestBody: '{"ExternalReference":"GEN-7-00001","x":1}' }),
      ex({ channel: "setup→sap", url: `${SAP}?$filter=ExternalReference%20eq%20%27REQ-9%27` }),
    ]);
    expect([...touched].sort()).toEqual(["GEN-7-00001", "REQ-7002"]);
  });

  it("groups exchanges per stage and finds the latest run", () => {
    const g = groupByStage([ex({ method: "POST", url: SAP })]);
    expect(g["sap-request"]).toHaveLength(1);
    expect(g["sap-response"]).toHaveLength(1);
    const events: LiveEvent[] = [
      { type: "run-started", at: "1", data: { id: "a" } },
      { type: "run-finished", at: "2", data: { id: "a" } },
      { type: "run-started", at: "3", data: { id: "b" } },
      { type: "http", at: "4", data: ex({}) },
    ];
    const latest = eventsOfLatestRun(events);
    expect((latest.run as { id: string }).id).toBe("b");
    expect(latest.events).toHaveLength(1);
    expect(latest.finished).toBeNull();
  });
});
