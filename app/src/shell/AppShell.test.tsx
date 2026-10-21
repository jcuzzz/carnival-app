// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import AppShell from "./AppShell";
import { reportReachable, reportUnreachable, _resetReachabilityForTests } from "../api/reachability";
import { PRODUCT_SHORT_NAME, _resetBrandingForTests, applyBranding } from "../branding/branding";
import { db } from "../db/db";
import { enqueue, pauseQueue } from "../sync/outbox";
import { STORAGE_PERSIST_KEY } from "../sync/storagePersistence";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import { signIn, signOut } from "../auth/session";
import {
  _resetSelectedCarnivalForTests,
  adoptCarnival,
  selectCarnival,
} from "../carnivals/selectedCarnival";
import type { Carnival } from "../api/types";

beforeEach(async () => {
  cleanup();
  _resetUuidv7StateForTests();
  _resetReachabilityForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
  signOut();
  _resetSelectedCarnivalForTests();
  _resetBrandingForTests();
});

afterEach(() => {
  cleanup();
});

describe("AppShell", () => {
  it("names the school and its monogram once branding lands (ADR-0040 §3)", () => {
    render(<AppShell>content</AppShell>);
    // Nothing configured yet: the product name stands alone, with no mark.
    expect(screen.getByText("Carnival App")).toBeTruthy();
    expect(screen.queryByText("RS")).toBeNull();

    act(() => {
      applyBranding({ schoolName: "Riverbend Secondary College", brandColour: "#123456" });
    });
    expect(screen.getByText("Riverbend Secondary College")).toBeTruthy();
    expect(screen.getByText("RS")).toBeTruthy();
    expect(screen.getByText(PRODUCT_SHORT_NAME)).toBeTruthy();
  });

  it("shows the signed-in official's name persistently (ADR-0006)", () => {
    signIn({ subject: "s1", displayName: "Olivia Official", roles: ["Recorder"] });
    render(<AppShell>content</AppShell>);
    expect(screen.getByText("Olivia Official")).toBeTruthy();
    expect(screen.getByText(/\((Recorder)\)/)).toBeTruthy();
  });

  it("offers sign-in when no official is active", () => {
    render(<AppShell>content</AppShell>);
    expect(screen.getByText("Sign in")).toBeTruthy();
  });

  it("reflects live outbox counts in the aggregate indicator", async () => {
    render(<AppShell>content</AppShell>);
    await waitFor(() => expect(screen.getByLabelText(/Outbox: all synced/)).toBeTruthy());

    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await waitFor(() => expect(screen.getByLabelText("Outbox: 2 pending")).toBeTruthy());
  });

  it("hides the demo banner in normal builds, shows it when VITE_DEMO_MODE=true", () => {
    render(<AppShell>content</AppShell>);
    expect(screen.queryByText(/PUBLIC DEMO/)).toBeNull();

    cleanup();
    vi.stubEnv("VITE_DEMO_MODE", "true");
    try {
      render(<AppShell>content</AppShell>);
      expect(
        screen.getByText("PUBLIC DEMO — synthetic data only, your copy is temporary"),
      ).toBeTruthy();
    } finally {
      vi.unstubAllEnvs();
    }
  });

  it("offers no Switch or Sign in in a demo build: one identity per visitor (ADR-0061 §5)", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_DEMO_MODE", "true");
    try {
      signIn({ subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] });
      render(<AppShell>content</AppShell>);
      expect(screen.getByLabelText("Signed in as Demo visitor")).toBeTruthy();
      expect(screen.queryByRole("link", { name: "Switch" })).toBeNull();
      expect(screen.queryByRole("link", { name: "Sign in" })).toBeNull();
      expect(screen.queryByRole("link", { name: "Sign out" })).toBeNull();
    } finally {
      vi.unstubAllEnvs();
    }
  });

  it("warns in the outbox chip when durable storage was refused", async () => {
    await db.syncMeta.put({
      key: STORAGE_PERSIST_KEY,
      value: { persisted: false, unsupported: false, requestedAt: "2026-07-31T00:00:00.000Z" },
    });
    render(<AppShell>content</AppShell>);
    await waitFor(() => expect(screen.getByLabelText(/storage not durable/)).toBeTruthy());

    cleanup();
    await db.syncMeta.put({
      key: STORAGE_PERSIST_KEY,
      value: { persisted: true, unsupported: false, requestedAt: "2026-07-31T00:00:00.000Z" },
    });
    render(<AppShell>content</AppShell>);
    await waitFor(() => expect(screen.getByLabelText(/Outbox: all synced/)).toBeTruthy());
    expect(screen.queryByLabelText(/storage not durable/)).toBeNull();
  });

  it("says nothing about connectivity while the server is reachable", () => {
    render(<AppShell>content</AppShell>);
    expect(screen.queryByText(/saved on this device/)).toBeNull();
  });

  it("shows the offline strip when the platform reports no network (U5)", async () => {
    render(<AppShell>content</AppShell>);
    fireEvent(window, new Event("offline"));
    await waitFor(() => expect(screen.getByText(/^Offline —/)).toBeTruthy());

    fireEvent(window, new Event("online"));
    await waitFor(() => expect(screen.queryByText(/^Offline —/)).toBeNull());
  });

  it("shows the unreachable strip when requests fail on a live network (the pool case)", async () => {
    render(<AppShell>content</AppShell>);
    // navigator.onLine stays true — the AP is up, the uplink is dead (C8/U5).
    act(() => reportUnreachable());
    await waitFor(() => expect(screen.getByText(/Can't reach the server/)).toBeTruthy());

    act(() => reportReachable());
    await waitFor(() => expect(screen.queryByText(/Can't reach the server/)).toBeNull());
  });

  it("surfaces a paused queue (auth expiry) in the indicator", async () => {
    render(<AppShell>content</AppShell>);
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await pauseQueue("Authentication expired");
    await waitFor(() => expect(screen.getByLabelText(/Outbox: paused · 1 waiting/)).toBeTruthy());
  });

  it("lets the offline strip name the backlog it is holding (ADR-0029 §3)", async () => {
    render(<AppShell>content</AppShell>);
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    fireEvent(window, new Event("offline"));
    await waitFor(() => expect(screen.getByText(/^Offline — 2 captures are waiting/)).toBeTruthy());
    // Still the reassuring strip, not an alarm: offline is a supported state.
    expect(document.querySelector(".stuck-banner")).toBeNull();
  });

  it("puts the stuck banner above the connectivity strip, worse thing first", async () => {
    render(<AppShell>content</AppShell>);
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await pauseQueue("Authentication expired");
    fireEvent(window, new Event("offline"));

    const banner = await waitFor(() => screen.getByRole("alert"));
    expect(banner.className).toContain("stuck-banner");
    const strip = document.querySelector(".connectivity-strip")!;
    expect(banner.compareDocumentPosition(strip) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("keeps the banner in the sticky block with the header, not below the fold", async () => {
    // A scrolled deck kept the outbox CHIP on screen and let the banner go —
    // the inverse of "prominence that survives being ignored" (ADR-0029 §4).
    render(<AppShell>content</AppShell>);
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await pauseQueue("Authentication expired");

    const banner = await waitFor(() => screen.getByRole("alert"));
    const top = document.querySelector(".shell-top")!;
    expect(top.contains(banner)).toBe(true);
    expect(top.contains(document.querySelector(".shell-header"))).toBe(true);
    expect(top.contains(screen.getByRole("navigation", { name: "Primary" }))).toBe(true);
    // The banner is the LAST thing in it: the strip and the screen scroll past.
    expect(top.lastElementChild).toBe(banner);
  });

  it("drops the strip's automatic-recovery promise under a paused banner", async () => {
    // The strip said the queue would sync on its own while the banner above it
    // said nothing would move until someone signed in. Both cannot be true.
    render(<AppShell>content</AppShell>);
    await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
    await pauseQueue("Authentication expired");
    fireEvent(window, new Event("offline"));

    await waitFor(() =>
      expect(document.querySelector(".connectivity-strip")?.textContent).toBe(
        "Offline — 1 capture saved on this device.",
      ),
    );
    expect(screen.getByRole("alert").textContent).toContain("until you sign in");
  });
});

function carnival(over: Partial<Carnival> = {}): Carnival {
  return {
    id: "c-1",
    name: "Preston Swimming",
    date: "2027-03-06",
    status: "Open",
    type: "Swimming",
    venue: "Preston Pool",
    ownerSubject: "s1",
    ruleVersion: "2027.1",
    version: 1,
    ...over,
  } as Carnival;
}

describe("the carnival context in the chrome (ADR-0024)", () => {
  it("names the selected carnival persistently, and offers the switch", () => {
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);
    const chip = screen.getByLabelText(/Carnival: Preston Swimming/);
    expect(chip.getAttribute("href")).toBe("#/carnivals");
    expect(screen.getByText("2027-03-06")).toBeTruthy();
  });

  it("renders a chip for a deep-linked carnival before its payload lands", () => {
    adoptCarnival("c-9");
    render(<AppShell>content</AppShell>);
    expect(screen.getByLabelText(/Carnival: selected/)).toBeTruthy();
    expect(screen.getByText("Carnival")).toBeTruthy();
  });

  it("shows the three carnival tabs to an administrator, School after them", () => {
    signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);
    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect(nav.querySelectorAll("a").length).toBe(4);
    expect(screen.getByRole("link", { name: "Deck" }).getAttribute("href")).toBe("#/carnivals/c-1");
    expect(screen.getByRole("link", { name: "Dashboard" }).getAttribute("href")).toBe(
      "#/carnivals/c-1/dashboard",
    );
    expect(screen.getByRole("link", { name: "Manage" }).getAttribute("href")).toBe(
      "#/carnivals/c-1/manage",
    );
  });

  it("hides the Manage TAB from an official who cannot manage (ADR-0024 §7)", () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);
    expect(screen.queryByRole("link", { name: "Manage" })).toBeNull();
    expect(screen.getByRole("link", { name: "Deck" })).toBeTruthy();
  });

  it("falls back to the school-level row when no carnival is selected", () => {
    render(<AppShell>content</AppShell>);
    expect(screen.getByRole("link", { name: "Carnivals" })).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Deck" })).toBeNull();
  });

  it("drops the Roster import nav entry — the School hub owns it (ADR-0055 §1)", () => {
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    render(<AppShell>content</AppShell>);
    expect(screen.queryByRole("link", { name: "Roster import" })).toBeNull();
    expect(screen.getByRole("link", { name: "School" })).toBeTruthy();
  });

  it("marks aria-current on the exact PAGE, not the active section", async () => {
    // The Implementation-notes amendment: `active` spans a section (capture
    // belongs to the deck), aria-current names the page being shown. Two
    // aria-current elements on a capture screen is what this prevents.
    signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    selectCarnival(carnival());
    location.hash = "#/carnivals/c-1/events/ev-1";
    render(<AppShell>content</AppShell>);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect(nav.querySelectorAll("[aria-current='page']").length).toBe(0);
    expect(screen.getByRole("link", { name: "Deck" }).className).toContain("active");
    await waitFor(() => expect(screen.getByLabelText(/^Outbox/)).toBeTruthy());
  });

  it("marks the deck as the current page when the deck IS the page", async () => {
    selectCarnival(carnival());
    location.hash = "#/carnivals/c-1";
    render(<AppShell>content</AppShell>);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect(nav.querySelectorAll("[aria-current='page']").length).toBe(1);
    expect(screen.getByRole("link", { name: "Deck" }).getAttribute("aria-current")).toBe("page");
    await waitFor(() => expect(screen.getByLabelText(/^Outbox/)).toBeTruthy());
  });

  it("marks Carnivals current on the app's own home, which renders the switcher", async () => {
    location.hash = "#/";
    render(<AppShell>content</AppShell>);

    expect(screen.getByRole("link", { name: "Carnivals" }).getAttribute("aria-current")).toBe(
      "page",
    );
    await waitFor(() => expect(screen.getByLabelText(/^Outbox/)).toBeTruthy());
  });

  // ADR-0055 §1 widened ADR-0045 §6's nav rule: the tab is shown to whoever can
  // use at least one hub row, and a CarnivalAdministrator can use two.
  it("offers School to either administrator, and to nobody else", () => {
    location.hash = "#/carnivals";
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    render(<AppShell>content</AppShell>);
    expect(screen.queryByRole("link", { name: "School" })).toBeNull();

    cleanup();
    signIn({ subject: "s3", displayName: "Cass Carnival", roles: ["CarnivalAdministrator"] });
    render(<AppShell>content</AppShell>);
    expect(screen.getByRole("link", { name: "School" }).getAttribute("href")).toBe("#/school");

    cleanup();
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    render(<AppShell>content</AppShell>);
    expect(screen.getByRole("link", { name: "School" }).getAttribute("href")).toBe("#/school");
  });

  it("keeps the outbox reachable without the Manage role — it rides the header chip", () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);
    expect(screen.getByLabelText(/^Outbox/).getAttribute("href")).toBe("#/outbox");
  });

  it("keeps School in the row once a carnival is selected (ADR-0045 §6)", () => {
    location.hash = "#/carnivals/c-1";
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect([...nav.querySelectorAll("a")].map((a) => a.textContent)).toEqual([
      "Deck",
      "Dashboard",
      "Manage",
      "School",
    ]);
    expect(screen.getByRole("link", { name: "School" }).getAttribute("href")).toBe("#/school");
  });

  it("keeps School in the carnival row for an administrator who is not a sysadmin", () => {
    location.hash = "#/carnivals/c-1";
    signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);
    expect(screen.getByRole("link", { name: "School" })).toBeTruthy();
    expect(screen.getByRole("link", { name: "Manage" })).toBeTruthy();

    cleanup();
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    render(<AppShell>content</AppShell>);
    expect(screen.queryByRole("link", { name: "School" })).toBeNull();
  });

  it("keeps School active across its sub-pages, current only on the hub (ADR-0055 §1)", async () => {
    location.hash = "#/school/events/new";
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    render(<AppShell>content</AppShell>);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect(nav.querySelector("a.active")?.textContent).toBe("School");
    expect(nav.querySelectorAll("[aria-current='page']").length).toBe(0);
    await waitFor(() => expect(screen.getByLabelText(/^Outbox/)).toBeTruthy());
  });

  // Roster import is a hub row, so the tab has to stay lit on its address too.
  it("keeps School active on /import, which the hub owns (ADR-0055 §1)", async () => {
    location.hash = "#/import";
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    render(<AppShell>content</AppShell>);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    expect(nav.querySelector("a.active")?.textContent).toBe("School");
    expect(nav.querySelectorAll("[aria-current='page']").length).toBe(0);
    await waitFor(() => expect(screen.getByLabelText(/^Outbox/)).toBeTruthy());
  });

  it("marks School as the current page on /school, with no carnival tab current", async () => {
    location.hash = "#/school";
    signIn({ subject: "s4", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] });
    selectCarnival(carnival());
    render(<AppShell>content</AppShell>);

    const nav = screen.getByRole("navigation", { name: "Primary" });
    const current = nav.querySelectorAll("[aria-current='page']");
    expect(current.length).toBe(1);
    expect(current[0].textContent).toBe("School");
    expect(nav.querySelector("a.active")?.textContent).toBe("School");
    await waitFor(() => expect(screen.getByLabelText(/^Outbox/)).toBeTruthy());
  });
});
