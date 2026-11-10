import { readFile } from "node:fs/promises";
import { expect, test } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { clearFaults, ensureSyncedWorkbook, noHorizontalScroll, problem, resetWorkspace, slowSap, waitIdle } from "./helpers";

// Titles start with the scenario ID: the Test Lab's scenario engine reads results by that prefix.

test("UX-001 Empty dashboard", async ({ page, request }) => {
  await resetWorkspace(request, "Empty");
  await page.goto("/");
  await expect(page.getByRole("heading", { name: "Dashboard" })).toBeVisible();
  await expect(page.getByTestId("empty-state")).toContainText("The lab workspace is empty");
  await expect(page.getByRole("button", { name: /Switch \/ reset workspace/ }).first()).toBeVisible();
  // An empty lab shows no numbers at all rather than zeros dressed up as results.
  await expect(page.getByTestId("stat-created")).toHaveCount(0);
  await resetWorkspace(request, "Workbook");
});

test("UX-002 Loading dashboard", async ({ page }) => {
  await page.route("**/api/lab/workspace/dashboard", async (route) => {
    await new Promise((r) => setTimeout(r, 2000));
    await route.continue();
  });
  await page.goto("/");
  await expect(page.getByTestId("loading")).toBeVisible();
  await expect(page.getByTestId("loading")).toHaveAttribute("aria-busy", "true");
  await expect(page.getByTestId("stat-total")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByTestId("loading")).toHaveCount(0);
});

test("UX-003 API unavailable", async ({ page }) => {
  await page.route("**/api/lab/**", (route) => route.fulfill(problem(502, "Test Lab API unavailable", "The web server could not reach the Test Lab API at http://localhost:5110 (connect ECONNREFUSED).")));
  await page.goto("/");
  const panel = page.getByTestId("error-panel");
  await expect(panel).toBeVisible();
  await expect(panel).toContainText("Test Lab API unavailable");
  await expect(panel).toContainText("ECONNREFUSED");
  await expect(page.getByTestId("api-banner")).toBeVisible();
  await expect(page.getByTestId("stat-total")).toHaveCount(0);
  await page.unroute("**/api/lab/**");
  await panel.getByRole("button", { name: "Retry" }).click();
  await expect(page.getByTestId("stat-total")).toBeVisible();
});

test("UX-004 Expired user session", async ({ page }) => {
  await page.route("**/api/lab/workspace/dashboard", (route) => route.fulfill(problem(401, "Lab key required", "Send the X-Lab-Key header; lab actions are refused without it.")));
  await page.goto("/");
  const panel = page.getByTestId("error-panel");
  await expect(panel).toHaveAttribute("data-error-kind", "unauthorized");
  await expect(panel).toContainText("Lab key rejected");
  await expect(panel).toContainText("not authorised");
  await expect(panel).toContainText("TESTLAB_KEY");
  await expect(page.getByTestId("stat-total")).toHaveCount(0);
});

test("UX-005 Slow network", async ({ page }) => {
  await page.route("**/api/lab/**", async (route) => {
    if (route.request().headers()["accept"]?.includes("text/event-stream")) return route.continue();
    await new Promise((r) => setTimeout(r, 1500));
    await route.continue();
  });
  await page.goto("/requisitions");
  await expect(page.getByTestId("loading")).toBeVisible();
  await expect(page.getByTestId("table-count")).toBeVisible({ timeout: 20_000 });
  await expect(page.getByTestId("error-panel")).toHaveCount(0);
});

test("UX-006 Broken network connection", async ({ page, context, request }) => {
  await ensureSyncedWorkbook(request);
  await page.goto("/control");
  await expect(page.getByTestId("run-sync")).toBeEnabled();
  const runsBefore = (await (await request.get("/api/lab/workspace/runs")).json()).length;
  await context.setOffline(true);
  await page.getByTestId("run-sync").click();
  const status = page.getByTestId("action-status");
  await expect(status).toHaveAttribute("data-phase", "unknown");
  await expect(status).toContainText("Unknown outcome");
  await expect(status).toContainText("may or may not have been applied");
  await expect(page.getByText("Confirmed by lab")).toHaveCount(0);
  await context.setOffline(false);
  // Nothing reached the lab: the backend confirms no run was started.
  const runsAfter = (await (await request.get("/api/lab/workspace/runs")).json()).length;
  expect(runsAfter).toBe(runsBefore);
});

test("UX-007 Browser refresh during active run", async ({ page, request }) => {
  await ensureSyncedWorkbook(request);
  await resetWorkspace(request, "Workbook");
  await slowSap(request, 120);
  await page.goto("/control");
  await page.getByTestId("run-sync").click();
  await expect(page.getByTestId("action-status")).toHaveAttribute("data-phase", "confirmed");
  await page.reload();
  // After the refresh the page learns about the run from the lab (GET /workspace), not from memory.
  await expect(page.getByTestId("run-sync")).toBeDisabled();
  await expect(page.getByTestId("run-sync")).toHaveText(/Run in progress/);
  await expect(page.getByText("Running", { exact: true }).first()).toBeVisible();
  await clearFaults(request);
  await waitIdle(request);
  await expect(page.getByTestId("run-sync")).toBeEnabled({ timeout: 30_000 });
});

test("UX-008 Two tabs controlling one run", async ({ browser, request }) => {
  await waitIdle(request);
  await slowSap(request, 80);
  const runsBefore = (await (await request.get("/api/lab/workspace/runs")).json()).length;
  const ctx = await browser.newContext();
  const a = await ctx.newPage();
  const b = await ctx.newPage();
  await a.goto("/control");
  await b.goto("/control");
  await expect(a.getByTestId("run-sync")).toBeEnabled();
  await expect(b.getByTestId("run-sync")).toBeEnabled();
  await Promise.all([a.getByTestId("run-sync").click(), b.getByTestId("run-sync").click()]);
  // Exactly one run starts; the other tab is refused (409) or sees the run and disables its button.
  await expect
    .poll(async () => (await (await request.get("/api/lab/workspace/runs")).json()).length)
    .toBe(runsBefore + 1);
  for (const p of [a, b]) {
    await expect(async () => {
      const conflict = await p.getByTestId("run-conflict").count();
      const accepted = await p.locator('[data-testid="action-status"][data-phase="confirmed"]').count();
      const busy = (await p.getByTestId("run-sync").textContent())?.includes("Run in progress");
      expect(conflict + accepted > 0 || busy).toBeTruthy();
    }).toPass();
  }
  const refused = (await a.getByTestId("run-conflict").count()) + (await b.getByTestId("run-conflict").count());
  if (refused > 0) await expect((await a.getByTestId("run-conflict").count()) ? a.getByTestId("run-conflict") : b.getByTestId("run-conflict")).toContainText("409");
  await clearFaults(request);
  await waitIdle(request);
  await ctx.close();
});

test("UX-009 Browser back/forward navigation", async ({ page, request }) => {
  await ensureSyncedWorkbook(request);
  await page.goto("/requisitions");
  await page.getByTestId("outcome-filter").selectOption("Quarantined");
  await expect(page).toHaveURL(/outcome=Quarantined/);
  await expect(page.getByTestId("table-count")).toHaveText("50 of 120 rows");
  await page.getByRole("link", { name: "REQ-7071", exact: true }).click();
  await expect(page.getByRole("heading", { name: /REQ-7071/ })).toBeVisible();
  await page.goBack();
  await expect(page).toHaveURL(/outcome=Quarantined/);
  await expect(page.getByTestId("outcome-filter")).toHaveValue("Quarantined");
  await expect(page.getByTestId("table-count")).toHaveText("50 of 120 rows");
  await page.goForward();
  await expect(page.getByRole("heading", { name: /REQ-7071/ })).toBeVisible();
});

test("UX-010 Invalid form inputs", async ({ page }) => {
  const puts: string[] = [];
  page.on("request", (r) => {
    if (r.method() === "PUT" && r.url().includes("/api/lab/workspace/requisitions/")) puts.push(r.url());
  });
  await page.goto("/requisitions/new?template=valid");
  await page.getByLabel("Requisition ID").fill("bad id!");
  await page.getByLabel("Line 1 Quantity").fill("abc");
  await page.getByLabel("Needed by").fill("2026-02-30");
  await page.getByRole("button", { name: "Save to lab source" }).click();
  await expect(page.getByText(/Requisition ID may only contain/).first()).toBeVisible();
  await expect(page.getByText(/Quantity must be a number/).first()).toBeVisible();
  await expect(page.getByLabel("Requisition ID")).toHaveAttribute("aria-invalid", "true");
  expect(puts).toHaveLength(0);

  // Raw JSON with a duplicate key goes to the lab verbatim; the lab refuses it (400) and stores nothing.
  await page.goto("/requisitions/new?template=duplicate-json");
  await expect(page.getByTestId("raw-mode")).toBeVisible();
  const [put] = await Promise.all([
    page.waitForResponse((r) => r.request().method() === "PUT" && r.url().includes("/api/lab/workspace/requisitions/")),
    page.getByRole("button", { name: "Save to lab source" }).click(),
  ]);
  expect(put.status()).toBe(400);
  const status = page.locator('[data-testid="action-status"]').first();
  await expect(status).toHaveAttribute("data-phase", "failed");
  await expect(status).toContainText("Duplicate JSON property");
  const id = decodeURIComponent(new URL(put.url()).pathname.split("/").pop()!);
  expect((await page.request.get(`/api/lab/workspace/requisitions/${encodeURIComponent(id)}`)).status()).toBe(404);
});

test("UX-011 Duplicate submit clicks", async ({ page, request }) => {
  await waitIdle(request);
  const syncs: string[] = [];
  page.on("request", (r) => {
    if (r.method() === "POST" && r.url().endsWith("/api/lab/workspace/sync")) syncs.push(r.url());
  });
  await page.goto("/control");
  const button = page.getByTestId("run-sync");
  await expect(button).toBeEnabled();
  await button.click({ clickCount: 3, delay: 0 });
  await button.click({ force: true }).catch(() => undefined);
  await expect(page.getByTestId("action-status")).toHaveAttribute("data-phase", /confirmed|failed/);
  expect(syncs).toHaveLength(1);
  await waitIdle(request);
});

test("UX-012 Repeated retry clicks", async ({ page, request }) => {
  await ensureSyncedWorkbook(request);
  const replays: string[] = [];
  page.on("request", (r) => {
    if (r.method() === "POST" && r.url().endsWith("/api/lab/workspace/replay")) replays.push(r.url());
  });
  await page.goto("/control");
  await page.getByLabel("Requisition ids (comma or space separated)").fill("REQ-7073");
  const replay = page.getByRole("button", { name: "Replay one" });
  await replay.click({ clickCount: 4, delay: 0 });
  await expect(page.getByTestId("action-status")).toHaveAttribute("data-phase", /confirmed|failed/);
  expect(replays).toHaveLength(1);
  await waitIdle(request);

  // Retrying a failed read many times leaves one error panel and recovers once the API answers.
  let failing = true;
  await page.route("**/api/lab/workspace/runs", (route) => (failing ? route.fulfill(problem(503, "Service unavailable")) : route.continue()));
  await page.reload();
  const panel = page.getByTestId("error-panel");
  await expect(panel).toBeVisible();
  for (let i = 0; i < 5; i++) await panel.getByRole("button", { name: "Retry" }).click({ force: true }).catch(() => undefined);
  await expect(page.getByTestId("error-panel")).toHaveCount(1);
  failing = false;
  await page.getByTestId("error-panel").getByRole("button", { name: "Retry" }).click();
  await expect(page.getByTestId("run-history")).toBeVisible();
});

test("UX-013 Large data table", async ({ page, request }) => {
  test.setTimeout(180_000);
  await resetWorkspace(request, "Generated", 2000);
  await page.goto("/requisitions");
  await expect(page.getByTestId("table-count")).toHaveText("2000 rows", { timeout: 60_000 });
  await page.getByLabel("Rows per page").selectOption("250");
  await expect(page.locator("tbody tr[data-row-id]")).toHaveCount(250);
  await page.getByRole("button", { name: "Last page" }).click();
  await expect(page.getByTestId("table-page")).toHaveText("Page 8 of 8");
  const started = Date.now();
  await page.getByTestId("table-search").fill("01999");
  await expect(page.getByTestId("table-count")).toHaveText(/^1 of 2000 rows$/);
  expect(Date.now() - started).toBeLessThan(5000);
  await resetWorkspace(request, "Workbook");
});

test("UX-014 Filter combined with pagination", async ({ page, request }) => {
  await ensureSyncedWorkbook(request);
  await page.goto("/requisitions");
  await page.getByTestId("outcome-filter").selectOption("Quarantined");
  await expect(page.getByTestId("table-count")).toHaveText("50 of 120 rows");
  await expect(page.getByTestId("table-page")).toHaveText("Page 1 of 2");
  await page.getByRole("button", { name: "Next page" }).click();
  await expect(page.getByTestId("table-page")).toHaveText("Page 2 of 2");
  const outcomes = await page.locator("tbody tr[data-row-id] [data-outcome]").allTextContents();
  expect(outcomes).toHaveLength(25);
  expect(new Set(outcomes)).toEqual(new Set(["Quarantined"]));
  await page.getByTestId("outcome-filter").selectOption("Skipped");
  await expect(page.getByTestId("table-count")).toHaveText("5 of 120 rows");
  await expect(page.getByTestId("table-page")).toHaveText("Page 1 of 1");
  await expect(page.locator("tbody tr[data-row-id]")).toHaveCount(5);
});

test("UX-015 Export after filtering", async ({ page, request }) => {
  await ensureSyncedWorkbook(request);
  await page.goto("/requisitions?outcome=Skipped");
  await expect(page.getByTestId("table-count")).toHaveText("5 of 120 rows");
  const [csvDownload] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Export CSV" }).click()]);
  const csv = await readFile((await csvDownload.path())!, "utf8");
  const lines = csv.trim().split(/\r\n/);
  expect(lines).toHaveLength(6);
  const header = (lines[0] ?? "").split(",");
  const outcomeCol = header.indexOf("outcome");
  expect(outcomeCol).toBeGreaterThan(-1);
  for (const line of lines.slice(1)) expect(line).toContain("Skipped");
  const [jsonDownload] = await Promise.all([page.waitForEvent("download"), page.getByRole("button", { name: "Export JSON" }).click()]);
  const json = JSON.parse(await readFile((await jsonDownload.path())!, "utf8")) as { outcome: string }[];
  expect(json).toHaveLength(5);
  expect(json.every((r) => r.outcome === "Skipped")).toBe(true);
});

test("UX-016 Form with unsaved changes", async ({ page }) => {
  await page.goto("/requisitions/new?template=valid");
  await expect(page.getByTestId("requisition-form")).toBeVisible();
  await page.getByLabel("Justification").fill("Changed but not saved");
  let message = "";
  page.once("dialog", async (d) => {
    message = d.message();
    await d.dismiss();
  });
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Dashboard" }).click();
  await expect.poll(() => message).toContain("unsaved changes");
  await expect(page).toHaveURL(/\/requisitions\/new/);
  await expect(page.getByLabel("Justification")).toHaveValue("Changed but not saved");
  page.once("dialog", (d) => d.accept());
  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Dashboard" }).click();
  await expect(page).toHaveURL(/\/$/);
});

test("UX-017 Incomplete requisition form", async ({ page }) => {
  const puts: string[] = [];
  page.on("request", (r) => {
    if (r.method() === "PUT" && r.url().includes("/api/lab/workspace/requisitions/")) puts.push(r.url());
  });
  await page.goto("/requisitions/new");
  await page.getByLabel("Requisition ID").fill("");
  await page.getByRole("button", { name: "Save to lab source" }).click();
  await expect(page.getByText("Requisition ID is required").first()).toBeVisible();
  await expect(page.getByLabel("Requisition ID")).toBeFocused();
  // Missing business fields are listed as warnings so the operator sees what the integration will reject.
  await expect(page.getByTestId("business-warnings")).toContainText(/Requester is missing/);
  expect(puts).toHaveLength(0);
});

test("UX-018 API returns unexpected schema", async ({ page }) => {
  await page.route("**/api/lab/workspace/dashboard", (route) => route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ totals: "nope", unexpected: true }) }));
  await page.goto("/");
  const panel = page.getByTestId("error-panel");
  await expect(panel).toHaveAttribute("data-error-kind", "schema");
  await expect(panel).toContainText("Unexpected response from the lab");
  await expect(page.getByTestId("stat-total")).toHaveCount(0);

  await page.route("**/api/lab/workspace/requisitions", (route) => route.fulfill({ status: 200, contentType: "application/json", body: '{"items":"not-a-list"}' }));
  await page.goto("/requisitions");
  await expect(page.getByTestId("error-panel")).toContainText("Unexpected response");
});

test("UX-019 Long validation messages", async ({ page }) => {
  const long = `Line 10: description ${"is far too long and keeps going ".repeat(60)}end`;
  const unbroken = `REQ-${"X".repeat(400)}`;
  await page.route("**/api/lab/workspace/validate", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        approved: true,
        status: "Approved",
        valid: false,
        eligible: false,
        problems: [
          { reason: "SchemaInvalid", field: "lines[0].description", message: long, value: unbroken },
          { reason: "UnknownPlant", field: "lines[0].plant", message: `Plant '${unbroken}' is not recognised`, value: unbroken },
        ],
        sapPayload: null,
      }),
    }),
  );
  for (const width of [1366, 375]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/requisitions/new?template=valid");
    await page.getByRole("button", { name: "Validate" }).click();
    const result = page.getByTestId("validation-result");
    await expect(result).toContainText("is far too long");
    await expect(result).toContainText("UnknownPlant");
    await noHorizontalScroll(page);
  }
});

test("UX-020 Keyboard navigation", async ({ page, request }) => {
  await ensureSyncedWorkbook(request);
  await page.goto("/");
  await page.keyboard.press("Tab");
  await expect(page.getByRole("link", { name: "Skip to content" })).toBeFocused();
  let reached = false;
  for (let i = 0; i < 25 && !reached; i++) {
    await page.keyboard.press("Tab");
    reached = await page.evaluate(() => document.activeElement?.textContent?.trim() === "Requisitions");
  }
  expect(reached).toBe(true);
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/\/requisitions$/);
  await expect(page.getByTestId("table-count")).toBeVisible();
  const expand = page.getByRole("button", { name: "Expand REQ-7001" });
  await expand.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("button", { name: "Collapse REQ-7001" })).toHaveAttribute("aria-expanded", "true");
  await expect(page.getByText("Open full detail for REQ-7001")).toBeVisible();
  const outline = await page.evaluate(() => getComputedStyle(document.activeElement!).boxShadow);
  expect(outline).not.toBe("none");
});

test("UX-021 Screen reader accessibility", async ({ page, request }) => {
  test.setTimeout(180_000);
  await ensureSyncedWorkbook(request);
  const pages = ["/", "/requisitions", "/requisitions/REQ-7071", "/requisitions/new", "/control", "/quarantine", "/reconciliation", "/scenarios", "/failures", "/duplicates", "/assistant", "/generator", "/import", "/http"];
  const failures: string[] = [];
  for (const path of pages) {
    await page.goto(path);
    await expect(page.locator("main h1").first()).toBeVisible();
    await page.waitForLoadState("networkidle").catch(() => undefined);
    const results = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).exclude(".monaco-editor").analyze();
    for (const v of results.violations.filter((v) => v.impact === "serious" || v.impact === "critical")) {
      failures.push(`${path}: [${v.impact}] ${v.id} (${v.nodes.length}) ${v.nodes.slice(0, 3).map((n) => n.target.join(" ")).join(" | ")}`);
    }
  }
  expect(failures, failures.join("\n")).toEqual([]);
});

test("UX-022 Mobile/tablet layout", async ({ browser, request }) => {
  await ensureSyncedWorkbook(request);
  for (const viewport of [
    { width: 375, height: 812 },
    { width: 768, height: 1024 },
  ]) {
    const ctx = await browser.newContext({ viewport, isMobile: viewport.width < 768, hasTouch: viewport.width < 768 });
    const page = await ctx.newPage();
    for (const path of ["/", "/requisitions", "/control", "/scenarios", "/requisitions/new"]) {
      await page.goto(path);
      await expect(page.locator("main h1").first()).toBeVisible();
      await noHorizontalScroll(page);
    }
    await expect(page.getByTestId("isolated-badge")).toBeVisible();
    await page.getByRole("button", { name: "Open navigation" }).click();
    await page.getByRole("dialog").getByRole("link", { name: "Quarantine" }).click();
    await expect(page).toHaveURL(/\/quarantine$/);
    await expect(page.getByRole("heading", { name: "Quarantine" })).toBeVisible();
    await ctx.close();
  }
});

test("UX-023 Dark and light themes", async ({ browser }) => {
  const ctx = await browser.newContext({ colorScheme: "dark" });
  const page = await ctx.newPage();
  await page.goto("/");
  await expect(page.locator("html")).toHaveClass(/dark/);
  const darkBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  await page.getByTestId("theme-toggle").click();
  await page.getByRole("menuitem", { name: "Light" }).click();
  await expect(page.locator("html")).not.toHaveClass(/dark/);
  const lightBg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(lightBg).not.toBe(darkBg);
  await page.reload();
  await expect(page.locator("html")).not.toHaveClass(/dark/);
  await page.getByTestId("theme-toggle").click();
  await page.getByRole("menuitem", { name: "System" }).click();
  await expect(page.locator("html")).toHaveClass(/dark/);
  await ctx.close();
});

test("UX-024 Timezone differences", async ({ browser, request }) => {
  await ensureSyncedWorkbook(request);
  const read = async (timezoneId: string) => {
    const ctx = await browser.newContext({ timezoneId, locale: "en-US" });
    const page = await ctx.newPage();
    await page.goto("/requisitions?q=REQ-7001");
    const row = page.locator('tbody tr[data-row-id="REQ-7001"]');
    await expect(row).toBeVisible();
    const cells = await row.locator("td").allTextContents();
    await page.goto("/control");
    const started = await page.getByTestId("run-history").locator("tbody tr").first().locator("td").nth(2).textContent();
    await ctx.close();
    return { cells, started: started ?? "" };
  };
  const doha = await read("Asia/Qatar");
  const la = await read("America/Los_Angeles");
  // A calendar date (neededBy 2026-12-01) is the same everywhere; it must not shift a day.
  expect(doha.cells.join("|")).toContain("Dec 01, 2026");
  expect(la.cells.join("|")).toContain("Dec 01, 2026");
  // Timestamps are shown in the viewer's zone, with the zone named.
  expect(doha.started).toMatch(/GMT\+3/);
  expect(la.started).toMatch(/PST|PDT|GMT-8|GMT-7/);
  expect(doha.started).not.toBe(la.started);
});

test("UX-025 Locale-specific formatting", async ({ browser, request }) => {
  await ensureSyncedWorkbook(request);
  const amountText = async (locale: string) => {
    const ctx = await browser.newContext({ locale });
    const page = await ctx.newPage();
    await page.goto("/reconciliation");
    const text = await page.getByRole("heading", { name: "Currency totals (source vs SAP)" }).locator("xpath=ancestor::div[contains(@class,'rounded-lg')][1]").innerText();
    await ctx.close();
    return text;
  };
  const us = await amountText("en-US");
  const de = await amountText("de-DE");
  expect(us).toMatch(/QAR\s?3,881,360\.00/);
  expect(de).toMatch(/3\.881\.360,00\s?QAR/);
});

test("UX-026 Browser says success, backend fails", async ({ page, request }) => {
  await waitIdle(request);
  const runsBefore = (await (await request.get("/api/lab/workspace/runs")).json()).length;
  await page.route("**/api/lab/workspace/sync", (route) => route.abort("timedout"));
  await page.goto("/control");
  await page.getByTestId("run-sync").click();
  const status = page.getByTestId("action-status");
  await expect(status).toHaveAttribute("data-phase", "unknown");
  await expect(status).toContainText("Unknown outcome");
  await expect(status).not.toContainText("Confirmed");
  // The page re-read the lab: no run is shown as started or succeeded.
  expect((await (await request.get("/api/lab/workspace/runs")).json()).length).toBe(runsBefore);
  await expect(page.getByTestId("run-sync")).toHaveText(/Run sync/);

  // A backend error after the request reached it is reported as failed, never as success.
  await page.unroute("**/api/lab/workspace/sync");
  await page.route("**/api/lab/workspace/sync", (route) => route.fulfill(problem(500, "Internal error", "The lab failed to start the run")));
  await page.getByTestId("run-sync").click();
  await expect(status).toHaveAttribute("data-phase", "failed");
  await expect(status).toContainText("Internal error");
});
