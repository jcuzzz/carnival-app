import { defineConfig, devices } from "@playwright/test";
import { ACCOUNTS_API_DIR, ACCOUNTS_API_ENV, ACCOUNTS_API_URL } from "./e2e/local-accounts.env";

/**
 * The one suite that signs in through real local accounts (ADR-0065): `npm run test:e2e:accounts`.
 *
 * A production build behind `vite preview`, so no dev header is ever sent and the session is the
 * API's cookie; the preview inherits the dev server's `/api` proxy, so every request is
 * same-origin as it is behind Caddy. The API gets an empty SQLite file of its own, and the global
 * setup asks the host command for the first administrator's link, as a fresh install does.
 */

const APP_URL = "http://localhost:4174";

// The proxy target is read when preview starts, never baked into the build.
const preview =
  "process.env.VITE_API_BASE='" + ACCOUNTS_API_URL + "';" +
  "require('child_process').spawn('npx vite preview --port 4174 --strictPort'," +
  "{stdio:'inherit',shell:true}).on('exit',c=>process.exit(c??1))";

export default defineConfig({
  testDir: "./e2e",
  testMatch: /local-accounts\.spec\.ts$/,
  workers: 1,
  fullyParallel: false,
  retries: 0,
  timeout: 240_000,
  expect: { timeout: 20_000 },
  reporter: [["list"]],
  globalSetup: "./e2e/local-accounts.setup.ts",
  use: {
    ...devices["Desktop Chrome"],
    baseURL: APP_URL,
    serviceWorkers: "block",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [{ name: "desktop" }],
  webServer: [
    {
      command:
        "node -e \"for (const s of ['', '-wal', '-shm']) " +
        "require('fs').rmSync(process.env.Database__SqlitePath + s, { force: true })\" && " +
        "dotnet run --no-launch-profile",
      cwd: ACCOUNTS_API_DIR,
      env: ACCOUNTS_API_ENV,
      url: `${ACCOUNTS_API_URL}/health`,
      reuseExistingServer: false,
      timeout: 240_000,
    },
    {
      command: `npm run build && node -e "${preview}"`,
      url: APP_URL,
      reuseExistingServer: false,
      timeout: 240_000,
    },
  ],
});
