// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import OutdoorToggle from "./OutdoorToggle";
import { applyTheme, currentTheme, initTheme } from "./theme";

const root = () => document.documentElement;

beforeEach(() => {
  cleanup();
  localStorage.clear();
  root().removeAttribute("data-theme");
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("the outdoor theme (ADR-0040 §6)", () => {
  it("marks <html> and remembers the choice for this device", () => {
    expect(currentTheme()).toBe("standard");

    applyTheme("outdoor");
    expect(root().getAttribute("data-theme")).toBe("outdoor");
    expect(currentTheme()).toBe("outdoor");
    expect(localStorage.getItem("sports.theme")).toBe("outdoor");

    applyTheme("standard");
    expect(root().hasAttribute("data-theme")).toBe(false);
    expect(localStorage.getItem("sports.theme")).toBe("standard");
  });

  it("applies the stored choice at boot, and nothing otherwise", () => {
    initTheme();
    expect(root().hasAttribute("data-theme")).toBe(false);

    localStorage.setItem("sports.theme", "outdoor");
    initTheme();
    expect(root().getAttribute("data-theme")).toBe("outdoor");
  });

  it("leaves the attribute off for a stored value it does not recognise", () => {
    localStorage.setItem("sports.theme", "dark");
    initTheme();
    expect(root().hasAttribute("data-theme")).toBe(false);
  });

  it("still applies the theme when storage refuses (private/managed browsers)", () => {
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("QuotaExceededError");
    });
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("SecurityError");
    });

    expect(() => applyTheme("outdoor")).not.toThrow();
    expect(root().getAttribute("data-theme")).toBe("outdoor");
    expect(() => initTheme()).not.toThrow();
  });
});

describe("the outdoor toggle", () => {
  it("flips the attribute on click and reports its state", () => {
    render(<OutdoorToggle />);
    const button = screen.getByRole("button", { name: "Outdoor" });
    expect(button.getAttribute("aria-pressed")).toBe("false");

    fireEvent.click(button);
    expect(button.getAttribute("aria-pressed")).toBe("true");
    expect(root().getAttribute("data-theme")).toBe("outdoor");

    fireEvent.click(button);
    expect(button.getAttribute("aria-pressed")).toBe("false");
    expect(root().hasAttribute("data-theme")).toBe(false);
  });

  it("opens pressed when the device is already on the outdoor theme", () => {
    localStorage.setItem("sports.theme", "outdoor");
    initTheme();
    render(<OutdoorToggle />);
    expect(screen.getByRole("button", { name: "Outdoor" }).getAttribute("aria-pressed")).toBe(
      "true",
    );
  });
});
