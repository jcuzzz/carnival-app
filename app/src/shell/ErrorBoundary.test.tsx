import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
// Intentionally no environment docblock: environmentMatchGlobs in vite.config
// gives every *.test.tsx a jsdom environment (assessment T4).
import { act, cleanup, render, screen } from "@testing-library/react";
import ErrorBoundary from "./ErrorBoundary";

function Boom(): never {
  throw new Error("render exploded");
}

beforeEach(() => {
  cleanup();
  // React logs the caught error itself; the boundary logs it again on purpose.
  vi.spyOn(console, "error").mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("ErrorBoundary", () => {
  it("renders its children when nothing throws", () => {
    render(
      <ErrorBoundary>
        <p>results</p>
      </ErrorBoundary>,
    );
    expect(screen.getByText("results")).toBeTruthy();
  });

  it("replaces a thrown render with a reassuring fallback and an outbox link", () => {
    render(
      <ErrorBoundary>
        <Boom />
      </ErrorBoundary>,
    );

    expect(screen.getByRole("alert")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Reload" })).toBeTruthy();
    expect(screen.getByText(/queued on this device/)).toBeTruthy();

    const link = screen.getByRole("link", { name: /queued work/ });
    expect(link.getAttribute("href")).toBe("#/outbox");
  });

  it("leaves the fallback when the hash changes, so Back and the nav recover", () => {
    let explode = true;
    function Screen() {
      if (explode) throw new Error("render exploded");
      return <p>results</p>;
    }
    render(
      <ErrorBoundary>
        <Screen />
      </ErrorBoundary>,
    );
    expect(screen.getByRole("alert")).toBeTruthy();

    explode = false;
    act(() => {
      window.dispatchEvent(new HashChangeEvent("hashchange"));
    });
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByText("results")).toBeTruthy();
  });
});
