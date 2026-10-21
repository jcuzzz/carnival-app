import { cleanup, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import type { ResultDto } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { correctionTarget, resultDisplay } from "./resultDisplay";

/**
 * ADR-0017: which results a recorder may still amend, and what the badge says.
 *
 * This file exists because the change it covers was invisible to the suite —
 * nothing asserted the lifecycle badge or the correction gate, so widening both
 * would have gone green either way.
 */

const base: ResultDto = {
  id: "res-1",
  entryId: "en-1",
  measuredValue: 34.56,
  status: "Ok",
  jumps: null,
  lifecycleState: "Verified",
  currentRevision: 2,
  recordedAtUtc: "2026-08-11T01:00:00Z",
  recordedBySubject: "official-1",
  place: null,
};

const at = (lifecycleState: ResultDto["lifecycleState"]): ResultDto => ({ ...base, lifecycleState });

describe("correctionTarget", () => {
  it("offers a correction on a Verified result — the state every capture now arrives in", () => {
    // The behaviour change. Before ADR-0017 this returned null and a human had to
    // return the result to provisional first; there is no such human now.
    const target = correctionTarget(at("Verified"), undefined);
    expect(target).toMatchObject({ resultId: "res-1", baseVersion: 2, initialRaw: "34.56" });
  });

  it("still offers a correction on a legacy Provisional row", () => {
    // Rows captured before this change keep their state; they must stay editable.
    expect(correctionTarget(at("Provisional"), undefined)).not.toBeNull();
  });

  it("refuses once the carnival is finalised", () => {
    // The one closed door, and the server agrees (RESULT_FINALISED).
    expect(correctionTarget(at("Final"), undefined)).toBeNull();
  });
});

/**
 * ADR-0051: an accepted capture's ack is `{id, currentRevision}` and nothing
 * else, and BOTH result kinds keep one — so a second offline correction has a
 * revision to base itself on instead of falling back to nothing.
 */
const accepted = (kind: string, serverResponse: OutboxItem["serverResponse"]): OutboxItem => ({
  id: "01920000-0000-7000-8000-00000000000a",
  kind,
  payload: {
    carnivalId: "c-1",
    carnivalEventId: "ev-1",
    entryId: "en-1",
    value: "34.56",
    status: null,
  },
  actorSubject: "official-1",
  capturedAt: "2026-08-14T09:50:00.000Z",
  status: "accepted",
  attempts: 1,
  serverResponse,
});

describe("correctionTarget from an accepted capture's ack", () => {
  it.each(["result.submit", "result.correct"])("bases a correction on a %s ack", (kind) => {
    const target = correctionTarget(undefined, accepted(kind, { id: "res-1", currentRevision: 4 }));
    expect(target).toEqual({ resultId: "res-1", baseVersion: 4, initialRaw: "34.56" });
  });

  it("offers nothing when the ack kept no revision", () => {
    expect(correctionTarget(undefined, accepted("result.submit", { id: "res-1" }))).toBeNull();
  });

  it("offers nothing for a kind whose ack keeps nothing at all", () => {
    expect(correctionTarget(undefined, accepted("entry.create", undefined))).toBeNull();
  });
});

describe("lifecycle badge", () => {
  beforeEach(cleanup);

  it.each([
    ["Verified", "Recorded"],
    ["Provisional", "Recorded"],
  ] as const)("shows %s as %s — never a word implying a person checked it", (state, label) => {
    const display = resultDisplay(at(state), undefined);
    render(<>{display?.badge}</>);
    expect(screen.getByText(label)).toBeTruthy();
    // "Verified" on a deck screen would claim a human check that ADR-0017
    // deliberately removed. The stored state keeps the name; the badge must not.
    expect(screen.queryByText("Verified")).toBeNull();
  });

  it("shows Final once the day is published", () => {
    const display = resultDisplay(at("Final"), undefined);
    render(<>{display?.badge}</>);
    expect(screen.getByText("Final")).toBeTruthy();
  });

  it("still renders the measured time alongside the badge", () => {
    expect(resultDisplay(at("Verified"), undefined)?.text).toBe("34.56");
  });
});
