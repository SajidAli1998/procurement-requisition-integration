// No Next.js imports here, so these can be unit tested.

const LAB_KEY_HEADER = "X-Lab-Key";
export const DEFAULT_TESTLAB_URL = "http://localhost:5110";

// Cookies, auth and any lab key sent by the browser are dropped.
const REQUEST_HEADER_ALLOWLIST = new Set([
  "accept",
  "content-type",
  "content-length",
  "if-match",
  "if-none-match",
  "last-event-id",
  "x-correlation-id",
]);

const RESPONSE_HEADER_BLOCKLIST = new Set([
  "connection",
  "keep-alive",
  "transfer-encoding",
  "content-encoding",
  "content-length",
  "upgrade",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailer",
  "set-cookie",
  "server",
]);

export class ProxyPathError extends Error {}

// Only /api/** on the lab host is reachable: no "..", empty segments, smuggled slashes or control characters.
export function buildUpstreamUrl(baseUrl: string, segments: string[], search: string): string {
  if (segments.length === 0) throw new ProxyPathError("Empty path");
  const safe = segments.map((raw) => {
    let s = raw;
    try {
      s = decodeURIComponent(raw);
    } catch {
      // Keep the raw value; it is re-encoded below.
    }
    if (s === "" || s === "." || s === ".." || /[\/\\]/.test(s) || /[\u0000-\u001f\u007f]/.test(s)) {
      throw new ProxyPathError(`Invalid path segment: ${raw}`);
    }
    return encodeURIComponent(s);
  });
  const base = baseUrl.replace(/\/+$/, "");
  const query = search && search !== "?" ? (search.startsWith("?") ? search : `?${search}`) : "";
  return `${base}/api/${safe.join("/")}${query}`;
}

export function buildUpstreamHeaders(incoming: Headers, labKey: string | undefined): Headers {
  const out = new Headers();
  incoming.forEach((value, key) => {
    if (REQUEST_HEADER_ALLOWLIST.has(key.toLowerCase())) out.set(key, value);
  });
  // fetch recomputes content-length when streaming a body.
  out.delete("content-length");
  if (labKey) out.set(LAB_KEY_HEADER, labKey);
  return out;
}

export function buildResponseHeaders(upstream: Headers): Headers {
  const out = new Headers();
  upstream.forEach((value, key) => {
    const k = key.toLowerCase();
    if (RESPONSE_HEADER_BLOCKLIST.has(k) || k === LAB_KEY_HEADER.toLowerCase()) return;
    out.set(key, value);
  });
  const type = out.get("content-type") ?? "";
  if (type.includes("text/event-stream")) {
    // no-transform stops any compression layer from buffering the stream.
    out.set("Cache-Control", "no-cache, no-transform");
    out.set("X-Accel-Buffering", "no");
  }
  return out;
}

export function isEventStream(headers: Headers): boolean {
  return (headers.get("content-type") ?? "").includes("text/event-stream");
}

export function problemJson(status: number, title: string, detail: string): Response {
  return new Response(JSON.stringify({ type: "about:blank", status, title, detail, source: "web-proxy" }), {
    status,
    headers: { "content-type": "application/problem+json", "cache-control": "no-store" },
  });
}
