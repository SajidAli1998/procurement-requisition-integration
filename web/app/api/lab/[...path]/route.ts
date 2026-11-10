import {
  DEFAULT_TESTLAB_URL,
  ProxyPathError,
  buildResponseHeaders,
  buildUpstreamHeaders,
  buildUpstreamUrl,
  problemJson,
} from "@/lib/proxy";

// The lab key (TESTLAB_KEY) is added here and never reaches the browser.

export const dynamic = "force-dynamic";
export const runtime = "nodejs";

type Context = { params: Promise<{ path: string[] }> };

async function forward(request: Request, context: Context): Promise<Response> {
  const { path } = await context.params;
  const base = process.env.TESTLAB_URL ?? DEFAULT_TESTLAB_URL;
  const key = process.env.TESTLAB_KEY;

  let url: string;
  try {
    url = buildUpstreamUrl(base, path ?? [], new URL(request.url).search);
  } catch (e) {
    if (e instanceof ProxyPathError) return problemJson(400, "Invalid lab path", e.message);
    throw e;
  }

  const method = request.method.toUpperCase();
  const hasBody = method !== "GET" && method !== "HEAD" && request.body !== null;
  const init: RequestInit & { duplex?: "half" } = {
    method,
    headers: buildUpstreamHeaders(request.headers, key),
    body: hasBody ? request.body : undefined,
    redirect: "manual",
    cache: "no-store",
    // Abort the upstream call (and close an SSE stream) when the browser goes away.
    signal: request.signal,
  };
  if (hasBody) init.duplex = "half";

  let upstream: Response;
  try {
    upstream = await fetch(url, init);
  } catch (e) {
    if (request.signal.aborted) return new Response(null, { status: 499 });
    const reason = e instanceof Error ? (e.cause instanceof Error ? e.cause.message : e.message) : String(e);
    return problemJson(
      502,
      "Test Lab API unavailable",
      `The web server could not reach the Test Lab API at ${base} (${reason}). Start it and retry.`,
    );
  }

  return new Response(upstream.body, {
    status: upstream.status,
    statusText: upstream.statusText,
    headers: buildResponseHeaders(upstream.headers),
  });
}

export const GET = forward;
export const POST = forward;
export const PUT = forward;
export const DELETE = forward;
