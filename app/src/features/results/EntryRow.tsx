import { useEffect, useRef, useState, type ReactNode, type RefObject } from "react";
import type { CarnivalEvent, Entry, ResultDto, SchoolRecord } from "../../api/types";
import type { NonOkResultStatus } from "../../api/outboxTransport";
import type { LaneControlHandle } from "../races/EntryLaneControl";
import { db, type OutboxItem } from "../../db/db";
import { RecordBadge } from "../records/RecordBadge";
import { cancelIfPending, needsRebase, requeue } from "../../sync/outbox";
import {
  capturedRaw,
  conflictDetail,
  correctionTarget,
  outboxHoldsDisplay,
  queuedSeries,
  recoverableHere,
  resultDisplay,
} from "./resultDisplay";
import { AttemptStrip } from "./AttemptStrip";
import { PlaceBadge } from "./PlaceBadge";
import { ResultForm } from "./ResultForm";
import { RowActionsMenu, type RowAction } from "./RowActionsMenu";
import { statusCodesFor, statusLabel } from "./statusLabels";
import { submitAttemptViaOutbox } from "../../results/submitAttempt";

const UNDO_WINDOW_MS = 5_000;

const SYNCING_NOTE = "Already syncing — it can be corrected once the server has it.";

function SavedBadge() {
  return (
    <span className="sync-badge saved">
      <span aria-hidden="true" className="sync-glyph">
        ✓
      </span>
      Saved
    </span>
  );
}

function LaneBlock({ lane }: { lane: number | null }) {
  return (
    <span className="lane-block">
      <span className="lane-caption">Lane</span>
      {lane === null ? (
        <>
          <span className="lane-number mono" aria-hidden="true">
            —
          </span>
          <span className="visually-hidden">none</span>
        </>
      ) : (
        <span className="lane-number mono">{lane}</span>
      )}
    </span>
  );
}

/** One deck row: swimmer, their recorded/queued result or the capture form. */
export function EntryRow({
  carnivalId,
  event,
  entry,
  heatDivider,
  createItem,
  onDismissCreate,
  serverResult,
  outboxItem,
  attemptItems,
  resultsUnavailable,
  lane,
  lanePending,
  unplaced,
  raceless,
  inRace,
  laneControl,
  standingRecord,
  actorSubject,
  inputRef,
  onSaved,
}: {
  carnivalId: string;
  event: CarnivalEvent | undefined;
  entry: Entry;
  /** Set only in a legacy race that carries two heats. */
  heatDivider: number | null;
  createItem: OutboxItem | undefined;
  onDismissCreate: (() => void) | undefined;
  serverResult: ResultDto | undefined;
  outboxItem: OutboxItem | undefined;
  /** This device's un-drained attempt captures, by ordinal. */
  attemptItems?: ReadonlyMap<number, OutboxItem>;
  /** The recorded-result list failed to load — this row's state is unknown. */
  resultsUnavailable: boolean;
  /**
   * This device's lane, undelivered placements folded in. Null is "no lane"; undefined falls
   * back to the entry's own lane.
   */
  lane?: number | null;
  /** A queued add in an auto-fill carnival: the server picks the lane at drain time. */
  lanePending?: boolean;
  /** Outside every race of a division that has them, so not ranked. */
  unplaced?: boolean;
  /** In no race of a division that has them; marked because it renders under the first heat. */
  raceless?: boolean;
  inRace?: boolean;
  laneControl?: (handle: RefObject<LaneControlHandle | null>) => ReactNode;
  standingRecord?: SchoolRecord;
  actorSubject: string | null;
  inputRef: RefObject<HTMLInputElement | null>;
  onSaved: () => void;
}) {
  const measureType = event?.measureType ?? "Time";
  const attemptCount = event?.attemptCount ?? null;
  const attemptsMode = attemptCount != null && attemptCount > 0;
  const display = resultDisplay(serverResult, outboxItem, measureType);
  const addPending = createItem?.status === "queued" || createItem?.status === "syncing";
  const addFailed = createItem?.status === "rejected" || createItem?.status === "conflict";
  const [editing, setEditing] = useState<
    {
      correcting?: { resultId: string; baseVersion: number };
      parked?: OutboxItem;
      rebase?: number;
      attempt?: { ordinal: number; foul?: boolean };
      initialRaw: string;
    } | null
  >(null);
  const [undoable, setUndoable] = useState<string | null>(null);
  const [rowNote, setRowNote] = useState<string | null>(null);
  const undoTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(undoTimer.current), []);
  const laneHandle = useRef<LaneControlHandle | null>(null);
  // The menu fires the open form's own status write, never a second one.
  const statusSubmit = useRef<((code: NonOkResultStatus) => void) | null>(null);
  const [recovering, setRecovering] = useState(false);
  const [requeueBusy, setRequeueBusy] = useState(false);
  const requeueRef = useRef<HTMLButtonElement | null>(null);

  const undoOpen = undoable !== null;
  const saveApplied = outboxItem === undefined || outboxItem.status === "accepted";
  // The place ranks the server's value, so it clears wherever the row shows anything else.
  const placeShown = !outboxHoldsDisplay(outboxItem) && editing === null && !unplaced;

  // Lanes and heats mean nothing on a field event, even where the data puts an entry in a heat.
  const timed = measureType === "Time";
  const raceNote = !timed ? "" : lanePending ? " · lane pending" : raceless ? " · no heat" : "";
  const shownLane = lane === undefined ? (entry.lane ?? null) : lane;

  const target = correctionTarget(serverResult, outboxItem, measureType);
  // Work the server has never seen is recalled and retyped, not corrected.
  const recallable = outboxItem?.status === "queued" ? outboxItem : undefined;
  // outboxItem is undefined until the outbox read lands, so no recovery renders off a guess.
  const parked = outboxItem?.status === "conflict" ? outboxItem : undefined;
  // A correction that lost the concurrency race moves only once re-based on the server's revision.
  const deadEnd = parked !== undefined && !recoverableHere(parked, serverResult);
  const stale = parked !== undefined && needsRebase(parked);
  const canRequeue = parked !== undefined && !deadEnd && !stale;
  const canRetype = parked !== undefined && !deadEnd && (!stale || target !== null);
  // Not gated on the event being Open: only carnival finalisation closes a result to correction.
  // An attempts row's value is derived, so marks are corrected per cell, the row only for a status.
  const rowCorrectable =
    !attemptsMode ||
    (serverResult !== undefined && serverResult.status !== "Ok" && serverResult.status !== "Nm");
  // A typed DNS/DNF/DQ closes the attempts route, so every cell goes through the correction sheet.
  const statusBlocksAttempts =
    attemptsMode &&
    (serverResult?.status === "Dns" ||
      serverResult?.status === "Dnf" ||
      serverResult?.status === "Dq");
  const canEdit =
    !editing &&
    (recallable !== undefined ||
      canRetype ||
      (parked === undefined && target !== null && rowCorrectable));

  // What was armed is gone: another surface (or another tab) resolved it.
  useEffect(() => {
    if (!canRequeue) setRecovering(false);
  }, [canRequeue]);

  useEffect(() => {
    if (recovering) requeueRef.current?.focus();
  }, [recovering]);

  function openUndoWindow(itemId: string | null) {
    clearTimeout(undoTimer.current);
    setRowNote(null);
    setUndoable(itemId);
    if (itemId !== null) {
      undoTimer.current = setTimeout(() => setUndoable(null), UNDO_WINDOW_MS);
    }
  }

  async function handleUndo() {
    const itemId = undoable;
    if (itemId === null) return;
    const cancelled = await cancelIfPending(itemId);
    clearTimeout(undoTimer.current);
    setUndoable(null);
    if (cancelled) return;
    // The worker got there first, so the only way back is the audited correction path.
    const item = await db.outbox.get(itemId);
    const later = correctionTarget(serverResult, item, measureType);
    if (later) {
      setEditing({ correcting: later, initialRaw: later.initialRaw });
    } else {
      setRowNote(SYNCING_NOTE);
    }
  }

  async function handleRequeue() {
    if (!parked) return;
    setRequeueBusy(true);
    try {
      await requeue(parked.id);
      setRecovering(false);
    } catch {
      // It left `conflict` under us — the outbox screen or another tab got it.
      setRowNote("That capture has already been dealt with somewhere else.");
      setRecovering(false);
    } finally {
      setRequeueBusy(false);
    }
  }

  async function captureAttempt(ordinal: number, value: string | null, foul: boolean) {
    if (!actorSubject || !eventOpen || attemptCount === null) return;
    setRowNote(null);
    if (statusBlocksAttempts) {
      if (target) {
        setEditing({ correcting: target, attempt: { ordinal, foul }, initialRaw: value ?? "" });
      }
      return;
    }
    try {
      await submitAttemptViaOutbox({
        carnivalId,
        carnivalEventId: entry.carnivalEventId,
        entryId: entry.id,
        entryDisplayName: entry.displayName,
        ordinal,
        value,
        foul,
        measureType,
        attemptCount,
        actorSubject,
      });
    } catch (err) {
      setRowNote(err instanceof Error ? err.message : String(err));
    }
  }

  function startEdit() {
    setRecovering(false);
    if (recallable) {
      void (async () => {
        const raw = capturedRaw(recallable);
        const cancelled = await cancelIfPending(recallable.id);
        // Where the server already holds a result, resubmitting would duplicate it.
        const base = correctionTarget(serverResult, undefined, measureType);
        if (cancelled) {
          setEditing(base ? { correcting: base, initialRaw: raw } : { initialRaw: raw });
          return;
        }
        const later = correctionTarget(
          serverResult,
          await db.outbox.get(recallable.id),
          measureType,
        );
        if (later) setEditing({ correcting: later, initialRaw: later.initialRaw });
        else setRowNote(SYNCING_NOTE);
      })();
      return;
    }
    // A parked item wins over a live correction base: retyping under a fresh key would leave it
    // conflicted forever.
    if (canRetype && parked) {
      setEditing({
        parked,
        initialRaw: capturedRaw(parked),
        ...(target ? { rebase: target.baseVersion } : {}),
      });
      return;
    }
    if (!target) return;
    // A typed status is undone by putting a mark back in a cell; the server refuses a best mark.
    setEditing(
      statusBlocksAttempts
        ? { correcting: target, attempt: { ordinal: 1 }, initialRaw: "" }
        : { correcting: target, initialRaw: target.initialRaw },
    );
  }

  const eventOpen = event === undefined || event.status === "Open";
  // No mark field of its own; it carries the row menu's DNS/DNF/DQ through the one write path.
  const statusFormShown =
    attemptsMode &&
    editing === null &&
    !addFailed &&
    !resultsUnavailable &&
    parked === undefined &&
    recallable === undefined;
  const capturing =
    !addFailed && (editing !== null || statusFormShown || (!display && !resultsUnavailable));
  const actions: RowAction[] = [
    ...(capturing
      ? statusCodesFor(measureType, attemptsMode).map((code) => ({
          key: code,
          label: statusLabel(code),
          disabled: !eventOpen,
          onSelect: () => statusSubmit.current?.(code),
        }))
      : []),
    ...(laneControl
      ? [
          ...(timed
            ? [
                {
                  key: "lane",
                  label: "Change lane",
                  onSelect: () => laneHandle.current?.openPicker(),
                },
              ]
            : []),
          ...(inRace
            ? [
                {
                  key: "remove",
                  label: timed ? "Remove from heat" : "Remove entry",
                  danger: true,
                  onSelect: () => laneHandle.current?.armRemove(),
                },
              ]
            : []),
        ]
      : []),
  ];

  const form = (opts: {
    initialRaw: string;
    correcting?: { resultId: string; baseVersion: number };
    parkedItem?: OutboxItem;
    rebase?: number;
    attemptOrdinal?: number;
    attemptFoul?: boolean;
    statusOnly?: boolean;
  }) => (
    <ResultForm
      carnivalId={carnivalId}
      event={event}
      entry={entry}
      actorSubject={actorSubject}
      inputRef={inputRef}
      initialRaw={opts.initialRaw}
      {...(opts.correcting ? { correcting: opts.correcting } : {})}
      {...(opts.parkedItem ? { parked: opts.parkedItem } : {})}
      {...(opts.rebase !== undefined ? { rebase: opts.rebase } : {})}
      {...(opts.statusOnly ? { statusOnly: true } : {})}
      {...(opts.attemptOrdinal !== undefined && attemptCount !== null
        ? {
            attemptEdit: {
              ordinal: opts.attemptOrdinal,
              attemptCount,
              attempts: queuedSeries(outboxItem) ?? target?.attempts,
              ...(opts.attemptFoul ? { foul: true } : {}),
            },
          }
        : {})}
      statusRef={statusSubmit}
      {...((opts.correcting || opts.parkedItem) && !opts.statusOnly
        ? { onCancel: () => setEditing(null) }
        : {})}
      onSubmitted={(itemId) => {
        setEditing(null);
        openUndoWindow(itemId);
        onSaved();
      }}
    />
  );

  return (
    <>
      {timed && heatDivider != null && (
        <li className="heat-divider" aria-hidden="true">
          Heat {heatDivider}
        </li>
      )}
      <li className={display || addFailed || resultsUnavailable ? "" : "awaiting"}>
        {/* deck-row: only a capture row takes the tablet two-column layout. */}
        <div className="entry-row deck-row">
          {timed && <LaneBlock lane={shownLane} />}
          <span className="row-main">
            <span className="row-title">
              {/* Own element: the e2e entryRow anchor matches this name exactly. */}
              <span>{entry.displayName}</span>
              {entry.overrideApplied && (
                <span className="badge info" title="Category override applied (ADR-0003)">
                  {" "}
                  override
                </span>
              )}
              {addPending && (
                <span className="badge muted" title="Queued to add — drains when online (ADR-0010)">
                  {" "}
                  adding…
                </span>
              )}
              {addFailed && <span className="badge bad"> not added</span>}
              {placeShown && <PlaceBadge place={serverResult?.place} />}
              <RecordBadge
                record={standingRecord}
                value={serverResult?.measuredValue}
                tier={event?.tier}
              />
            </span>
            {entry.isTeam ? (
              <span className="row-sub deck-meta">
                <span>
                  {entry.houseCode}
                  {entry.legs && entry.legs.length > 0
                    ? ` · ${entry.legs.map((l) => l.displayName).join(", ")}`
                    : ""}
                  {raceNote}
                </span>
                {laneControl?.(laneHandle)}
              </span>
            ) : (
              <span className="row-sub deck-meta">
                <span>
                  {entry.houseCode} · Yr {entry.yearLevel}
                  {timed && entry.heat != null ? ` · heat ${entry.heat}` : ""}
                  {raceNote}
                </span>
                {laneControl?.(laneHandle)}
              </span>
            )}
          </span>
          {addFailed ? null : editing ? (
            form({
              initialRaw: editing.initialRaw,
              ...(editing.correcting ? { correcting: editing.correcting } : {}),
              ...(editing.parked ? { parkedItem: editing.parked } : {}),
              ...(editing.rebase !== undefined ? { rebase: editing.rebase } : {}),
              ...(editing.attempt ? { attemptOrdinal: editing.attempt.ordinal } : {}),
              ...(editing.attempt?.foul ? { attemptFoul: true } : {}),
            })
          ) : display ? (
            /* Both slots are width-reserved in CSS, so state swaps never shift the row. */
            <span className="row-status">
              <span
                className="mono row-title result-value"
                aria-label={`Result for ${entry.displayName}`}
              >
                {display.text}
              </span>
              <span className="row-state">
                {undoOpen && saveApplied ? (
                  <SavedBadge />
                ) : canRequeue ? (
                  <button
                    type="button"
                    className="badge-button"
                    aria-expanded={recovering}
                    aria-label={`Sync conflict for ${entry.displayName} — recover this capture`}
                    onClick={() => setRecovering((open) => !open)}
                  >
                    {display.badge}
                  </button>
                ) : (
                  display.badge
                )}
              </span>
              <span className="row-action">
                {undoOpen ? (
                  <button
                    type="button"
                    className="undo-result"
                    aria-label={`Undo the result just saved for ${entry.displayName}`}
                    onClick={() => void handleUndo()}
                  >
                    Undo
                  </button>
                ) : canEdit ? (
                  <button type="button" className="edit-result" onClick={startEdit}>
                    Edit
                  </button>
                ) : null}
              </span>
            </span>
          ) : resultsUnavailable ? (
            <span className="row-sub" aria-label={`Result for ${entry.displayName}`}>
              result unknown — results unavailable
            </span>
          ) : attemptsMode ? null : (
            form({ initialRaw: "" })
          )}
          {statusFormShown &&
            form({
              initialRaw: "",
              statusOnly: true,
              // A DNS on a row with a series is a correction; a submit is refused RESULT_EXISTS.
              ...(target !== null ? { correcting: target } : {}),
            })}
          <RowActionsMenu displayName={entry.displayName} actions={actions} />
        </div>
        {attemptsMode && !addFailed && !resultsUnavailable && editing === null && (
          <AttemptStrip
            entry={entry}
            measureType={measureType}
            attemptCount={attemptCount!}
            attempts={queuedSeries(outboxItem) ?? serverResult?.attempts ?? undefined}
            queued={attemptItems}
            disabled={
              !eventOpen || actorSubject === null || (statusBlocksAttempts && target === null)
            }
            onCapture={(ordinal, value, foul) => void captureAttempt(ordinal, value, foul)}
            onEdit={
              target === null
                ? null
                : (ordinal, initialRaw) =>
                    setEditing({ correcting: target, attempt: { ordinal }, initialRaw })
            }
          />
        )}
        {/* Always mounted, so a screen reader hears the text change and nothing below moves. */}
        <span className="visually-hidden" role="status">
          {undoOpen ? `Saved ${entry.displayName}'s result — undo available.` : ""}
        </span>
        {rowNote && (
          <div className="alert notice" role="status">
            {rowNote}
          </div>
        )}
        {addFailed && (
          <div className="alert error" role="alert">
            Couldn't add {entry.displayName} to this event: {conflictDetail(createItem!)}{" "}
            {onDismissCreate && (
              <button type="button" className="danger" onClick={onDismissCreate}>
                Dismiss
              </button>
            )}
          </div>
        )}
        {parked && (
          <div className="alert error" role="alert">
            Sync conflict for {entry.displayName}: {conflictDetail(parked)}{" "}
            {recovering ? (
              <button
                type="button"
                ref={requeueRef}
                disabled={requeueBusy}
                onClick={() => void handleRequeue()}
              >
                {requeueBusy ? "Sending…" : "Send again"}
              </button>
            ) : canRequeue ? (
              "Tap the conflict badge to send it again, or Edit to retype it."
            ) : canRetype ? (
              "Sending it again would be refused the same way — tap Edit to retype it against the recorded result."
            ) : (
              "Nothing here can send this one — it can be let go from the outbox screen."
            )}
          </div>
        )}
        {outboxItem?.status === "rejected" && (
          <div className="alert error" role="alert">
            Submission rejected by the server for {entry.displayName}: {conflictDetail(outboxItem)}
          </div>
        )}
      </li>
    </>
  );
}
