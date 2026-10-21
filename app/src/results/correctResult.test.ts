import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import { resultDisplay } from "../features/results/resultDisplay";

vi.mock("../api/syncSetup", () => ({
  syncWorker: { start: vi.fn(), stop: vi.fn(), syncNow: vi.fn().mockResolvedValue(undefined) },
  resumeAfterSignIn: vi.fn(),
}));
vi.mock("../api/resources", () => ({ correctResult: vi.fn() }));

import { correctResult } from "../api/resources";
import { submitCorrection, type CorrectionInput } from "./correctResult";

const input = (over: Partial<CorrectionInput> = {}): CorrectionInput => ({
  carnivalId: "c-1",
  carnivalEventId: "ev-1",
  entryId: "en-1",
  entryDisplayName: "Sam Swimmer",
  resultId: "res-1",
  baseVersion: 3,
  value: null,
  status: null,
  measureType: "Distance",
  reason: "Corrected at the deck",
  actorSubject: "official-1",
  ...over,
});

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  vi.mocked(correctResult).mockReset();
  Object.defineProperty(navigator, "onLine", { value: false, configurable: true });
});

afterEach(() => {
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
});

describe("an offline attempts correction", () => {
  it("queues the whole series and reads on the row as its best mark, not '?'", async () => {
    const outcome = await submitCorrection(
      input({
        attempts: [
          { value: "4.50", foul: false },
          { value: null, foul: true },
          { value: "4.32", foul: false },
        ],
      }),
    );

    expect(correctResult).not.toHaveBeenCalled();
    expect(outcome.kind).toBe("queued");
    const item = outcome.kind === "queued" ? outcome.item : undefined;
    expect(item?.payload).toMatchObject({ value: null, status: null });
    expect(resultDisplay(undefined, item, "Distance")?.text).toBe("4.50 m");
  });

  it("reads an all-foul series as NM, the status the server will derive", async () => {
    const outcome = await submitCorrection(
      input({ attempts: [{ value: null, foul: true }, null, { value: null, foul: true }] }),
    );
    const item = outcome.kind === "queued" ? outcome.item : undefined;
    expect(resultDisplay(undefined, item, "Distance")?.text).toBe("NM");
  });
});
