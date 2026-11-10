import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DELETE, GET, POST } from "@/app/api/lab/[...path]/route";

const ctx = (path: string[]) => ({ params: Promise.resolve({ path }) });

beforeEach(() => {
  vi.stubEnv("TESTLAB_URL", "http://lab.test:5110");
  vi.stubEnv("TESTLAB_KEY", "secret-key");
});
afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe("/api/lab proxy route", () => {
  it("forwards method, path, query and body with the server-held key", async () => {
    const fetch = vi.fn(async () => new Response('{"ok":true}', { status: 202, headers: { "content-type": "application/json" } }));
    vi.stubGlobal("fetch", fetch);
    const res = await POST(new Request("http://localhost:3000/api/lab/workspace/replay?x=1", { method: "POST", body: '{"ids":["A"]}', headers: { "content-type": "application/json", "x-lab-key": "browser-guess" } }), ctx(["workspace", "replay"]));
    expect(res.status).toBe(202);
    const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("http://lab.test:5110/api/workspace/replay?x=1");
    expect(init.method).toBe("POST");
    expect(new Headers(init.headers).get("x-lab-key")).toBe("secret-key");
    expect(await new Response(init.body).text()).toBe('{"ids":["A"]}');
  });

  it("streams server-sent events through unchanged", async () => {
    const stream = new ReadableStream({
      start(c) {
        c.enqueue(new TextEncoder().encode('event: hello\ndata: {"dataset":"Workbook"}\n\n'));
        c.close();
      },
    });
    vi.stubGlobal("fetch", vi.fn(async () => new Response(stream, { headers: { "content-type": "text/event-stream" } })));
    const res = await GET(new Request("http://localhost:3000/api/lab/workspace/live"), ctx(["workspace", "live"]));
    expect(res.headers.get("content-type")).toBe("text/event-stream");
    expect(res.headers.get("cache-control")).toContain("no-transform");
    expect(await res.text()).toContain("event: hello");
  });

  it("answers 502 problem details when the lab is down", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => Promise.reject(new TypeError("fetch failed", { cause: new Error("connect ECONNREFUSED") }))));
    const res = await GET(new Request("http://localhost:3000/api/lab/health"), ctx(["health"]));
    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.title).toBe("Test Lab API unavailable");
    expect(body.detail).toContain("ECONNREFUSED");
    expect(JSON.stringify(body)).not.toContain("secret-key");
  });

  it("refuses path traversal", async () => {
    vi.stubGlobal("fetch", vi.fn());
    const res = await DELETE(new Request("http://localhost:3000/api/lab/x", { method: "DELETE" }), ctx(["..", "admin"]));
    expect(res.status).toBe(400);
  });
});
