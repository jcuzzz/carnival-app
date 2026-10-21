// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, screen, waitFor } from "@testing-library/react";
import { renderWithQuery } from "./test/queryWrapper";
import { signIn, signOut } from "./auth/session";
import { _resetBrandingForTests, applyBranding } from "./branding/branding";
import { db } from "./db/db";
import { _resetSelectedCarnivalForTests } from "./carnivals/selectedCarnival";

/**
 * ADR-0031 (B4): `/outbox` is the one screen that prints other officials'
 * captured student names, so it is gated on a session at the route — not by
 * hiding controls inside it.
 */

vi.mock("./api/resources", () => ({
  listCarnivals: vi.fn().mockResolvedValue([]),
  getCarnival: vi.fn().mockResolvedValue(null),
  getRecordsForAdministration: vi.fn().mockResolvedValue([]),
}));

vi.mock("./api/syncSetup", () => ({
  syncWorker: { start: vi.fn(), stop: vi.fn(), syncNow: vi.fn() },
  resumeAfterSignIn: vi.fn(),
}));

import App from "./App";

beforeEach(async () => {
  cleanup();
  // App scrolls to the top on every navigation (U7); jsdom has no scrollTo.
  vi.spyOn(window, "scrollTo").mockImplementation(() => {});
  await db.outbox.clear();
  await db.syncMeta.clear();
  signOut();
  _resetSelectedCarnivalForTests();
  location.hash = "#/outbox";
});

afterEach(() => {
  cleanup();
  signOut();
});

describe("the /outbox route", () => {
  it("asks a signed-out device to sign in instead of listing the queue", async () => {
    renderWithQuery(<App />);

    expect(await screen.findByText(/Sign in to read this device's queue/)).toBeTruthy();
    expect(screen.queryByRole("table")).toBeNull();
  });

  it("renders the queue for a named official", async () => {
    signIn({ subject: "official-1", displayName: "Ali Recorder", roles: ["Recorder"] });
    renderWithQuery(<App />);

    const empty = "Nothing waiting — every capture on this device has reached the server.";
    await waitFor(() => expect(screen.getByText(empty)).toBeTruthy());
  });

  it("still shows the aggregate chip signed out — counts are not names", async () => {
    // An unattended queue must still be able to shout (ADR-0029); what is
    // withheld is the per-item detail, not the fact that work is waiting.
    renderWithQuery(<App />);

    await waitFor(() => expect(screen.getByLabelText(/^Outbox:/)).toBeTruthy());
  });
});

describe("the document title", () => {
  afterEach(() => {
    _resetBrandingForTests();
  });

  it("names the school once branding lands, and the product until then", async () => {
    renderWithQuery(<App />);

    await waitFor(() => expect(document.title).toBe("Outbox — Carnival App"));

    act(() => {
      applyBranding({ schoolName: "Riverbend Secondary College", brandColour: null });
    });
    await waitFor(() => expect(document.title).toBe("Outbox — Riverbend Secondary College"));
  });
});

describe("the /school/records route", () => {
  beforeEach(() => {
    location.hash = "#/school/records";
  });

  it("offers a sign-in to a signed-out reader rather than naming a role", async () => {
    // "Needs the carnival administrator role" is unanswerable advice to somebody
    // whose session simply lapsed, which is the ordinary way to arrive here.
    renderWithQuery(<App />);

    expect(await screen.findByText(/Sign in to administer the school record book/)).toBeTruthy();
    expect(screen.queryByText(/needs the carnival administrator role/)).toBeNull();
  });

  it("keeps the clean refusal for a signed-in official with the wrong role", async () => {
    signIn({ subject: "official-1", displayName: "Ali Recorder", roles: ["Recorder"] });
    renderWithQuery(<App />);

    expect(await screen.findByText(/needs the carnival administrator role/)).toBeTruthy();
  });
});

describe("the School hub (ADR-0055 §1)", () => {
  beforeEach(() => {
    location.hash = "#/school";
  });

  it("lists only the sections the signed-in roles can use", async () => {
    signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    renderWithQuery(<App />);

    await screen.findByRole("heading", { name: "School", level: 1 });
    expect(screen.getByRole("link", { name: /Event catalogue/ })).toBeTruthy();
    expect(screen.getByRole("link", { name: /School records/ })).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Roster import/ })).toBeNull();
    expect(screen.queryByRole("link", { name: /School settings/ })).toBeNull();
    expect(screen.queryByRole("link", { name: /^Roles/ })).toBeNull();
    expect(screen.queryByRole("link", { name: /Event templates/ })).toBeNull();
  });

  it("gives a system administrator every row, in the ADR's order", async () => {
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    renderWithQuery(<App />);

    await screen.findByRole("heading", { name: "School", level: 1 });
    const rows = [...document.querySelectorAll(".row-list .row-link .row-title")];
    expect(rows.map((row) => row.textContent)).toEqual([
      "School settings",
      "Roles",
      "Roster import",
      "Event templates",
      "Event catalogue",
      "School records",
    ]);
    expect(
      [...document.querySelectorAll<HTMLAnchorElement>(".row-list .row-link")].map((a) => a.hash),
    ).toEqual([
      "#/school/settings",
      "#/school/roles",
      "#/import",
      "#/school/templates",
      "#/school/events",
      "#/school/records",
    ]);
  });

  // Signed out is not "your roles don't reach this": the one thing that helps
  // somebody whose session lapsed is the way back in, as /outbox already does.
  it("offers a sign-in to a signed-out reader rather than naming roles", async () => {
    renderWithQuery(<App />);

    expect(await screen.findByText(/Sign in to reach the school-wide screens/)).toBeTruthy();
    expect(screen.queryByText(/Nothing here is available to your roles/)).toBeNull();
    expect(document.querySelector(".row-list")).toBeNull();
  });

  it("says so rather than showing an empty list when no row is usable", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<App />);

    expect(await screen.findByText(/Nothing here is available to your roles/)).toBeTruthy();
    expect(document.querySelector(".row-list")).toBeNull();
  });
});

describe("the moved addresses (ADR-0055)", () => {
  it("forwards /records-admin to /school/records, replacing the entry", async () => {
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    location.hash = "#/records-admin";
    renderWithQuery(<App />);

    await waitFor(() => expect(location.hash).toBe("#/school/records"));
  });

  it("keeps /school as the hub, not the settings form it used to be", async () => {
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    location.hash = "#/school";
    renderWithQuery(<App />);

    await screen.findByRole("heading", { name: "School", level: 1 });
    expect(screen.queryByLabelText("School name")).toBeNull();
  });
});

/** ADR-0061 §5: in the public demo build, a visitor without a sandbox sees only the landing page. */
describe("the demo build", () => {
  beforeEach(() => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_DEMO_MODE", "true");
  });

  afterEach(() => vi.unstubAllEnvs());

  it("shows the landing page on any route until a sandbox exists", async () => {
    location.hash = "#/school/settings";
    renderWithQuery(<App />);

    expect(await screen.findByRole("button", { name: "Start demo" })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "School settings" })).toBeNull();
  });

  it("routes a visitor with a sandbox as usual", async () => {
    signIn({ subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] });
    location.hash = "#/outbox";
    renderWithQuery(<App />);

    const empty = "Nothing waiting — every capture on this device has reached the server.";
    await waitFor(() => expect(screen.getByText(empty)).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Start demo" })).toBeNull();
  });

  it("has no Switch screen to pick another identity on", async () => {
    signIn({ subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] });
    location.hash = "#/login";
    renderWithQuery(<App />);

    await waitFor(() => expect(location.hash).toBe("#/"));
    expect(screen.queryByText("Alex Admin")).toBeNull();
  });
});
