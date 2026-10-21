import { expect, type BrowserContext, type Locator, type Page } from "@playwright/test";

/** Shared driving helpers: sign-in, row locators, and the offline switch. */

/** Escape a snapshot display name for use inside a RegExp. */
function literal(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/**
 * Sign in through the dev login picker (ADR-0006 named individual). Alex Admin
 * holds CarnivalAdministrator, which may record for any event (ADR-0016)
 * (ADR-0009), so one persona covers setup and the deck.
 */
export async function signIn(page: Page, displayName = "Alex Admin"): Promise<void> {
  await page.goto("/#/login");
  await page.getByRole("button", { name: displayName }).click();
  await expect(page.getByRole("heading", { name: "Carnivals", level: 1 })).toBeVisible();
  await expect(page.getByText(displayName).first()).toBeVisible();
}

/**
 * A tile on the carnival landing, inside one of its blocks (ADR-0014 §3).
 * `block` is "My events" for the signed-in official's assigned events, "All
 * events" for the full grid beside it, or "Events" when there is no assignment
 * block at all.
 *
 * Every tile is a plain link into a division (ADR-0018 §2, owner refinement
 * 2026-08-14).
 *
 * The block name is matched EXACTLY: accessible-name matching is substring by
 * default, so "Events" also matches "My events" and "All events", and the next
 * journey to ask for it walks into strict mode with three candidates.
 */
export function eventTile(page: Page, block: string, name: string): Locator {
  return page.getByRole("region", { name: block, exact: true }).getByRole("link", { name });
}

/** The division selector on the event page. */
export function divisionSelector(page: Page): Locator {
  return page.getByRole("group", { name: "Divisions", exact: true });
}

/** A naming item carries the whole division and its count: "13 · Girls 0/3". */
function namingItem(page: Page, division: string): Locator {
  return page.getByRole("menuitem", { name: new RegExp(`^${literal(division)}\\s`) });
}

/**
 * Open the picker that names whole divisions (ADR-0042 §2) — the finest axis
 * with MORE THAN ONE value, so age on most definitions and category where the
 * page's category has a single age. Which one it is depends on the seed, so
 * this asks rather than assumes. Base UI menus need a real click, and the popup
 * is portalled, so the items are reached from the page.
 */
export async function openNamingPicker(scope: Locator, division: string): Promise<void> {
  const page = scope.page();
  // Counting a locator does not wait: nothing here may be read off a selector
  // that has not rendered, nor off a portalled popup a frame behind its click.
  await expect(scope.getByRole("button").first()).toBeVisible();
  for (const axis of [/^Age group/, /^Category/, /^Tier/]) {
    const trigger = scope.getByRole("button", { name: axis });
    if ((await trigger.count()) === 0) continue;
    await trigger.click();
    await expect(page.getByRole("menu")).toBeVisible();
    if ((await namingItem(page, division).count()) > 0) return;
    await page.keyboard.press("Escape");
    await expect(page.getByRole("menu")).toBeHidden();
  }
  throw new Error(`No picker in the selector offers the division "${division}".`);
}

/** Pick a division through the selector (ADR-0018 §2, ADR-0042 §2). */
export async function chooseDivision(scope: Locator, division: string): Promise<void> {
  await openNamingPicker(scope, division);
  await namingItem(scope.page(), division).click();
}

/** The deck row for one swimmer (the heat divider is a sibling `li`). Anchored on the
 * exact name element: since ADR-0030 every row's lane select names OTHER swimmers
 * ("Lane 2 — swap with Sam Berg"), so a bare hasText match picks the wrong row. */
export function entryRow(page: Page, displayName: string): Locator {
  return page
    .locator("li")
    .filter({ has: page.getByText(displayName, { exact: true }) })
    .first();
}

/** The time field on that row (see results/timeMask.ts). */
export function timeField(page: Page, displayName: string): Locator {
  return page.getByLabel(new RegExp(`^Time for ${literal(displayName)}`));
}

/** The mark field on a `Distance`/`Height` row (see results/markMask.ts). */
export function markField(page: Page, displayName: string): Locator {
  return page.getByLabel(new RegExp(`^Mark for ${literal(displayName)}`));
}

/** One untaken cell of the attempt strip (ADR-0053 §6). */
export function attemptField(page: Page, displayName: string, ordinal: number): Locator {
  return page.getByLabel(`Attempt ${ordinal} mark for ${displayName}`);
}

/**
 * ADR-0041 §2: DNS/DNF/DQ, "Change lane" and "Remove from heat" are items in
 * one per-row actions menu. The popup is portalled to the body, so the items
 * are reached from the page, not from the row.
 */
export async function rowAction(page: Page, displayName: string, item: string): Promise<void> {
  await page.getByRole("button", { name: `Actions for ${displayName}` }).click();
  await page.getByRole("menuitem", { name: item, exact: true }).click();
}

/** That row's revealed lane picker — idempotent, so a journey can pick twice. */
export async function lanePicker(page: Page, displayName: string): Promise<Locator> {
  await rowAction(page, displayName, "Change lane");
  return page.getByLabel(`Lane for ${displayName}`);
}

/** Aggregate queue state in the shell chrome (OutboxIndicator). */
export function outboxChip(page: Page): Locator {
  return page.locator("a.outbox-chip");
}

/**
 * Cut the device off.
 *
 * Two mechanisms on purpose: `setOffline` is what flips `navigator.onLine`,
 * which is the signal the sync worker's drain gate reads, while aborting
 * /api/v1/** covers the pool case the product actually cares about (the AP is
 * up, the uplink is dead) and any browser where offline emulation is partial.
 */
export async function goOffline(context: BrowserContext): Promise<void> {
  await context.route("**/api/v1/**", (route) => route.abort("internetdisconnected"));
  await context.setOffline(true);
}

export async function goOnline(context: BrowserContext): Promise<void> {
  await context.unroute("**/api/v1/**");
  await context.setOffline(false);
}

/**
 * A refusal to fulfil in place of the real server: the ADR-0011 body shape
 * (`error` is the machine code the client branches on, `detail` the sentence it
 * shows), against one request the capture under review has to park.
 */
export interface Refusal {
  /** Substring of the request URL — the per-entry route is what makes it unique. */
  path: string;
  /** Defaults to POST; a placement is a PUT. */
  method?: string;
  status: number;
  error: string;
  detail: string;
}

/**
 * Park captures on demand. A conflict or a rejection needs the SERVER to refuse,
 * and the states ADR-0025 recovers from (a taken lane, a second result for one
 * entry) cannot be produced from one browser against a carnival this device
 * owns. Everything unmatched falls through to the real API, so the reads behind
 * the screen under review are the real ones.
 */
export async function refuseWrites(
  context: BrowserContext,
  refusals: readonly Refusal[],
): Promise<void> {
  await context.route("**/api/v1/**", async (route) => {
    const request = route.request();
    const match = refusals.find(
      (r) => request.url().includes(r.path) && request.method() === (r.method ?? "POST"),
    );
    if (!match) return route.fallback();
    await route.fulfill({
      status: match.status,
      contentType: "application/json",
      body: JSON.stringify({ error: match.error, detail: match.detail }),
    });
  });
}

/** 320 to 1440: the narrowest phone the pilot expects, out to a laptop. */
export const CLIPPING_WIDTHS = [320, 390, 600, 768, 820, 900, 1024, 1280, 1440] as const;

/**
 * Every control inside `scroller` is fully within it, at every width.
 *
 * `scrollWidth === clientWidth` alone is not enough: the sideways scroll it
 * catches is what MAKES a control unreachable, but a control can also be cut by
 * a cell inside a scroller that reports no overflow. So the box of each button
 * is measured against the box that would hide it — which is the check a red
 * sliver of a Discard button escaped for a whole review.
 */
export async function expectNothingClipped(page: Page, scroller: Locator): Promise<void> {
  const restore = page.viewportSize();
  for (const width of CLIPPING_WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    const at = `at ${width}px`;
    const box = await scroller.boundingBox();
    expect(box, `scroller measurable ${at}`).not.toBeNull();
    const overflow = await scroller.evaluate((el) => el.scrollWidth - el.clientWidth);
    expect(overflow, `sideways scroll ${at}`).toBeLessThanOrEqual(1);
    const controls = await scroller.getByRole("button").all();
    expect(controls.length, `controls found ${at}`).toBeGreaterThan(0);
    for (const control of controls) {
      const rect = await control.boundingBox();
      expect(rect, `control measurable ${at}`).not.toBeNull();
      const label = (await control.textContent())?.trim() ?? "?";
      // Sub-pixel rounding is not clipping; a fraction of a device pixel is.
      expect(rect!.x, `"${label}" cut on the left ${at}`).toBeGreaterThanOrEqual(box!.x - 1);
      expect(rect!.x + rect!.width, `"${label}" cut on the right ${at}`).toBeLessThanOrEqual(
        box!.x + box!.width + 1,
      );
    }
  }
  if (restore) await page.setViewportSize(restore);
}
