import { expect, test } from "@playwright/test";
import { signIn } from "./app";
import { reportDeviceQueue, seedCarnival, type SeededCarnival } from "./dev-api";

/**
 * The organiser's fleet view (ADR-0034), end to end: two devices report through
 * the real endpoint, and the desk sees the difference between the one that is
 * still talking and the one that went quiet holding six captures.
 *
 * The shape ADR-0029 could not detect — an iPad in a bag whose holder went to
 * lunch — only exists on this screen, so it is worth a round trip.
 */

let seed: SeededCarnival;

test.beforeAll(async ({ playwright }) => {
  const api = await playwright.request.newContext();
  seed = await seedCarnival(api, "devices");
  // `lastSeenUtc` is stamped SERVER-side, so the stranded state cannot be seeded
  // by backdating a timestamp. `lastDrainUtc` CAN be: it is derived from the
  // reported count falling, so a device that reports once and never again has
  // never been seen to drain — which is the not-draining state, end to end.
  await reportDeviceQueue(api, seed.carnivalId, "e2e-marshal-ipad", { pending: 6, parked: 2 });
  await reportDeviceQueue(api, seed.carnivalId, "e2e-desk-laptop", { pending: 3, parked: 0 });
  await reportDeviceQueue(api, seed.carnivalId, "e2e-desk-laptop", { pending: 0, parked: 0 });
  await api.dispose();
});

test("the organiser sees every device that has reported, and what it is holding", async ({
  page,
}) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/manage`);
  await expect(page.getByRole("heading", { name: "Manage carnival", level: 1 })).toBeVisible();

  const devices = page.getByRole("region", { name: "Devices" });
  await expect(page.getByRole("heading", { name: "Devices", level: 2 })).toBeVisible();
  await expect(devices.getByText(/6 waiting to sync/)).toBeVisible();
  await expect(devices.getByText(/2 refused, needing a decision/)).toBeVisible();
  // The quiet device is listed too — a fleet view that only shows trouble
  // cannot tell "nothing wrong" from "nothing reporting".
  await expect(devices.getByText("nothing waiting")).toBeVisible();
});

test("a device that reports and never drains is called out, not shown green", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/manage`);

  const devices = page.getByRole("region", { name: "Devices" });
  await expect(devices.getByText("not draining", { exact: true })).toBeVisible();
  await expect(devices.getByText(/reporting but not draining/)).toBeVisible();
  // …and the device that WAS seen to deliver keeps its plain row.
  await expect(devices.getByText("reporting", { exact: true })).toBeVisible();
});

test("the undrained device is named beside Finalise, and never blocks it", async ({ page }) => {
  await signIn(page);
  await page.goto(`/#/carnivals/${seed.carnivalId}/manage`);
  await page.getByRole("button", { name: "Close carnival" }).click();

  const zone = page.locator(".danger-zone");
  await expect(zone.getByRole("button", { name: "Finalise carnival" })).toBeVisible();
  await expect(zone.getByText(/still reporting but nothing is leaving/)).toBeVisible();
});

test("a signed-in role cannot report a queue on another device's behalf", async ({
  playwright,
}) => {
  // Reporting is carnivals:view, so without the X-Device-Id check any role could
  // zero a device's row and erase the warning above (ADR-0034 §2).
  const api = await playwright.request.newContext();
  const response = await api.put(
    `http://localhost:5080/api/v1/carnivals/${seed.carnivalId}` +
      "/devices/e2e-marshal-ipad/sync-status",
    {
      headers: {
        "X-Dev-Subject": "e2e-forger",
        "X-Dev-Roles": "Viewer",
        "X-Device-Id": "e2e-some-other-device",
      },
      data: { pending: 0, parked: 0, oldestPendingCapturedAt: null, appVersion: "e2e" },
    },
  );
  expect(response.status()).toBe(403);
  expect((await response.json()).error).toBe("DEVICE_ID_MISMATCH");
  await api.dispose();
});
