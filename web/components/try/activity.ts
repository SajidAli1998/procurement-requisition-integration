import type { HttpExchange } from "@/lib/types";

interface ActivityLine {
  seq: number;
  at: string;
  text: string;
  tone: "ok" | "warn" | "bad" | "plain";
}

function referenceOf(body: string | null | undefined): string | null {
  if (!body) return null;
  try {
    const parsed = JSON.parse(body) as Record<string, unknown>;
    const ref = parsed.ExternalReference ?? parsed.externalReference;
    return typeof ref === "string" ? ref : null;
  } catch {
    return null;
  }
}

function lookupReference(url: string): string | null {
  const match = /ExternalReference eq '([^']+)'/.exec(decodeURIComponent(url));
  return match?.[1]?.replace(/''/g, "'") ?? null;
}

function foundCount(body: string | null | undefined): number | null {
  if (!body) return null;
  try {
    const parsed = JSON.parse(body) as { value?: unknown[] };
    return Array.isArray(parsed.value) ? parsed.value.length : null;
  } catch {
    return null;
  }
}

export function describe(e: HttpExchange): ActivityLine | null {
  const base = { seq: e.seq, at: e.at };
  const noAnswer = e.status == null;
  if (e.channel.endsWith("→sap")) {
    if (e.method === "POST") {
      const ref = referenceOf(e.requestBody) ?? "a requisition";
      if (noAnswer) return { ...base, tone: "bad", text: `Sent ${ref} to SAP, but no answer came back (connection lost)` };
      if (e.status === 201) return { ...base, tone: "ok", text: `${ref} created in SAP` };
      if (e.status === 409) return { ...base, tone: "warn", text: `SAP says ${ref} already exists` };
      if (e.status === 429) return { ...base, tone: "warn", text: `SAP asked to slow down before taking ${ref}` };
      return { ...base, tone: "bad", text: `SAP refused ${ref} (HTTP ${e.status})` };
    }
    if (e.method === "PATCH") return { ...base, tone: noAnswer || (e.status ?? 0) >= 400 ? "bad" : "ok", text: noAnswer ? "Update to SAP got no answer" : `Updated an existing SAP document (HTTP ${e.status})` };
    const ref = lookupReference(e.url);
    if (ref) {
      if (noAnswer) return { ...base, tone: "bad", text: `Could not reach SAP to look up ${ref}` };
      const n = foundCount(e.responseBody);
      return { ...base, tone: n ? "warn" : "plain", text: n ? `Looked up ${ref} in SAP: already there, so it is linked, not created again` : `Looked up ${ref} in SAP: not there yet` };
    }
    if (noAnswer) return { ...base, tone: "bad", text: "Could not reach SAP" };
    return null;
  }
  if (e.channel.endsWith("→source")) {
    if (noAnswer) return { ...base, tone: "bad", text: "Could not reach the source system" };
    if (e.method === "GET" && /\/api\/requisitions\?/.test(e.url)) return { ...base, tone: "plain", text: "Read a page of requisitions from the source system" };
    if (e.method === "PUT") return { ...base, tone: "plain", text: "Wrote the SAP status back to the source system" };
  }
  return null;
}

export function collapse(lines: ActivityLine[]): (ActivityLine & { times: number })[] {
  const out: (ActivityLine & { times: number })[] = [];
  for (const line of lines) {
    const prev = out[out.length - 1];
    if (prev && prev.text === line.text) prev.times++;
    else out.push({ ...line, times: 1 });
  }
  return out;
}
