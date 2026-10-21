// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { IDLE_TIMEOUT_MS, createIdleGuard } from "./idleGuard";

/**
 * ADR-0031: the shared-device idle rule. The asymmetry is the whole point —
 * reachable the device signs itself out, unreachable it must not, because
 * clearing a roster that cannot be re-fetched strands the deck mid-carnival.
 * Reachability is the injected PROBE, never `navigator.onLine` — jsdom reports
 * onLine=true throughout these tests, and the guard must not care.
 */

const RETRY_MS = 5 * 60 * 1000;

/** A stand-in window, so the state machine is driven without a real DOM. */
function fakeTarget() {
  const listeners = new Map<string, Set<() => void>>();
  return {
    addEventListener(type: string, listener: () => void) {
      (listeners.get(type) ?? listeners.set(type, new Set()).get(type)!).add(listener);
    },
    removeEventListener(type: string, listener: () => void) {
      listeners.get(type)?.delete(listener);
    },
    emit(type: string) {
      for (const listener of [...(listeners.get(type) ?? [])]) listener();
    },
    count(type: string) {
      return listeners.get(type)?.size ?? 0;
    },
  };
}

function guardOn(reachable: boolean) {
  const target = fakeTarget();
  const onSignOut = vi.fn();
  const onLockedChange = vi.fn();
  let connected = reachable;
  const guard = createIdleGuard({
    onSignOut,
    onLockedChange,
    probe: () => Promise.resolve(connected),
    retryMs: RETRY_MS,
    target,
  });
  guard.start();
  return { guard, target, onSignOut, onLockedChange, connect: () => (connected = true) };
}

const locked = (onLockedChange: ReturnType<typeof vi.fn>) =>
  onLockedChange.mock.calls.at(-1)?.[0] ?? false;

beforeEach(() => vi.useFakeTimers());
afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("idle guard (ADR-0031)", () => {
  it("signs out an idle device whose server probe answers", async () => {
    const { onSignOut, onLockedChange } = guardOn(true);

    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    expect(onSignOut).toHaveBeenCalledTimes(1);
    // No overlay: the signed-out shell is already the private state.
    expect(locked(onLockedChange)).toBe(false);
  });

  it("covers, not clears, when the probe fails — even though navigator.onLine is true", async () => {
    // The pool-AP-up/uplink-dead case: the platform says online, the server is
    // gone, and the roster cannot be re-fetched. jsdom's navigator.onLine is
    // true here, so this pins that the probe decides, not the platform flag.
    expect(navigator.onLine).toBe(true);
    const { onSignOut, onLockedChange } = guardOn(false);

    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    expect(onSignOut).not.toHaveBeenCalled();
    expect(locked(onLockedChange)).toBe(true);
  });

  it("completes the deferred sign-out when an online event brings the server back", async () => {
    const { target, onSignOut, onLockedChange, connect } = guardOn(false);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    connect();
    target.emit("online");
    await vi.advanceTimersByTimeAsync(0);

    expect(onSignOut).toHaveBeenCalledTimes(1);
    expect(locked(onLockedChange)).toBe(false);
  });

  it("completes the deferred sign-out on the retry cadence with NO online event", async () => {
    // The AP-up case again: navigator never went offline, so no online event
    // will ever fire. The locked state re-probes on a timer instead.
    const { onSignOut, connect } = guardOn(false);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);
    await vi.advanceTimersByTimeAsync(RETRY_MS); // still unreachable — stays locked
    expect(onSignOut).not.toHaveBeenCalled();

    connect();
    await vi.advanceTimersByTimeAsync(RETRY_MS);

    expect(onSignOut).toHaveBeenCalledTimes(1);
  });

  it("cancels the deferred sign-out when the official resumes", async () => {
    const { guard, target, onSignOut, onLockedChange, connect } = guardOn(false);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    guard.resume();
    expect(locked(onLockedChange)).toBe(false);
    connect();
    target.emit("online");
    await vi.advanceTimersByTimeAsync(0);

    expect(onSignOut).not.toHaveBeenCalled();
  });

  it("re-arms after a resume, so a device put down again locks again", async () => {
    const { guard, onSignOut, onLockedChange } = guardOn(false);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);
    guard.resume();

    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    expect(locked(onLockedChange)).toBe(true);
    expect(onSignOut).not.toHaveBeenCalled();
  });

  it("resets the deadline on real interaction", async () => {
    const { target, onSignOut } = guardOn(true);

    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS - 1_000);
    target.emit("pointerdown");
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS - 1_000);

    expect(onSignOut).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1_000);
    expect(onSignOut).toHaveBeenCalledTimes(1);
  });

  it("throttles the reset, so a burst of taps is not a burst of work", async () => {
    // The deadline is an hour; re-arming it on every tap of a capture screen
    // buys nothing. A tap inside the throttle window leaves the deadline where
    // the last accepted one put it.
    const { target, onSignOut } = guardOn(true);
    await vi.advanceTimersByTimeAsync(1_000);
    target.emit("keydown"); // inside the throttle window — ignored
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS - 1_000);

    expect(onSignOut).toHaveBeenCalledTimes(1);
  });

  it("ignores taps behind the overlay — only Resume lowers it", async () => {
    const { target, onLockedChange } = guardOn(false);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    target.emit("pointerdown");
    await vi.advanceTimersByTimeAsync(0);

    expect(locked(onLockedChange)).toBe(true);
  });

  it("stops cleanly: no timer, no listeners, no overlay left standing", async () => {
    const { guard, target, onSignOut, onLockedChange } = guardOn(false);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS);

    guard.stop();

    expect(locked(onLockedChange)).toBe(false);
    expect(target.count("pointerdown")).toBe(0);
    expect(target.count("online")).toBe(0);
    await vi.advanceTimersByTimeAsync(IDLE_TIMEOUT_MS * 2);
    expect(onSignOut).not.toHaveBeenCalled();
  });

  it("is an hour, which no e2e journey comes near", () => {
    expect(IDLE_TIMEOUT_MS).toBe(60 * 60 * 1000);
  });
});
