import { describe, expect, it } from "vitest";
import type { components } from "./generated";
import type {
  ApiErrorBody,
  CarnivalEvent,
  Entry,
  ResultDto,
  ResultStatus,
  StagedRow,
} from "./types";

/**
 * K6 smoke: the generated document is committed, importable, and IS the app's
 * wire vocabulary — not a second opinion beside a hand-written one.
 *
 * Most of this file is compile-time: a broken alias fails `tsc -b` (and the
 * build) rather than any assertion here. The runtime expectations exist so the
 * checks live in the suite everyone runs, and so a deleted alias is a red test
 * rather than a silently skipped file.
 */

/** Compile-time assertion helper: fails to typecheck unless T is exactly U. */
type Exact<T, U> = [T] extends [U] ? ([U] extends [T] ? true : never) : never;

describe("generated wire types", () => {
  it("are re-exported under the app's own names, not re-described", () => {
    // Each line is the real check: it does not compile if types.ts has drifted
    // from the generated schema.
    const entry: Exact<Entry, components["schemas"]["EntryDto"]> = true;
    const result: Exact<ResultDto, components["schemas"]["ResultDto"]> = true;
    const event: Exact<CarnivalEvent, components["schemas"]["CarnivalEventDto"]> = true;
    const status: Exact<ResultStatus, components["schemas"]["ResultStatus"]> = true;
    expect([entry, result, event, status]).toEqual([true, true, true, true]);
  });

  it("spell the result statuses the way the server does (C10 casing)", () => {
    // The read casing IS the client's canonical casing; only the request body
    // uses capitals, converted once in outboxTransport.
    const statuses: ResultStatus[] = ["Ok", "Dns", "Dnf", "Dq"];
    expect(statuses).toHaveLength(4);
  });

  it("pins the staged row's always-present fields (the document understates them)", () => {
    const row: Pick<StagedRow, "rowNumber" | "disposition"> = {
      rowNumber: 1,
      disposition: "New",
    };
    expect(row.rowNumber).toBe(1);
  });

  it("keeps the error body open for context and pre-contract shapes (ADR-0011)", () => {
    const contract: ApiErrorBody = { error: "CONCURRENCY_CONFLICT", detail: "…", currentRevision: 3 };
    const legacy: ApiErrorBody = { title: "Unprocessable" };
    expect(contract.currentRevision).toBe(3);
    expect(legacy.error).toBeUndefined();
  });
});
