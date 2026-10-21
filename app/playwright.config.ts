import { defineConfig, devices } from "@playwright/test";

/**
 * End-to-end suite (T3/PR2) — the carnival-day journeys on the devices a
 * carnival is actually run from: school iPads first, phones second, a laptop
 * for the administrator. Deliberately NOT part of `npm test` (vitest): these
 * need the real API + Vite dev loop, so they are their own command
 * (`npm run test:e2e`) and their own directory (excluded in vite.config.ts).
 *
 * The whole loop is the throwaway SQLite dev stack described in
 * docs/development/local-dev.md — dev header auth, synthetic seed, no Docker. The
 * specs seed their own carnival through the API before driving the UI.
 */

// Overridable: 5173 is a shared default, and another project's dev server on
// it would otherwise be reused and driven instead of this app.
const APP_URL = process.env.SPORTS_APP_URL ?? "http://localhost:5173";
const API_URL = "http://localhost:5080";

export default defineConfig({
  testDir: "./e2e",
  // The UI review pass shares this directory and this dev loop but is NOT a
  // journey: it produces screenshots for a human to look at, and it must not be
  // able to fail (or lengthen) the gate. It runs from its own config —
  // playwright.screenshots.config.ts, `npm run screenshots`.
  testIgnore: /screenshots\.spec\.ts$/,
  // One writer at a time: the dev loop is a single API over a single SQLite
  // file, so parallel workers would race each other's seed, not find bugs.
  workers: 1,
  fullyParallel: false,
  // No retries on purpose — a journey that only passes on the second attempt is
  // a flaky journey, and hiding that is exactly what this suite must not do.
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 20_000 },
  reporter: [["list"]],
  use: {
    baseURL: APP_URL,
    trace: "retain-on-failure",
    // The offline journey types into the deck's time field; a screenshot of the
    // failing state is worth more than the DOM dump alone.
    screenshot: "only-on-failure",
  },
  projects: [
    // The pilot's primary device: an iPad in landscape on the pool deck, where
    // the two-column deck row (U10) applies.
    { name: "ipad", use: { ...devices["iPad (gen 7) landscape"] } },
    // Under the 600px breakpoint, where the row is one column per line.
    { name: "iphone", use: { ...devices["iPhone 14"] } },
    { name: "pixel", use: { ...devices["Pixel 7"] } },
    { name: "desktop", use: { ...devices["Desktop Chrome"] } },
  ],
  webServer: [
    {
      // Same thing scripts/dev-run.ps1 starts, minus the extra window: the
      // `http` launch profile is what sets Development + the synthetic seed
      // (api/.../Properties/launchSettings.json).
      command: "dotnet run --launch-profile http",
      cwd: "../api/src/CarnivalApp.Api",
      url: `${API_URL}/health`,
      // A dev loop already running (scripts/dev-run.ps1) is reused; on a bare
      // machine — or a CI runner — nothing is listening and both are started.
      reuseExistingServer: true,
      timeout: 180_000,
    },
    {
      // The port comes from APP_URL so an override cannot start Vite on one
      // port and then drive another project's server on the default.
      // Default-port URLs (http://host/) parse to an EMPTY port, which would
      // hand Vite `--port --strictPort` and make it read the flag as the port.
      command: `npm run dev -- --port ${new URL(APP_URL).port || "5173"} --strictPort`,
      url: APP_URL,
      reuseExistingServer: true,
      timeout: 120_000,
    },
  ],
});
