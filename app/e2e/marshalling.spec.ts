import { expect, test } from "@playwright/test";
import { seedMarshallingCarnival, type SeededMarshallingCarnival } from "./dev-api";
import { entryRow, lanePicker, outboxChip, rowAction, signIn, timeField } from "./app";

/**
 * The flat deck (ADR-0020, ADR-0021): lanes and capture on ONE surface, with no
 * mode to enter — assign three lanes from the rows themselves (the first pick
 * minting the division's race), capture a time, DNS another swimmer, and take
 * the third out of the race through the armed two-step. Since ADR-0041 §2 all
 * three of those acts start in the row's own actions menu.
 */
let seed: SeededMarshallingCarnival;
/** A carnival of its own for the heat-order journey, which adds swimmers. */
let heatSeed: SeededMarshallingCarnival;
/** …and one for the lane swap, which needs three untouched lanes (ADR-0030). */
let swapSeed: SeededMarshallingCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedMarshallingCarnival(api, "marshal", 3);
  heatSeed = await seedMarshallingCarnival(api, "heat-order", 2);
  swapSeed = await seedMarshallingCarnival(api, "lane-swap", 3);
  await api.dispose();
});

test("assign three lanes inline, then capture, DNS and remove", async ({ page }) => {
  const [first, second, third] = seed.primaryEntries;
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.primaryEventId}`);
  await expect(entryRow(page, first!.displayName)).toBeVisible();

  const laneOf = (name: string) => lanePicker(page, name);

  // The division has ONE heat and every swimmer is in it, unlaned: since
  // ADR-0039 §1 the server places every create, so the entries this seed made
  // bare arrived in a heat of the server's own minting. What is left to a
  // marshal is the lane.
  const heats = page.getByRole("group", { name: "Heats", exact: true });
  await expect(heats.getByText("Heat 1")).toBeVisible();
  await expect(heats.getByText("Heat 2")).toHaveCount(0);
  await expect(await laneOf(first!.displayName)).toHaveValue("");

  // Each pick is a placement into the heat on screen; nobody moves heat.
  for (const [i, swimmer] of [first!, second!, third!].entries()) {
    await (await laneOf(swimmer.displayName)).selectOption(String(i + 1));
    await expect(await laneOf(swimmer.displayName)).toHaveValue(String(i + 1));
  }
  await expect(entryRow(page, first!.displayName)).toContainText("heat");

  // Still the only heat, and its closed button counts the field standing behind
  // it — the deck is a picker row and a list, nothing else (ADR-0039).
  await expect(heats.getByText("Heat 1")).toBeVisible();
  await expect(heats.getByText(`0/${seed.primaryEntries.length}`)).toBeVisible();

  // …and the deck is in the order it was marshalled into, not the order the
  // entries were created in.
  await expect(page.locator(".deck .row-title")).toHaveText([
    first!.displayName,
    second!.displayName,
    third!.displayName,
  ]);

  // Capture a time on the deck the lanes were just assigned from.
  await timeField(page, first!.displayName).fill("34.12");
  await entryRow(page, first!.displayName)
    .getByRole("button", { name: "Save", exact: true })
    .click();
  await expect(entryRow(page, first!.displayName)).toContainText("34.12");

  // DNS is two taps at every width now (ADR-0041 §2): the row's menu, then
  // the item — the same write the row's own button used to make.
  const other = entryRow(page, third!.displayName);
  await expect(other).toBeVisible();
  await rowAction(page, third!.displayName, "DNS");
  await expect(other).toContainText("DNS");
  await expect(other.locator(".badge, .sync-badge").first()).toHaveText(/Recorded|synced/);

  // Taking a swimmer out CLEARS THE HEAT with them, so it is still a two-step:
  // the menu item arms a strip, and the strip ignores the second half of a
  // double-tap for 300ms before it will confirm.
  await rowAction(page, second!.displayName, "Remove from heat");
  const confirm = page.getByRole("button", {
    name: `Confirm removing ${second!.displayName} from this race`,
  });
  await expect(confirm).toBeVisible();
  await page.waitForTimeout(400);
  await confirm.click();
  // They stay ON the deck — a swimmer taken out of the heat is a swimmer to
  // re-marshal, and their row keeps the control that does it (ADR-0039 §3) —
  // marked as being in no heat, so it does not read as a member of this one.
  await expect(await laneOf(second!.displayName)).toHaveValue("");
  await expect(entryRow(page, second!.displayName)).toContainText("no heat");
});

test("two swimmers exchange lanes and NOTHING parks", async ({ page }) => {
  // The ADR-0030 P0 end to end: the act that used to leave two LANE_TAKEN
  // conflicts. The screenshot spec captures the ARMED strip; what is pinned
  // here is the outcome — three writes drain clean, the lanes are exchanged on
  // the server's own read, and the queue is empty rather than parked.
  const [first, second, third] = swapSeed.primaryEntries;
  await signIn(page);
  await page.goto(`/#/carnivals/${swapSeed.carnivalId}/events/${swapSeed.primaryEventId}`);
  await expect(entryRow(page, first!.displayName)).toBeVisible();

  const laneOf = (name: string) => lanePicker(page, name);
  for (const [i, swimmer] of [first!, second!, third!].entries()) {
    await (await laneOf(swimmer.displayName)).selectOption(String(i + 1));
    await expect(await laneOf(swimmer.displayName)).toHaveValue(String(i + 1));
  }
  await expect(outboxChip(page)).toHaveText(/all synced/);

  // The two-tap swap: an occupied lane is offered as a named exchange, and the
  // pick ARMS rather than fires — one tap here moves a swimmer who is not on
  // this row, and three writes is not a thing to fire on a `change` event.
  await (await laneOf(first!.displayName)).selectOption({
    label: `Lane 2 — swap with ${second!.displayName}`,
  });
  const confirm = page.getByRole("button", {
    name: `Confirm swapping ${first!.displayName} with ${second!.displayName}`,
  });
  await expect(confirm).toBeFocused();
  // The same 300ms arming lockout Remove has: a wet finger double-taps, and the
  // second tap of one gesture must not BE the confirmation.
  await page.waitForTimeout(400);
  await confirm.click();

  // Vacate, cross, back — in capture order, and all three land.
  await expect(outboxChip(page)).toHaveText(/all synced/);
  await expect(await laneOf(first!.displayName)).toHaveValue("2");
  await expect(await laneOf(second!.displayName)).toHaveValue("1");
  // The third swimmer never moved, and no lane holds two people.
  await expect(await laneOf(third!.displayName)).toHaveValue("3");

  // The whole point of the ADR: nothing to recover. Before it, the obvious
  // two-tap swap left two parked captures on this screen.
  await page.goto("/#/outbox");
  await expect(
    page.getByText("Nothing waiting — every capture on this device has reached the server."),
  ).toBeVisible();
  await expect(page.getByText(/0 conflict/)).toBeVisible();

  // And the exchange is the SERVER's, not this device's optimism: a reload
  // reads it back from the API.
  await page.goto(`/#/carnivals/${swapSeed.carnivalId}/events/${swapSeed.primaryEventId}`);
  await expect(await laneOf(first!.displayName)).toHaveValue("2");
  await expect(await laneOf(second!.displayName)).toHaveValue("1");
});

test("adding a heat and filling it leaves the heats where they were", async ({ page }) => {
  // The owner's defect (ADR-0039 amendment): heat numbers were derived in FILL
  // order, so the first swimmer into a heat that was not the next one to be
  // numbered took a heat belonging further up the axis — and the items, which
  // read the scoring heat, shuffled under the marshal's finger. The number is
  // now the race's ordinal within the division.
  await signIn(page);
  await page.goto(`/#/carnivals/${heatSeed.carnivalId}/events/${heatSeed.primaryEventId}`);
  await expect(entryRow(page, heatSeed.primaryEntries[0]!.displayName)).toBeVisible();

  const heats = page.getByRole("group", { name: "Heats", exact: true });
  const trigger = heats.getByRole("button", { name: /^Heat / });
  const items = page.getByRole("menuitem", { name: /^Heat \d/ });
  const open = async () => {
    await trigger.click();
    await expect(page.getByRole("menu")).toBeVisible();
  };
  const close = async () => {
    await page.keyboard.press("Escape");
    await expect(page.getByRole("menu")).toBeHidden();
  };
  // The mint is the picker's last item now (ADR-0042 §2), and choosing it
  // closes the menu.
  const mint = async () => {
    await open();
    await page.getByRole("menuitem", { name: /^New heat/ }).click();
    // A picked item closes the popup, mint or hash link alike — nothing is left
    // standing over the deck it just changed.
    await expect(page.getByRole("menu")).toHaveCount(0);
  };

  await open();
  await expect(items).toHaveText([/^Heat 1/]);
  await close();

  // TWO empty heats, one mint at a time (ADR-0037's gate), so heat 2 is still
  // empty when heat 3 is filled — the interleave that used to invert them.
  await mint();
  await expect(outboxChip(page)).toHaveText(/all synced/);
  await open();
  await expect(items).toHaveText([/^Heat 1/, /^Heat 2/]);
  await close();
  await mint();
  await expect(outboxChip(page)).toHaveText(/all synced/);
  await open();
  await expect(items).toHaveText([/^Heat 1/, /^Heat 2/, /^Heat 3/]);
  await close();
  await expect(page.getByText("No swimmers yet — add one below.")).toBeVisible();

  // A late swimmer added from the panel under the empty third heat.
  const listed = page.locator(".eligible-list li");
  await expect(listed.first()).toBeVisible();
  const added = (await listed.first().locator(".row-title").textContent())!;
  await listed.first().getByRole("button", { name: "Add" }).click();
  await expect(entryRow(page, added)).toBeVisible();
  await expect(outboxChip(page)).toHaveText(/all synced/);

  // After the drain: same items, same order, and the swimmer is still under the
  // one the marshal was standing at — which is still the third.
  await open();
  await expect(items).toHaveText([/^Heat 1/, /^Heat 2/, /^Heat 3/]);
  await expect(items.nth(2)).toHaveAttribute("aria-current", "true");
  await expect(items.nth(2)).toContainText("0/1");
  // The second is still the EMPTY one, and still second: filling the third did
  // not hand it the third's number and send it to the back.
  await expect(items.nth(1)).toContainText("0 entered");
  await close();
  await expect(entryRow(page, added)).toBeVisible();
});
