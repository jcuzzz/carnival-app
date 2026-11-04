/** Shared by the config's API server and the global setup's host command: one database. */

export const ACCOUNTS_API_URL = "http://localhost:5081";

export const ACCOUNTS_API_DIR = "../api/src/CarnivalApp.Api";

// Development only because SQLite is Development-only; the app sends no dev header, so every
// request is authenticated by the local-account cookie.
export const ACCOUNTS_API_ENV: Record<string, string> = {
  ASPNETCORE_ENVIRONMENT: "Development",
  ASPNETCORE_URLS: ACCOUNTS_API_URL,
  Database__Provider: "Sqlite",
  // Relative to the API directory; Playwright empties test-results/ before the servers start.
  Database__SqlitePath: "../../../app/test-results/local-accounts-e2e.db",
  Database__SeedSyntheticData: "false",
  Identity__DevAuth__Enabled: "false",
};
