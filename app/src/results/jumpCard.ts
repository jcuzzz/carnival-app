import type { OutboxItem, OutboxStatus } from "../db/db";
import type { JumpMark, ResultJumpPayload } from "../api/outboxTransport";
import type { CarnivalEvent, Jump, MeasureType, ResultDto } from "../api/types";
import { formatMeasure, measureRaw } from "./measureFormat";

// Display predicates only: the server owns the best height, the status and the ranking.

export const DEFAULT_ATTEMPTS_PER_HEIGHT = 3;

export function attemptsPerHeightOf(event: Pick<CarnivalEvent, "attemptsPerHeight">): number {
  return event.attemptsPerHeight ?? DEFAULT_ATTEMPTS_PER_HEIGHT;
}

/**
 * A Height NM with no marks left: the row stands but nothing is recorded; a typed NM on another
 * field event is recorded.
 */
export function cardCleared(
  result: Pick<ResultDto, "status" | "jumps"> | undefined,
  measureType: MeasureType | undefined,
): boolean {
  return (
    measureType === "Height" &&
    result?.status === "Nm" &&
    (result.jumps == null || result.jumps.length === 0)
  );
}

/** Whole centimetres, so 1.2 and 1.20 are one height wherever a key is needed. */
export function heightKey(height: number): string {
  return String(Math.round(height * 100));
}

export function sameHeight(a: number, b: number): boolean {
  return heightKey(a) === heightKey(b);
}

/** A pass is `-` as stored and an en dash everywhere it is printed. */
export function inNotation(marks: string): string {
  return marks.replace(/-/g, "\u2013");
}

export function formatHeight(height: number): string {
  return measureRaw("Height", height);
}

export function heightLabel(height: number): string {
  return formatMeasure("Height", height);
}

export interface CardCell {
  height: number;
  stored: string;
  /** Marks this device has captured and the server has not acknowledged. */
  queued: string;
  marks: string;
}

/**
 * An accepted capture is the server's now, and a parked one has been answered: neither may move a
 * predicate, or a refused mark would eliminate an athlete the server still has jumping.
 */
const PENDING: readonly OutboxStatus[] = ["queued", "syncing"];

function queuedMarksAt(items: readonly OutboxItem[] | undefined): string {
  return marksOf(items, (item) => PENDING.includes(item.status));
}

export function pendingMarks(queued: ReadonlyMap<string, OutboxItem[]> | undefined): OutboxItem[] {
  return [...(queued?.values() ?? [])]
    .flat()
    .filter((item) => PENDING.includes(item.status))
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
}

export function deviceItemsAt(items: readonly OutboxItem[] | undefined): OutboxItem[] {
  return (items ?? []).filter((item) => item.status !== "accepted");
}

/** For display only: the official sees parked work too. */
export function deviceMarksAt(items: readonly OutboxItem[] | undefined): string {
  return marksOf(deviceItemsAt(items), () => true);
}

function marksOf(
  items: readonly OutboxItem[] | undefined,
  keep: (item: OutboxItem) => boolean,
): string {
  return (items ?? [])
    .filter(keep)
    .map((item) => (item.payload as ResultJumpPayload).mark)
    .join("");
}

export function cellAt(
  stored: readonly Jump[] | null | undefined,
  queued: ReadonlyMap<string, OutboxItem[]> | undefined,
  height: number,
): CardCell {
  const storedMarks = stored?.find((jump) => sameHeight(jump.height, height))?.marks ?? "";
  const queuedMarks = queuedMarksAt(queued?.get(heightKey(height)));
  return { height, stored: storedMarks, queued: queuedMarks, marks: storedMarks + queuedMarks };
}

export function markedCells(
  stored: readonly Jump[] | null | undefined,
  queued: ReadonlyMap<string, OutboxItem[]> | undefined,
): CardCell[] {
  const heights = new Map<string, number>();
  for (const jump of stored ?? []) heights.set(heightKey(jump.height), jump.height);
  for (const [key, items] of queued ?? []) {
    if (heights.has(key)) continue;
    const first = items[0];
    if (first) heights.set(key, (first.payload as ResultJumpPayload).height);
  }
  return [...heights.values()]
    .sort((a, b) => a - b)
    .map((height) => cellAt(stored, queued, height))
    .filter((cell) => cell.marks !== "");
}

/**
 * At 3, three consecutive fails, a clearance breaking the run and `-` not a fail; at 2, both
 * attempts at one height failed.
 */
export function eliminated(
  cells: readonly CardCell[],
  attemptsPerHeight = DEFAULT_ATTEMPTS_PER_HEIGHT,
): boolean {
  return attemptsPerHeight === 2
    ? cells.some((cell) => cell.marks === "XX")
    : failRun(cells).includes("XXX");
}

function lastMarkedHeight(cells: readonly CardCell[]): number | null {
  return cells.length === 0 ? null : cells[cells.length - 1]!.height;
}

export function isLastHeight(cells: readonly CardCell[], height: number): boolean {
  const last = lastMarkedHeight(cells);
  return last !== null && sameHeight(last, height);
}

export function attemptNumber(cells: readonly CardCell[], height: number): number {
  return marksAt(cells, height).length + 1;
}

/**
 * The server's own refusals read forward, so a cell that would only be turned down is not a
 * button. `closed` carries the row-level blocks.
 */
export function markable(
  cells: readonly CardCell[],
  height: number,
  closed = false,
  attemptsPerHeight = DEFAULT_ATTEMPTS_PER_HEIGHT,
): boolean {
  if (closed || eliminated(cells, attemptsPerHeight)) return false;
  const last = lastMarkedHeight(cells);
  if (last !== null && Math.round(height * 100) < Math.round(last * 100)) return false;
  const marks = marksAt(cells, height);
  return !marks.includes("O") && !marks.endsWith("-") && marks.length < attemptsPerHeight;
}

export function nextFailEliminates(
  cells: readonly CardCell[],
  height: number,
  attemptsPerHeight = DEFAULT_ATTEMPTS_PER_HEIGHT,
): boolean {
  if (attemptsPerHeight === 2) return marksAt(cells, height) === "X";
  const run = failRun(cells.filter((cell) => Math.round(cell.height * 100) <= Math.round(height * 100)));
  return /X*$/.exec(run)![0].length === 2;
}

export function nextBarHeight(
  heights: readonly number[] | null | undefined,
  ladder: { start: number; step: number } | null | undefined,
): number | null {
  const last = heights && heights.length > 0 ? heights[heights.length - 1]! : null;
  if (last === null) return ladder ? round2(ladder.start) : null;
  return ladder ? round2(last + ladder.step) : null;
}

export function round2(value: number): number {
  return Math.round(value * 100) / 100;
}

function marksAt(cells: readonly CardCell[], height: number): string {
  return cells.find((cell) => sameHeight(cell.height, height))?.marks ?? "";
}

/** Every mark in height order with passes removed: a pass carries the run on. */
function failRun(cells: readonly CardCell[]): string {
  return cells.map((cell) => cell.marks).join("").replace(/-/g, "");
}

export interface DroppedMark {
  height: number;
  /** 1-based, as the box it sat in is numbered. */
  attempt: number;
  mark: string;
}

/**
 * Normalised as the server's replay judges it: marks after an O or pass at a height come off, as
 * does all after an eliminating jump. `attempt` is the 0-based box; `dropped` omits the edit.
 */
export function editCard(
  card: readonly Jump[] | null | undefined,
  height: number,
  attempt: number,
  replacement: JumpMark | null,
  attemptsPerHeight = DEFAULT_ATTEMPTS_PER_HEIGHT,
): { card: Jump[]; dropped: DroppedMark[] } {
  const edited = [...(card ?? [])]
    .sort((a, b) => a.height - b.height)
    .map((jump) => {
      const marks = [...jump.marks].map((mark, i) => ({ mark, attempt: i + 1 }));
      if (!sameHeight(jump.height, height)) return { height: jump.height, marks };
      if (replacement === null) marks.splice(attempt, 1);
      else marks[attempt] = { mark: replacement, attempt: attempt + 1 };
      return { height: jump.height, marks };
    });

  const kept: CardCell[] = [];
  const dropped: DroppedMark[] = [];
  for (const jump of edited) {
    const cell: CardCell = { height: jump.height, stored: "", queued: "", marks: "" };
    kept.push(cell);
    for (const { mark, attempt: at } of jump.marks) {
      const ended = /[O-]$/.test(cell.marks) || cell.marks.length >= attemptsPerHeight;
      if (ended || eliminated(kept, attemptsPerHeight)) {
        dropped.push({ height: jump.height, attempt: at, mark });
        continue;
      }
      cell.marks += mark;
      cell.stored = cell.marks;
    }
  }
  return {
    card: kept
      .filter((cell) => cell.marks !== "")
      .map((cell) => ({ height: cell.height, marks: cell.marks })),
    dropped,
  };
}

/** A height that loses every mark is named by the height alone. */
export function describeDropped(
  dropped: readonly DroppedMark[],
  before: readonly Jump[] | null | undefined,
): string {
  const byHeight = new Map<string, DroppedMark[]>();
  for (const drop of dropped) {
    const key = heightKey(drop.height);
    byHeight.set(key, [...(byHeight.get(key) ?? []), drop]);
  }
  const parts = [...byHeight.values()].map((drops) => {
    const height = drops[0]!.height;
    const had = before?.find((jump) => sameHeight(jump.height, height))?.marks.length ?? 0;
    if (drops.length === had) return heightLabel(height);
    const numbers = drops.map((drop) => drop.attempt);
    const list = numbers.length === 1 ? `${numbers[0]}` : joinAnd(numbers.map(String));
    return `${numbers.length === 1 ? "attempt" : "attempts"} ${list} at ${heightLabel(height)}`;
  });
  return joinAnd(parts);
}

function joinAnd(parts: readonly string[]): string {
  return parts.length <= 1
    ? (parts[0] ?? "")
    : `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;
}
