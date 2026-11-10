"use client";

import {
  Area,
  AreaChart,
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  ComposedChart,
  Legend,
  Line,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import type { DashboardRun, NameValue, ScenarioHistoryPoint } from "@/lib/types";
import { fmtTime } from "@/lib/format";

const OUTCOME_COLOURS: Record<string, string> = {
  Created: "var(--chart-1)",
  Linked: "var(--chart-2)",
  Quarantined: "var(--chart-3)",
  Skipped: "var(--chart-4)",
  Pending: "var(--chart-5)",
};
const PALETTE = ["var(--chart-1)", "var(--chart-2)", "var(--chart-3)", "var(--chart-4)", "var(--chart-5)", "var(--chart-6)", "var(--chart-7)"];

const tooltipStyle = {
  contentStyle: { background: "var(--popover)", border: "1px solid var(--border)", borderRadius: 6, color: "var(--popover-foreground)", fontSize: 12 },
  itemStyle: { color: "var(--popover-foreground)" },
  labelStyle: { color: "var(--muted-foreground)" },
};
const axis = { stroke: "var(--muted-foreground)", fontSize: 11, tickLine: false };

function NoData({ text = "No data yet" }: { text?: string }) {
  return <div className="flex h-full min-h-40 items-center justify-center text-sm text-muted-foreground">{text}</div>;
}

function SrTable({ caption, rows }: { caption: string; rows: [string, string | number][] }) {
  return (
    <table className="sr-only">
      <caption>{caption}</caption>
      <tbody>
        {rows.map(([k, v], i) => (
          // Labels can repeat (several batches share a name), so the position is part of the key.
          <tr key={`${i}-${k}`}>
            <th scope="row">{k}</th>
            <td>{v}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function OutcomesChart({ data }: { data: NameValue[] }) {
  const total = data.reduce((s, d) => s + d.value, 0);
  if (total === 0) return <NoData text="No outcomes yet: run a sync" />;
  return (
    <div className="grid gap-2 sm:grid-cols-2">
      <div className="h-52" aria-hidden>
        <ResponsiveContainer width="100%" height="100%">
          <PieChart accessibilityLayer={false}>
            <Pie data={data} dataKey="value" nameKey="name" innerRadius="55%" outerRadius="85%" paddingAngle={1} isAnimationActive={false} rootTabIndex={-1}>
              {data.map((d) => (
                <Cell key={d.name} fill={OUTCOME_COLOURS[d.name] ?? "var(--chart-4)"} />
              ))}
            </Pie>
            <Tooltip {...tooltipStyle} />
          </PieChart>
        </ResponsiveContainer>
      </div>
      <div className="h-52" aria-hidden>
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={data} layout="vertical" margin={{ left: 8, right: 16 }} accessibilityLayer={false}>
            <XAxis type="number" {...axis} allowDecimals={false} />
            <YAxis type="category" dataKey="name" {...axis} width={80} />
            <Tooltip {...tooltipStyle} cursor={{ fill: "var(--muted)" }} />
            <Bar dataKey="value" isAnimationActive={false} radius={[0, 3, 3, 0]}>
              {data.map((d) => (
                <Cell key={d.name} fill={OUTCOME_COLOURS[d.name] ?? "var(--chart-4)"} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </div>
      <SrTable caption="Outcomes" rows={data.map((d) => [d.name, d.value])} />
    </div>
  );
}

export function NameValueBar({ data, caption, empty, colourByIndex }: { data: NameValue[]; caption: string; empty: string; colourByIndex?: boolean }) {
  if (data.length === 0) return <NoData text={empty} />;
  return (
    <>
      <div className="h-52" aria-hidden>
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={data} margin={{ left: -16, right: 8 }} accessibilityLayer={false}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" vertical={false} />
            <XAxis dataKey="name" {...axis} interval={0} angle={data.length > 4 ? -20 : 0} textAnchor={data.length > 4 ? "end" : "middle"} height={data.length > 4 ? 56 : 24} />
            <YAxis {...axis} allowDecimals={false} />
            <Tooltip {...tooltipStyle} cursor={{ fill: "var(--muted)" }} />
            <Bar dataKey="value" isAnimationActive={false} radius={[3, 3, 0, 0]}>
              {data.map((d, i) => (
                <Cell key={d.name} fill={colourByIndex ? PALETTE[i % PALETTE.length] : statusColour(d.name)} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </div>
      <SrTable caption={caption} rows={data.map((d) => [d.name, d.value])} />
    </>
  );
}

function statusColour(name: string): string {
  const n = Number(name);
  if (!Number.isFinite(n)) return "var(--chart-7)";
  if (n >= 200 && n < 300) return "var(--chart-1)";
  if (n === 409 || n === 429) return "var(--chart-5)";
  return "var(--chart-3)";
}

export function RunsChart({ runs }: { runs: DashboardRun[] }) {
  if (runs.length === 0) return <NoData text="No finished runs yet" />;
  const data = runs.map((r) => ({ name: fmtTime(r.startedAt), throughput: r.throughput, durationS: r.durationMs ? Math.round(r.durationMs / 100) / 10 : 0 }));
  return (
    <>
      <div className="h-56" aria-hidden>
        <ResponsiveContainer width="100%" height="100%">
          <ComposedChart data={data} margin={{ left: -8, right: -8 }} accessibilityLayer={false}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" vertical={false} />
            <XAxis dataKey="name" {...axis} />
            <YAxis yAxisId="l" {...axis} label={{ value: "records/s", angle: -90, position: "insideLeft", fill: "var(--muted-foreground)", fontSize: 10 }} />
            <YAxis yAxisId="r" orientation="right" {...axis} label={{ value: "seconds", angle: 90, position: "insideRight", fill: "var(--muted-foreground)", fontSize: 10 }} />
            <Tooltip {...tooltipStyle} />
            <Legend wrapperStyle={{ fontSize: 12 }} />
            <Bar yAxisId="l" dataKey="throughput" name="Throughput (records/s)" fill="var(--chart-2)" isAnimationActive={false} radius={[3, 3, 0, 0]} />
            <Line yAxisId="r" dataKey="durationS" name="Duration (s)" stroke="var(--chart-6)" strokeWidth={2} dot isAnimationActive={false} />
          </ComposedChart>
        </ResponsiveContainer>
      </div>
      <SrTable caption="Runs" rows={runs.map((r) => [`${r.id} ${r.kind}`, `${r.throughput}/s, ${r.durationMs ?? 0} ms, ${r.outcome ?? ""}`])} />
    </>
  );
}

export function ScenarioHistoryChart({ history }: { history: ScenarioHistoryPoint[] }) {
  if (history.length === 0) return <NoData text="No scenario batches yet: open the scenario runner" />;
  const data = history.map((h) => ({
    name: `${fmtTime(h.startedAt)}`,
    batch: h.name,
    Passed: h.tally.passed,
    Failed: h.tally.failed,
    Blocked: h.tally.blocked,
    "Not run": h.tally.notRun + h.tally.notImplemented,
  }));
  return (
    <>
      <div className="h-56" aria-hidden>
        <ResponsiveContainer width="100%" height="100%">
          <AreaChart data={data} margin={{ left: -16, right: 8 }} accessibilityLayer={false}>
            <CartesianGrid strokeDasharray="3 3" stroke="var(--border)" vertical={false} />
            <XAxis dataKey="name" {...axis} />
            <YAxis {...axis} allowDecimals={false} />
            <Tooltip {...tooltipStyle} labelFormatter={(_, p) => (p?.[0]?.payload?.batch as string) ?? ""} />
            <Legend wrapperStyle={{ fontSize: 12 }} />
            <Area type="monotone" stackId="1" dataKey="Passed" stroke="var(--chart-1)" fill="var(--chart-1)" fillOpacity={0.5} isAnimationActive={false} />
            <Area type="monotone" stackId="1" dataKey="Failed" stroke="var(--chart-3)" fill="var(--chart-3)" fillOpacity={0.5} isAnimationActive={false} />
            <Area type="monotone" stackId="1" dataKey="Blocked" stroke="var(--chart-5)" fill="var(--chart-5)" fillOpacity={0.5} isAnimationActive={false} />
            <Area type="monotone" stackId="1" dataKey="Not run" stroke="var(--chart-4)" fill="var(--chart-4)" fillOpacity={0.3} isAnimationActive={false} />
          </AreaChart>
        </ResponsiveContainer>
      </div>
      <SrTable caption="Scenario history" rows={history.map((h) => [h.name, `${h.tally.passed} passed, ${h.tally.failed} failed, ${h.tally.blocked} blocked`])} />
    </>
  );
}
