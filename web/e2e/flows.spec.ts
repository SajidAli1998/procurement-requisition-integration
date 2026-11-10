import { expect, test } from "@playwright/test";
import { dashboard, resetWorkspace, syncAndWait, waitIdle } from "./helpers";

test("E2E-001 Run a sync from the control center and see dashboard totals change", async ({ page, request }) => {
  await resetWorkspace(request, "Workbook");
  await page.goto("/");
  await expect(page.getByTestId("stat-created").locator("[data-value]")).toHaveText("0");
  await expect(page.getByTestId("stat-quarantined").locator("[data-value]")).toHaveText("0");

  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Control center" }).click();
  await page.getByTestId("run-sync").click();
  const status = page.getByTestId("action-status");
  await expect(status).toHaveAttribute("data-phase", "confirmed");
  await expect(status).toContainText("accepted by the lab");
  await waitIdle(request);
  await expect(page.getByTestId("run-sync")).toHaveText(/Run sync/, { timeout: 30_000 });

  await page.getByRole("navigation", { name: "Main" }).getByRole("link", { name: "Dashboard" }).click();
  await expect(page.getByTestId("stat-created").locator("[data-value]")).toHaveText("58");
  await expect(page.getByTestId("stat-linked").locator("[data-value]")).toHaveText("7");
  await expect(page.getByTestId("stat-quarantined").locator("[data-value]")).toHaveText("50");
  await expect(page.getByTestId("stat-skipped").locator("[data-value]")).toHaveText("5");
  await expect(page.getByTestId("stat-duplicateReferencesInSap").locator("[data-value]")).toHaveText("0");
  const created = page.getByTestId("baseline-table").locator('[data-metric="Created"]');
  await expect(created.locator('[data-col="expected"]')).toHaveText("58");
  await expect(created.locator('[data-col="actual"]')).toHaveText("58");
  const d = await dashboard(request);
  expect(d.totals.created).toBe(58);
});

test("E2E-002 Run scenario VAL-001 and see a Pass result with evidence", async ({ page }) => {
  test.setTimeout(180_000);
  await page.goto("/scenarios");
  await page.getByLabel("Search scenarios").fill("VAL-001");
  const card = page.getByTestId("scenario-card-VAL-001");
  await expect(card).toBeVisible();
  await card.getByRole("button", { name: "Run VAL-001" }).click();
  const progress = page.getByTestId("batch-progress");
  await expect(progress).toBeVisible();
  await expect(progress).toContainText("Finished", { timeout: 150_000 });
  await expect(card.locator("[data-status]").first()).toHaveAttribute("data-status", "Pass", { timeout: 30_000 });
  await card.getByRole("button", { name: /Single valid approved requisition/ }).click();
  const drawer = page.getByRole("dialog");
  await expect(drawer.getByRole("heading", { name: "Assertions" })).toBeVisible();
  await expect(drawer.getByRole("heading", { name: "Invariants" })).toBeVisible();
  await expect(drawer.getByRole("heading", { name: "Evidence" })).toBeVisible();
  await expect(drawer).toContainText("created events");
  await expect(drawer).toContainText("INV-01");
});

test("E2E-003 Quarantine repair flow", async ({ page, request }) => {
  test.setTimeout(150_000);
  await resetWorkspace(request, "Workbook");
  await syncAndWait(request);
  const before = (await (await request.get("/api/lab/workspace/requisitions")).json()) as { id: string; outcome: string }[];
  expect(before.find((r) => r.id === "REQ-7071")?.outcome).toBe("Quarantined");

  await page.goto("/quarantine");
  await page.getByTestId("table-search").fill("REQ-7071");
  await page.getByRole("button", { name: "Open repair for REQ-7071" }).click();
  const repair = page.getByTestId("repair-REQ-7071");
  await expect(repair).toContainText("AmountNotPositive");
  await repair.getByLabel(/New value for totalAmount/i).fill("25452");
  await repair.getByRole("button", { name: "Apply edit to lab source" }).click();
  await expect(repair.getByTestId("action-status")).toHaveAttribute("data-phase", "confirmed");
  await repair.getByRole("button", { name: "Re-validate" }).click();
  await expect(repair.getByTestId("revalidation")).toContainText("Valid and eligible");
  await repair.getByRole("button", { name: "Resubmit through pipeline" }).click();
  await expect(repair.getByTestId("action-status")).toHaveAttribute("data-phase", "confirmed");
  await waitIdle(request);
  // The entry stays in view after the action and shows what the lab now reports.
  await expect(repair).toContainText("Resolved", { timeout: 30_000 });

  // The backend confirms the record left quarantine and reached SAP once.
  await expect
    .poll(async () => ((await (await request.get("/api/lab/workspace/requisitions")).json()) as { id: string; outcome: string }[]).find((r) => r.id === "REQ-7071")?.outcome)
    .toBe("Created");
  await page.reload();
  await page.getByTestId("table-search").fill("REQ-7071");
  await expect(page.getByRole("button", { name: "Open repair for REQ-7071" })).toHaveCount(0);
  await page.getByLabel("Filter by status").selectOption("Resolved");
  await expect(page.getByRole("button", { name: "Open repair for REQ-7071" })).toBeVisible();
  const detail = (await (await request.get("/api/lab/workspace/requisitions/REQ-7071")).json()) as { sapDocuments: unknown[] };
  expect(detail.sapDocuments).toHaveLength(1);
});

test("E2E-004 What if: the internet goes down mid-sync, then comes back", async ({ page }) => {
  test.setTimeout(240_000);
  await page.goto("/what-if");
  await page.getByRole("button", { name: "Try: The internet goes down mid-sync" }).click();
  const story = page.getByTestId("whatif-story");
  await expect(story).toBeVisible({ timeout: 180_000 });
  await expect(page.getByTestId("whatif-headline")).toContainText("Connection lost mid-sync");
  await expect(story).toHaveAttribute("data-safe", "true");
  await expect(page.getByTestId("whatif-numbers").locator('[data-metric="Waiting to be sent"] [data-col="after"]')).not.toHaveText("0");
  await expect(page.getByTestId("whatif-numbers").locator('[data-metric="Duplicates in SAP"] [data-col="after"]')).toHaveText("0");

  await page.getByTestId("whatif-next").click();
  await expect(page.getByTestId("whatif-headline")).toContainText("Back online", { timeout: 180_000 });
  await expect(page.getByTestId("whatif-story")).toHaveAttribute("data-safe", "true");
  await expect(page.getByTestId("whatif-numbers").locator('[data-metric="Created in SAP"] [data-col="after"]')).toHaveText("58");
  await expect(page.getByTestId("whatif-numbers").locator('[data-metric="Waiting to be sent"] [data-col="after"]')).toHaveText("0");
  await expect(page.getByTestId("whatif-numbers").locator('[data-metric="Duplicates in SAP"] [data-col="after"]')).toHaveText("0");
});

test("E2E-005 Try it yourself: pause, cut SAP, resume, reconnect, and see it worked", async ({ page, request }) => {
  test.setTimeout(240_000);
  await page.goto("/try");
  await page.getByTestId("try-reset").click();
  await expect(page.getByTestId("try-verdict")).toHaveAttribute("data-tone", "neutral", { timeout: 30_000 });
  await page.getByTestId("try-start").click();
  await expect(page.getByTestId("try-progress")).toHaveAttribute("data-state", "Running", { timeout: 30_000 });
  await page.getByTestId("try-pause").click();
  await expect(page.getByTestId("try-progress")).toHaveAttribute("data-state", "Paused", { timeout: 30_000 });
  await page.getByRole("group", { name: "Connection to SAP" }).getByRole("button", { name: "Down" }).click();
  await page.getByTestId("try-resume").click();
  await expect(page.getByTestId("try-verdict")).toHaveAttribute("data-tone", "warning", { timeout: 120_000 });
  await expect(page.getByTestId("try-verdict")).toContainText("switched off");
  await expect(page.getByTestId("try-verdict").locator('[data-metric="Duplicates in SAP"] dd')).toHaveText("0");

  await page.getByRole("group", { name: "Connection to SAP" }).getByRole("button", { name: "Working" }).click();
  await page.getByRole("group", { name: "Speed" }).getByRole("button", { name: "Full speed" }).click();
  for (let i = 0; i < 4; i++) {
    if ((await page.getByTestId("try-verdict").getAttribute("data-tone")) === "success") break;
    await expect(page.getByTestId("try-start")).toBeEnabled({ timeout: 60_000 });
    await page.getByTestId("try-start").click();
    await expect(page.getByTestId("try-progress")).not.toHaveAttribute("data-state", "Running", { timeout: 60_000 });
    await page.waitForTimeout(2500);
  }
  await expect(page.getByTestId("try-verdict")).toHaveAttribute("data-tone", "success", { timeout: 30_000 });
  await expect(page.getByTestId("try-verdict").locator('[data-metric="In SAP"] dd')).toHaveText("65");
  await expect(page.getByTestId("try-verdict").locator('[data-metric="Duplicates in SAP"] dd')).toHaveText("0");
  await expect(page.getByTestId("try-last-run")).toBeVisible();

  await expect(page.getByTestId("try-start")).toBeEnabled({ timeout: 60_000 });
  await page.getByRole("button", { name: "Run the sync again" }).click();
  await expect(page.getByTestId("try-last-run")).toContainText("nothing new to send", { timeout: 60_000 });
  void request;
});
