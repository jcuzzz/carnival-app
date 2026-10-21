# app/ — React PWA (skeleton)

Stack per [ADR-0001](../docs/adr/0001-technology-stack.md): React 19 + TypeScript + Vite SPA, vite-plugin-pwa (Workbox), Dexie over IndexedDB with an explicit outbox ([ADR-0002](../docs/adr/0002-offline-sync-design.md)).

**Note:** dependencies are declared but not installed (no Node toolchain on the authoring machine). First run:

```
cd app
npm install
npm run dev
```

Version pins are best-effort; `npm install` will resolve current compatible versions.

## Layout

- `src/db/db.ts` — Dexie schema (v8): `outbox` (append-only, UUIDv7 idempotency keys, per-item actor; accepted items pruned after 7 days per ADR-0013), `syncMeta`, `eventRosters`.
- `src/api/generated.d.ts` — wire types GENERATED from the API's OpenAPI document ([ADR-0012](../docs/adr/0012-client-data-layer.md)) and committed, so a build never needs a live API. `src/api/types.ts` only gives those schemas the app's own names.
- `src/carnivals/` — the selected carnival ([ADR-0024](../docs/adr/0024-carnival-context-and-primary-navigation.md)): `selectedCarnival.ts` is a device-local store over `localStorage` (never server-side — deck devices are shared, ADR-0006), `landing.ts` holds the status-shaped landing rule and which tab owns a path.
- `vite.config.ts` — PWA precache + NetworkFirst for API reads; writes bypass the service worker by design.

## Regenerating the wire types

After any change to an API response shape:

```
pwsh scripts\dev-run.ps1 -ApiOnly    # from the repo root; the document is Development-only
cd app && npm run generate:types     # rewrites src/api/generated.d.ts
```

Commit the diff — it is the review record of a server contract change, and a type error at the call sites is the point.

## Lint

`npm run lint` (eslint flat config, correctness rules only — no formatting rules; the code is
hand-formatted). `react-hooks/exhaustive-deps` is at error; the in-tree disable comments are the
only sanctioned exceptions. Not yet a CI job.

## End-to-end tests (device profiles)

`npm test` is vitest only. The carnival-day journeys run in real browsers:

```
npx playwright install chromium webkit   # once
npm run test:e2e                         # 16 journeys x 4 device profiles
npx playwright test --project=ipad       # one profile
SCREENSHOTS_FIXED=1 npm run screenshots  # pixel-reproducible captures, for diffing a CSS-only change
```

`playwright.config.ts` starts the whole dev loop itself — the API on the
throwaway SQLite database with the synthetic seed, and Vite — and reuses either
if it is already running (`scripts/dev-run.ps1`). **Set `SPORTS_APP_URL` if port
5173 is taken by another project** — `reuseExistingServer` will otherwise adopt and
drive whatever is there; the Vite port is derived from that URL. Profiles: iPad landscape and
iPhone (WebKit), Pixel and desktop (Chromium). The specs seed a carnival of
their own through the API (`e2e/dev-api.ts`), so the suite is re-runnable
without `scripts/dev-reset.ps1` in between; reset only when the dev database
predates a schema change.

`npm run screenshots` writes the review gallery (`ui-review/index.html`, 31 scenarios at 390 and 820). With
`SCREENSHOTS_FIXED=1` the run is pixel-reproducible — a fresh SQLite, fixed seed names, a fixed clock, server
stamps pinned at the route layer, motion off (`e2e/fixed.ts`) — so a CSS-only change is proven visually
neutral by diffing the PNGs against a baseline run rather than by looking.

The flagship journey (`e2e/offline-capture.spec.ts`) captures a result with the
context offline and asserts it queues, then drains on reconnect — ADR-0002's
whole promise, checked on every profile. Since ADR-0014 the journeys enter
through the carnival landing TILES rather than the flat event list, and cover a
division-chip switch (asserting the URL really changes — chips are one route per
instance, not in-page state), a Social event scored per entry with no time
captured at all, and the organiser building a program then checking the print
view under `emulateMedia({media:"print"})`, so the print stylesheet is not left
untested.

## Status

Navigation is carnival-scoped since [ADR-0024](../docs/adr/0024-carnival-context-and-primary-navigation.md): a selected carnival in the header chrome, and three tabs — Deck (capture), Dashboard (read-mostly standings), Manage (organiser, role-gated). The deck is ONE HEAT since [ADR-0039](../docs/adr/0039-the-heat-is-the-context.md), with a condensed header of three pickers — category, age, heat — since [ADR-0042](../docs/adr/0042-condensed-deck-header.md): that heat's rows in lane order, Add entry adding into the selected heat, placings on placed rows ([ADR-0036](../docs/adr/0036-the-deck-shows-placings.md)) and lanes auto-filled when the carnival's Lanes card says so ([ADR-0038](../docs/adr/0038-auto-fill-lanes.md)). The Dashboard is the meet-day board ([ADR-0043](../docs/adr/0043-dashboard-progress-and-meet-day-stats.md)) with optional house colours ([ADR-0044](../docs/adr/0044-configurable-house-colours.md)), and a SystemAdministrator edits school name, brand colour and houses in-app at `/school` ([ADR-0045](../docs/adr/0045-school-settings-in-app.md)). The design system ([ADR-0040](../docs/adr/0040-design-system.md), `docs/development/design-system.md`) is the constraint layer for every UI change. Sync layer (foreground drain loop — deliberately no Background Sync registration; iOS unreliability, ADR-0002), screens, and PWA install assets are built; `navigator.storage.persist()` is requested at startup (2026-07-31). Current state of play: `docs/HANDOFF.md`; task tracker: `docs/BACKLOG.md`. Tests: `npm test` (vitest).
