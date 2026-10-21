// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { CREATED_WITHOUT_ID_TEXT, NewCarnivalForm } from "./NewCarnivalForm";
import { renderWithQuery } from "../../test/queryWrapper";
import type { Carnival } from "../../api/types";

/**
 * ADR-0038 §1 amendment: a new carnival states its lanes on creation, with
 * auto-fill already ticked — and the pair rule holds here as it does in the
 * settings editor. ADR-0054 §5 fills the count in too, so the first press
 * creates.
 */

const createCarnival = vi.fn();

vi.mock("../../api/resources", () => ({
  createCarnival: (...args: unknown[]) => createCarnival(...args),
}));

function fillRequired() {
  fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Preston Swimming" } });
  fireEvent.change(screen.getByLabelText("Date"), { target: { value: "2027-03-06" } });
}

function body(): Record<string, unknown> {
  return createCarnival.mock.calls[0]![0] as Record<string, unknown>;
}

beforeEach(() => {
  cleanup();
  createCarnival.mockReset().mockResolvedValue({ id: "c-1" } as Carnival);
  location.hash = "#/carnivals";
});

describe("NewCarnivalForm", () => {
  // ADR-0054 §5: eight lanes and the box ticked, so name + date is a carnival.
  it("offers eight lanes with auto-fill already ticked", () => {
    renderWithQuery(<NewCarnivalForm onCancel={vi.fn()} />);
    expect((screen.getByLabelText("Auto-fill lanes") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Lanes per venue") as HTMLInputElement).value).toBe("8");
  });

  it("warns rather than creates when the ticked box is left with no lane count", () => {
    renderWithQuery(<NewCarnivalForm onCancel={vi.fn()} />);
    fillRequired();
    fireEvent.change(screen.getByLabelText("Lanes per venue"), { target: { value: "" } });
    fireEvent.click(screen.getByText("Create"));

    expect(createCarnival).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toBe("Set the lane count first.");
  });

  it("carries the defaulted lane count and the ticked box into the create", async () => {
    renderWithQuery(<NewCarnivalForm onCancel={vi.fn()} />);
    fillRequired();
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => expect(createCarnival).toHaveBeenCalledTimes(1));
    expect(body()).toMatchObject({ laneCount: 8, autoFillLanes: true });
    await waitFor(() => expect(location.hash).toBe("#/carnivals/c-1"));
  });

  it("stays put and says so when the create answers without an id", async () => {
    createCarnival.mockResolvedValue(undefined);
    renderWithQuery(<NewCarnivalForm onCancel={vi.fn()} />);
    fillRequired();
    fireEvent.click(screen.getByText("Create"));

    expect(await screen.findByText(CREATED_WITHOUT_ID_TEXT)).toBeTruthy();
    expect(location.hash).toBe("#/carnivals");
  });

  it("creates with auto-fill off and says nothing about the count", async () => {
    renderWithQuery(<NewCarnivalForm onCancel={vi.fn()} />);
    fillRequired();
    fireEvent.click(screen.getByLabelText("Auto-fill lanes"));
    fireEvent.change(screen.getByLabelText("Lanes per venue"), { target: { value: "" } });
    fireEvent.click(screen.getByText("Create"));

    await waitFor(() => expect(createCarnival).toHaveBeenCalledTimes(1));
    expect(body()).toMatchObject({ autoFillLanes: false });
    expect(Object.keys(body())).not.toContain("laneCount");
  });

  it("rejects a lane count outside the venue bounds before any write", () => {
    renderWithQuery(<NewCarnivalForm onCancel={vi.fn()} />);
    fillRequired();
    fireEvent.change(screen.getByLabelText("Lanes per venue"), { target: { value: "0" } });
    fireEvent.click(screen.getByText("Create"));

    expect(createCarnival).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("between 1 and 64");
  });
});
