import { expect, test } from "@playwright/test";
import { seedCarnival, type SeededCarnival } from "./dev-api";
import { entryRow, eventTile, goOffline, goOnline, outboxChip, signIn, timeField } from "./app";

/**
 * THE journey (ADR-0002): a result captured with no network is never lost. The
 * whole product rests on this working on a school iPad at a pool, so it is
 * exercised on every device profile — capture offline, see the honest pending
 * state, reconnect, watch the queue drain.
 */

let seed: SeededCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedCarnival(api, "offline");
  await api.dispose();
});

test("capture a result offline, then watch it drain on reconnect", async ({ page, context }) => {
  const swimmer = seed.entries[0]!.displayName;
  await signIn(page);
  // Through the tiles, the way an official actually arrives (ADR-0014 §3).
  await page.goto(`/#/carnivals/${seed.carnivalId}`);
  await eventTile(page, "My events", seed.eventName).click();
  await expect(page).toHaveURL(new RegExp(`events/${seed.eventId}`));

  const row = entryRow(page, swimmer);
  await expect(row).toBeVisible();
  await expect(timeField(page, swimmer)).toBeVisible();

  await goOffline(context);
  await expect(page.locator(".connectivity-strip")).toBeVisible();

  await timeField(page, swimmer).fill("34.56");
  await row.getByRole("button", { name: "Save", exact: true }).click();

  // Optimistic row + honest badge: the time is on screen immediately, labelled
  // as not yet sent, and the chrome carries the queue depth.
  await expect(row).toContainText("34.56");
  await expect(row.locator(".sync-badge.queued")).toHaveText(/pending/);
  await expect(outboxChip(page)).toHaveText(/1 pending/);

  await goOnline(context);

  // The drain is triggered by the platform's own `online` event — nothing in
  // the UI is tapped here, because on the deck nobody taps anything.
  await expect(outboxChip(page)).toHaveText(/all synced \(1\)/);
  await expect(row.locator(".row-status .badge, .row-status .sync-badge").first())
    .toHaveText(/Recorded|synced/);
  await expect(page.locator(".connectivity-strip")).toBeHidden();
});
