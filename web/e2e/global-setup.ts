export default async function globalSetup() {
  const url = `${process.env.TESTLAB_URL ?? "http://localhost:5110"}/api/health`;
  try {
    const res = await fetch(url, { signal: AbortSignal.timeout(5000) });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
  } catch (e) {
    throw new Error(`The e2e suite needs the Test Lab API at ${url} (${e instanceof Error ? e.message : String(e)}). Start it first (see README).`);
  }
}
