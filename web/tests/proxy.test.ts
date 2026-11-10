import { describe, expect, it } from "vitest";
import { ProxyPathError, buildResponseHeaders, buildUpstreamHeaders, buildUpstreamUrl, isEventStream, problemJson } from "@/lib/proxy";

describe("buildUpstreamUrl", () => {
  it("joins segments under /api and keeps the query string", () => {
    expect(buildUpstreamUrl("http://localhost:5110/", ["workspace", "requisitions", "REQ-7001"], "?version=3")).toBe(
      "http://localhost:5110/api/workspace/requisitions/REQ-7001?version=3",
    );
    expect(buildUpstreamUrl("http://lab:5110", ["batches", "live", "pending-1"], "")).toBe("http://lab:5110/api/batches/live/pending-1");
  });

  it("re-encodes segments so spaces and unicode stay inside one segment", () => {
    expect(buildUpstreamUrl("http://x", ["workspace", "requisitions", "REQ 1é"], "")).toBe("http://x/api/workspace/requisitions/REQ%201%C3%A9");
  });

  it.each([[[".."]], [["workspace", ".."]], [["a%2Fb"]], [["a\\b"]], [[""]], [[]], [["%00"]]])("refuses unsafe path %j", (segments) => {
    expect(() => buildUpstreamUrl("http://x", segments, "")).toThrow(ProxyPathError);
  });
});

describe("buildUpstreamHeaders", () => {
  it("adds the server lab key and drops cookies, auth and any client-supplied key", () => {
    const incoming = new Headers({
      "content-type": "application/json",
      cookie: "session=1",
      authorization: "Bearer stolen",
      "x-lab-key": "client-guess",
      accept: "text/event-stream",
    });
    const out = buildUpstreamHeaders(incoming, "server-key");
    expect(out.get("x-lab-key")).toBe("server-key");
    expect(out.get("content-type")).toBe("application/json");
    expect(out.get("accept")).toBe("text/event-stream");
    expect(out.get("cookie")).toBeNull();
    expect(out.get("authorization")).toBeNull();
  });

  it("sends no key when none is configured", () => {
    expect(buildUpstreamHeaders(new Headers({ "x-lab-key": "client" }), undefined).get("x-lab-key")).toBeNull();
  });
});

describe("buildResponseHeaders", () => {
  it("never echoes a lab key and strips hop-by-hop headers", () => {
    const out = buildResponseHeaders(new Headers({ "x-lab-key": "k", connection: "keep-alive", "content-type": "application/json", "set-cookie": "a=b", "content-disposition": "attachment" }));
    expect(out.get("x-lab-key")).toBeNull();
    expect(out.get("connection")).toBeNull();
    expect(out.get("set-cookie")).toBeNull();
    expect(out.get("content-disposition")).toBe("attachment");
  });

  it("marks event streams as no-transform so they are not buffered", () => {
    const h = new Headers({ "content-type": "text/event-stream" });
    expect(isEventStream(h)).toBe(true);
    const out = buildResponseHeaders(h);
    expect(out.get("cache-control")).toContain("no-transform");
    expect(out.get("x-accel-buffering")).toBe("no");
  });
});

describe("problemJson", () => {
  it("returns RFC 7807 problem details", async () => {
    const r = problemJson(502, "Test Lab API unavailable", "down");
    expect(r.status).toBe(502);
    expect(r.headers.get("content-type")).toContain("problem+json");
    expect(await r.json()).toMatchObject({ status: 502, title: "Test Lab API unavailable", detail: "down" });
  });
});
