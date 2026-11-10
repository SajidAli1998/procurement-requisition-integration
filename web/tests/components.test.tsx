import { describe, expect, it, vi } from "vitest";
import { act, render, renderHook, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { OutcomeBadge, ScenarioStatusBadge } from "@/components/status-badge";
import { ErrorPanel } from "@/components/error-panel";
import { ActionStatus } from "@/components/action-status";
import { BaselineTable, baselineRows } from "@/components/dashboard/baseline-table";
import { DataTable, textFilter } from "@/components/data-table";
import { useLabAction } from "@/hooks/use-lab-action";
import { ApiError } from "@/lib/api";
import type { Baseline, DashboardTotals } from "@/lib/types";

function wrapper({ children }: { children: React.ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
}

describe("OutcomeBadge", () => {
  it("never marks quarantined or skipped as synchronised", () => {
    render(
      <>
        <OutcomeBadge outcome="Quarantined" />
        <OutcomeBadge outcome="Skipped" />
        <OutcomeBadge outcome="Created" />
      </>,
    );
    expect(screen.getByText("Quarantined").closest("[data-synchronised]")).toHaveAttribute("data-synchronised", "false");
    expect(screen.getByText("Skipped").closest("[data-synchronised]")).toHaveAttribute("data-synchronised", "false");
    expect(screen.getByText("Created").closest("[data-synchronised]")).toHaveAttribute("data-synchronised", "true");
  });

  it("renders markup in values as text", () => {
    render(<OutcomeBadge outcome={`<img src=x onerror="alert(1)">`} />);
    expect(screen.getByText(`<img src=x onerror="alert(1)">`)).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
  });
});

describe("ScenarioStatusBadge", () => {
  it("shows human labels", () => {
    render(<ScenarioStatusBadge status="NotImplemented" />);
    expect(screen.getByText("Not implemented")).toBeInTheDocument();
  });
});

describe("ErrorPanel", () => {
  it("explains a rejected lab key", () => {
    const err = new ApiError({ kind: "unauthorized", status: 401, message: "Lab key required", path: "/workspace/dashboard", method: "GET", problem: { title: "Lab key required", detail: "Send the X-Lab-Key header" } });
    render(<ErrorPanel error={err} />);
    expect(screen.getByText("Lab key rejected")).toBeInTheDocument();
    expect(screen.getByText(/TESTLAB_KEY does not match/)).toBeInTheDocument();
    expect(screen.getByText("GET /api/lab/workspace/dashboard")).toBeInTheDocument();
  });

  it("explains an unavailable API and offers retry", async () => {
    const onRetry = vi.fn();
    const err = new ApiError({ kind: "http", status: 502, message: "down", path: "/health", method: "GET", problem: { title: "Test Lab API unavailable", detail: "connect ECONNREFUSED" } });
    render(<ErrorPanel error={err} onRetry={onRetry} />);
    expect(screen.getByText("Test Lab API unavailable")).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /retry/i }));
    expect(onRetry).toHaveBeenCalledOnce();
  });
});

describe("ActionStatus", () => {
  it("labels unknown outcomes distinctly from success", () => {
    const { rerender } = render(<ActionStatus phase="unknown" message="no answer" />);
    expect(screen.getByRole("status")).toHaveTextContent("Unknown outcome");
    rerender(<ActionStatus phase="confirming" message="checking" />);
    expect(screen.getByRole("status")).toHaveTextContent("Pending confirmation");
    rerender(<ActionStatus phase="confirmed" message="ok" />);
    expect(screen.getByRole("status")).toHaveTextContent("Confirmed by lab");
  });
});

describe("useLabAction", () => {
  it("goes sending → confirming → confirmed only after the re-read", async () => {
    let resolveRun!: (v: string) => void;
    const { result } = renderHook(
      () => useLabAction<void, string>({ name: "Run sync", run: () => new Promise<string>((r) => (resolveRun = r)), invalidate: [], confirm: (r) => `run ${r} accepted` }),
      { wrapper },
    );
    let p!: Promise<unknown>;
    act(() => {
      p = result.current.execute();
    });
    expect(result.current.phase).toBe("sending");
    expect(result.current.pending).toBe(true);
    await act(async () => {
      resolveRun("ws-1");
      await p;
    });
    expect(result.current.phase).toBe("confirmed");
    expect(result.current.message).toBe("run ws-1 accepted");
  });

  it("ignores a second click while the first is in flight", async () => {
    const run = vi.fn(() => new Promise<void>((r) => setTimeout(r, 20)));
    const { result } = renderHook(() => useLabAction({ name: "x", run, invalidate: [] }), { wrapper });
    await act(async () => {
      await Promise.all([result.current.execute(), result.current.execute(), result.current.execute()]);
    });
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("reports unknown (not failed, not success) when the network drops", async () => {
    const { result } = renderHook(
      () =>
        useLabAction({
          name: "Run sync",
          invalidate: [],
          run: () => Promise.reject(new ApiError({ kind: "network", status: 0, message: "network error", path: "/workspace/sync", method: "POST" })),
        }),
      { wrapper },
    );
    await act(async () => {
      await result.current.execute();
    });
    expect(result.current.phase).toBe("unknown");
    expect(result.current.message).toMatch(/may or may not have been applied/);
  });

  it("reports failed for a definite error such as 409", async () => {
    const { result } = renderHook(
      () => useLabAction({ name: "Run sync", invalidate: [], run: () => Promise.reject(new ApiError({ kind: "conflict", status: 409, message: "A run is already in progress", path: "/workspace/sync", method: "POST" })) }),
      { wrapper },
    );
    await act(async () => {
      await result.current.execute();
    });
    expect(result.current.phase).toBe("failed");
  });
});

describe("BaselineTable", () => {
  const baseline: Baseline = {
    source: "workbook.xlsx",
    metrics: [
      { metric: "Total", expected: "120" },
      { metric: "Created", expected: "58" },
      { metric: "AI contract tests", expected: "60/60" },
    ],
  };
  const totals = { total: 120, created: 57 } as DashboardTotals;

  it("shows expected and actual side by side and never copies the fixture into actual", () => {
    render(<BaselineTable rows={baselineRows(baseline, totals, null, "Workbook")} source={baseline.source} />);
    const table = screen.getByTestId("baseline-table");
    expect(within(table).getByText("Expected (client workbook)")).toBeInTheDocument();
    expect(within(table).getByText("Actual (lab)")).toBeInTheDocument();
    const created = table.querySelector('[data-metric="Created"]')!;
    expect(created.querySelector('[data-col="expected"]')).toHaveTextContent("58");
    expect(created.querySelector('[data-col="actual"]')).toHaveTextContent("57");
    expect(within(created as HTMLElement).getByLabelText("differs")).toBeInTheDocument();
    const ai = table.querySelector('[data-metric="AI contract tests"]')!;
    expect(ai.querySelector('[data-col="actual"]')).toHaveTextContent(/not measured/);
  });

  it("shows nothing as actual when there are no totals yet", () => {
    const rows = baselineRows(baseline, null, null, "Workbook");
    expect(rows.every((r) => r.actual === null)).toBe(true);
  });
});

describe("DataTable", () => {
  type Row = { id: string; outcome: string };
  const data: Row[] = Array.from({ length: 30 }, (_, i) => ({ id: `REQ-${String(i + 1).padStart(3, "0")}`, outcome: i % 3 === 0 ? "Quarantined" : "Created" }));
  const columns: ColumnDef<Row, unknown>[] = [
    { id: "id", accessorKey: "id", header: "ID", meta: { label: "ID", filter: "text" }, filterFn: textFilter },
    { id: "outcome", accessorKey: "outcome", header: "Outcome", meta: { label: "Outcome" } },
  ];

  it("searches, paginates and resets to page 1 when filtering", async () => {
    const user = userEvent.setup();
    render(<DataTable data={data} columns={columns} getRowId={(r) => r.id} initialPageSize={10} testId="t" />);
    expect(screen.getByTestId("table-count")).toHaveTextContent("30 rows");
    expect(screen.getByTestId("table-page")).toHaveTextContent("Page 1 of 3");
    await user.click(screen.getByRole("button", { name: "Next page" }));
    expect(screen.getByTestId("table-page")).toHaveTextContent("Page 2 of 3");
    await user.type(screen.getByLabelText("Search table"), "Quarantined");
    await waitFor(() => expect(screen.getByTestId("table-count")).toHaveTextContent("10 of 30 rows"));
    expect(screen.getByTestId("table-page")).toHaveTextContent("Page 1 of 1");
  });

  it("sorts by a column header", async () => {
    const user = userEvent.setup();
    render(<DataTable data={data} columns={columns} getRowId={(r) => r.id} initialPageSize={10} />);
    await user.click(screen.getByRole("button", { name: /^ID/ }));
    await user.click(screen.getByRole("button", { name: /^ID/ }));
    const firstRow = document.querySelector("tbody tr");
    expect(firstRow).toHaveAttribute("data-row-id", "REQ-030");
  });
});
