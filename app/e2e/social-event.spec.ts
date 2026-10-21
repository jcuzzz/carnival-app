import { expect, test } from "@playwright/test";
import { seedSocialCarnival, type SeededSocialCarnival } from "./dev-api";
import { divisionSelector, eventTile, outboxChip, signIn } from "./app";

/**
 * The Social tier end to end (ADR-0014 §4): a Social event scores a flat point
 * per ENTRY and needs no time at all. Entering a swimmer IS the whole job — so
 * this journey adds one through the deck UI and watches the point land in the
 * house standings without a single result being recorded.
 *
 * It also covers the no-assignment landing shape: this official is assigned to
 * nothing here, so there is no "My events" block and the plain "Events" grid is
 * all there is — an absence, never an empty state.
 */

let seed: SeededSocialCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedSocialCarnival(api, "social");
  await api.dispose();
});

test("enter a student in a Social event and watch the flat point score", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}`);

  // The tier is legible from the grid: a Social event must not look ordinary.
  const tile = eventTile(page, "Events", seed.eventName);
  await expect(tile).toContainText("Social");
  await expect(tile).toContainText("no entries yet");
  await tile.click();

  await expect(page).toHaveURL(new RegExp(`events/${seed.eventId}`));
  // ADR-0042 §1: the axis pickers are what name the division on screen.
  const [age, category] = seed.divisionLabel.split(" · ");
  await expect(divisionSelector(page)).toContainText(age!);
  await expect(divisionSelector(page)).toContainText(category!);

  // The eligible roster is BROWSABLE — it is on screen with the panel, before a
  // single key is pressed (owner UX request 2026-08-13). Typing narrows it; it
  // is not what conjures it.
  const list = page.locator(".eligible-list");
  await expect(list.getByText(seed.entrantName)).toBeVisible();
  await expect(page.getByLabel("Search eligible students")).toHaveValue("");

  // Add the entrant from that list (ADR-0010: the add goes through the outbox,
  // so this is the same path a poolside iPad takes offline). The search narrows
  // to them first, which is still how a named late swimmer is found.
  await page.getByLabel("Search eligible students").fill(seed.entrantSearchTerm);
  const candidate = list.locator("li").filter({ hasText: seed.entrantName }).first();
  await candidate.getByRole("button", { name: "Add" }).click();

  // Added, they leave the list at once — nobody is entered twice off a list
  // that keeps offering them.
  await expect(list.getByText(seed.entrantName)).toHaveCount(0);

  // The row appears optimistically and drains; NO time is entered anywhere.
  await expect(page.locator("li").filter({ hasText: seed.entrantName }).first()).toBeVisible();
  // TWO captures, not one: the division had no heat, so the add materialised
  // the one it names and rode FIFO behind it (ADR-0039 §2).
  await expect(outboxChip(page)).toHaveText(/all synced \(2\)/);

  await page.goto(`/#/carnivals/${seed.carnivalId}`);
  await page.getByRole("link", { name: "Dashboard" }).click();
  await expect(page.getByRole("heading", { name: "Dashboard", level: 1 })).toBeVisible();

  // One entry, one point, scored as participation — not as a placing.
  await expect(page.getByText(/1 participation/)).toBeVisible();
  await expect(page.getByText(/From 1 results across 1 events/)).toBeVisible();
});
