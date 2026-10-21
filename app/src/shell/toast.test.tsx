// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import Toaster from "./Toaster";
import { showToast, TOAST_DISMISS_MS, _resetToastsForTests } from "./toast";

/**
 * The one transient-confirmation channel (U13). What matters is that it goes
 * away on its own, announces politely, and never becomes the place errors live.
 */

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  vi.useFakeTimers();
});

afterEach(() => {
  vi.useRealTimers();
});

describe("Toaster", () => {
  it("shows a raised toast and clears it after the dismiss window", () => {
    render(<Toaster />);
    expect(screen.queryByText("Scoring rules saved — standings recomputed.")).toBeNull();

    act(() => {
      showToast("Scoring rules saved — standings recomputed.");
    });
    expect(screen.getByText("Scoring rules saved — standings recomputed.")).toBeTruthy();

    act(() => {
      vi.advanceTimersByTime(TOAST_DISMISS_MS - 1);
    });
    expect(screen.getByText("Scoring rules saved — standings recomputed.")).toBeTruthy();

    act(() => {
      vi.advanceTimersByTime(1);
    });
    expect(screen.queryByText("Scoring rules saved — standings recomputed.")).toBeNull();
  });

  it("announces politely from a live region that is always present", () => {
    render(<Toaster />);
    // Present before any toast: a live region created at the same moment as its
    // content is not reliably announced.
    const region = screen.getByRole("status");
    expect(region.getAttribute("aria-live")).toBe("polite");

    act(() => {
      showToast("BANK team added.");
    });
    expect(region.textContent).toContain("BANK team added.");
  });

  it("can be dismissed early, and dismissing one leaves the others alone", () => {
    render(<Toaster />);
    act(() => {
      showToast("First confirmation.");
      showToast("Second confirmation.");
    });

    const dismissers = screen.getAllByLabelText("Dismiss notification");
    expect(dismissers).toHaveLength(2);
    fireEvent.click(dismissers[0]!);

    expect(screen.queryByText("First confirmation.")).toBeNull();
    expect(screen.getByText("Second confirmation.")).toBeTruthy();

    // The dismissed toast's timer is cancelled, not left to fire on the survivor.
    act(() => {
      vi.advanceTimersByTime(TOAST_DISMISS_MS);
    });
    expect(screen.queryByText("Second confirmation.")).toBeNull();
  });
});
