import { expect, test } from "@playwright/test";
import { seedCarnival, type SeededCarnival } from "./dev-api";
import {
  chooseDivision,
  divisionSelector,
  entryRow,
  eventTile,
  outboxChip,
  signIn,
  timeField,
} from "./app";

/**
 * The online carnival-day path: sign in, find the carnival, tap through the
 * tiles into an event, switch division in the selector, capture a time, see it
 * counted in the house standings.
 *
 * The deck route is carnival → tile → capture (ADR-0018 §2, owner refinement
 * 2026-08-14): every tile links straight into a division, and the selector on
 * the event page is the sideways move. The filter list still exists (it is the
 * find-anything and awaiting-verification path) but it is no longer how you get
 * to work.
 *
 * Serial because journey 3 (standings) asserts the consequence of journey 2 —
 * that is the point of it, and a standings screen seeded straight through the
 * API would prove nothing about the capture path.
 */
test.describe.configure({ mode: "serial" });

let seed: SeededCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedCarnival(api, "deck");
  await api.dispose();
});

test("sign in, open a carnival and see its event tiles, assigned ones first", async ({ page }) => {
  await signIn(page);

  await page.getByRole("link", { name: seed.carnivalName }).click();
  await expect(page.getByRole("heading", { name: seed.carnivalName, level: 1 })).toBeVisible();

  // The official is assigned to one of the two divisions, so the definition's
  // tile leads — with its results-entered progress.
  const mine = eventTile(page, "My events", seed.eventName);
  await expect(mine).toBeVisible();
  await expect(mine).toContainText(`0/${seed.entries.length}`);

  // ORDERING, NOT ACCESS CONTROL: everything is still in the grid below, and
  // the filter list is still there behind the disclosure.
  const all = eventTile(page, "All events", seed.eventName);
  await expect(all).toBeVisible();
  await expect(page.getByText(/Find an event/)).toBeVisible();
  // The carnival-wide surfaces are tabs now, not a button row on the deck
  // (ADR-0024 §4/§5).
  await expect(page.getByRole("link", { name: "Dashboard" })).toBeVisible();

  // Both divisions sit behind that tile and it links straight into the first of
  // them, so capture is two taps (ADR-0018 §2, owner refinement 2026-08-14).
  // The sideways move is the selector on the event page.
  await expect(all).toContainText("2 divisions");
  await all.click();
  await expect(page).toHaveURL(new RegExp(`events/${seed.eventId}`));
  await chooseDivision(divisionSelector(page), seed.siblingDivisionLabel);
  await expect(page).toHaveURL(new RegExp(`events/${seed.siblingEventId}`));
});

test("tile → division → capture a time on the deck and see it sync", async ({ page }) => {
  const swimmer = seed.entries[0]!.displayName;
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}`);

  await eventTile(page, "My events", seed.eventName).click();
  await expect(page).toHaveURL(new RegExp(`events/${seed.eventId}`));
  await expect(page.getByRole("heading", { name: seed.eventName, level: 1 })).toBeVisible();

  // Switching division is a URL NAVIGATION to the sibling instance (ADR-0014
  // rejects in-page switching): the route, the entries and the selector move.
  await chooseDivision(divisionSelector(page), seed.siblingDivisionLabel);
  await expect(page).toHaveURL(new RegExp(`events/${seed.siblingEventId}`));
  await expect(page.getByText("No entries yet")).toBeVisible();

  // …and back the same way. The division on screen is the one in the URL.
  await chooseDivision(divisionSelector(page), seed.divisionLabel);
  await expect(page).toHaveURL(new RegExp(`events/${seed.eventId}`));
  const row = entryRow(page, swimmer);
  await expect(row).toBeVisible();

  // The swimmer's name sits ABOVE their division line, never against it: the
  // two are adjacent inline spans with no separator in the markup (JSX drops
  // the newline), which is how the deck row came to read "Marley SinghBANKSIA ·
  // Yr 8" (owner screenshot 2026-08-14).
  const name = (await row.locator(".row-main > .row-title").boundingBox())!;
  const meta = (await row.locator(".row-main > .row-sub").boundingBox())!;
  expect(meta.y).toBeGreaterThanOrEqual(name.y + name.height);

  // The program's suggestion is present and is only ever a suggestion — the
  // selector above it is the escape, and nothing here gates entry.
  await expect(page.getByRole("link", { name: /Next event/ })).toBeVisible();

  // ADR-0041 §2: the rare acts are one menu, at every width — no second
  // control set to switch between.
  await expect(row.getByRole("button", { name: `Actions for ${swimmer}` })).toBeVisible();
  await expect(row.locator("select.no-time")).toHaveCount(0);
  await expect(row.locator(".status-tap")).toHaveCount(0);

  // Positional digits (ADR-0041 §1 as amended): what was typed, read the way a
  // stopwatch face reads, and the hundredths the field will supply ghosted.
  const field = timeField(page, swimmer);
  await field.fill("34.56");
  await expect(field).toHaveValue("34.56");
  await expect(row.locator(".time-hint")).toContainText("123 reads 1:23");
  await row.getByRole("button", { name: "Save", exact: true }).click();

  await expect(row).toContainText("34.56");

  // U3 v2 (owner UX review 2026-08-14): the save state lives in the row's own
  // status cluster and nothing is added below the row, so the row and the
  // cluster are the same size during the undo window as they are after it
  // closes. MEASURED, because what this replaced was a line that appeared for
  // five seconds and then vanished, moving every row under it twice.
  const cluster = row.locator(".row-status");
  await expect(cluster.getByRole("button", { name: /^Undo/ })).toBeVisible();
  // ADR-0036: the drained result comes back ranked, so "1st" lands on the
  // identity line inside the window — wait for it, or the two measurements
  // below race the badge and disagree by a line-height.
  await expect(row.locator(".badge.first")).toBeVisible();
  const box = async (l: typeof row) => {
    const b = (await l.boundingBox())!;
    return { width: Math.round(b.width), height: Math.round(b.height) };
  };
  const clusterDuringWindow = await box(cluster);
  const rowDuringWindow = await box(row);

  // The window closes on its own and the cluster settles to Recorded + Edit —
  // the "synced" badge in between is deliberately transient (an accepted item
  // invalidates the read cache, ADR-0012, and the authoritative recorded row
  // replaces it).
  await expect(cluster.getByRole("button", { name: "Edit" })).toBeVisible();
  // Scoped to the cluster: the identity line's place badge (ADR-0036) is not
  // the save state.
  await expect(cluster.locator(".badge, .sync-badge").first()).toHaveText(/Recorded|synced/);
  expect(await box(cluster)).toEqual(clusterDuringWindow);
  expect(await box(row)).toEqual(rowDuringWindow);

  await expect(outboxChip(page)).toHaveText(/all synced \(1\)/);
  await expect(page.locator(".deck-progress")).toContainText(
    `1 of ${seed.entries.length} recorded`,
  );
});

test("house standings count the captured result", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}`);
  await page.getByRole("link", { name: "Dashboard" }).click();

  await expect(page.getByRole("heading", { name: "Dashboard", level: 1 })).toBeVisible();
  await expect(page.getByRole("heading", { name: /House standings/, level: 2 })).toBeVisible();
  await expect(page.locator(".standings-row").filter({ hasText: seed.entries[0]!.houseCode }))
    .toBeVisible();
  await expect(page.getByText(/From 1 results across 1 events/)).toBeVisible();
});

test("the dashboard carries progress beside the standings, and fits a phone", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/dashboard`);

  await expect(page.getByRole("heading", { name: /^Progress/, level: 2 })).toBeVisible();
  await expect(page.locator(".dash-big")).toContainText(/of \d+ results recorded/);
  // The division the journey recorded ONE time on still owes the rest, so it is
  // on the still-to-record list and links to its own deck.
  const outstanding = page.locator(".dash-list .row-link").first();
  await expect(outstanding).toBeVisible();
  await expect(outstanding).toHaveAttribute("href", /\/events\//);

  // ADR-0043 §3: the phone renders one column. Nothing may scroll sideways.
  await page.setViewportSize({ width: 390, height: 900 });
  // The resize reflows asynchronously; a scrollWidth read before it lands is the
  // wide layout's, so the assertion would pass or fail on the race.
  await expect(page.locator(".dash-grid")).toBeVisible();
  await page.waitForFunction(() => document.documentElement.clientWidth <= 390);
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(1);
});
