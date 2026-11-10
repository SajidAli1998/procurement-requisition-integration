import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, api, labUrl, request } from "@/lib/api";

function mockFetch(impl: (url: string, init?: RequestInit) => Promise<Response>) {
  const fn = vi.fn(impl);
  vi.stubGlobal("fetch", fn);
  return fn;
}

const json = (body: unknown, status = 200, type = "application/json") => new Response(JSON.stringify(body), { status, headers: { "content-type": type } });

afterEach(() => vi.unstubAllGlobals());

describe("api client", () => {
  it("calls the same-origin proxy, never the lab directly", async () => {
    const fetch = mockFetch(async () => json({ status: "Healthy", isolated: true }));
    await api.health();
    expect(fetch.mock.calls[0]?.[0]).toBe("/api/lab/health");
    expect(labUrl("workspace")).toBe("/api/lab/workspace");
  });

  it("sends JSON bodies and the right method", async () => {
    const fetch = mockFetch(async () => json({ id: "ws-1", kind: "replay", startedAt: "x", state: "Running" }, 202));
    await api.replay(["REQ-1", "REQ-2"]);
    const [url, init] = fetch.mock.calls[0]!;
    expect(url).toBe("/api/lab/workspace/replay");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(String(init?.body))).toEqual({ ids: ["REQ-1", "REQ-2"] });
  });

  it("sends raw JSON text verbatim (duplicate keys survive)", async () => {
    const fetch = mockFetch(async () => json({ id: "X" }));
    const raw = '{"id":"X","totalAmount":1,"totalAmount":2}';
    await api.putRequisition("X", raw, 4);
    expect(fetch.mock.calls[0]?.[0]).toBe("/api/lab/workspace/requisitions/X?version=4");
    expect(fetch.mock.calls[0]?.[1]?.body).toBe(raw);
  });

  it("uses the summary and latest endpoints for scenario lists", async () => {
    const fetch = mockFetch(async () => json({ "VAL-001": { batchId: "b1", at: "x", status: "Pass" } }));
    const latest = await api.scenariosLatest();
    expect(latest["VAL-001"]?.status).toBe("Pass");
    await api.batchSummary("batch 1").catch(() => undefined);
    expect(fetch.mock.calls.map((c) => c[0])).toEqual(["/api/lab/scenarios/latest", "/api/lab/batches/batch%201?summary=true"]);
  });

  it("passes the run seed to POST /batches", async () => {
    const fetch = mockFetch(async () => json({ id: "pending-1", name: "n", total: 1 }, 202));
    await api.runBatch({ ids: ["VAL-001"], seed: 7 });
    expect(JSON.parse(String(fetch.mock.calls[0]?.[1]?.body))).toEqual({ ids: ["VAL-001"], seed: 7 });
  });

  it("returns undefined for 204", async () => {
    mockFetch(async () => new Response(null, { status: 204 }));
    await expect(api.clearFaults()).resolves.toBeUndefined();
  });

  it("turns problem details into a typed ApiError", async () => {
    mockFetch(async () => json({ title: "A run is already in progress", detail: "One run at a time", status: 409 }, 409, "application/problem+json"));
    const err = (await api.sync().catch((e) => e)) as ApiError;
    expect(err).toBeInstanceOf(ApiError);
    expect(err.kind).toBe("conflict");
    expect(err.status).toBe(409);
    expect(err.message).toContain("A run is already in progress");
    expect(err.outcomeUnknown).toBe(false);
  });

  it("reports a rejected lab key as unauthorized", async () => {
    mockFetch(async () => json({ title: "Lab key required", status: 401 }, 401));
    const err = (await api.dashboard().catch((e) => e)) as ApiError;
    expect(err.kind).toBe("unauthorized");
  });

  it("marks network failures and 502 as unknown outcome, not failure", async () => {
    mockFetch(async () => {
      throw new TypeError("Failed to fetch");
    });
    const net = (await api.sync().catch((e) => e)) as ApiError;
    expect(net.kind).toBe("network");
    expect(net.outcomeUnknown).toBe(true);

    mockFetch(async () => json({ title: "Test Lab API unavailable" }, 502));
    const gw = (await api.sync().catch((e) => e)) as ApiError;
    expect(gw.outcomeUnknown).toBe(true);
  });

  it("times out slow requests with a timeout error", async () => {
    mockFetch((_url, init) => new Promise((_, reject) => init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")))));
    const err = (await request("/workspace", { timeoutMs: 20 }).catch((e) => e)) as ApiError;
    expect(err.kind).toBe("timeout");
    expect(err.outcomeUnknown).toBe(true);
  });

  it("rejects responses with an unexpected schema instead of rendering them", async () => {
    mockFetch(async () => json({ hello: "world" }));
    const err = (await api.dashboard().catch((e) => e)) as ApiError;
    expect(err.kind).toBe("schema");
    mockFetch(async () => json({ not: "a list" }));
    expect(((await api.requisitions().catch((e) => e)) as ApiError).kind).toBe("schema");
  });

  it("normalises fault state casing from the SAP mock", async () => {
    mockFetch(async () =>
      json({
        dataset: "Workbook",
        createdAt: "x",
        scheduled: false,
        scheduleSeconds: 10,
        active: null,
        sap: { total: 0, duplicateReferences: 0 },
        faults: {
          sap: { settings: { TransientErrorRate: 0.5, HonorIdempotencyKey: false }, pendingScript: [{ Kind: "Throttle", Count: 2 }], injected: { Throttle: 1 } },
          source: { settings: { pageOverlap: true }, pendingScript: [], injected: {} },
        },
        crashArmed: null,
      }),
    );
    const ws = await api.workspace();
    expect(ws.faults.sap?.settings.transientErrorRate).toBe(0.5);
    expect(ws.faults.sap?.settings.honorIdempotencyKey).toBe(false);
    expect(ws.faults.sap?.pendingScript[0]).toMatchObject({ kind: "Throttle", count: 2 });
    expect(ws.faults.source?.settings.pageOverlap).toBe(true);
    expect(ws.faults.source?.settings.seed).toBe(1234);
  });
});
