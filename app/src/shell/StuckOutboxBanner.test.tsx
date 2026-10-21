// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import StuckOutboxBanner, { useOutboxHealth } from "./StuckOutboxBanner";
import { _resetReachabilityForTests, reportUnreachable } from "../api/reachability";
import { db, type OutboxStatus } from "../db/db";
import { enqueue, pauseQueue, resumeQueue, transition } from "../sync/outbox";
import { STUCK_AFTER_MS } from "../sync/stuckOutbox";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";

/** Flipped by the one test that needs the real-auth (Entra) sign-in target. */
let entraOn = false;
vi.mock("../auth/entra", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../auth/entra")>()),
  entraAuthEnabled: () => entraOn,
}));

/**
 * The wiring, as AppShell renders it (ADR-0029): observables in, one banner
 * out, keyed on the cause so a change replaces the live region.
 */
function Harness() {
  const health = useOutboxHealth();
  return health.cause ? <StuckOutboxBanner key={health.cause} health={health} /> : null;
}

beforeEach(async () => {
  cleanup();
  _resetUuidv7StateForTests();
  _resetReachabilityForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  entraOn = false;
  location.hash = "";
});

/** A capture backdated by `agoMs`, in whatever status the case needs. */
async function capture(agoMs: number, status: OutboxStatus = "queued"): Promise<string> {
  const item = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "s1" });
  await db.outbox.put({
    ...item,
    status,
    capturedAt: new Date(Date.now() - agoMs).toISOString(),
  });
  return item.id;
}

/** Let the Dexie liveQuery land before asserting an ABSENCE. */
async function settle(): Promise<void> {
  await act(async () => {
    await new Promise((resolve) => setTimeout(resolve, 20));
  });
}

describe("the stuck-outbox banner", () => {
  it("renders nothing at all for a healthy queue", async () => {
    await capture(30_000);
    render(<Harness />);
    await settle();
    expect(screen.queryByRole("status")).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("is assertive for a paused queue and sends the official to sign in", async () => {
    await capture(5_000);
    await pauseQueue("Authentication expired");
    render(<Harness />);

    const banner = await waitFor(() => screen.getByRole("alert"));
    expect(banner.textContent).toContain("Sync is paused");
    expect(banner.textContent).toContain("1 capture is waiting");
    expect(banner.getAttribute("aria-live")).toBe("assertive");
    expect(screen.getByRole("link", { name: "Sign in to resume" }).getAttribute("href")).toBe(
      "#/login",
    );
  });

  it("brings the marshal back to the row they were on after the IdP round trip", async () => {
    // A full-page sign-in with no returnTo lands on the carnival list, three
    // taps from the deck the swimmers are still standing on.
    entraOn = true;
    location.hash = "#/carnivals/c-1/events/ev-1";
    await capture(5_000);
    await pauseQueue("Authentication expired");
    render(<Harness />);

    const link = await waitFor(() => screen.getByRole("link", { name: "Sign in to resume" }));
    expect(link.getAttribute("href")).toBe(
      "/api/v1/auth/login?returnUrl=%2F%23%2Fcarnivals%2Fc-1%2Fevents%2Fev-1",
    );
  });

  it("drops the paused banner as soon as the queue resumes", async () => {
    await capture(5_000);
    await pauseQueue("Authentication expired");
    render(<Harness />);
    await waitFor(() => expect(screen.getByRole("alert")).toBeTruthy());

    await resumeQueue();
    await waitFor(() => expect(screen.queryByRole("alert")).toBeNull());
  });

  it("is polite for a stall and sends the official to the Outbox", async () => {
    await capture(STUCK_AFTER_MS + 7 * 60_000);
    render(<Harness />);

    const banner = await waitFor(() => screen.getByRole("status"));
    expect(banner.textContent).toContain("Nothing has synced for 12 minutes");
    expect(banner.getAttribute("aria-live")).toBe("polite");
    expect(screen.getByRole("link", { name: "Open the Outbox" }).getAttribute("href")).toBe(
      "#/outbox",
    );
  });

  it("clears the stall when the head item finally drains", async () => {
    const id = await capture(STUCK_AFTER_MS * 2);
    render(<Harness />);
    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());

    await transition(id, "syncing");
    await transition(id, "accepted", { syncedAt: new Date().toISOString() });
    await waitFor(() => expect(screen.queryByRole("status")).toBeNull());
  });

  it("stays silent offline, however old the queue is (§3)", async () => {
    await capture(3 * 60 * 60 * 1000);
    render(<Harness />);
    fireEvent(window, new Event("offline"));
    await settle();
    expect(screen.queryByRole("status")).toBeNull();

    // Back on the network, the same queue is a fault.
    fireEvent(window, new Event("online"));
    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
  });

  it("stays silent when the server is unreachable on a live network (the pool case)", async () => {
    await capture(3 * 60 * 60 * 1000);
    render(<Harness />);
    act(() => reportUnreachable());
    await settle();
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("names parked work in its own tone once nobody has come back for it", async () => {
    await capture(STUCK_AFTER_MS + 1_000, "conflict");
    await capture(STUCK_AFTER_MS + 1_000, "rejected");
    render(<Harness />);

    const banner = await waitFor(() => screen.getByRole("status"));
    expect(banner.className).toContain("parked");
    expect(banner.textContent).toContain("2 captures were refused");
    expect(screen.getByRole("link", { name: "Open the Outbox" })).toBeTruthy();
  });

  it("shows one banner only — the worst cause wins", async () => {
    await capture(STUCK_AFTER_MS * 2);
    await capture(STUCK_AFTER_MS * 2, "conflict");
    await pauseQueue("Authentication expired");
    render(<Harness />);

    await waitFor(() => expect(screen.getByRole("alert")).toBeTruthy());
    expect(document.querySelectorAll(".stuck-banner").length).toBe(1);
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("raises the banner on the CLOCK alone — a stall writes nothing to Dexie", async () => {
    // setTimeout stays real: fake-indexeddb and waitFor both run on it.
    vi.useFakeTimers({ toFake: ["setInterval", "clearInterval", "Date"] });
    await capture(STUCK_AFTER_MS - 30_000);
    render(<Harness />);
    await settle();
    expect(screen.queryByRole("status")).toBeNull();

    act(() => vi.advanceTimersByTime(60_000));
    await waitFor(() => expect(screen.getByRole("status")).toBeTruthy());
  });
});
