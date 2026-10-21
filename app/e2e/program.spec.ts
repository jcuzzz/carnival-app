import { expect, test } from "@playwright/test";
import { signIn } from "./app";
import { seedProgramCarnival, type SeededProgramCarnival } from "./dev-api";

/**
 * The organiser path (ADR-0014 §2): build the program in the app — stream, time
 * slot, an event placed into it — and see the same thing on the printable
 * program. This is the journey that makes the app the source of truth for the
 * program instead of a Word document maintained beside it, so it is worth a
 * round trip through the real API.
 *
 * Serial: the print view asserts what the builder journey put there.
 */
test.describe.configure({ mode: "serial" });

let seed: SeededProgramCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedProgramCarnival(api, "program");
  await api.dispose();
});

test("an organiser builds a stream, a slot and a placement", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}`);

  // The builder is an organiser surface now, reached through Manage rather than
  // from a button row on the deck (ADR-0024 §4/§7).
  await page.getByRole("link", { name: "Manage" }).click();
  await expect(page.getByRole("heading", { name: "Manage carnival", level: 1 })).toBeVisible();
  await page.getByRole("link", { name: /^Program/ }).click();
  await expect(page.getByRole("heading", { name: "Program", level: 1 })).toBeVisible();
  // A fresh carnival has no program, and every event is unscheduled — which is
  // an ordinary state, not a fault: all of them are already enterable.
  await expect(page.getByRole("heading", { name: "Not on the program (2)" })).toBeVisible();

  await page.getByLabel("New stream").fill("Pool");
  await page.getByRole("button", { name: "Add stream" }).click();
  const pool = page.getByRole("region", { name: "Stream Pool" });
  await expect(pool).toBeVisible();

  await page.getByRole("button", { name: "Add a slot to Pool…" }).click();
  await page.getByLabel("Printed label").fill("FREESTYLE EVENTS");
  await page.getByLabel("Start").fill("09:45");
  await page.getByRole("button", { name: "Add slot" }).click();
  await expect(pool.getByText("FREESTYLE EVENTS")).toBeVisible();

  // The picker offers exactly the unscheduled events; placing one takes it off
  // the worklist and puts it on the program.
  await page
    .getByLabel("Add an event to this block")
    .selectOption({ label: `${seed.eventName} — ${seed.divisionLabel}` });
  await page.getByRole("button", { name: "Add", exact: true }).click();
  await expect(page.getByRole("heading", { name: "Not on the program (1)" })).toBeVisible();
  await expect(pool.getByText("1 event", { exact: true })).toBeVisible();

  // Re-ordering is up/down controls — drag-and-drop is an ADR-0014 non-goal —
  // and the single slot sits at both ends of its stream, so neither is offered.
  await expect(page.getByLabel("Move FREESTYLE EVENTS earlier")).toBeDisabled();
  await expect(page.getByLabel("Move FREESTYLE EVENTS later")).toBeDisabled();
});

test("the printable program shows the block, its time and its divisions", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/program`);

  // Streams are columns; swimming's one stream is one column of timed blocks.
  const column = page.getByRole("region", { name: "Pool" });
  await expect(column).toBeVisible();
  await expect(column.getByText("09:45")).toBeVisible();
  await expect(column.getByText("FREESTYLE EVENTS")).toBeVisible();
  await expect(column.getByText(seed.divisionLabel)).toBeVisible();

  // What a program needs at the top, and the honest caveat at the foot.
  await expect(page.getByRole("heading", { name: seed.carnivalName, level: 1 })).toBeVisible();
  await expect(page.getByText("E2E Aquatic Centre")).toBeVisible();
  await expect(page.getByText(/Times are indicative/)).toBeVisible();

  // The unplaced division is not on the program — and is still a normal event.
  await expect(column.getByText(seed.siblingDivisionLabel)).toHaveCount(0);

  // The seeded carnival has never been opened, and a half-built program that
  // reaches paper carries no status of its own — so it says what it is.
  await expect(page.getByText(/^DRAFT —/)).toBeVisible();

  // What actually comes out of the printer. The print stylesheet is otherwise
  // untestable and silently breakable: screen chrome must not print, and the
  // program must survive losing it.
  await page.emulateMedia({ media: "print" });
  await expect(page.locator(".shell-header")).toBeHidden();
  await expect(page.locator(".shell-nav")).toBeHidden();
  await expect(page.getByRole("button", { name: "Print" })).toBeHidden();
  await expect(column.getByText("FREESTYLE EVENTS")).toBeVisible();
  // The draft mark is the one piece of context that must survive the print
  // stylesheet stripping everything the page shows around the program.
  await expect(page.getByText(/^DRAFT —/)).toBeVisible();

  // One stream: nothing asks for landscape, which would waste half of it.
  await expect(page.locator("style", { hasText: "size: landscape" })).toHaveCount(0);

  // A column taller than the page (athletics' Track runs ~18 slots) continues
  // overleaf, and an unlabelled column of times is useless to staff. The stream
  // name is a table header group for print precisely because that is what a
  // browser repeats on every page its table spans.
  await expect(page.locator(".program-stream-name").first()).toHaveCSS(
    "display",
    "table-header-group",
  );
});
