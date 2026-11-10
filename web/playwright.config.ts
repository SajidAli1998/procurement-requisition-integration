import { defineConfig, devices } from "@playwright/test";

// The e2e suite drives the real web app against the real Test Lab API (http://localhost:5110 by default).
// The scenario engine reads ../reports/playwright/results.json and matches tests by their ID prefix, such as UX-001.
const PORT = Number(process.env.WEB_PORT ?? 3000);

export default defineConfig({
  testDir: "./e2e",
  // One lab workspace is shared by every test, so tests run one at a time.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  globalSetup: "./e2e/global-setup.ts",
  reporter: [["list"], ["json", { outputFile: "../reports/playwright/results.json" }]],
  use: {
    baseURL: `http://localhost:${PORT}`,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    acceptDownloads: true,
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1366, height: 900 } } }],
  webServer: {
    command: `npm run build && npm run start -- -p ${PORT}`,
    url: `http://localhost:${PORT}`,
    reuseExistingServer: !process.env.CI,
    timeout: 240_000,
    env: {
      TESTLAB_URL: process.env.TESTLAB_URL ?? "http://localhost:5110",
      TESTLAB_KEY: process.env.TESTLAB_KEY ?? "lab-dev-key-0123456789",
    },
  },
});
