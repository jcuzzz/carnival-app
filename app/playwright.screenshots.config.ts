import { defineConfig, devices } from "@playwright/test";
import base from "./playwright.config";
import { FIXED, FIXED_LOCALE, FIXED_SQLITE_PATH, FIXED_TIMEZONE } from "./e2e/fixed";

/**
 * The UI review pass (`npm run screenshots`) — see app/ui-review/README.md.
 *
 * Its own config rather than a fifth project in playwright.config.ts, because
 * every project there runs on `npx playwright test`: the journey suite must
 * stay exactly the journeys, and a screenshot run must never be able to fail
 * the gate. The dev loop is the SAME one the journeys use — `webServer` is
 * imported, not copied, so a running stack is reused either way.
 *
 * One browser, one viewport-per-shot (the spec resizes): the widths under
 * review are a CSS breakpoint apart, not a device apart, and shooting both from
 * one page keeps the seed and the captured state identical between them.
 */
export default defineConfig({
  testDir: "./e2e",
  testMatch: /screenshots\.spec\.ts$/,
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 180_000,
  expect: { timeout: 20_000 },
  reporter: [["list"]],
  use: {
    ...devices["Desktop Chrome"],
    baseURL: process.env.CARNIVAL_APP_URL ?? "http://localhost:5173",
    trace: "retain-on-failure",
    ...(FIXED ? { timezoneId: FIXED_TIMEZONE, locale: FIXED_LOCALE } : {}),
  },
  projects: [{ name: "screenshots" }],
  // Writes ui-review/index.html over the PNGs the run just took, so the review
  // artefact is a local page and nothing has to be uploaded to look at the set.
  globalTeardown: "./scripts/screenshots-gallery.mjs",
  webServer: FIXED ? fixedWebServer() : base.webServer,
});

// The fixed pass needs its own empty database, so a running dev API is never reused.
function fixedWebServer() {
  const servers = Array.isArray(base.webServer) ? base.webServer : [base.webServer!];
  return servers.map((server) =>
    server.cwd?.includes("CarnivalApp.Api")
      ? {
          ...server,
          command:
            "node -e \"for (const s of ['', '-wal', '-shm']) " +
            "require('fs').rmSync(process.env.Database__SqlitePath + s, { force: true })\" && " +
            server.command,
          env: { Database__SqlitePath: FIXED_SQLITE_PATH },
          reuseExistingServer: false,
        }
      : server,
  );
}
