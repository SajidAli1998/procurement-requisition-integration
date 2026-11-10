import { expect, type APIRequestContext, type Page } from "@playwright/test";

// Lab state is changed through the web app's own proxy (/api/lab), exactly as the browser does.

export async function resetWorkspace(request: APIRequestContext, dataset: "Workbook" | "Generated" | "Empty" = "Workbook", count?: number) {
  const res = await request.post("/api/lab/workspace/reset", { data: { dataset, seed: 42, count }, timeout: 120_000 });
  expect(res.status(), await res.text()).toBe(204);
  await clearFaults(request);
}

export async function clearFaults(request: APIRequestContext) {
  const res = await request.delete("/api/lab/workspace/faults");
  expect([204, 200]).toContain(res.status());
}

export async function waitIdle(request: APIRequestContext, timeoutMs = 120_000) {
  await expect
    .poll(
      async () => {
        const ws = await (await request.get("/api/lab/workspace")).json();
        return ws.active === null;
      },
      { timeout: timeoutMs, intervals: [250, 500, 1000] },
    )
    .toBe(true);
}

export async function syncAndWait(request: APIRequestContext) {
  await waitIdle(request);
  const res = await request.post("/api/lab/workspace/sync", { data: {} });
  expect(res.status(), await res.text()).toBe(202);
  await waitIdle(request);
}

export async function dashboard(request: APIRequestContext) {
  return (await request.get("/api/lab/workspace/dashboard")).json();
}

// Workbook dataset after one sync: 58 created, 7 linked, 50 quarantined, 5 skipped.
export async function ensureSyncedWorkbook(request: APIRequestContext) {
  const d = await dashboard(request);
  const ws = await (await request.get("/api/lab/workspace")).json();
  const faultsQuiet = (ws.faults?.sap?.pendingScript?.length ?? ws.faults?.sap?.PendingScript?.length ?? 0) === 0;
  if (d.dataset === "Workbook" && d.totals.total === 120 && d.totals.created === 58 && d.totals.quarantined === 50 && d.totals.pending === 0 && faultsQuiet && ws.active === null) return;
  await resetWorkspace(request, "Workbook");
  await syncAndWait(request);
}

export async function slowSap(request: APIRequestContext, latencyMs: number) {
  const res = await request.put("/api/lab/workspace/faults/sap", {
    data: { transientErrorRate: 0, throttleRate: 0, throttleRetryAfterSeconds: 1, lostResponseRate: 0, latencyMs, pageOverlap: false, honorIdempotencyKey: true, referenceSearchLag: false, seed: 1234 },
  });
  expect(res.ok(), await res.text()).toBeTruthy();
}

export async function noHorizontalScroll(page: Page) {
  const { scroll, width } = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, width: window.innerWidth }));
  expect(scroll, `page is ${scroll}px wide in a ${width}px viewport`).toBeLessThanOrEqual(width + 1);
}

export function problem(status: number, title: string, detail = "") {
  return { status, contentType: "application/problem+json", body: JSON.stringify({ status, title, detail }) };
}
