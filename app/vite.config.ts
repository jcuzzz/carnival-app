/// <reference types="vitest/config" />
import { defineConfig, loadEnv, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// Dev-server API origin: VITE_API_BASE (app/.env.development, see
// docs/development/local-dev.md) with a fallback matching the local API profile.
const apiBase =
  loadEnv("development", process.cwd(), "VITE_").VITE_API_BASE ??
  "http://localhost:5080";

/**
 * Which same-origin API reads the `api-reads` runtime cache may hold — an
 * ALLOWLIST (ADR-0031): a route not named here is never written to disk, so a
 * new endpoint carrying student data is uncached until someone decides
 * otherwise. What is named is the carnival deck's own data, the set a cold
 * start at a venue with no uplink has to work from.
 *
 * Deliberately absent: `/imports/*` (the whole staged roster, admin-only by
 * ADR-0026 §4), `/reports/*` (generated and AUDITED server-side, ADR-0004),
 * `/eligible-students` (Dexie already mirrors it, ADR-0026 §3), `/me` and
 * `/version` (identity and build state, wrong when stale).
 *
 * Workbox SERIALISES this function into sw.js with `toString()`, so it must
 * reference nothing outside its own parameters — no imports, no module
 * constants. Exported only so vite.config.test.ts can pin the rule.
 */
export const isCacheableApiRead = ({ url, sameOrigin }: { url: URL; sameOrigin: boolean }) => {
  if (!sameOrigin) return false;
  // A trailing slash reaches the same route on the server, so it must hit the same rule.
  const path = url.pathname.replace(/\/+$/, "");
  const seg = "[^/]+";
  const carnival = `/api/v1/carnivals/${seg}`;
  return [
    "/api/v1/carnivals",
    "/api/v1/event-definitions",
    "/api/v1/records",
    "/api/v1/school/houses",
    carnival,
    `${carnival}/adjustments`,
    `${carnival}/events`,
    `${carnival}/events/${seg}`,
    `${carnival}/events/${seg}/entries`,
    `${carnival}/events/${seg}/results`,
    `${carnival}/races`,
    `${carnival}/progress`,
    `${carnival}/schedule`,
    `${carnival}/settings`,
    `${carnival}/standings`,
  ].some((route) => new RegExp(`^${route}$`).test(path));
};

/**
 * A public demo build is never indexed (ADR-0061 §4): the API's noindex header does not reach
 * the HTML, which the proxy serves.
 */
export function demoNoindex(): Plugin {
  let demo = false;
  return {
    name: "demo-noindex",
    configResolved(config) {
      demo = config.env.VITE_DEMO_MODE === "true";
    },
    transformIndexHtml: () =>
      demo
        ? [{ tag: "meta", attrs: { name: "robots", content: "noindex, nofollow" }, injectTo: "head" }]
        : [],
  };
}

// ADR-0001: plain client-rendered SPA (no SSR); PWA via vite-plugin-pwa/Workbox.
// App shell precached; API reads NetworkFirst; API writes NEVER go through the
// service worker — they go to the Dexie outbox (ADR-0002).
export default defineConfig({
  plugins: [
    react(),
    demoNoindex(),
    VitePWA({
      // autoUpdate + skipWaiting/clientsClaim: a new build activates on the
      // next load without the user having to clear caches. "prompt" (the old
      // setting) left users stranded on the previously-cached build.
      registerType: "autoUpdate",
      // Referenced only from index.html, so Workbox would not precache it
      // otherwise (the manifest icons are picked up automatically).
      includeAssets: ["apple-touch-icon.png"],
      manifest: {
        name: "Carnival App",
        short_name: "Carnival App",
        description: "Carnival results entry and school records (per-school deployment)",
        start_url: "/",
        display: "standalone",
        theme_color: "#123456",
        background_color: "#ffffff",
        // Placeholder artwork (app/scripts/generate-icons.py) — school branding
        // replaces these before the pilot. An empty icons array blocks the
        // Chrome/Android install prompt outright, so a maskable variant and the
        // 192/512 pair are the minimum installability set.
        icons: [
          { src: "/pwa-192x192.png", sizes: "192x192", type: "image/png", purpose: "any" },
          { src: "/pwa-512x512.png", sizes: "512x512", type: "image/png", purpose: "any" },
          {
            src: "/pwa-maskable-512x512.png",
            sizes: "512x512",
            type: "image/png",
            purpose: "maskable",
          },
        ],
      },
      workbox: {
        // The plugin's default (js/css/html) PLUS woff2: without it the Inter
        // subsets are fetched over the network on a cold start and a venue with
        // no uplink renders the fallback stack (ADR-0040 §4). Deliberately no
        // png/svg — the icons are already injected from `manifest`/
        // `includeAssets`, and globbing them precaches each one TWICE.
        globPatterns: ["**/*.{js,css,html,woff2}"],
        skipWaiting: true,
        clientsClaim: true,
        cleanupOutdatedCaches: true,
        // The SPA precache installs an index.html navigation fallback for deep
        // links. It MUST NOT swallow full-page navigations to the server's own
        // endpoints — above all /api/v1/auth/login, the OIDC sign-in redirect:
        // served index.html instead, the browser never reaches the IdP and the
        // user is trapped in a sign-in loop. Keep these on the network.
        navigateFallbackDenylist: [
          /^\/api\//,
          /^\/signin-oidc/,
          /^\/signout-callback-oidc/,
          /^\/health$/,
        ],
        runtimeCaching: [
          {
            // Kept because it is the ONLY read cache a cold start at a venue
            // with no uplink has — TanStack's is memory-only (ADR-0012).
            // Sign-out deletes it (auth/session.ts, ADR-0026 §3).
            urlPattern: isCacheableApiRead,
            method: "GET",
            handler: "NetworkFirst",
            options: {
              cacheName: "api-reads",
              networkTimeoutSeconds: 5,
              // Bounded so a carnival's worth of reads can't grow without
              // limit, and stale enough entries are dropped rather than served
              // (there is no staleness indicator in the UI yet). 24h, NOT the
              // 12h cookie: a device primed the afternoon before an 8am
              // carnival must still open at the pool (ADR-0031).
              expiration: { maxEntries: 200, maxAgeSeconds: 60 * 60 * 24 },
              // Never cache 4xx/5xx or opaque responses — an auth redirect or a
              // 500 must not be replayed as if it were data.
              cacheableResponse: { statuses: [200] },
            },
          },
        ],
      },
    }),
  ],
  test: {
    environment: "node",
    // e2e/ is Playwright's (playwright.config.ts): same *.spec.ts naming, a
    // completely different runner. Vitest must never pick those up.
    exclude: ["**/node_modules/**", "**/dist/**", "e2e/**"],
    // Component tests need a DOM; matching on the extension means a new
    // *.test.tsx works without the easily-forgotten @vitest-environment
    // docblock (which stays harmless where it is already written).
    environmentMatchGlobs: [["**/*.test.tsx", "jsdom"]],
    setupFiles: ["src/test/setup.ts"],
  },
  server: {
    proxy: {
      "/api": apiBase,
    },
  },
});
