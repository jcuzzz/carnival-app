// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import { Picker } from "./Picker";

/**
 * The three shapes one picker item can take (ADR-0042 §2). The third is the
 * point: an item with nowhere to go must not be an <a>, which looks tappable
 * on a deck and does nothing.
 */

beforeEach(cleanup);

describe("Picker", () => {
  it("renders a link only where the item has somewhere to go", () => {
    render(
      <Picker
        axisLabel="Heat"
        value="Heat 1"
        items={[
          { key: "1", label: "Heat 1", current: true, ariaCurrent: "true" },
          { key: "2", label: "Heat 2", href: "#/heat-2" },
          { key: "3", label: "Heat 3" },
        ]}
      />,
    );
    fireEvent.click(screen.getByRole("button", { name: "Heat Heat 1" }));

    const here = screen.getByRole("menuitem", { name: "Heat 1" });
    expect(here.tagName).toBe("DIV");
    expect(here.className).toContain("current");
    expect(here.getAttribute("aria-current")).toBe("true");

    const link = screen.getByRole("menuitem", { name: "Heat 2" });
    expect(link.tagName).toBe("A");
    expect(link.getAttribute("href")).toBe("#/heat-2");

    const dead = screen.getByRole("menuitem", { name: "Heat 3" });
    expect(dead.tagName).toBe("DIV");
    expect(dead.getAttribute("aria-current")).toBeNull();
    expect(document.querySelectorAll("a[role='menuitem']")).toHaveLength(1);
  });

  it("omits the axis name where the value already carries it", () => {
    render(<Picker value="Heat 2" items={[{ key: "2", label: "Heat 2", current: true }]} />);
    expect(screen.getByRole("button", { name: "Heat 2" })).toBeTruthy();
  });
});
