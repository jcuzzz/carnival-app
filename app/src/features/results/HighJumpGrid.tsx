import { useEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from "react";
import type { CarnivalEvent, ResultDto } from "../../api/types";
import type {
  JumpMark,
  NonOkResultStatus,
  ResultCapturePayload,
} from "../../api/outboxTransport";
import type { OutboxItem } from "../../db/db";
import StatusBadge from "../../sync/StatusBadge";
import { isAbandoned, needsRebase, requeue } from "../../sync/outbox";
import type { LaneControlHandle } from "../races/EntryLaneControl";
import {
  attemptsPerHeightOf,
  cardCleared,
  cellAt,
  deviceMarksAt,
  eliminated,
  formatHeight,
  heightKey,
  heightLabel,
  inNotation,
  markable,
  markedCells,
  sameHeight,
  type CardCell,
} from "../../results/jumpCard";
import { AttemptsPerHeightControl } from "./AttemptsPerHeightControl";
import type { DeckRow } from "./deckRows";
import { JumpCellPanel } from "./JumpCellPanel";
import { RaiseBarControl } from "./RaiseBarControl";
import { ResultForm } from "./ResultForm";
import { conflictDetail, correctionTarget, recoverableHere } from "./resultDisplay";
import { RowActionsMenu, type RowAction } from "./RowActionsMenu";
import { STATUS_CODES, statusCodesFor, statusLabel } from "./statusLabels";

/** The read half is down: the card on screen is not the card. */
const UNAVAILABLE_NOTICE =
  "This event's results could not be read, so nothing here can be marked or corrected until they come back. Marks already captured on this device are safe.";

/** A Scheduled division may take its opening height before it opens. */
const BAR_STATUSES: readonly CarnivalEvent["status"][] = ["Scheduled", "Open"];

export interface JumpGridRow extends DeckRow {
  /** For "Remove entry"; absent = no actions. */
  laneControl?: (handle: RefObject<LaneControlHandle | null>) => ReactNode;
  inRace: boolean;
}

/** Everything the grid derives per athlete, once — the sort reads `out` alone. */
interface RowState {
  row: JumpGridRow;
  serverResult: ResultDto | undefined;
  queued: Map<string, OutboxItem[]> | undefined;
  /** This device's undelivered `result.submit` / `result.correct` for the row. */
  resultItem: OutboxItem | undefined;
  cells: CardCell[];
  /** A DNS/DNF/DQ the server holds, or one this device has captured. */
  statusTyped: boolean;
  /** How the row names that state: "DNS", or "out" for an elimination. */
  stateWord: string;
  out: boolean;
}

export function HighJumpGrid({
  carnivalId,
  event,
  rows,
  resultsByEntry,
  jumpsByEntry,
  outboxByEntry,
  resultsUnavailable,
  reachable,
  actorSubject,
  actorDisplayName,
  onChanged,
}: {
  carnivalId: string;
  event: CarnivalEvent;
  rows: readonly JumpGridRow[];
  resultsByEntry: ReadonlyMap<string, ResultDto>;
  /** This device's queued marks, by entry then height key (`jumpItemsForEvent`). */
  jumpsByEntry: ReadonlyMap<string, Map<string, OutboxItem[]>>;
  /** This device's result captures by entry (`resultItemsForEvent`) — a typed
   *  DNS/DNF/DQ taken from the row menu leaves no other trace on the grid. */
  outboxByEntry: ReadonlyMap<string, OutboxItem>;
  resultsUnavailable: boolean;
  reachable: boolean;
  actorSubject: string | null;
  actorDisplayName?: string;
  /** A landed correction or a raised bar — the deck's reads are stale. */
  onChanged: () => void;
}) {
  const [openCell, setOpenCell] = useState<{
    entryId: string;
    height: number;
    attempt: number;
  } | null>(null);
  const heights = useMemo(() => event.heights ?? [], [event.heights]);
  const barHeight = heights.length > 0 ? heights[heights.length - 1]! : null;
  const eventOpen = event.status === "Open";
  const perHeight = attemptsPerHeightOf(event);
  const barEditable =
    BAR_STATUSES.includes(event.status) && actorSubject !== null && !resultsUnavailable;
  // A mark queued here counts too: switching under it would change the rules it was taken by.
  const anyMark = useMemo(
    () =>
      [...resultsByEntry.values()].some((result) => (result.jumps?.length ?? 0) > 0) ||
      [...jumpsByEntry.values()].some((byHeight) =>
        [...byHeight.values()].some((items) => items.some((item) => !isAbandoned(item.status))),
      ),
    [resultsByEntry, jumpsByEntry],
  );
  // A card read off a failed results read is not the card.
  const blocked = !eventOpen || actorSubject === null || resultsUnavailable;
  const deck = useRef<HTMLDivElement | null>(null);
  const wrap = useRef<HTMLDivElement | null>(null);

  // The bar is the RIGHTMOST group, so a phone would open the scroller left of
  // it. `--jump-gutter` masks the half-covered group behind the sticky athlete.
  useEffect(() => {
    const el = wrap.current;
    if (!el) return;
    const measure = () => {
      const sticky = el.querySelector(".jump-athlete-head");
      if (!sticky) return;
      const edge = sticky.getBoundingClientRect().right;
      // Nearest GROUP edge right of the sticky one, so a height shows whole or not
      // at all. Best heads the first header row: document order is not left-to-right.
      let gutter = Infinity;
      for (const head of el.querySelectorAll(".jump-attempt-head.first, .jump-best-head")) {
        const left = head.getBoundingClientRect().left;
        if (left >= edge - 0.5) gutter = Math.min(gutter, left - edge);
      }
      const width = Number.isFinite(gutter) ? Math.max(0, gutter) : 0;
      el.style.setProperty("--jump-gutter", `${width}px`);
    };
    const snap = () => {
      el.scrollLeft = el.scrollWidth;
      measure();
    };
    // Only a change of WIDTH re-opens at the bar: an expanded box grows the table
    // taller, and snapping then would scroll away the box being edited.
    let widths = "";
    const resized = () => {
      const now = `${el.clientWidth}:${el.scrollWidth}`;
      if (now === widths) return measure();
      widths = now;
      snap();
    };
    resized();
    el.addEventListener("scroll", measure, { passive: true });
    const observer = typeof ResizeObserver === "undefined" ? null : new ResizeObserver(resized);
    observer?.observe(el);
    // The table reflows as results land without the scroller changing size.
    if (el.firstElementChild) observer?.observe(el.firstElementChild);
    return () => {
      el.removeEventListener("scroll", measure);
      observer?.disconnect();
    };
  }, [heights.length]);

  // A tap anywhere off the grid collapses the open cell: the panel is inline, so
  // nothing else takes the tap away from it the way a modal backdrop did.
  useEffect(() => {
    if (!openCell) return;
    const onDown = (e: Event) => {
      if (!deck.current?.contains(e.target as Node)) setOpenCell(null);
    };
    document.addEventListener("pointerdown", onDown);
    return () => document.removeEventListener("pointerdown", onDown);
  }, [openCell]);

  // Still-in first, out below: a stable sort, so the deck order survives inside each group.
  const ordered = useMemo(() => {
    const states = rows.map<RowState>((row) => {
      const serverResult = resultsByEntry.get(row.entry.id);
      const queued = jumpsByEntry.get(row.entry.id);
      const resultItem = outboxByEntry.get(row.entry.id);
      const cells = markedCells(serverResult?.jumps, queued);
      const captured = capturedStatus(resultItem);
      const statusTyped = nonStarter(serverResult) || captured !== null;
      const stateWord = nonStarter(serverResult)
        ? statusLabel(serverResult!.status)
        : captured !== null
          ? statusLabel(captured)
          : "out";
      return {
        row,
        serverResult,
        queued,
        resultItem,
        cells,
        statusTyped,
        stateWord,
        out: eliminated(cells, perHeight) || statusTyped,
      };
    });
    return states.sort((a, b) => Number(a.out) - Number(b.out));
  }, [rows, resultsByEntry, jumpsByEntry, outboxByEntry, perHeight]);

  return (
    <div className="deck-group jump-deck" ref={deck}>
      <div className="card jump-card">
        <div className="jump-card-head">
          <div className="jump-bar">
            <h2 className="jump-bar-name">
              {barHeight === null ? "No opening height yet" : `Bar at ${heightLabel(barHeight)}`}
            </h2>
            <AttemptsPerHeightControl
              event={event}
              switchable={barEditable && !anyMark}
              reachable={reachable}
              onChanged={onChanged}
            />
          </div>
          <RaiseBarControl
            event={event}
            reachable={reachable}
            disabled={!barEditable}
            onRaised={onChanged}
            onStale={onChanged}
          />
        </div>
        {resultsUnavailable && (
          <p className="alert notice jump-note" role="status">
            {UNAVAILABLE_NOTICE}
          </p>
        )}
        <div className="table-wrap jump-scroll" ref={wrap}>
          <table className="jump-grid">
            <thead>
              <tr>
                <th scope="col" rowSpan={2} className="jump-athlete-head">
                  Athlete
                </th>
                {heights.map((height) => (
                  <th
                    key={heightKey(height)}
                    scope="colgroup"
                    colSpan={perHeight}
                    className={`jump-height${barHeight === height ? " bar" : ""}`}
                  >
                    {formatHeight(height)}
                  </th>
                ))}
                <th scope="col" rowSpan={2} className="jump-best-head">
                  Best
                </th>
                <th scope="col" rowSpan={2} className="jump-actions-head">
                  <span className="visually-hidden">Actions</span>
                </th>
              </tr>
              <tr>
                {heights.flatMap((height) =>
                  attemptIndexes(perHeight).map((i) => (
                    <th
                      key={`${heightKey(height)}:${i}`}
                      scope="col"
                      className={`jump-attempt-head${i === 0 ? " first" : ""}${
                        barHeight === height ? " bar" : ""
                      }`}
                    >
                      {i + 1}
                    </th>
                  )),
                )}
              </tr>
            </thead>
            <tbody>
              {ordered.map((state) => (
                <GridRow
                  key={state.row.entry.id}
                  carnivalId={carnivalId}
                  event={event}
                  state={state}
                  heights={heights}
                  barHeight={barHeight}
                  resultsUnavailable={resultsUnavailable}
                  blocked={blocked}
                  eventOpen={eventOpen}
                  actorSubject={actorSubject}
                  {...(actorDisplayName !== undefined ? { actorDisplayName } : {})}
                  open={openCell?.entryId === state.row.entry.id ? openCell : null}
                  onOpenCell={(box) =>
                    setOpenCell(box === null ? null : { entryId: state.row.entry.id, ...box })
                  }
                  onChanged={onChanged}
                />
              ))}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}

function GridRow({
  carnivalId,
  event,
  state,
  heights,
  barHeight,
  resultsUnavailable,
  blocked,
  eventOpen,
  actorSubject,
  actorDisplayName,
  open,
  onOpenCell,
  onChanged,
}: {
  carnivalId: string;
  event: CarnivalEvent;
  state: RowState;
  heights: readonly number[];
  barHeight: number | null;
  resultsUnavailable: boolean;
  /** Shut for a reason that is not this row's — see `HighJumpGrid`. */
  blocked: boolean;
  eventOpen: boolean;
  actorSubject: string | null;
  actorDisplayName?: string;
  open: OpenBox | null;
  onOpenCell: (box: OpenBox | null) => void;
  onChanged: () => void;
}) {
  const { row, serverResult, queued, resultItem, cells, statusTyped, stateWord, out } = state;
  const perHeight = attemptsPerHeightOf(event);
  const { entry } = row;
  const laneHandle = useRef<LaneControlHandle | null>(null);
  const statusSubmit = useRef<((code: NonOkResultStatus) => void) | null>(null);
  const inputRef = useRef<HTMLInputElement | null>(null);
  const boxRefs = useRef(new Map<string, HTMLButtonElement | null>());
  // The box focus goes to once the mark just taken reaches the card and opens it.
  const pendingFocus = useRef<string | null>(null);
  const [requeueBusy, setRequeueBusy] = useState(false);
  const [rowNote, setRowNote] = useState<string | null>(null);

  const closed = closedFor(serverResult, statusTyped, blocked);
  // An accepted mark's ack is a correction base too, so a DNS typed straight after it amends that
  // result instead of going out as a second submit the server refuses.
  const target = correctionTarget(serverResult, latestAccepted(queued), "Height");
  const addFailed = row.createItem?.status === "rejected" || row.createItem?.status === "conflict";
  const captured = capturedStatus(resultItem);
  const parked =
    captured !== null && (resultItem!.status === "conflict" || resultItem!.status === "rejected")
      ? resultItem!
      : undefined;
  // Same rule as EntryRow: a refusal this row cannot clear must not offer a
  // send that re-parks, and every act on a capture needs a named official.
  const canRequeue =
    parked?.status === "conflict" &&
    !needsRebase(parked) &&
    recoverableHere(parked, serverResult) &&
    actorSubject !== null;
  // No field of its own; it carries the menu's DNS/DNF/DQ. Withdrawn while a status is unanswered,
  // or a retype would orphan it under a fresh key.
  const statusFormShown = !addFailed && !resultsUnavailable && captured === null;

  async function handleRequeue() {
    if (!parked) return;
    setRequeueBusy(true);
    try {
      await requeue(parked.id);
    } catch {
      setRowNote("That capture has already been dealt with somewhere else.");
    } finally {
      setRequeueBusy(false);
    }
  }

  useEffect(() => {
    const key = pendingFocus.current;
    const node = key === null ? undefined : boxRefs.current.get(key);
    if (!node) return;
    node.focus();
    pendingFocus.current = null;
  });

  /** Focus first, close second: the box is still mounted while it has focus. */
  function collapse(box: OpenBox, marked?: JumpMark) {
    pendingFocus.current = null;
    if (marked === undefined) {
      boxRefs.current.get(boxKey(box.height, box.attempt))?.focus();
      onOpenCell(null);
      return;
    }
    const taken = cellAt(serverResult?.jumps, queued, box.height).marks.length;
    const markedBox = boxRefs.current.get(boxKey(box.height, taken));
    (markedBox ?? boxRefs.current.get(boxKey(box.height, box.attempt)))?.focus();
    const after = cells.some((cell) => sameHeight(cell.height, box.height))
      ? cells.map((cell) =>
          sameHeight(cell.height, box.height) ? { ...cell, marks: cell.marks + marked } : cell,
        )
      : [...cells, { height: box.height, stored: "", queued: marked, marks: marked }].sort(
          (a, b) => a.height - b.height,
        );
    if (markable(after, box.height, closed, perHeight))
      pendingFocus.current = boxKey(box.height, taken + 1);
    onOpenCell(null);
  }

  function openBox(box: OpenBox) {
    pendingFocus.current = null;
    onOpenCell(box);
  }

  const actions: RowAction[] = [
    ...(statusFormShown
      ? statusCodesFor(event.measureType, true).map((code) => ({
          key: code,
          label: statusLabel(code),
          disabled: !eventOpen,
          onSelect: () => statusSubmit.current?.(code),
        }))
      : []),
    ...(row.laneControl && row.inRace
      ? [
          {
            key: "remove",
            label: "Remove entry",
            danger: true,
            onSelect: () => laneHandle.current?.armRemove(),
          },
        ]
      : []),
  ];

  // Mounted only where Remove entry can arm it: idle, it still costs a line.
  const showLane = row.laneControl !== undefined && row.inRace;
  const extras = showLane || parked !== undefined || rowNote !== null;

  return (
    <>
      <tr className={out ? "jump-row out" : "jump-row"}>
        <th scope="row" className="jump-athlete">
          <span className="jump-name">{entry.displayName}</span>
          <span className="jump-state">
            {entry.houseCode}
            {out ? ` · ${stateWord}` : ""}
            {resultItem && captured !== null && <StatusBadge status={resultItem.status} />}
          </span>
          {statusFormShown && (
            <ResultForm
              carnivalId={carnivalId}
              event={event}
              entry={entry}
              actorSubject={actorSubject}
              inputRef={inputRef}
              initialRaw=""
              statusOnly
              statusRef={statusSubmit}
              // Always a CORRECTION where a result exists: a DNS on an athlete
              // who already has a card is `result.correct`, which clears it.
              {...(target !== null ? { correcting: target } : {})}
              onSubmitted={() => undefined}
            />
          )}
        </th>
        {heights.flatMap((height) => {
          const cell = cellAt(serverResult?.jumps, queued, height);
          const items = queued?.get(heightKey(height));
          const canMark = markable(cells, height, closed, perHeight);
          // A typed status clears the card, so every height stays correctable to give it back.
          const correctable = target !== null && !blocked && (statusTyped || cell.stored !== "");
          // A parked mark is shown but never counted: the card behind it is the
          // server's, and the badge says whose answer it is.
          const shown = cell.stored + deviceMarksAt(items);
          const badge = latestQueued(items);
          const next = canMark ? cell.marks.length : -1;
          const bar = barHeight === height;
          const at = `${entry.displayName} at ${heightLabel(height)}`;
          return attemptIndexes(perHeight).map((i) => {
            const mark = shown[i] ?? "";
            const device = mark !== "" && i >= cell.stored.length;
            const label =
              i === next
                ? `Mark ${at}, attempt ${i + 1} of ${perHeight}`
                : (mark !== "" && (correctable || device)) ||
                    (i === 0 && shown === "" && statusTyped && correctable)
                  ? `Correct ${at}, attempt ${i + 1}`
                  : null;
            const isOpen =
              open !== null && sameHeight(open.height, height) && open.attempt === i;
            const last = mark !== "" && i === Math.min(shown.length, perHeight) - 1;
            const body = (
              <>
                {mark !== "" && (
                  <span className={`mono jump-marks-text${device ? " jump-queued" : ""}`}>
                    {inNotation(mark)}
                  </span>
                )}
                {badge && device && last && <StatusBadge status={badge.status} />}
              </>
            );
            return (
              <td
                key={boxKey(height, i)}
                className={`jump-cell jump-box${i === 0 ? " first" : ""}${bar ? " bar" : ""}${
                  isOpen ? " open" : ""
                }`}
              >
                {label !== null ? (
                  <button
                    type="button"
                    ref={(node) => {
                      boxRefs.current.set(boxKey(height, i), node);
                    }}
                    className={`jump-mark${i === next ? " markable" : ""}`}
                    aria-expanded={isOpen}
                    aria-label={label}
                    onClick={() => (isOpen ? onOpenCell(null) : openBox({ height, attempt: i }))}
                    onKeyDown={(e) => {
                      if (e.key === "Escape" && isOpen) onOpenCell(null);
                    }}
                  >
                    {body}
                  </button>
                ) : (
                  <span className="jump-mark">{body}</span>
                )}
              </td>
            );
          });
        })}
        <td className="jump-best mono">{bestText(serverResult, event.measureType)}</td>
        <td className="jump-actions">
          <RowActionsMenu displayName={entry.displayName} actions={actions} />
        </td>
      </tr>
      {open !== null && (
        <tr className="jump-panel-row">
          <td colSpan={heights.length * perHeight + 3}>
            <div className="jump-panel-pin">
              <JumpCellPanel
                // A fresh panel per cell: the mode it opens in is a property of
                // the cell that was tapped, not of the one before it.
                key={boxKey(open.height, open.attempt)}
                onClose={(marked) => collapse(open, marked)}
                carnivalId={carnivalId}
                event={event}
                entry={entry}
                height={open.height}
                attempt={open.attempt}
                stored={serverResult?.jumps}
                queued={queued}
                markable={markable(cells, open.height, closed, perHeight)}
                correcting={target}
                statusTyped={statusTyped}
                actorSubject={actorSubject}
                {...(actorDisplayName !== undefined ? { actorDisplayName } : {})}
                onCorrected={onChanged}
              />
            </div>
          </td>
        </tr>
      )}
      {extras && (
        <tr className="jump-extra">
          <td colSpan={heights.length * perHeight + 3}>
            <div className="jump-panel-pin">
              {showLane ? row.laneControl?.(laneHandle) : null}
              {parked && (
                <div className="alert error" role="alert">
                  Sync conflict for {entry.displayName}: {conflictDetail(parked)}{" "}
                  {canRequeue ? (
                    <button
                      type="button"
                      disabled={requeueBusy}
                      onClick={() => void handleRequeue()}
                    >
                      {requeueBusy ? "Sending…" : "Send again"}
                    </button>
                  ) : (
                    "Nothing here can send this one — it can be retyped or let go from the outbox screen."
                  )}
                </div>
              )}
              {rowNote && (
                <div className="alert notice" role="status">
                  {rowNote}
                </div>
              )}
            </div>
          </td>
        </tr>
      )}
    </>
  );
}

/** A tapped box: its height, and which attempt (0-based) it stands for. */
interface OpenBox {
  height: number;
  attempt: number;
}

function boxKey(height: number, attempt: number): string {
  return `${heightKey(height)}:${attempt}`;
}

function attemptIndexes(perHeight: number): number[] {
  return Array.from({ length: perHeight }, (_, i) => i);
}

/** The server's best height, never derived here. A cleared card is not recorded. */
function bestText(
  result: ResultDto | undefined,
  measureType: CarnivalEvent["measureType"],
): string {
  if (!result || cardCleared(result, measureType)) return "—";
  if (result.status !== "Ok") return statusLabel(result.status);
  return result.measuredValue == null ? "—" : formatHeight(Number(result.measuredValue));
}

function nonStarter(result: ResultDto | undefined): boolean {
  return result !== undefined && (STATUS_CODES as readonly string[]).includes(result.status);
}

/** A typed status still held here — queued, syncing or parked. */
function capturedStatus(item: OutboxItem | undefined): NonOkResultStatus | null {
  if (!item || item.status === "accepted" || isAbandoned(item.status)) return null;
  return (item.payload as ResultCapturePayload).status ?? null;
}

/** Nothing on this row can take another mark, whatever the card says. */
function closedFor(
  result: ResultDto | undefined,
  statusTyped: boolean,
  blocked: boolean,
): boolean {
  return blocked || statusTyped || result?.lifecycleState === "Final";
}

/** The last mark of this card the server took — the ack a correction sits on. */
function latestAccepted(queued: Map<string, OutboxItem[]> | undefined): OutboxItem | undefined {
  let latest: OutboxItem | undefined;
  for (const items of queued?.values() ?? [])
    for (const item of items)
      if (item.status === "accepted" && (!latest || item.capturedAt > latest.capturedAt))
        latest = item;
  return latest;
}

/** The badge the cell shows: this device's most recent un-drained mark there. */
function latestQueued(items: readonly OutboxItem[] | undefined): OutboxItem | null {
  const pending = (items ?? []).filter((item) => item.status !== "accepted");
  return pending[pending.length - 1] ?? null;
}
