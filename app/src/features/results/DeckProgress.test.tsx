// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ResultDto } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { DeckProgress } from "./DeckProgress";

/**
 * The title row's counts (ADR-0041 §4). A field or high-jump deck captures marks,
 * not submits, so what is still in flight there has to reach the same readout.
 */

function result(entryId: string): ResultDto {
  return { id: `res-${entryId}`, entryId, status: "Ok" } as ResultDto;
}

function item(id: string, status: OutboxItem["status"]): OutboxItem {
  return {
    id,
    kind: "result.jump",
    payload: {},
    actorSubject: "official-1",
    capturedAt: "2027-03-06T01:00:00Z",
    status,
    attempts: 0,
  } as OutboxItem;
}

function renderProgress(over?: {
  attempts?: [string, Map<number, OutboxItem>][];
  jumps?: [string, Map<string, OutboxItem[]>][];
}) {
  return render(
    <DeckProgress
      total={4}
      resultsByEntry={new Map([["en-1", result("en-1")]])}
      outboxByEntry={new Map()}
      attemptsByEntry={new Map(over?.attempts ?? [])}
      jumpsByEntry={new Map(over?.jumps ?? [])}
      resultsUnavailable={false}
    />,
  );
}

describe("DeckProgress with marks in flight", () => {
  it("counts a queued mark as pending, once per athlete however many taps it was", () => {
    const { container } = renderProgress({
      jumps: [
        ["en-2", new Map([["140", [item("j-1", "queued"), item("j-2", "queued")]]])],
        ["en-3", new Map([["140", [item("j-3", "syncing")]]])],
      ],
    });
    expect(screen.getByRole("status").textContent).toContain("2 pending");
    // Recorded stays the SERVER's derivation — a mark is not a result.
    expect(screen.getByRole("status").textContent).toContain("1 of 4 recorded");
    expect(container.querySelector(".deck-progress")!.className).toContain("pending");
  });

  it("counts a parked mark as needing attention, and turns the header's tone", () => {
    const { container } = renderProgress({
      jumps: [["en-2", new Map([["140", [item("j-1", "conflict")]]])]],
    });
    expect(screen.getByRole("status").textContent).toContain("1 needs attention");
    expect(container.querySelector(".deck-progress")!.className).toContain("trouble");
  });

  it("counts a queued attempt cell the same way", () => {
    renderProgress({ attempts: [["en-2", new Map([[1, item("a-1", "queued")]])]] });
    expect(screen.getByRole("status").textContent).toContain("1 pending");
  });

  it("says nothing about an accepted mark — the server's own count carries it", () => {
    const { container } = renderProgress({
      jumps: [["en-2", new Map([["140", [item("j-1", "accepted")]]])]],
    });
    expect(screen.getByRole("status").textContent).not.toContain("pending");
    expect(container.querySelector(".deck-progress")!.className).toContain("ok");
  });
});
