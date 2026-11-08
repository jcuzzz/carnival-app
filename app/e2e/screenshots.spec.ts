import { expect, test, type Locator, type Page } from "@playwright/test";
import {
  reportDeviceQueue,
  seedAthleticsCarnival,
  seedCarnival,
  seedDivisionProfile,
  seedEventDefinition,
  seedFreshCarnival,
  seedMarshallingCarnival,
  seedDenseCarnival,
  seedRecordBook,
  listEventDefinitions,
  renameEventDefinition,
  seedRevokedRoleAssignment,
  seedRoleAssignment,
  type FreshCarnival,
  type SeededCarnival,
  type SeededMarshallingCarnival,
  type SeededAthleticsCarnival,
  type SeededDenseCarnival,
  type SeededRecordBook,
} from "./dev-api";
import {
  attemptField,
  divisionSelector,
  entryRow,
  eventTile,
  expectNothingClipped,
  goOffline,
  goOnline,
  lanePicker,
  refuseWrites,
  rowAction,
  signIn,
  timeField,
} from "./app";
import { FIXED, settleForCapture, stabilise } from "./fixed";

/**
 * The UI review pass: every screen this project argues about, at both widths
 * that matter, into app/ui-review/ for a human to look at. NOT a journey suite
 * and NOT pixel-diffing — it asserts only enough to know a capture is of the
 * state it claims to be (see app/ui-review/README.md).
 *
 * Excluded from `npx playwright test` on purpose: it lives in its own config
 * (playwright.screenshots.config.ts) so the journey count stays the journey
 * count, and it runs as `npm run screenshots`.
 *
 * Serial, one seed per shape: the later captures depend on results the earlier
 * ones recorded (the standings would otherwise be an empty table).
 */
test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page, context }) => {
  if (FIXED) await stabilise(page, context);
});

/** 390: the smallest phone the pilot expects. 820: an iPad in portrait, past
 *  the 768px two-column deck breakpoint (U10). */
const WIDTHS = [390, 820] as const;

let seed: SeededCarnival;
let dense: SeededDenseCarnival;
let lanes: SeededMarshallingCarnival;
let autoFilled: SeededMarshallingCarnival;
let recovery: SeededCarnival;
let stuck: SeededCarnival;
let fleet: SeededCarnival;
let book: SeededRecordBook;
let athletics: SeededAthleticsCarnival;
let fresh: FreshCarnival;
let rostered: FreshCarnival;
let stale: FreshCarnival;
let adding: FreshCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  // Four swimmers: enough for a heat to look like a heat, few enough that the
  // division still has eligible students left to show in the add-entry list.
  seed = await seedCarnival(api, "screens", 4);
  dense = await seedDenseCarnival(api, "screens-dense");
  // Three swimmers in alternating lanes, so the rows' pickers have real gaps
  // to offer.
  lanes = await seedMarshallingCarnival(api, "screens-lanes", 3);
  // ADR-0038 §1–§2: eight lanes and the box ticked BEFORE the entries exist, so
  // the three swimmers arrive already laned and the deck has no lane to decide.
  autoFilled = await seedMarshallingCarnival(api, "screens-autofill", 3, {
    laneCount: 8,
    autoFillLanes: true,
  });
  // ADR-0025 needs a division nobody has recorded on: the recovery captures
  // park four captures of their own, and a row already holding a result would
  // put the deck on the correction path instead of the capture one.
  recovery = await seedCarnival(api, "screens-recovery", 4);
  // ADR-0029 wants a row nobody has recorded on for the same reason recovery
  // does — the banner is shot over a deck mid-capture, not mid-correction.
  stuck = await seedCarnival(api, "screens-stuck", 2);
  // ADR-0034's panel needs devices on it, and each state is seeded through the
  // real endpoint. `LastDrainUtc` is derived from the reported count falling, so
  // "reporting but not draining" is reachable (report once and never drain) —
  // but `LastSeenUtc` is stamped server-side, so STRANDED is not: nothing a
  // client can send makes a device look quiet for fifteen minutes.
  fleet = await seedCarnival(api, "screens-devices", 2);
  await reportDeviceQueue(api, fleet.carnivalId, "screens-desk-laptop", { pending: 2, parked: 0 });
  await reportDeviceQueue(api, fleet.carnivalId, "screens-desk-laptop", { pending: 0, parked: 0 });
  await reportDeviceQueue(api, fleet.carnivalId, "screens-timer-ipad", { pending: 4, parked: 0 });
  await reportDeviceQueue(api, fleet.carnivalId, "screens-timer-ipad", { pending: 2, parked: 0 });
  // One report, never a second: no drain has ever been observed on this device.
  await reportDeviceQueue(api, fleet.carnivalId, "screens-marshal-ipad", { pending: 6, parked: 2 });
  book = await seedRecordBook(api, "screens-records");
  // ADR-0048 §8: one unbound row, one ordinary grant and one revoked, so the
  // roles list shows all three states it can be in.
  await seedRoleAssignment(api, "pool-deck@school.test", "Recorder", "Timing desk on carnival day.");
  await seedRoleAssignment(
    api, "carnival-office@school.test", "CarnivalAdministrator", "Runs the swimming carnival.");
  await seedRevokedRoleAssignment(api, "left-school@school.test", "Viewer");
  // Last, so the swimming seeds above keep the carnival order they had. Its
  // template puts a second sport in the shared catalogue, which is what makes
  // the setup screen's fold (ADR-0052 §9) render at all.
  athletics = await seedAthleticsCarnival(api, "screens-athletics");
  // ADR-0054: a carnival created and then left alone — Draft, no profile, no
  // events, no program. Nothing else may touch it: it IS the empty state.
  fresh = await seedFreshCarnival(api, "screens-fresh");
  // ADR-0057: one carnival to shoot the roster default on, and one whose saved
  // profile names an age the roster it is shot against does not have.
  rostered = await seedFreshCarnival(api, "screens-rostered");
  stale = await seedFreshCarnival(api, "screens-stale-ages");
  await seedDivisionProfile(api, stale.carnivalId);
  // ADR-0059: a swimming definition no carnival here contests, so the setup
  // screen has something under "Available to add" to shoot. The code is the
  // swimming template's own, so applying that template reuses this row.
  await seedEventDefinition(api, {
    code: "SW-50-BACK",
    name: "Backstroke 50m",
    sport: "Swimming",
    measureType: "Time",
    units: "seconds",
  });
  // And a carnival with ages and no events at all, so every swimming definition
  // is a row with an Add on it. Its own carnival because the press WRITES
  // divisions, and no other capture may see them.
  adding = await seedFreshCarnival(api, "screens-add-event");
  await seedDivisionProfile(api, adding.carnivalId);
  await api.dispose();
});

/**
 * One full-page PNG, named `{screen}-{width}.png`. The viewport is set before
 * the shot rather than per-project so both widths come out of one browser and
 * one seed — the layout is CSS-driven, so nothing needs a reload.
 */
async function capture(page: Page, screen: string, width: number): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  // Back to the top first: the app bar is sticky, so a page left scrolled (by
  // a focused field, say) shoots it floating over the middle of the capture.
  await page.evaluate(() => window.scrollTo(0, 0));
  if (FIXED) await settleForCapture(page);
  await page.screenshot({
    path: `ui-review/${screen}-${width}.png`,
    fullPage: true,
    ...(FIXED ? { animations: "disabled" as const } : {}),
  });
}

/**
 * One PNG of a REGION, named the same way. For a panel that shares a page with
 * something already captured: a second full-page shot of the same screen is
 * noise in the gallery, and the panel is what needs reading.
 */
async function captureRegion(
  page: Page,
  region: Locator,
  screen: string,
  width: number,
): Promise<void> {
  await page.setViewportSize({ width, height: 900 });
  if (FIXED) await settleForCapture(page);
  await region.screenshot({
    path: `ui-review/${screen}-${width}.png`,
    ...(FIXED ? { animations: "disabled" as const } : {}),
  });
}

/**
 * Pin ADR-0057 §1/§2's read-only fields on one carnival's settings read. The
 * roster's codes are whatever the synthetic snapshot derives, so a capture of
 * the roster default is only the same shot run to run if the codes are stated
 * here — and `codes: []` is a state no seed can produce at all, because the
 * promoted roster is school-wide.
 */
async function pinRosterAges(page: Page, carnivalId: string, codes: string[]): Promise<void> {
  await page.route(`**/api/v1/carnivals/${carnivalId}/settings`, async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    const body = (await response.json()) as { divisions: unknown };
    await route.fulfill({
      json: {
        ...body,
        rosterAgeGroups: codes,
        divisionsDefault:
          body.divisions !== null || codes.length === 0
            ? null
            : {
                ageGroups: codes,
                categories: ["M", "F"],
                tiers: ["Competitive"],
              },
      },
    });
  });
}

/** Wait for the deck to be drawn before shooting it — a half-loaded list is a
 *  screenshot of the spinner, and it would differ run to run. */
async function settled(locator: Locator): Promise<void> {
  await expect(locator).toBeVisible();
}

test("carnival tiles, the flat division page and the add-entry list", async ({ page }) => {
  await signIn(page);
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${seed.carnivalId}`);
    await settled(eventTile(page, "My events", seed.eventName));
    await capture(page, "carnival-tiles", width);
  }

  // How many students the division has left to offer, learned once and then
  // waited for: the second pass returns to a hash the page is ALREADY on, so
  // nothing reloads and the search box still holds what the first pass typed.
  let eligibleCount: number | null = null;
  for (const width of WIDTHS) {
    // Two divisions of one definition: the selector's FLAT row (ADR-0018 §2).
    await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.eventId}`);
    await settled(entryRow(page, seed.entries[0]!.displayName));
    // One picker per axis (ADR-0042 §2): category and age on this definition.
    await expect(divisionSelector(page).locator(".picker-trigger")).toHaveCount(2);
    const search = page.getByLabel("Search eligible students");
    await search.fill("");
    const listed = page.locator(".eligible-list li");
    if (eligibleCount === null) {
      await expect(listed.first()).toBeVisible();
      eligibleCount = await listed.count();
    } else {
      await expect(listed).toHaveCount(eligibleCount);
    }
    await capture(page, "division-flat", width);

    // The browsable eligible list, narrowed the way a recorder narrows it. The
    // term comes off a student the list is already offering, so it matches
    // whatever the synthetic roster happens to be called.
    const surname = (await page.locator(".eligible-list .row-title").first().textContent())!
      .split(" ")
      .pop()!;
    await search.fill(surname);
    // Typing is debounced: without waiting for the list to actually narrow, the
    // capture is of the term typed over the UNnarrowed roster.
    await expect(listed.filter({ hasNotText: surname })).toHaveCount(0);
    await expect(listed.first()).toBeVisible();
    await capture(page, "add-entry-eligible", width);
  }

  // The state a division starts the morning in (ADR-0039 §1): nobody entered,
  // so no race exists and the deck stands at a PROSPECTIVE "Heat 1" that the
  // first add materialises. The sibling division is the one still in it — since
  // ADR-0039 §1 the server places every create, so any division with an entry
  // in it has a real heat by the time the deck loads.
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.siblingEventId}`);
    const heats = page.getByRole("group", { name: "Heats", exact: true });
    await settled(heats.getByText("Heat 1"));
    await expect(page.getByText("No entries yet — add students below.")).toBeVisible();
    await capture(page, "deck-heat-first", width);
  }
});

test("a division page with the dense selector", async ({ page }) => {
  await signIn(page);
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${dense.carnivalId}/events/${dense.fromEventId}`);
    await settled(page.getByRole("heading", { name: dense.eventName, level: 1 }));
    // All three axes (ADR-0018 §2, ADR-0042 §2): tier, category and age.
    await settled(divisionSelector(page));
    expect(await divisionSelector(page).locator(".picker-trigger").count()).toBe(3);
    await capture(page, "division-dense", width);
  }
});

test("a row inside the undo window, and the same row settled", async ({ page }) => {
  await signIn(page);
  // One swimmer per width: the window is five seconds, which is not enough to
  // resize, re-shoot and still be inside it.
  for (const [i, width] of WIDTHS.entries()) {
    const swimmer = seed.entries[i]!.displayName;
    await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.eventId}`);
    await page.setViewportSize({ width, height: 900 });
    const row = entryRow(page, swimmer);
    await settled(row);

    await timeField(page, swimmer).fill(`34.${5 + i}${6 + i}`);
    await row.getByRole("button", { name: "Save", exact: true }).click();

    // Inside the window: the state slot and the Undo beside it.
    await expect(row.getByRole("button", { name: /^Undo/ })).toBeVisible();
    await capture(page, "deck-undo-window", width);

    // Settled: same slots, now Recorded + Edit, and the row has not moved.
    await expect(row.getByRole("button", { name: "Edit" })).toBeVisible();
    await capture(page, "deck-settled", width);
  }
});

test("the outbox with an item in it", async ({ page, context }) => {
  await signIn(page);
  // Warm the route while the uplink is up: the screens are code-split, so a
  // FIRST visit to /#/outbox with the network cut fails its chunk import and
  // lands on the error boundary instead.
  await page.goto("/#/outbox");
  await expect(page.getByRole("heading", { name: "Outbox", level: 1 })).toBeVisible();

  // Captured offline so the queue is guaranteed to hold something when the
  // screenshot is taken — an accepted item drains and the screen is a list of
  // synced rows, which is not the state worth reviewing.
  await page.goto(`/#/carnivals/${seed.carnivalId}/events/${seed.eventId}`);
  const swimmer = seed.entries[WIDTHS.length]!.displayName;
  await settled(entryRow(page, swimmer));
  await goOffline(context);
  await timeField(page, swimmer).fill("41.23");
  await entryRow(page, swimmer).getByRole("button", { name: "Save", exact: true }).click();
  await expect(entryRow(page, swimmer).locator(".sync-badge.queued")).toBeVisible();

  for (const width of WIDTHS) {
    await page.goto("/#/outbox");
    await expect(page.locator("table tbody tr").first()).toBeVisible();
    await capture(page, "outbox", width);
  }
  await goOnline(context);
});

test("the dashboard, the manage page and the carnival switcher (ADR-0024)", async ({ page }) => {
  await signIn(page);
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${seed.carnivalId}/dashboard`);
    await expect(page.locator(".standings-row").first()).toBeVisible();
    // The dev configuration carries a colour per house (ADR-0044), so this is
    // the coloured board; the text-only one is captured below.
    await expect(page.locator(".house-swatch").first()).toBeVisible();
    await capture(page, "dashboard-standings", width);

    // The organiser surface the carnival landing's button row became.
    await page.goto(`/#/carnivals/${seed.carnivalId}/manage`);
    await settled(page.getByRole("heading", { name: "Manage carnival", level: 1 }));
    await capture(page, "manage", width);

    // Reports (ADR-0004 tier 1): the standings and the results book on one
    // printable page. Waited on the BOOK, not the heading — the heading is
    // there while both reads are still in flight.
    await page.goto(`/#/carnivals/${seed.carnivalId}/reports`);
    await settled(page.locator(".report-block").first());
    // ADR-0028: the record book and the district button share this page. The
    // seeded carnival is not finalised and no record stands, which is exactly
    // the state the pilot spends its whole first carnival in — so the capture
    // is of the empty book and the honest refusal, not of a hypothetical one.
    await settled(page.getByRole("heading", { name: "School records", level: 2 }));
    await expect(page.getByRole("button", { name: "Download district CSV" })).toBeDisabled();
    await capture(page, "reports", width);

    // The switcher, reached the way an official reaches it — the header chip.
    await page.getByRole("link", { name: /^Carnival:/ }).click();
    await settled(page.getByRole("heading", { name: "Carnivals", level: 1 }));
    await capture(page, "carnival-switcher", width);
  }
});

/**
 * ADR-0066 §4 and ADR-0067 §5: a carnival created before any student list is imported. The
 * roster is school-wide and seeded, so its ages and its student count are answered empty here.
 */
async function pinNoStudents(page: Page, carnivalId: string): Promise<void> {
  await pinRosterAges(page, carnivalId, []);
  await page.route(`**/api/v1/carnivals/${carnivalId}/progress`, async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    const body = (await response.json()) as { participation: object[] };
    await route.fulfill({
      json: {
        ...body,
        participation: body.participation.map((house) => ({ ...house, competitors: 0 })),
      },
    });
  });
}

/**
 * The moment after a carnival is created, with no student list: Students is the step pointed
 * at, and Event setup, New event and the ages editor all point at the import instead.
 */
test("a fresh carnival with no student list: the checklist, the blocked setup screens", async ({
  page,
}) => {
  await signIn(page);
  await pinNoStudents(page, fresh.carnivalId);
  const notice = page.getByText(
    "Import the student list first: this carnival's ages come from it.",
  );
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${fresh.carnivalId}/manage`);
    await settled(page.getByRole("heading", { name: "Manage carnival", level: 1 }));
    // Only one step is ever pointed at.
    await expect(page.locator("li.awaiting .row-title")).toHaveText(/1\. Students/);
    await expect(page.getByText("Do this next")).toHaveCount(1);
    await expect(page.getByText("Import the students first")).toBeVisible();
    await capture(page, "manage-checklist", width);

    await page.goto(`/#/carnivals/${fresh.carnivalId}/setup/events`);
    await settled(notice);
    await expect(page.getByRole("link", { name: "Import the students" })).toBeVisible();
    await expect(
      page.getByRole("heading", { name: "Start from the standard events" }),
    ).toHaveCount(0);
    await capture(page, "event-setup-no-roster", width);

    await page.goto(`/#/carnivals/${fresh.carnivalId}/setup/events/new`);
    await settled(notice);
    await expect(
      page.getByRole("button", { name: "Import the student list first" }),
    ).toBeDisabled();
    await capture(page, "event-new-blocked", width);

    // No fallback range (ADR-0067 §5): with nothing to narrow, there is no form.
    await page.goto(`/#/carnivals/${fresh.carnivalId}/setup/divisions`);
    await settled(notice);
    await expect(page.getByRole("button", { name: "Save" })).toHaveCount(0);
    await capture(page, "ages-no-roster", width);

    // ADR-0066 §7: the Deck teaches no setup; a manager gets the one way to Manage.
    await page.goto(`/#/carnivals/${fresh.carnivalId}`);
    await settled(page.getByRole("link", { name: "Set the carnival up" }));
    await capture(page, "deck-not-set-up", width);
  }
});

/**
 * ADR-0066 §4: the standard events are Event setup's own button, even over an empty catalogue.
 * The catalogue is school-wide and seeded, so its read is answered empty here.
 */
test("event setup with an empty catalogue", async ({ page }) => {
  await signIn(page);
  await page.route("**/api/v1/event-definitions?*", (route) =>
    route.request().method() === "GET" ? route.fulfill({ json: [] }) : route.fallback(),
  );
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${fresh.carnivalId}/setup/events`);
    await settled(page.getByText("The event catalogue is empty."));
    await expect(
      page.getByRole("button", { name: "Add the standard swimming events" }),
    ).toBeVisible();
    await capture(page, "event-setup-empty-catalogue", width);
  }
});

/**
 * ADR-0067 §1–2 and ADR-0058 §4: a carnival whose student list is in. Event setup states the
 * ages it will use and offers the standard events as its primary, "New event" draws its grid
 * from the ages, and the editor is a From/To pair over the student list's own ages.
 */
test("ages from the student list: event setup, the add-event grid, the editor", async ({
  page,
}) => {
  // SNR is in the roster and no From/To range can reach it — the one code that
  // proves the checkbox keeps it and that a left-out age is called out.
  const roster = ["12", "13", "14", "15", "16", "17", "18", "SNR"];
  await signIn(page);
  await pinRosterAges(page, rostered.carnivalId, roster);

  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${rostered.carnivalId}/manage`);
    await settled(page.locator("li.awaiting .row-title"));
    await expect(page.locator("li.awaiting .row-title")).toHaveText(/2\. Events/);
    await capture(page, "manage-checklist-roster-default", width);

    // An empty carnival: the standard events are the screen's primary.
    await page.goto(`/#/carnivals/${rostered.carnivalId}/setup/events`);
    await settled(
      page.getByText("Ages 12–18, SNR from the student list · Boys, Girls · Competitive"),
    );
    await expect(page.getByLabel("Carnival template")).toHaveValue("swimming");
    await expect(
      page.getByRole("button", { name: "Add the standard swimming events" }),
    ).toHaveClass(/primary/);
    await capture(page, "event-setup-standard-events", width);

    await page.goto(`/#/carnivals/${rostered.carnivalId}/setup/events/new`);
    await settled(page.getByText("Ages from the student list: 12–18, SNR", { exact: false }));
    // Not blocked: the student list's ages are enough to schedule from.
    await expect(page.getByRole("button", { name: /^Create \d+ division/ })).toBeVisible();
    await capture(page, "event-new-roster-default", width);

    // Pre-filled from the student list: the screen an organiser lands on before
    // anything is saved.
    await page.goto(`/#/carnivals/${rostered.carnivalId}/setup/divisions`);
    await settled(page.getByRole("heading", { name: "Ages and categories", level: 1 }));
    await expect(page.getByLabel(/^From/)).toHaveValue("12");
    await capture(page, "divisions-roster-default", width);

    // A narrowed range with the seniors unticked: the ages left out are named
    // rather than silently dropped.
    await page.getByLabel(/^To/).selectOption("16");
    await page.getByRole("checkbox", { name: "SNR" }).uncheck();
    await settled(
      page.getByText("Ages on the student list left out: 17, 18, SNR", { exact: false }),
    );
    await capture(page, "divisions-narrowed", width);
  }
});

/**
 * ADR-0067 §6: saved ages against a student list that has since changed. The `stale` carnival
 * saves 12–14; each case pins the student list's ages to say something different about them.
 */
test("saved ages against the student list: stale, left out, and no list at all", async ({
  page,
}) => {
  await signIn(page);
  const editor = `/#/carnivals/${stale.carnivalId}/setup/divisions`;
  const setup = `/#/carnivals/${stale.carnivalId}/setup/events`;

  // 14 is saved and no current student has it: a warning and one press that removes it.
  await pinRosterAges(page, stale.carnivalId, ["12", "13"]);
  for (const width of WIDTHS) {
    await page.goto(setup);
    await settled(page.getByText("This carnival has ages no student has: 14.", { exact: false }));
    await expect(page.getByRole("button", { name: "Remove those ages" })).toBeEnabled();
    await capture(page, "event-setup-ages-stale", width);

    await page.goto(editor);
    await settled(page.getByText("Saved ages no current student has: 14", { exact: false }));
    await capture(page, "divisions-stale-age", width);
  }

  // 15 and 16 are on the student list and not saved: a deliberate narrowing, noted quietly.
  await page.unroute(`**/api/v1/carnivals/${stale.carnivalId}/settings`);
  await pinRosterAges(page, stale.carnivalId, ["12", "13", "14", "15", "16"]);
  // A reload, not a hash change: the settings read is cached for longer than this test runs.
  await page.reload();
  for (const width of WIDTHS) {
    await page.goto(setup);
    await settled(page.getByText("Left out of this carnival: 15, 16.", { exact: false }));
    await expect(page.getByRole("button", { name: "Remove those ages" })).toHaveCount(0);
    await capture(page, "event-setup-ages-left-out", width);
  }

  // Saved ages and no student list: the ages are read-only, categories and tiers still change.
  await page.unroute(`**/api/v1/carnivals/${stale.carnivalId}/settings`);
  await pinRosterAges(page, stale.carnivalId, []);
  await page.reload();
  for (const width of WIDTHS) {
    await page.goto(editor);
    await settled(page.getByText("No student list is imported yet", { exact: false }));
    await expect(page.getByLabel(/^From/)).toHaveCount(0);
    await capture(page, "ages-saved-no-roster", width);
  }
});

/**
 * ADR-0060 §4: a roster imported for another year than the carnival's. Pinned on the settings
 * read, because the seeded roster and every seeded carnival share a year.
 */
test("the setup checklist warning a roster imported for another year", async ({ page }) => {
  // Fixed mode pins every carnival date to 2026; otherwise the seed dates it today.
  const rosterYear = (FIXED ? 2026 : new Date().getFullYear()) - 1;
  await signIn(page);
  await page.route(`**/api/v1/carnivals/${rostered.carnivalId}/settings`, async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    await route.fulfill({ json: { ...(await response.json()), rosterYear } });
  });
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${rostered.carnivalId}/manage`);
    const checklist = page.locator("section").filter({
      has: page.getByRole("heading", { name: "Set the carnival up" }),
    });
    // First: Ready to open repeats the sentence under the Open step.
    await settled(checklist.getByText(`ages are for ${rosterYear}`, { exact: false }).first());
    await captureRegion(page, checklist, "manage-checklist-roster-year", width);
  }
});

/**
 * The same board at a school that configured no house colours (ADR-0044 §3):
 * the name carries every row on its own, and nothing else moves. Both captures
 * need reviewing before a UI-affecting commit.
 */
test("the dashboard with no house colours configured (ADR-0044)", async ({ page }) => {
  await signIn(page);
  await page.route("**/api/v1/school/houses", async (route) => {
    const response = await route.fetch();
    const body = (await response.json()) as {
      houses: string[];
      houseMarks: { code: string; name: string; colour: string | null }[];
    };
    await route.fulfill({
      json: { ...body, houseMarks: body.houseMarks.map((mark) => ({ ...mark, colour: null })) },
    });
  });
  // A reload, not a hash change: the colours are minted at boot, so the
  // intercept only takes effect on a fresh document.
  await page.goto(`/#/carnivals/${seed.carnivalId}/dashboard`);
  await page.reload();
  await settled(page.locator(".standings-row").first());
  await expect(page.locator(".house-swatch")).toHaveCount(0);

  for (const width of WIDTHS) await capture(page, "dashboard-text-only", width);
});

test("the deck with lanes auto-filled (ADR-0038 §2), and an add landing in the heat", async ({
  page,
}) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${autoFilled.carnivalId}/events/${autoFilled.primaryEventId}`);
  await settled(entryRow(page, autoFilled.primaryEntries[0]!.displayName));
  // Server-laned in entry order, so the first swimmer holds lane 1 — the capture
  // is of a deck nobody has marshalled, which is the whole point of the setting.
  await expect(
    await lanePicker(page, autoFilled.primaryEntries[0]!.displayName),
  ).toHaveValue("1");

  // …and an add from the panel below lands in the heat on screen (ADR-0039 §2),
  // laned by the server, which is what the capture is of.
  const listed = page.locator(".eligible-list li");
  await expect(listed.first()).toBeVisible();
  const added = (await listed.first().locator(".row-title").textContent())!;
  await listed.first().getByRole("button", { name: "Add" }).click();
  await expect(await lanePicker(page, added)).toHaveValue("4");
  for (const width of WIDTHS) await capture(page, "deck-auto-fill", width);
});

test("the flat deck: lanes, the heat picker, the armed Swap and Remove", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${lanes.carnivalId}/events/${lanes.primaryEventId}`);
  await settled(entryRow(page, lanes.primaryEntries[0]!.displayName));

  // Three swimmers laned from the rows themselves, into ALTERNATE lanes, so the
  // gaps a picker offers are real. The first pick mints the race and the rest
  // join it.
  for (const [i, swimmer] of lanes.primaryEntries.slice(0, 3).entries()) {
    const lane = await lanePicker(page, swimmer.displayName);
    await lane.selectOption(String(i * 2 + 1));
    await expect(await lanePicker(page, swimmer.displayName)).toHaveValue(String(i * 2 + 1));
  }
  await settled(page.getByRole("group", { name: "Heats", exact: true }).getByText("Heat 1"));
  for (const width of WIDTHS) await capture(page, "deck-lanes", width);

  // The armed Swap (ADR-0030): an occupied lane offered as a named exchange,
  // and the two-step it fires behind. Not red — a swap loses nothing — which is
  // exactly the thing only a capture can check.
  const swapper = lanes.primaryEntries[0]!.displayName;
  const partner = lanes.primaryEntries[1]!.displayName;
  await (await lanePicker(page, swapper)).selectOption({
    label: `Lane 3 — swap with ${partner}`,
  });
  const confirmSwap = page.getByRole("button", {
    name: `Confirm swapping ${swapper} with ${partner}`,
  });
  await expect(confirmSwap).toBeFocused();
  for (const width of WIDTHS) await capture(page, "deck-swap-confirm", width);
  await page.getByRole("button", { name: "Cancel" }).click();

  // The armed Remove (ADR-0020 §2) — the strip whose label rendered red on red
  // until `button.armed` was made to outrank `button.danger`. Armed ONCE for
  // both widths: the confirm takes focus, and the strip's own onFocus cancels
  // the four-second auto-disarm, so the state survives the resize.
  const removed = lanes.primaryEntries[0]!.displayName;
  await rowAction(page, removed, "Remove from heat");
  const confirmRemove = page.getByRole("button", {
    name: `Confirm removing ${removed} from this race`,
  });
  await expect(confirmRemove).toBeFocused();
  for (const width of WIDTHS) await capture(page, "deck-remove-confirm", width);
  await page.getByRole("button", { name: "Cancel" }).click();

  // ADR-0039 §1, ADR-0042 §2: "New heat" mints the next one and SELECTS it,
  // so what the marshal lands on is an empty heat with the add panel under it.
  const heats = page.getByRole("group", { name: "Heats", exact: true });
  const heatTrigger = heats.getByRole("button", { name: /^Heat / });
  await heatTrigger.click();
  await page.getByRole("menuitem", { name: /^New heat/ }).click();
  await expect(heatTrigger).toContainText("Heat 2");
  await expect(page.getByText("No swimmers yet — add one below.")).toBeVisible();
  for (const width of WIDTHS) await capture(page, "deck-heat-empty", width);

  // Then one swimmer split into it from heat 1, so the picker carries two real
  // counts and the second heat is the one on screen.
  await heatTrigger.click();
  await page.getByRole("menuitem", { name: /^Heat 1/ }).click();
  await expect(page.getByRole("menu")).toHaveCount(0);
  const split = lanes.primaryEntries[2]!.displayName;
  const picker = await lanePicker(page, split);
  // By VALUE off the Heat 2 group: "Lane 1" is a label both groups can carry,
  // and a move is encoded as `__move:{raceId}:{lane}` (ADR-0038 §3 note 1).
  const intoHeat2 = await picker
    .locator('optgroup[label="Heat 2"] > option')
    .first()
    .getAttribute("value");
  await picker.selectOption(intoHeat2!);
  const confirmMove = page.getByRole("button", { name: new RegExp(`^Confirm moving ${split}`) });
  await expect(confirmMove).toBeFocused();
  await page.waitForTimeout(400);
  await confirmMove.click();
  await heatTrigger.click();
  await page.getByRole("menuitem", { name: /^Heat 2/ }).click();
  await expect(page.getByRole("menu")).toHaveCount(0);
  await expect(entryRow(page, split)).toBeVisible();
  for (const width of WIDTHS) await capture(page, "deck-heats", width);

  // Open, because the per-heat progress the chip row used to show at a glance
  // now lives inside the menu (ADR-0042, the accepted cost).
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(() => window.scrollTo(0, 0));
    await heatTrigger.click();
    await expect(page.getByRole("menu")).toBeVisible();
    await capture(page, "deck-heat-picker", width);
    await page.keyboard.press("Escape");
  }
});

test("event setup: the catalogue and its ages line, an event, a new event, the ages", async ({
  page,
  playwright,
}) => {
  // The seeded carnival is built by scheduling divisions directly, so it has no
  // profile until one is written — and an unconfigured grid is the empty state,
  // not the screen under review.
  const api = await playwright.request.newContext();
  await seedDivisionProfile(api, seed.carnivalId);
  await api.dispose();

  await signIn(page);
  // The saved ages, matched by the student list, so the line stands alone (ADR-0067 §1).
  await pinRosterAges(page, seed.carnivalId, ["12", "13", "14"]);
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${seed.carnivalId}/setup/events`);
    await settled(page.getByRole("heading", { name: "Event setup", level: 1 }));
    const line = page.locator(".button-row").filter({
      has: page.getByRole("link", { name: "Ages and categories" }),
    });
    await settled(line.getByText("Ages 12–14 · Boys, Girls · Competitive, Social"));
    await capture(page, "event-setup", width);
    await captureRegion(page, line, "event-setup-ages-line", width);

    // A carnival with events keeps the standard events behind a disclosure.
    await page.getByRole("button", { name: "Add the rest of the standard events" }).click();
    const standard = page.locator("section").filter({
      has: page.getByRole("button", { name: "Hide the standard events" }),
    });
    await settled(standard.getByLabel("Carnival template"));
    await expect(standard.getByLabel("Carnival template")).toHaveValue("swimming");
    await captureRegion(page, standard, "event-setup-standard-events-open", width);

    await page.getByRole("link").filter({ hasText: seed.eventName }).first().click();
    await settled(page.getByRole("button", { name: "Save event" }));
    await capture(page, "event-editor", width);

    await page.goto(`/#/carnivals/${seed.carnivalId}/setup/events/new`);
    await settled(page.getByRole("button", { name: /^Create \d+ division/ }));
    await capture(page, "event-new", width);

    // ADR-0056 amendment: the event TYPE is the selection, and Attempts belongs
    // to the best-of-attempts type alone.
    await page.getByLabel("Event type").selectOption("Distance");
    await expect(page.getByRole("spinbutton", { name: "Attempts" })).toHaveValue("3");
    await capture(page, "event-new-attempts", width);

    // And the bar-ladder type takes the two deck SUGGESTIONS in its place.
    await page.getByLabel("Event type").selectOption("Height");
    await expect(page.getByLabel("Suggested opening height")).toHaveValue("1.20");
    await expect(page.getByLabel("Usual rise")).toHaveValue("0.05");
    await expect(page.getByLabel("Attempts per height")).toHaveValue("3");
    await capture(page, "event-new-bar-ladder", width);

    await page.goto(`/#/carnivals/${seed.carnivalId}/setup/divisions`);
    await settled(page.getByRole("heading", { name: "Ages and categories", level: 1 }));
    await expect(page.getByLabel(/^To/)).toHaveValue("14");
    await capture(page, "divisions-profile", width);
  }
});

/**
 * ADR-0052 §9: two sports share one catalogue, so the swimming carnival's setup
 * screen lists its own and folds the athletics definitions behind a count.
 */
test("event setup with a second sport in the catalogue (ADR-0052 §9)", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/setup/events`);
  await settled(page.getByRole("heading", { name: "Event setup", level: 1 }));
  // The whole athletics template, none of which this carnival contests. The
  // folded state is already what `event-setup` captures, so only the opened one
  // is worth a picture.
  // The count is whatever the catalogue holds, not a number this test pins:
  // what matters is that the fold names one and flips when it opens.
  const fold = page.getByRole("button", { name: /from other sports$/ });
  await expect(fold).toHaveText(/^Show \d+ from other sports$/);

  await fold.click();
  await expect(fold).toHaveText(/^Hide \d+ from other sports$/);
  for (const width of WIDTHS) await capture(page, "event-setup-other-sports-open", width);
});

/**
 * ADR-0059 §2: one press puts the whole profile matrix of a catalogue event in
 * the carnival, and the row moves to "In this carnival" with its count.
 */
test("event setup: adding a catalogue event in one press (ADR-0059)", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${adding.carnivalId}/setup/events`);
  await settled(page.getByRole("heading", { name: "Available to add" }));
  // A row's Add, not the standard events' "Add the standard … events" above the list.
  const add = page.locator("button.add-event").first();
  const name = (await add.getAttribute("aria-label"))!.replace(/^Add /, "");

  await add.click();
  const inCarnival = page
    .locator("section")
    .filter({ has: page.getByRole("heading", { name: "In this carnival" }) });
  await expect(inCarnival.getByText(name, { exact: true })).toBeVisible();
  await expect(inCarnival.getByText(/\d+ divisions/).first()).toBeVisible();
  // After the toast, not under it: it is position:fixed, so a full-page capture
  // paints it over the middle of the list rather than at the foot of the screen.
  await expect(page.locator(".toast")).toHaveCount(0);
  for (const width of WIDTHS) await capture(page, "event-setup-added", width);
});

/**
 * ADR-0066 §5–6: Ready to open over the divisions the one-press add just wrote (ages 12–14),
 * against a roster pinned to 12 and 13 and to last year, so two warnings have something to say.
 */
test("Manage's Ready to open, warning before the open", async ({ page }) => {
  const rosterYear = (FIXED ? 2026 : new Date().getFullYear()) - 1;
  await signIn(page);
  await page.route(`**/api/v1/carnivals/${adding.carnivalId}/settings`, async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    await route.fulfill({
      json: { ...(await response.json()), rosterAgeGroups: ["12", "13"], rosterYear },
    });
  });
  // While Draft, the checklist's Open step holds the check and both opens.
  const openStep = page.locator("li").filter({
    has: page.getByRole("button", { name: "Open carnival" }),
  });
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${adding.carnivalId}/manage`);
    await settled(openStep.getByText("an age no current student has", { exact: false }));
    await expect(openStep.getByText(`ages are for ${rosterYear}`, { exact: false })).toBeVisible();
    await expect(openStep.getByRole("button", { name: "Open the carnival only" })).toBeVisible();
    await captureRegion(page, openStep, "manage-ready-to-open", width);
  }

  // The same Draft carnival on the Deck: it has events, so a manager is told where it opens.
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${adding.carnivalId}`);
    await settled(page.getByText("This carnival isn't open yet.", { exact: false }));
    await expect(page.locator(".alert.notice").getByRole("link", { name: "Manage" })).toBeVisible();
    await capture(page, "deck-draft-notice", width);
  }
});

/**
 * The field deck (ADR-0052 §1–§4, ADR-0053 §6): the attempt strip with its
 * centimetre mask, a series whose best mark is derived, an all-foul series read
 * as NM, no heats anywhere, and the status menu that offers DNS/DNF/DQ and NOT
 * NM because the count derives it.
 */
test("the field-event deck: the attempt strip, a derived best, an NM and the statuses", async ({
  page,
}) => {
  await signIn(page);
  const capturing = athletics.entries[2]!.displayName;
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${athletics.carnivalId}/events/${athletics.eventId}`);
    await settled(entryRow(page, capturing));
    const marked = entryRow(page, athletics.entries[0]!.displayName);
    // Derived from 4.10 / X / 4.32 — never typed (ADR-0053 §2).
    await expect(marked.locator(".result-value")).toHaveText("4.32 m");
    await expect(marked.locator(".attempt-mark").nth(1)).toHaveText("X");
    await expect(
      entryRow(page, athletics.entries[1]!.displayName).locator(".result-value"),
    ).toHaveText("NM");
    // Heats are a Time event's vocabulary (§6): no axis, no "heat N", no divider.
    await expect(page.getByRole("group", { name: "Heats" })).toHaveCount(0);
    // Focused, so the row being worked shows the rule it is typed under.
    await attemptField(page, capturing, 2).focus();
    await capture(page, "deck-field-mark", width);
  }

  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.getByRole("button", { name: `Actions for ${capturing}` }).click();
    await expect(page.getByRole("menuitem", { name: "DNF", exact: true })).toBeVisible();
    await expect(page.getByRole("menuitem", { name: "NM", exact: true })).toHaveCount(0);
    await capture(page, "deck-field-statuses", width);
    await page.keyboard.press("Escape");
  }
});

/** One row of the card grid, found by the athlete standing on it (ADR-0056 §4). */
function jumpRow(page: Page, name: string): Locator {
  return page.locator(".jump-row").filter({ hasText: name });
}

/**
 * The high-jump deck (ADR-0056 §4, amended 2026-09-21): one plane at the deck's
 * full width, the bar named in the card header beside the control that moves
 * it, and a cell that expands where it sits to take one mark.
 */
test("the high-jump deck: the card grid, an expanded cell and the raise control", async ({
  page,
}) => {
  await signIn(page);
  const deck = `/#/carnivals/${athletics.carnivalId}/events/${athletics.highJumpEventId}`;
  const atTheBar = athletics.highJumpEntries[0]!.displayName;
  const overIt = athletics.highJumpEntries[1]!.displayName;
  const out = athletics.highJumpEntries[3]?.displayName;

  for (const width of WIDTHS) {
    await page.goto(deck);
    await settled(page.locator(".jump-grid"));
    // Derived by the server from the card, never typed (ADR-0056 §1).
    await expect(jumpRow(page, overIt).locator(".jump-best")).toHaveText("1.30");
    // One tinted column: the bar that is up is the last height on the ladder.
    await expect(page.locator(".jump-height.bar")).toHaveText("1.30");
    // An outlined cell is where a mark can still land — the `_` glyph is gone.
    await expect(jumpRow(page, atTheBar).locator(".jump-mark.markable").first()).toBeVisible();
    // Amendment 2026-09-25 (b): one box per attempt, three to a height here.
    await expect(jumpRow(page, atTheBar).locator(".jump-box")).toHaveCount(9);
    await expect(page.locator(".jump-next")).toHaveCount(0);
    // The bar and the control that moves it read together, above the grid.
    await expect(page.locator(".jump-card-head .jump-bar-name")).toHaveText("Bar at 1.30 m");
    if (out) await expect(jumpRow(page, out)).toHaveClass(/out/);
    // Heats are a Time event's vocabulary (ADR-0053 §6) — no axis, no divider.
    await expect(page.getByRole("group", { name: "Heats" })).toHaveCount(0);
    await capture(page, "deck-high-jump", width);
  }

  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    // A finger's tap, not click(): Playwright would scroll the grid itself first.
    const box = page.getByRole("button", { name: `Mark ${atTheBar} at 1.30 m, attempt 2 of 3` });
    await box.dispatchEvent("click");
    // In the grid, under the athlete's own row — never a dialog (amendment
    // 2026-09-21), at either width.
    await expect(page.locator(".jump-panel")).toBeVisible();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(
      page.getByRole("button", { name: new RegExp(`^O clear for ${atTheBar}`) }),
    ).toBeVisible();
    await capture(page, "deck-high-jump-cell", width);
    await box.dispatchEvent("click");
    await expect(page.locator(".jump-panel")).toHaveCount(0);
  }

  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    // Pre-filled from the ladder: 1.30 plus the definition's 0.05 step.
    await page.getByRole("button", { name: "Raise bar to 1.35 m" }).click();
    await expect(page.locator(".jump-card-head").getByLabel("New height")).toBeVisible();
    await capture(page, "deck-high-jump-raise", width);
    await page.locator(".jump-raise").getByRole("button", { name: "Cancel" }).click();
    await expect(page.locator(".jump-raise-open")).toHaveCount(0);
  }
});

/**
 * ADR-0056 amendment 2026-09-25 (c): a marked box edits its own mark. Blake's
 * 1.25 reads XXO, so a clearance at attempt 1 names the two marks it takes with it.
 */
test("the high-jump deck: a marked box's edit panel, with its knock-on named", async ({
  page,
}) => {
  await signIn(page);
  const deck = `/#/carnivals/${athletics.carnivalId}/events/${athletics.highJumpEventId}`;
  const scrappy = athletics.highJumpEntries[2]!.displayName;
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(deck);
    await settled(page.locator(".jump-grid"));
    // The grid opens at the bar; bring the 1.25 group in beside the athletes.
    await page.locator(".jump-scroll").evaluate((el) => {
      const edge = el.querySelector(".jump-athlete-head")!.getBoundingClientRect().right;
      const group = [...el.querySelectorAll(".jump-height")].find(
        (th) => th.textContent === "1.25",
      )!;
      el.scrollLeft += group.getBoundingClientRect().left - edge;
    });
    await page
      .getByRole("button", { name: `Correct ${scrappy} at 1.25 m, attempt 1` })
      .dispatchEvent("click");
    await expect(page.locator(".jump-panel")).toContainText("attempt 1 — now X");
    await page.getByRole("button", { name: `O clear for ${scrappy} at 1.25 m` }).click();
    await expect(page.locator(".jump-panel")).toContainText(
      "This also removes attempts 2 and 3 at 1.25 m.",
    );
    await expect(page.getByRole("button", { name: "Undo last mark" })).toHaveCount(0);
    await capture(page, "deck-high-jump-edit", width);
    await page.locator(".jump-panel").getByRole("button", { name: "Cancel" }).click();
    await expect(page.locator(".jump-panel")).toHaveCount(0);
  }
});

/**
 * ADR-0056 amendment 2026-09-17: the TYPE is the capture mode. A Height
 * division whose definition suggests nothing is still the card grid, with an
 * opening height the official sets on the day.
 */
test("the high-jump deck with nothing suggested by the definition", async ({ page }) => {
  await signIn(page);
  const deck = `/#/carnivals/${athletics.carnivalId}/events/${athletics.bareHighJumpEventId}`;
  for (const width of WIDTHS) {
    await page.goto(deck);
    await settled(page.locator(".jump-grid"));
    await expect(page.locator(".jump-bar-name")).toHaveText("No opening height yet");
    await expect(page.getByRole("button", { name: "Set opening height", exact: true })).toBeVisible();
    // No mark yet, so the attempts per height is still the division's to switch.
    await expect(page.getByLabel("Attempts per height")).toHaveValue("3");
    await capture(page, "high-jump-no-suggestions", width);
  }
});

/**
 * ADR-0056 amendment 2026-09-25: a division run at two attempts per height. The
 * header states it once a mark has landed, and XX at one height is out.
 */
test("the high-jump deck at two attempts per height", async ({ page }) => {
  await signIn(page);
  const deck = `/#/carnivals/${athletics.carnivalId}/events/${athletics.twoAttemptEventId}`;
  const atTheBar = athletics.twoAttemptEntries[0]!.displayName;
  const out = athletics.twoAttemptEntries[1]!.displayName;
  for (const width of WIDTHS) {
    await page.goto(deck);
    await settled(page.locator(".jump-grid"));
    await expect(page.locator(".jump-card-head")).toContainText("2 attempts per height");
    await expect(jumpRow(page, out)).toHaveClass(/out/);
    await capture(page, "deck-high-jump-two-attempts", width);
  }
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 900 });
    const box = page.getByRole("button", { name: `Mark ${atTheBar} at 1.25 m, attempt 2 of 2` });
    await box.dispatchEvent("click");
    await expect(page.locator(".jump-panel")).toContainText("attempt 2 of 2");
    await expect(page.locator(".jump-panel")).toContainText("A second fail at this height");
    await capture(page, "deck-high-jump-two-attempts-cell", width);
    await box.dispatchEvent("click");
    await expect(page.locator(".jump-panel")).toHaveCount(0);
  }
});

/** The same card on paper (ADR-0056 §4), with the countback rule under it. */
test("the results book prints the high-jump card", async ({ page }) => {
  await signIn(page);
  for (const width of WIDTHS) {
    await page.goto(`/#/carnivals/${athletics.carnivalId}/reports`);
    const block = page
      .locator(".report-block")
      .filter({ hasText: athletics.highJumpEventName })
      .first();
    await settled(block.locator("table.jump-book"));
    await expect(block.getByText(/Places by best height/)).toBeVisible();
    await block.scrollIntoViewIfNeeded();
    await capture(page, "book-high-jump", width);
  }
});

/**
 * Seven streams of program (ADR-0052 §8) — on screen, and again under the print
 * stylesheet, which is the one ADR-0014 phase 6 turns landscape from five.
 */
test("the athletics program, on screen and in print", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${athletics.carnivalId}/program`);
  await expect(page.locator(".program-stream")).toHaveCount(7);
  for (const width of WIDTHS) await capture(page, "program-athletics", width);

  await page.emulateMedia({ media: "print" });
  try {
    for (const width of WIDTHS) await capture(page, "program-athletics-print", width);
  } finally {
    await page.emulateMedia({ media: null });
  }
});

/**
 * The recovery surface ADR-0025 built, in the states a reviewer has to see: the
 * three parked shapes side by side, the armed discard open, what a discarded
 * capture leaves behind, and the correction field mid-edit.
 *
 * The refusals are fulfilled rather than provoked — a taken lane and a second
 * result for one entry both need a SECOND actor, which one browser holding one
 * device's queue does not have. Everything else on the screen is the real API.
 */
test("outbox recovery: parked work, the armed discard, a discard and the time editor", async ({
  page,
  context,
}) => {
  await signIn(page);
  // Warm the code-split route before anything is intercepted, as the outbox
  // capture above does.
  await page.goto("/#/outbox");
  await expect(page.getByRole("heading", { name: "Outbox", level: 1 })).toBeVisible();

  const settledSwimmer = recovery.entries[0]!.displayName;
  const conflicted = recovery.entries[1]!;
  const refused = recovery.entries[2]!;
  const laned = recovery.entries[3]!;
  // Server wording verbatim (ResultEndpoints, RaceEndpoints): `detail` is what
  // the deck and the outbox actually print, so a made-up sentence would make
  // the capture evidence of copy nobody ships.
  await refuseWrites(context, [
    {
      path: `/entries/${conflicted.id}/results`,
      status: 409,
      error: "RESULT_EXISTS",
      detail: "This entry already has a result. Correct it rather than capturing it again.",
    },
    // Terminal (rejected, not parked): a 403 is the one refusal the outbox
    // discards on purpose — a role revoked mid-day (ApiErrorAuthorizationResultHandler).
    {
      path: `/entries/${refused.id}/results`,
      status: 403,
      error: "FORBIDDEN",
      detail: "Your role does not permit this action.",
    },
    {
      path: `/entries/${laned.id}/placement`,
      method: "PUT",
      status: 409,
      error: "LANE_TAKEN",
      detail: "Lane 6 is already taken in this race by another entry.",
    },
  ]);

  await page.goto(`/#/carnivals/${recovery.carnivalId}/events/${recovery.eventId}`);
  await settled(entryRow(page, settledSwimmer));

  // One capture that lands, first: the parked rows below are only reviewable
  // against a settled row in the same shot — `.row-state` reserves its width,
  // and a badge that became a button must not have taken any more of it.
  await timeField(page, settledSwimmer).fill("34.12");
  await entryRow(page, settledSwimmer).getByRole("button", { name: "Save", exact: true }).click();
  await expect(entryRow(page, settledSwimmer).locator(".result-value")).toHaveText("34.12");

  await timeField(page, conflicted.displayName).fill("35.67");
  await entryRow(page, conflicted.displayName)
    .getByRole("button", { name: "Save", exact: true })
    .click();
  await expect(entryRow(page, conflicted.displayName).locator(".sync-badge.conflict")).toBeVisible();

  await timeField(page, refused.displayName).fill("36.88");
  await entryRow(page, refused.displayName)
    .getByRole("button", { name: "Save", exact: true })
    .click();
  await expect(entryRow(page, refused.displayName).locator(".sync-badge.rejected")).toBeVisible();

  // A NON-result capture parked the same way: every lane tap is an entry.assign.
  await (await lanePicker(page, laned.displayName)).selectOption("6");
  await expect(
    entryRow(page, laned.displayName).locator(".lane-control .sync-badge.conflict"),
  ).toBeVisible();

  // Settled first, every row: a capture taken inside the undo window shows four
  // Undo buttons and no Edit, which is a picture of the five seconds after a
  // save rather than of the row a parked capture leaves behind.
  await expect(entryRow(page, settledSwimmer).getByRole("button", { name: "Edit" })).toBeVisible();
  await expect(
    entryRow(page, conflicted.displayName).getByRole("button", { name: "Edit" }),
  ).toBeVisible();
  await expect(
    entryRow(page, refused.displayName).getByRole("button", { name: /^Undo/ }),
  ).toHaveCount(0);
  for (const width of WIDTHS) await capture(page, "deck-parked", width);

  await page.goto("/#/outbox");
  const parked = page.locator(".outbox-table").first();
  const done = page.locator(".outbox-table").nth(1);
  await expect(parked.locator("tbody tr")).toHaveCount(3);
  // Which actions each parked shape offers is the whole decision (§1, §3), so
  // the capture is pinned to it: a result kind gets both requeues, another kind
  // gets one, and a rejection gets neither.
  const resultRow = parked.locator("tr").filter({ hasText: conflicted.displayName });
  await expect(resultRow.getByRole("button", { name: "Requeue", exact: true })).toBeVisible();
  await expect(resultRow.getByRole("button", { name: "Edit and requeue" })).toBeVisible();
  const laneRow = parked.locator("tr").filter({ hasText: laned.displayName });
  await expect(laneRow.getByRole("button", { name: "Requeue", exact: true })).toBeVisible();
  await expect(laneRow.getByRole("button", { name: "Edit and requeue" })).toHaveCount(0);
  const rejectedRow = parked.locator("tr").filter({ hasText: refused.displayName });
  await expect(rejectedRow.getByRole("button", { name: "Requeue", exact: true })).toHaveCount(0);
  await expect(rejectedRow.getByRole("button", { name: /^Discard/ })).toBeVisible();
  // A recovery control behind a sideways scroll is not a recovery control, and
  // the band that broke — an iPad in portrait — is between the two widths shot.
  await expectNothingClipped(page, parked);
  for (const width of WIDTHS) await capture(page, "outbox-parked", width);

  // The armed confirm, open — never captured in this codebase before, and where
  // an invisible label hid for weeks. Armed once for both widths: the confirm
  // takes focus, which cancels the auto-disarm.
  await rejectedRow.getByRole("button", { name: /^Discard/ }).click();
  const confirmDiscard = page.getByRole("button", { name: /^Confirm discarding/ });
  await expect(confirmDiscard).toBeFocused();
  // Worse than a clipped action: a destructive confirm nobody can reach to
  // cancel. The strip is inside the same scroller, so the sweep covers it.
  await expectNothingClipped(page, parked);
  for (const width of WIDTHS) await capture(page, "outbox-discard-armed", width);

  // The armed confirm ignores taps for its first 300ms (a wet finger
  // double-taps, and the second tap of one gesture must not BE the
  // confirmation). Both captures together take less than that, so the
  // confirming tap has to wait the way a person reading the sentence does.
  await page.waitForTimeout(400);
  await confirmDiscard.click();
  // Resolved, so it leaves the attention list entirely — and takes the only
  // record of the capture with it (§2).
  await expect(parked.locator("tbody tr")).toHaveCount(2);
  await expect(parked.locator("tr").filter({ hasText: refused.displayName })).toHaveCount(0);
  const discardedRow = done.locator("tr").filter({ hasText: refused.displayName });
  await expect(discardedRow.locator(".sync-badge.discarded")).toBeVisible();
  await expect(discardedRow.getByText(/^Discarded by /)).toBeVisible();
  for (const width of WIDTHS) await capture(page, "outbox-discarded", width);

  await resultRow.getByRole("button", { name: "Edit and requeue" }).click();
  await expect(page.locator(".outbox-time-edit")).toBeVisible();
  await expect(page.getByRole("button", { name: "Save and requeue" })).toBeVisible();
  for (const width of WIDTHS) await capture(page, "outbox-time-edit", width);
});

/**
 * The detection half (ADR-0029): a lapsed session, loud over whatever screen
 * the recorder is on. The paused cause is the one of the three that is cheap to
 * seed — it needs no threshold, so a single refused write raises it. The other
 * two are derived from AGE and would need a clock the harness does not drive;
 * they are covered by unit tests instead.
 */
test("a lapsed session is loud on the deck (ADR-0029)", async ({ page, context }) => {
  await signIn(page);
  const swimmer = stuck.entries[0]!;
  await refuseWrites(context, [
    {
      path: `/entries/${swimmer.id}/results`,
      status: 401,
      error: "UNAUTHENTICATED",
      detail: "Sign in again to continue.",
    },
  ]);

  await page.goto(`/#/carnivals/${stuck.carnivalId}/events/${stuck.eventId}`);
  await settled(entryRow(page, swimmer.displayName));
  await timeField(page, swimmer.displayName).fill("34.21");
  await entryRow(page, swimmer.displayName)
    .getByRole("button", { name: "Save", exact: true })
    .click();

  // The capture is safe on the device — the queue paused, it was not dropped.
  const banner = page.locator(".stuck-banner");
  await expect(banner).toHaveClass(/paused/);
  await expect(banner).toContainText("1 capture is waiting");
  await expect(banner.getByRole("link", { name: "Sign in to resume" })).toBeVisible();
  for (const width of WIDTHS) await capture(page, "deck-stuck-paused", width);
});

/**
 * The organiser half (ADR-0034): the fleet panel with real rows on it, and the
 * warning it puts beside Finalise. The device that is REPORTING and delivering
 * nothing is the one worth looking at — it is the shape that read green until
 * `lastDrainUtc` was consumed.
 */
test("the device fleet panel, and the warning beside Finalise", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${fleet.carnivalId}/manage`);
  await settled(page.getByRole("heading", { name: "Manage carnival", level: 1 }));

  const devices = page.getByRole("region", { name: "Devices" });
  await expect(devices.getByText("not draining", { exact: true })).toBeVisible();
  await expect(devices.getByText(/reporting but not draining/)).toBeVisible();
  await expect(devices.getByText("nothing waiting")).toBeVisible();
  for (const width of WIDTHS) await capture(page, "manage-devices", width);

  // The warning only has somewhere to sit once Finalise is the next transition.
  await page.getByRole("button", { name: "Close carnival" }).click();
  const zone = page.locator(".danger-zone");
  await expect(zone.getByRole("button", { name: "Finalise carnival" })).toBeVisible();
  await expect(zone.getByText(/still reporting but nothing is leaving/)).toBeVisible();
  for (const width of WIDTHS) await capture(page, "manage-finalise-warning", width);
});

/**
 * Record custody (ADR-0035), which had no capture at all: the book with a
 * withdrawn record still on it — struck, with its reason — and the armed confirm
 * that takes a typed sentence before it will enable.
 */
test("the record book, a voided record and the armed void", async ({ page }) => {
  await signIn(page);
  await page.goto("/#/school/records");
  await settled(page.getByRole("heading", { name: "School records", level: 1 }));
  await expect(page.getByText(/^Voided /).first()).toBeVisible();
  await expect(
    page.locator(".row-title.voided").filter({ hasText: book.voidedDivision }),
  ).toBeVisible();
  for (const width of WIDTHS) await capture(page, "records-admin", width);

  // Armed once for both widths: the reason field takes focus, and this strip
  // carries no auto-disarm (ADR-0035) — the typed sentence IS the second step.
  await page.getByRole("button", { name: "Void…" }).first().click();
  const confirm = page.getByRole("button", { name: /^Confirm voiding/ });
  await expect(confirm).toBeVisible();
  await expect(confirm).toBeDisabled();
  await page.getByLabel(/Reason for voiding/).fill("Timekeeper's sheet misread — the swim was 34.02.");
  await expect(confirm).toBeEnabled();
  for (const width of WIDTHS) await capture(page, "records-admin-void-armed", width);
});

/**
 * School settings (ADR-0045 §6): the colour triggers with the live preview band,
 * and the house rows — then one capture with a panel open, which is the only
 * state that shows the picker (ADR-0046). Read-only here: the journey spec is
 * what saves, and this pass must not move the school row other captures render
 * against.
 */
test("the school settings form", async ({ page }) => {
  await signIn(page);
  await page.goto("/#/school/settings");
  await settled(page.getByRole("heading", { name: "School settings", level: 1 }));
  await settled(page.locator(".brand-preview"));
  await expect(page.locator(".house-rows > li").first()).toBeVisible();
  for (const width of WIDTHS) await capture(page, "school-settings", width);

  await page.getByRole("button", { name: /^Brand colour/ }).click();
  await settled(page.locator(".react-colorful"));
  for (const width of WIDTHS) await capture(page, "school-settings-picker", width);
});

/**
 * Roles (ADR-0048 §8): the list with its three states and the assign form, shot
 * with one revoke armed — the confirmation naming person and role is the state
 * worth looking at, and a full-page capture of the resting screen is already
 * `school-settings`. Nothing is saved: the strip is left un-confirmed.
 */
test("the roles list, with a revoke asking for its reason", async ({ page }) => {
  await signIn(page);
  await page.goto("/#/school/roles");
  await settled(page.getByRole("heading", { name: "Roles", level: 1 }));
  await settled(page.getByRole("heading", { name: "Assign a role" }));
  await expect(page.getByText("has not signed in yet").first()).toBeVisible();

  await page.getByRole("button", { name: /^Revoke / }).first().click();
  await page
    .getByLabel(/^Reason for revoking/)
    .fill("Left the school at the end of term.");
  for (const width of WIDTHS) await capture(page, "school-roles", width);
});

/**
 * Updating the event catalogue from a template (ADR-0050 §9): the preview table a system administrator reads
 * before overwriting the shared catalogue. The dev catalogue is seeded from the
 * same template it is diffed against, so a KNOWN drift is created first — the
 * pre-ADR-0018 event name — and put back straight after: this pass must not
 * move the rows the other captures render against, and it never applies.
 */
test("the event-template preview, with a real diff to read", async ({ playwright, page }) => {
  const api = await playwright.request.newContext();
  const definitions = await listEventDefinitions(api);
  const freestyle = definitions.find((d) => d.code === "SW-50-FREE");
  expect(freestyle, "the dev seed's SW-50-FREE definition").toBeTruthy();
  await renameEventDefinition(api, freestyle!, "50m Freestyle");

  try {
    await signIn(page);
    await page.goto("/#/school/templates");
    await settled(page.getByRole("heading", { name: "Update the event catalogue", level: 1 }));
    // Named, not defaulted: the section selects the first readable template,
    // which since ADR-0052 is athletics — and the drift below is swimming's.
    await page.getByLabel("Template").selectOption("swimming");
    await settled(page.getByRole("table"));
    for (const width of WIDTHS) await capture(page, "school-templates", width);
  } finally {
    await renameEventDefinition(api, freestyle!, freestyle!.name);
    await api.dispose();
  }
});

/**
 * The School area (ADR-0055): the hub that replaced the one long page, the
 * catalogue's carnival-less home, and the create ADR-0054 §3 took off the
 * carnival. The create form is shot at rest — this pass must not put a
 * definition in the catalogue the other captures render against.
 */
test("the School hub, the event catalogue and a new definition", async ({ page }) => {
  await signIn(page);
  await page.goto("/#/school");
  await settled(page.getByRole("heading", { name: "School", level: 1 }));
  await expect(page.getByRole("link", { name: /Event catalogue/ })).toBeVisible();
  // The dev seed sets the school up and adds a carnival, so School shows no setup list.
  await settled(page.getByRole("link", { name: /School settings/ }));
  await expect(page.getByRole("heading", { name: "Set up your school" })).toHaveCount(0);
  for (const width of WIDTHS) await capture(page, "school-hub", width);

  await page.goto("/#/school/events");
  await settled(page.getByRole("heading", { name: "Event catalogue", level: 1 }));
  // Two sports in the dev catalogue since ADR-0052, so the grouping is real.
  await expect(page.getByRole("heading", { name: "Athletics", level: 2 })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Swimming", level: 2 })).toBeVisible();
  for (const width of WIDTHS) await capture(page, "school-catalogue", width);

  await page.goto("/#/school/events/new");
  await settled(page.getByRole("heading", { name: "New definition", level: 1 }));
  await expect(page.getByRole("option", { name: "Swimming" })).toBeAttached();
  for (const width of WIDTHS) await capture(page, "school-catalogue-new", width);
});

/**
 * ADR-0066 §2: a school nobody has set up yet. The dev school is set up and has carnivals, so
 * the three reads that decide the list are answered as a fresh deployment's would be.
 */
test("a fresh school: the School checklist and the empty carnival list", async ({ page }) => {
  // Before signing in: sign-in lands on the carnival list, whose read would otherwise be cached.
  await page.route("**/api/v1/school/settings", async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    await route.fulfill({
      json: { ...(await response.json()), name: "(unconfigured)", setupConfirmed: false },
    });
  });
  await page.route("**/api/v1/admin/role-assignments", (route) =>
    route.request().method() === "GET" ? route.fulfill({ json: [] }) : route.fallback(),
  );
  await page.route("**/api/v1/carnivals", (route) =>
    route.request().method() === "GET" ? route.fulfill({ json: [] }) : route.fallback(),
  );
  // A fresh install has no school name, so the header is the product's: "Carnival App" + trophy.
  await page.route("**/api/v1/branding", (route) =>
    route.fulfill({ json: { schoolName: "(unconfigured)", brandColour: null } }),
  );
  await signIn(page);

  await page.goto("/#/school");
  await settled(page.getByRole("heading", { name: "Set up your school" }));
  await expect(page.getByText("Do this next")).toHaveCount(1);
  await expect(page.locator(".shell-title")).toHaveText("Carnival App");
  for (const width of WIDTHS) await capture(page, "school-hub-setup", width);

  await page.goto("/#/carnivals");
  await settled(page.getByRole("link", { name: "Set up the school first" }));
  for (const width of WIDTHS) await capture(page, "carnivals-empty", width);
});

/** The dev server's sign-in picker: each persona with its roles in words. */
test("the dev sign-in picker", async ({ page }) => {
  await page.goto("/#/login");
  await settled(page.getByRole("button", { name: "Alex Admin" }));
  await expect(page.getByText("(ADR-", { exact: false })).toHaveCount(0);
  for (const width of WIDTHS) await capture(page, "dev-login", width);
});

/**
 * The shell header (ADR-0040 §3): before the school is named it reads "Carnival App" beside a
 * trophy; named, it carries the monogram. Shot on the default navy and on a light yellow brand,
 * whose ink flips to dark.
 */
test("the shell header: unnamed and named, on a dark and a light brand", async ({ page }) => {
  const shots = [
    { screen: "header-pre-name", schoolName: "(unconfigured)", brandColour: null },
    { screen: "header-pre-name-yellow", schoolName: "(unconfigured)", brandColour: "#fbc02d" },
    { screen: "header-named-yellow", schoolName: "Example Synthetic College", brandColour: "#fbc02d" },
  ];
  let branding = shots[0]!;
  await page.route("**/api/v1/branding", (route) =>
    route.fulfill({ json: { schoolName: branding.schoolName, brandColour: branding.brandColour } }),
  );
  await signIn(page);
  const header = page.locator(".shell-header");
  for (const shot of shots) {
    branding = shot;
    await page.goto("/#/carnivals");
    await page.reload();
    await settled(page.getByRole("heading", { name: "Carnivals", level: 1 }));
    await expect(page.locator(".shell-title")).toHaveText(
      shot.schoolName === "(unconfigured)" ? "Carnival App" : shot.schoolName,
    );
    for (const width of WIDTHS) await captureRegion(page, header, shot.screen, width);
  }
});

test("the new carnival form, and a roster import with validation issues", async ({ page }) => {
  await signIn(page);
  // ADR-0038 amendment: the lane pair sits on the create form, auto-fill pre-ticked.
  await page.goto("/#/carnivals");
  await page.getByRole("button", { name: "New carnival" }).click();
  await settled(page.getByLabel("Auto-fill lanes"));
  for (const width of WIDTHS) {
    await capture(page, "carnival-new", width);
  }

  // The synthetic roster carries deliberate DUPLICATE_STKEY / MISSING_HOUSE rows,
  // so one upload shows the validation table the reviewer works from.
  await page.goto("/#/import");
  const year = page.getByLabel("Competition year");
  await settled(year);
  // The default follows the real calendar; the fixed set pins the synthetic year.
  if (FIXED) await year.selectOption("2026");
  await page.locator("input[type=file]").setInputFiles("../data/synthetic/ST_SYNTH.csv");
  await settled(page.getByRole("heading", { name: /Reconciliation preview/ }));
  for (const width of WIDTHS) {
    await capture(page, "import-review", width);
  }

  // ADR-0066 §4: launched from a carnival's Students step, the year is the carnival's.
  await page.goto(`/#/import?carnival=${fresh.carnivalId}`);
  await settled(page.getByRole("link", { name: "← Manage" }));
  await expect(year).toHaveValue(FIXED ? "2026" : String(new Date().getFullYear()));
  await settled(page.getByRole("heading", { name: /Reconciliation preview/ }));
  for (const width of WIDTHS) await capture(page, "import-from-carnival", width);

  // ADR-0066 §8: the one press. Both writes are answered here: promoting the synthetic file's
  // deliberate issues would change the roster every other capture and journey reads.
  let promotedBatch: string | null = null;
  await page.route("**/api/v1/imports/*/approve", (route) =>
    route.fulfill({ json: { id: "screens-batch", status: "Approved" } }),
  );
  await page.route("**/api/v1/imports/*/promote", (route) => {
    promotedBatch = new URL(route.request().url()).pathname.split("/").at(-2)!;
    return route.fulfill({
      json: { id: promotedBatch, status: "Promoted", promotedAtUtc: new Date().toISOString() },
    });
  });
  // The re-reads after the press then say what a real promote would have left.
  await page.route("**/api/v1/imports**", async (route) => {
    if (route.request().method() !== "GET" || promotedBatch === null) return route.fallback();
    const response = await route.fetch();
    const body = (await response.json()) as { id?: string; status?: string }[] | { status?: string };
    const promote = (row: { id?: string; status?: string }) =>
      row.id === promotedBatch ? { ...row, status: "Promoted" } : row;
    await route.fulfill({
      json: Array.isArray(body)
        ? body.map(promote)
        : route.request().url().includes(promotedBatch)
          ? { ...body, status: "Promoted" }
          : body,
    });
  });
  await page.getByRole("button", { name: "Approve and update the student list" }).click();
  await settled(page.getByRole("link", { name: /^Back to / }));
  await expect(page.locator(".toast")).toHaveCount(0);
  for (const width of WIDTHS) await capture(page, "import-promoted-back", width);
  // The result line: what the update did, from the batch's own counts.
  const result = page.locator(".card").filter({
    has: page.getByRole("status").filter({ hasText: /^Student list updated: / }),
  });
  await settled(result);
  for (const width of WIDTHS) await captureRegion(page, result, "import-result", width);
});

/**
 * A school with no houses (ADR-0066 §2): the upload is refused and the screen points at School
 * settings. The dev school has houses, so the refusal is answered here.
 */
test("a roster upload refused because the school has no houses", async ({ page }) => {
  await signIn(page);
  await page.route("**/api/v1/imports", (route) =>
    route.request().method() === "POST"
      ? route.fulfill({
          status: 409,
          contentType: "application/problem+json",
          json: {
            status: 409,
            error: "SCHOOL_HOUSES_REQUIRED",
            detail: "The school has no houses, so no row's house can be checked.",
          },
        })
      : route.fallback(),
  );
  await page.goto("/#/import");
  await settled(page.getByLabel("Competition year"));
  await page.locator("input[type=file]").setInputFiles("../data/synthetic/ST_SYNTH.csv");
  await settled(page.getByRole("link", { name: "Add the houses in School settings" }));
  for (const width of WIDTHS) await capture(page, "import-houses-required", width);
});

/**
 * The public demo's landing page (ADR-0061 §5), first visit and after an expiry. Shot through the
 * dev-only preview route: the dev server is never a demo build, so the real gate cannot show it.
 */
test("the public demo's landing page, fresh and after an expiry", async ({ page }) => {
  await page.goto("/#/dev/demo-landing");
  await settled(page.getByRole("button", { name: "Start demo" }));
  for (const width of WIDTHS) await capture(page, "demo-landing", width);

  await page.goto("/#/dev/demo-landing?expired=1");
  await settled(page.getByText(/previous demo has ended/));
  for (const width of WIDTHS) await capture(page, "demo-landing-expired", width);
});

/**
 * Built-in local accounts (ADR-0065). The dev server signs in through the header picker, so
 * every local-account answer here is a `page.route` stand-in for the API's: the real path is
 * `npm run test:e2e:accounts`. The sign-in box is shot through its dev-only route.
 */
const LOCAL_ME = {
  subject: "local:screens-1",
  displayName: "Backup Admin",
  roles: ["SystemAdministrator"],
  localAccount: true,
};
const ENROLMENT = {
  secret: "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
  otpauthUri:
    "otpauth://totp/Example%20Synthetic%20College:backup-admin" +
    "?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=Example%20Synthetic%20College",
};
const RECOVERY_CODES = [
  "k7qf-3mzp", "x2bn-9wte", "r4hd-6cja", "m8vs-1gku", "p3ny-7fqe",
  "t9wc-2hlx", "b6ze-4rdm", "f1ju-8snv", "h5ka-0pyt", "w2gl-3xqc",
];

async function localSession(page: Page): Promise<void> {
  await page.addInitScript((session) => {
    localStorage.setItem("carnival.session", JSON.stringify(session));
  }, LOCAL_ME);
}

test("sign-in: the username, password, code and enrolment steps", async ({ page }) => {
  await page.route("**/api/v1/auth/methods", (route) => route.fulfill({ json: { provider: null } }));
  await page.route("**/api/v1/auth/identify", (route) =>
    route.fulfill({ json: { method: "password" } }),
  );
  const answers: object[] = [
    { status: "codeRequired" },
    { status: "enrolmentRequired", ...ENROLMENT },
    { status: "signedIn", me: LOCAL_ME, recoveryCodes: RECOVERY_CODES },
  ];
  await page.route("**/api/v1/auth/local", (route) => route.fulfill({ json: answers.shift() }));

  await page.goto("/#/dev/sign-in");
  await settled(page.getByLabel("Username", { exact: true }));
  for (const width of WIDTHS) await capture(page, "sign-in-username", width);

  await page.getByLabel("Username", { exact: true }).fill("backup-admin");
  await page.getByRole("button", { name: "Continue" }).click();
  await settled(page.getByLabel("Password", { exact: true }));
  for (const width of WIDTHS) await capture(page, "sign-in-password", width);

  await page.getByLabel("Password", { exact: true }).fill("lantern orchard quietly ticking");
  await page.getByRole("button", { name: "Sign in" }).click();
  await settled(page.getByLabel("The 6-digit code from your authenticator app"));
  for (const width of WIDTHS) await capture(page, "sign-in-code", width);

  // A school that requires an authenticator, met by an account without one.
  await page.getByRole("button", { name: "Not you?" }).click();
  await page.getByLabel("Username", { exact: true }).fill("backup-admin");
  await page.getByRole("button", { name: "Continue" }).click();
  await page.getByLabel("Password", { exact: true }).fill("lantern orchard quietly ticking");
  await page.getByRole("button", { name: "Sign in" }).click();
  await settled(page.getByRole("img", { name: /QR code/ }));
  for (const width of WIDTHS) await capture(page, "sign-in-enrol", width);

  await page.getByLabel("The 6-digit code the app now shows").fill("123456");
  await page.getByRole("button", { name: "Confirm" }).click();
  await settled(page.getByRole("list", { name: "Recovery codes" }));
  for (const width of WIDTHS) await capture(page, "sign-in-recovery-codes", width);
});

test("setup: the first administrator's form and the authenticator offer", async ({ page }) => {
  await page.route("**/api/v1/auth/setup/inspect", (route) =>
    route.fulfill({
      json: {
        kind: "firstAdministrator",
        username: null,
        displayName: null,
        expiresAtUtc: new Date(Date.now() + 24 * 3600_000).toISOString(),
        requireAuthenticator: false,
      },
    }),
  );
  await page.route("**/api/v1/auth/setup/complete", (route) =>
    route.fulfill({ json: { status: "signedIn", me: LOCAL_ME } }),
  );

  await page.goto("/#/setup/screens-token");
  await expect(page).toHaveURL(/#\/setup$/);
  await settled(page.getByText(/first system administrator/));
  await page.getByLabel("Username").fill("backup-admin");
  await page.getByLabel("Display name").fill("Backup Admin");
  await page.getByLabel("Password", { exact: true }).fill("lantern orchard");
  for (const width of WIDTHS) await capture(page, "setup-first-admin", width);

  await page.getByLabel("Password", { exact: true }).fill("lantern orchard quietly ticking");
  await page.getByLabel("Type it again").fill("lantern orchard quietly ticking");
  await page.getByRole("button", { name: "Save password" }).click();
  await settled(page.getByRole("button", { name: "Set up an authenticator app (recommended)" }));
  for (const width of WIDTHS) await capture(page, "setup-offer", width);
});

test("People: every account state, and a new person's one-time link", async ({ page }) => {
  const day = (offsetHours: number) => new Date(Date.now() + offsetHours * 3600_000).toISOString();
  const account = (overrides: object) => ({
    authenticatorEnrolled: false,
    isSystemAdministrator: false,
    lockedOut: false,
    lastSignInAtUtc: null,
    setupLink: null,
    createdAtUtc: day(-72),
    status: "Active",
    ...overrides,
  });
  const accounts = [
    account({
      id: "screens-1", username: "backup-admin", displayName: "Backup Admin",
      isSystemAdministrator: true, authenticatorEnrolled: true, lastSignInAtUtc: day(-1),
      setupLink: { expiresAtUtc: day(-48), usedAtUtc: day(-70), usedFromAddress: "10.20.0.14" },
    }),
    account({
      id: "screens-2", username: "helper1", displayName: "Helper One", lastSignInAtUtc: day(-3),
      setupLink: { expiresAtUtc: day(-24), usedAtUtc: day(-40), usedFromAddress: "10.20.3.88" },
    }),
    account({
      id: "screens-3", username: "timing-desk", displayName: "Timing Desk", status: "AwaitingSetup",
      setupLink: { expiresAtUtc: day(20), usedAtUtc: null, usedFromAddress: null },
    }),
    account({
      id: "screens-4", username: "pool-marshal", displayName: "Pool Marshal", lockedOut: true,
      lastSignInAtUtc: day(-26),
    }),
    account({ id: "screens-5", username: "left-2025", displayName: "Former Helper", status: "Disabled" }),
  ];
  await page.route("**/api/v1/admin/local-accounts", (route) =>
    route.request().method() === "POST"
      ? route.fulfill({
          status: 201,
          json: {
            account: account({
              id: "screens-6", username: "jsmith", displayName: "Jo Smith", status: "AwaitingSetup",
            }),
            setupToken: "screens-setup-token-0123456789abcdef",
            expiresAtUtc: day(24),
          },
        })
      : route.fulfill({ json: accounts }),
  );

  // Signed in as screens-1, so its row is the "(you)" one with no actions.
  await localSession(page);
  await page.goto("/#/school/people");
  await settled(page.getByText("awaiting setup"));
  await expect(page.getByText(/\(you\)/)).toBeVisible();
  for (const width of WIDTHS) await capture(page, "school-people", width);

  // Answered here: the dev API would otherwise keep a real grant for jsmith.
  await page.route("**/api/v1/admin/role-assignments", (route) =>
    route.request().method() === "POST"
      ? route.fulfill({
          status: 201,
          json: {
            id: "01900000-0000-7000-8000-00000000a006", upn: "jsmith", displayName: null,
            role: "CarnivalAdministrator", bootstrap: false, boundToSubject: false,
            localAccount: true, reason: "Added on People", assignedBySubject: "local:screens-1",
            assignedAtUtc: day(0), revokedAtUtc: null, revokedBySubject: null,
            revocationReason: null,
          },
        })
      : route.fallback(),
  );
  await page.getByLabel("Username").fill("jsmith");
  await page.getByLabel("Display name").fill("Jo Smith");
  await page.getByLabel("Role").selectOption("CarnivalAdministrator");
  const form = page.locator("form").filter({
    has: page.getByRole("heading", { name: "Add a person" }),
  });
  for (const width of WIDTHS) await captureRegion(page, form, "school-people-role", width);
  await page.getByRole("button", { name: "Add person" }).click();
  await settled(page.getByRole("img", { name: /QR code of the setup link/ }));
  for (const width of WIDTHS) await capture(page, "school-people-link", width);
});

test("School settings' Sign-in section beside a provider, and a local role grant", async ({
  page,
}) => {
  await page.route("**/api/v1/school/settings", async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    const body = (await response.json()) as object;
    await route.fulfill({
      json: {
        ...body,
        requireAuthenticator: true,
        localAccounts: "SystemAdministrators",
        providerConfigured: true,
      },
    });
  });
  await page.route("**/api/v1/admin/role-assignments", async (route) => {
    if (route.request().method() !== "GET") return route.fallback();
    const response = await route.fetch();
    const rows = (await response.json()) as object[];
    await route.fulfill({
      json: [
        {
          id: "01900000-0000-7000-8000-00000000a001", upn: "helper1", displayName: "Helper One",
          role: "Recorder", bootstrap: false, boundToSubject: true, localAccount: true,
          reason: "Carnival-day helper with no school account.", assignedBySubject: "local:screens-1",
          assignedAtUtc: new Date().toISOString(), revokedAtUtc: null, revokedBySubject: null,
          revocationReason: null,
        },
        ...rows,
      ],
    });
  });

  await signIn(page);
  await page.goto("/#/school/settings");
  const section = page.locator("section[aria-labelledby=sign-in-heading]");
  await settled(section.getByLabel("Who may use a local account"));
  for (const width of WIDTHS) await captureRegion(page, section, "school-settings-sign-in", width);

  await page.goto("/#/school/roles");
  await settled(page.getByText("local account", { exact: true }));
  for (const width of WIDTHS) await capture(page, "school-roles-local", width);
});

test("My account, the School page's reminder and enrolling from My account", async ({ page }) => {
  await localSession(page);
  await page.route("**/api/v1/account", (route) =>
    route.fulfill({
      json: {
        username: "backup-admin",
        displayName: "Backup Admin",
        authenticatorEnrolled: false,
        recoveryCodesLeft: 0,
        requireAuthenticator: false,
      },
    }),
  );
  await page.route("**/api/v1/account/authenticator", (route) => route.fulfill({ json: ENROLMENT }));

  await page.goto("/#/school");
  await settled(page.getByText(/has no authenticator app/));
  for (const width of WIDTHS) await capture(page, "school-hub-reminder", width);

  await page.goto("/#/account");
  await settled(page.getByRole("heading", { name: "Change password" }));
  for (const width of WIDTHS) await capture(page, "my-account", width);

  await page.getByLabel("Your password, to confirm it's you").fill("lantern orchard quietly ticking");
  await page.getByRole("button", { name: "Set up an authenticator app" }).click();
  await settled(page.getByRole("img", { name: /QR code/ }));
  for (const width of WIDTHS) await capture(page, "my-account-enrol", width);
});
