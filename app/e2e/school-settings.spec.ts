import { expect, test, type APIRequestContext, type Locator, type Page } from "@playwright/test";
import {
  listEventDefinitions,
  listRoleAssignments,
  readSchoolSettings,
  restoreSchoolSettings,
  revokeRoleAssignment,
  seedCarnival,
  seedResult,
  type SchoolSettingsSeed,
  type SeededCarnival,
} from "./dev-api";
import { signIn } from "./app";

/**
 * ADR-0045 §6: school identity administered in-app. The point of the journey is
 * the REPAINT — a saved colour has to reach the chrome and the dashboard
 * without a reload, which no unit test can observe.
 *
 * The school row is shared by every other spec, so the original is read first
 * and put back afterwards.
 */

/** ADR-0046: hex entry lives inside the panel, so it has to be opened first. */
async function openColour(page: Page, name: string): Promise<Locator> {
  const trigger = page.getByRole("button", { name: new RegExp(`^${name}\\b`) });
  if ((await trigger.getAttribute("aria-expanded")) !== "true") await trigger.click();
  return page.getByLabel(`${name} hex`);
}

let original: SchoolSettingsSeed;
let seed: SeededCarnival;

const NEW_BRAND = "#0b3d2c";
const NEW_HOUSE = "#7b2d8e";
const PRESET = "#1976d2";

test.beforeAll(async ({ playwright }) => {
  const api: APIRequestContext = await playwright.request.newContext();
  original = await readSchoolSettings(api);
  seed = await seedCarnival(api, "brand-repaint", 3);
  await seedResult(api, seed, 0, "31.20");
  await seedResult(api, seed, 1, "32.40");
  await api.dispose();
});

test.afterAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  await restoreSchoolSettings(api, original);
  await api.dispose();
});

test("a saved brand and house colour repaint the chrome and the dashboard", async ({ page }) => {
  // Whichever house is actually on this carnival's ladder is the one worth
  // recolouring — the seed picks its swimmers from the synthetic roster.
  await signIn(page);
  // The school-level nav carries the entry only while no carnival is selected,
  // so it is read here — the dashboard below adopts one.
  await expect(page.getByRole("link", { name: "School", exact: true })).toHaveAttribute(
    "href",
    "#/school",
  );

  await page.goto(`/#/carnivals/${seed.carnivalId}/dashboard`);
  const topHouse = page.locator(".standings-row").first().locator(".house-mark");
  await expect(topHouse).toBeVisible();
  const houseName = (await topHouse.innerText()).trim();

  await page.goto("/#/school");
  await expect(page.getByRole("heading", { name: "School settings", level: 1 })).toBeVisible();
  await (await openColour(page, "Brand colour")).fill(NEW_BRAND);

  const rows = page.locator(".house-rows > li");
  const count = await rows.count();
  let houseIndex = -1;
  for (let i = 0; i < count; i++) {
    if ((await page.getByLabel(`House ${i + 1} display name`).inputValue()).trim() === houseName)
      houseIndex = i;
  }
  expect(houseIndex, `no house row named ${houseName}`).toBeGreaterThanOrEqual(0);
  await (await openColour(page, `House ${houseIndex + 1} colour`)).fill(NEW_HOUSE);

  await page.getByRole("button", { name: "Save" }).click();
  await expect(page.getByText("School settings saved")).toBeVisible();

  // The chrome repaints from the refetched branding, not from the form.
  await expect
    .poll(() => page.evaluate(() => document.documentElement.style.getPropertyValue("--brand")))
    .toBe(NEW_BRAND);

  await page.goto(`/#/carnivals/${seed.carnivalId}/dashboard`);
  const swatch = page.locator(".standings-row").first().locator(".house-swatch");
  await expect(swatch).toBeVisible();
  await expect
    .poll(() =>
      swatch.evaluate((el) => getComputedStyle(el).backgroundColor.replace(/\s/g, "")),
    )
    .toBe("rgb(123,45,142)");
});

test("the in-app colour picker drives the value, in a real engine", async ({ page }) => {
  // The unit suite pins this in jsdom; only a real engine drags a pointer across
  // the saturation area — which, unlike the OS dialog it replaced, is real DOM.
  await signIn(page);
  await page.goto("/#/school");
  await expect(page.getByRole("heading", { name: "School settings", level: 1 })).toBeVisible();

  const trigger = page.getByRole("button", { name: /^House 1 colour/ });
  const hex = await openColour(page, "House 1 colour");
  await hex.fill("#123abc");
  await expect(trigger).toContainText("#123abc");
  const preview = page.locator(".house-rows > li").first().locator(".house-swatch");
  await expect(preview).toHaveCSS("background-color", "rgb(18, 58, 188)");

  // `hover` rather than a bare mouse.move: it scrolls the area into view first,
  // and an iPad-height page leaves it below the fold, where mouse events land
  // on nothing at all.
  const area = page.locator(".react-colorful__saturation");
  await area.hover({ position: { x: 12, y: 12 } });
  await page.mouse.down();
  const box = (await area.boundingBox())!;
  await page.mouse.move(box.x + box.width * 0.8, box.y + box.height * 0.8);
  await page.mouse.up();
  await expect(hex).not.toHaveValue("#123abc");

  // A preset is one tap: it writes the value and closes the panel, where the
  // bands above it stay open for fine-tuning (ADR-0046 amendment).
  await page.getByRole("button", { name: `Preset blue ${PRESET}` }).click();
  await expect(page.locator(".colour-panel")).toHaveCount(0);
  await expect(trigger).toContainText(PRESET);
  await expect(preview).toHaveCSS("background-color", "rgb(25, 118, 210)");

  // With no hex field left in the form, "none" is a button (ADR-0046 §3).
  await openColour(page, "House 1 colour");
  await page.getByRole("button", { name: "No colour — text only" }).click();
  await expect(trigger).toContainText("No colour");
  await expect(preview).toHaveCount(0);
  // No save: the shared school row must stay as the other journeys expect it.
});

/**
 * ADR-0048 §8, the second slice's journey: a system administrator gives someone
 * a role by UPN and takes it back, with a reason on both. The row is the point —
 * it is what a termly access review reads, and no unit test proves it survived
 * the round trip through the real API.
 */
const ROLE_UPN = "journey-recorder@school.test";

test("a role is assigned by UPN and revoked, both with a reason", async ({ playwright, page }) => {
  // Rows are never deleted (§2), so a previous run's grant is still active and
  // the server would refuse this one as a duplicate.
  const api = await playwright.request.newContext();
  for (const row of await listRoleAssignments(api))
    if (!row.bootstrap && row.upn === ROLE_UPN && row.revokedAtUtc === null && row.id !== null)
      await revokeRoleAssignment(api, row.id, "Left over from an earlier test run.");
  await api.dispose();

  await signIn(page);
  await page.goto("/#/school");
  await expect(page.getByRole("heading", { name: "Roles", level: 2 })).toBeVisible();

  await page.getByLabel("Sign-in address (UPN or email)").fill(ROLE_UPN);
  await page.getByLabel("Role", { exact: true }).selectOption("Recorder");
  await page
    .getByLabel(/^Reason \(recorded/)
    .fill("Timing desk for the swimming carnival.");
  await page.getByRole("button", { name: "Assign role" }).click();

  // Last, not first: an earlier run's revoked row for this UPN is still listed
  // (rows are never deleted) and sorts ahead of the one just granted.
  const row = page.locator(".row-list > li", { hasText: ROLE_UPN }).last();
  await expect(row.getByText(`${ROLE_UPN} · Recorder`)).toBeVisible();
  // Never signed in, so the row cannot be bound to a subject yet (§5).
  await expect(row.getByText("has not signed in yet")).toBeVisible();

  await row.getByRole("button", { name: `Revoke Recorder for ${ROLE_UPN}` }).click();
  await page.getByLabel(`Reason for revoking Recorder for ${ROLE_UPN}`).fill("Carnival is over.");
  await page.getByRole("button", { name: `Confirm revoking Recorder for ${ROLE_UPN}` }).click();

  await expect(row.getByText("revoked", { exact: true })).toBeVisible();
  await expect(row.getByText(/Revoked .*Carnival is over\./)).toBeVisible();
  await expect(row.getByRole("button", { name: /^Revoke / })).toHaveCount(0);
});

/**
 * ADR-0050 §9, the third slice's journey: the shared event catalogue is brought
 * back into line with the template document the deploy shipped. Written to be
 * re-runnable — the first run applies the dev seed's drift (one catalogue row
 * against an eight-definition swimming template), and every run after it finds
 * a catalogue that already matches, which is the state the section has to
 * render honestly rather than offering an apply that would do nothing.
 */
test("a template is previewed against the catalogue and applied with a reason", async ({
  playwright,
  page,
}) => {
  const api = await playwright.request.newContext();
  const before = await listEventDefinitions(api);
  await api.dispose();
  expect(before.some((d) => d.code === "SW-50-FREE"), "the dev seed's catalogue").toBe(true);

  await signIn(page);
  await page.goto("/#/school");
  await expect(page.getByRole("heading", { name: "Event templates", level: 2 })).toBeVisible();
  // The picker names the document the last deploy shipped, not a database row.
  await expect(page.getByLabel("Template")).toContainText("Swimming carnival");
  await expect(page.getByText("swimming.json")).toBeVisible();

  const apply = page.getByRole("button", { name: "Apply template" });
  await expect(apply).toBeVisible();
  await expect(apply).toBeDisabled();
  await page
    .getByLabel(/^Reason for applying/)
    .fill("Bring the catalogue up to the deployed swimming template.");

  // Nothing left to apply, so the section says so instead of offering the
  // button — and a row the server holds back stays held back, which is why
  // this is not "the catalogue matches the template".
  const settled = page.getByText("Nothing is selected to apply.");

  if (await apply.isEnabled()) {
    await expect(page.getByRole("table")).toBeVisible();
    await apply.click();
    await expect(page.getByText(/^Swimming carnival applied —/)).toBeVisible();
    // The preview re-reads itself after an apply: what it proposed is now done.
    await expect(settled).toBeVisible();
  } else {
    await expect(settled).toBeVisible();
  }
});
