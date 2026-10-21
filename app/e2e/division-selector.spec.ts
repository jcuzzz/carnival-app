import { expect, test } from "@playwright/test";
import { seedDenseCarnival, type SeededDenseCarnival } from "./dev-api";
import { divisionSelector, openNamingPicker, signIn } from "./app";

/**
 * The selector as three pickers (ADR-0018 §2, ADR-0042 §2), on a definition
 * dense enough to need all of them: tier, category and age, derived from the
 * divisions that exist.
 *
 * What these journeys prove is the 2026-08-14 refinement itself — **every tap
 * navigates**. There is no narrowing state left to browse, because an item that
 * changed nothing on the page is indistinguishable from a broken one at arm's
 * length on a wet deck.
 */

let seed: SeededDenseCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedDenseCarnival(api, "dense");
  await api.dispose();
});

test("a category tap navigates, and the closed buttons name where it landed", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.fromEventId}`);
  const selector = divisionSelector(page);
  // ADR-0042 §1 deleted the meta line: the closed buttons ARE the page's
  // statement of which division is on screen.
  const [fromAge, fromCategory] = seed.fromDivisionLabel.split(" · ");
  await expect(selector).toContainText(fromAge!);
  await expect(selector).toContainText(fromCategory!);

  // The shape this journey depends on, asserted rather than assumed: a category
  // axis with somewhere else to be.
  const category = selector.getByRole("button", { name: /^Category/ });
  await expect(category).toBeVisible();
  await category.click();

  // The page's own category is a marked item, not a link — there is nowhere
  // for it to go; every other one is a real route change.
  await expect(page.locator('[role="menuitem"].current')).toHaveCount(1);
  const to = seed.toDivisionLabel.split(" · ")[1]!;
  // A category item reads as a bare value where a finer axis names the division
  // and as the whole division where this axis is the naming one — one item
  // carrying that category either way.
  await page.getByRole("menuitem", { name: new RegExp(`(^|\\s)${to}(\\s|$)`) }).click();
  // A hash href re-renders the deck rather than navigating off it, so the popup
  // has to close itself.
  await expect(page.getByRole("menu")).toHaveCount(0);

  // Immediately a different page, naming the division it landed on: the same
  // age where that category has one, the nearest where it does not.
  await expect(page).not.toHaveURL(new RegExp(`events/${seed.fromEventId}`));
  await expect(selector.getByRole("button", { name: `Category ${to}` })).toBeVisible();

  // Nothing is left pressed or announcing.
  await expect(page.getByRole("status").filter({ hasText: "Showing" })).toHaveCount(0);
});

test("only a division-naming axis may claim the page, and the axes always name it", async ({
  page,
}) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.fromEventId}`);
  const selector = divisionSelector(page);

  // ADR-0042 §1: the h1 names the EVENT and the closed controls name the
  // DIVISION — no meta line is left to repeat either. An axis with a single
  // value says so as static text rather than offering a menu of one.
  await expect(page.getByRole("heading", { name: seed.eventName, level: 1 })).toBeVisible();
  const [age, category] = seed.fromDivisionLabel.split(" · ");
  await expect(selector).toContainText(age!);
  await expect(selector).toContainText(category!);
  await expect(selector).toContainText("Competitive");

  // Tier is a SCOPE here — the page's own category has a sibling category, so a
  // finer axis names the division. Its items stand for many divisions each, so
  // the one this page sits in is marked current and NOTHING there claims to be
  // the page (ADR-0018 §2). Two answers to "where am I" is the failure mode.
  await selector.getByRole("button", { name: /^Tier/ }).click();
  await expect(page.getByRole("menu")).toBeVisible();
  await expect(page.locator('[role="menuitem"].current')).toHaveCount(1);
  await expect(page.locator("[aria-current='page']")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("menu")).toBeHidden();

  // The naming axis is the finest one with a CHOICE on it, and it is the only
  // place the page is claimed — exactly once.
  await openNamingPicker(selector, seed.fromDivisionLabel);
  const claimed = page.locator("[aria-current='page']");
  await expect(claimed).toHaveCount(1);
  await expect(claimed).toContainText(seed.fromDivisionLabel);
});

test("the tile links straight to a division", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}`);

  const tile = page
    .getByRole("region", { name: "Events", exact: true })
    .getByRole("link", { name: new RegExp(seed.eventName) });
  await expect(tile).toContainText(`${seed.divisionCount} divisions`);
  await tile.click();

  // Two taps from the carnival to a capture surface: ADR-0014 §3's sentence
  // stands unamended (owner refinement 2026-08-14).
  await expect(page).toHaveURL(new RegExp(`/carnivals/${seed.carnivalId}/events/[0-9a-f-]+$`));
  await expect(divisionSelector(page)).toBeVisible();
});
