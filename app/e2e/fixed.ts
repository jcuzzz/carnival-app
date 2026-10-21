import type { BrowserContext, Page } from "@playwright/test";

/**
 * `SCREENSHOTS_FIXED=1 npm run screenshots`: a pixel-reproducible capture set, for proving a
 * CSS-only change visually neutral with a PNG diff. Nothing here runs without the variable.
 */
export const FIXED = process.env.SCREENSHOTS_FIXED === "1";

export const FIXED_TIMEZONE = "Australia/Melbourne";
export const FIXED_LOCALE = "en-AU";
const FIXED_INSTANT = "2026-09-25T09:00:00+10:00";
const FIXED_UTC = new Date(FIXED_INSTANT).toISOString();
const FIXED_DATE = "2026-09-25";
const FIXED_DEVICE_ID = "01900000-0000-7000-8000-000000f1eed0";

/** A fresh database per fixed run, so carnival lists and record books do not accumulate. */
export const FIXED_SQLITE_PATH = `${process.env.LOCALAPPDATA ?? "."}\\CarnivalApp\\screens-fixed.db`;

/** Seeded carnival names: timestamped for re-runnable journeys, bare on a fresh fixed database. */
export function seedName(label: string): string {
  return FIXED ? `E2E ${label}` : `E2E ${label} ${Date.now()}`;
}

const ISO_INSTANT = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?$/;
const ISO_DATE = /^\d{4}-\d{2}-\d{2}$/;

function pinStamps(value: unknown): unknown {
  if (typeof value === "string") {
    if (ISO_INSTANT.test(value)) return FIXED_UTC;
    if (ISO_DATE.test(value)) return FIXED_DATE;
    return value;
  }
  if (Array.isArray(value)) return value.map(pinStamps);
  if (value !== null && typeof value === "object") {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, pinStamps(v)]));
  }
  return value;
}

/**
 * Server stamps (grant/void/import days, device ages, carnival dates) are real time, so every
 * API read has them replaced by the fixed instant. Safe for this pass only: it never saves the
 * one form (school settings) that echoes a stamp back as a concurrency token. A predicate, not
 * the glob, so `goOnline`'s unroute of the glob leaves this route in place.
 */
async function pinServerStamps(context: BrowserContext): Promise<void> {
  await context.route(
    (url) => url.pathname.startsWith("/api/v1/"),
    async (route) => {
      if (route.request().method() !== "GET") return route.fallback();
      const response = await route.fetch();
      const type = response.headers()["content-type"] ?? "";
      if (!type.includes("json")) return route.fulfill({ response });
      const body = pinStamps(await response.json());
      await route.fulfill({ response, json: body });
    },
  );
}

const STILL_CSS =
  "*, *::before, *::after { animation: none !important; transition: none !important; " +
  "caret-color: transparent !important; }";

/** Call before the test's first navigation. */
export async function stabilise(page: Page, context: BrowserContext): Promise<void> {
  await page.clock.install({ time: FIXED_INSTANT });
  await page.emulateMedia({ reducedMotion: "reduce" });
  await context.addInitScript(
    ({ css, deviceId }) => {
      try {
        localStorage.setItem("sports.deviceId", deviceId);
      } catch {
        // An opaque origin before the first navigation has no storage.
      }
      const style = document.createElement("style");
      style.textContent = css;
      const attach = () => (document.head ?? document.documentElement).appendChild(style);
      if (document.documentElement) attach();
      else document.addEventListener("DOMContentLoaded", attach);
    },
    { css: STILL_CSS, deviceId: FIXED_DEVICE_ID },
  );
  await pinServerStamps(context);
}

/**
 * The clock starts at the fixed instant but runs, so a wall-clock time rendered mid-test carries
 * however many seconds the run took to get there. Minutes and seconds are zeroed in the DOM.
 */
export async function settleForCapture(page: Page): Promise<void> {
  // A late re-render (a refetch, a reserved slot filling) can still move the page after the
  // spec's own wait, so the capture waits for the layout and text to hold still.
  const signature = () =>
    page.evaluate(() => `${document.documentElement.scrollHeight}|${document.body.innerText}`);
  let last = await signature();
  for (let quiet = 0, tries = 0; quiet < 2 && tries < 20; tries++) {
    await page.waitForTimeout(150);
    const next = await signature();
    quiet = next === last ? quiet + 1 : 0;
    last = next;
  }
  await page.evaluate(async () => {
    await document.fonts.ready;
    const clock = /\b(\d{1,2}):\d{2}(:\d{2})?(\s?[ap]\.?m\.?)/gi;
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.nodeValue ?? "";
      const pinned = text.replace(clock, (_, h, s, ampm) => `${h}:00${s ? ":00" : ""}${ampm}`);
      if (pinned !== text) node.nodeValue = pinned;
    }
  });
}
