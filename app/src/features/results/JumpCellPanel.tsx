import { useState, type KeyboardEvent } from "react";
import { errorText } from "../../api/errorText";
import type { CarnivalEvent, Entry, Jump, JumpCorrection } from "../../api/types";
import type { JumpMark, ResultJumpPayload } from "../../api/outboxTransport";
import type { OutboxItem } from "../../db/db";
import { cancelIfPending } from "../../sync/outbox";
import { submitCorrection } from "../../results/correctResult";
import { submitJumpViaOutbox } from "../../results/submitJump";
import {
  attemptNumber,
  attemptsPerHeightOf,
  cellAt,
  describeDropped,
  deviceItemsAt,
  editCard,
  heightKey,
  heightLabel,
  inNotation,
  markedCells,
  nextFailEliminates,
  pendingMarks,
  type DroppedMark,
} from "../../results/jumpCard";

/**
 * The marks a card entry may hold, in the order the panel offers them. A pass is
 * `-` on the wire and an en dash on the page — the notation officials read.
 */
const MARKS: readonly { mark: JumpMark; glyph: string; label: string; tone: string }[] = [
  { mark: "O", glyph: "O", label: "clear", tone: "clear" },
  { mark: "X", glyph: "X", label: "fail", tone: "fail" },
  { mark: "-", glyph: "–", label: "pass", tone: "pass" },
];

/** The worker sent the mark between the tap and the edit; the card has not caught up. */
const DRAINED_TEXT =
  "That mark has already reached the server. Once the card shows it, tap the box again to " +
  "change it there.";

/** What the tapped box holds, which decides everything the panel offers. */
type Box =
  | { kind: "next" }
  | { kind: "status" }
  | { kind: "server"; mark: string }
  | { kind: "queued"; mark: string; item: OutboxItem };

/** An edit waiting on Confirm because it takes other marks with it. */
interface Plan {
  replacement: JumpMark | null;
  card: Jump[];
  dropped: DroppedMark[];
  /** A queued edit: this device's marks to cancel, the edited one first. */
  cancel?: OutboxItem[];
}

export function JumpCellPanel({
  onClose,
  carnivalId,
  event,
  entry,
  height,
  attempt,
  stored,
  queued,
  markable,
  correcting,
  statusTyped,
  actorSubject,
  actorDisplayName,
  onCorrected,
}: {
  /** Collapses the panel; `marked` is the next mark just taken, when one was. */
  onClose: (marked?: JumpMark) => void;
  carnivalId: string;
  event: CarnivalEvent;
  entry: Entry;
  height: number;
  /** The tapped box, 0-based. */
  attempt: number;
  /** The card as the SERVER holds it — what a correction restates. */
  stored: readonly Jump[] | null | undefined;
  /** This device's queued marks for this entry, by height key. */
  queued: ReadonlyMap<string, OutboxItem[]> | undefined;
  /** The height can take its next mark. */
  markable: boolean;
  /** Null where nothing can be corrected: no server result, or a final one. */
  correcting: { resultId: string; baseVersion: number } | null;
  /** A typed status cleared the card: a mark here is the correction that gives one back. */
  statusTyped: boolean;
  actorSubject: string | null;
  actorDisplayName?: string;
  onCorrected: () => void;
}) {
  const [reason, setReason] = useState("");
  const [plan, setPlan] = useState<Plan | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const perHeight = attemptsPerHeightOf(event);
  const cells = markedCells(stored, queued);
  const cell = cellAt(stored, queued, height);
  const pending = pendingMarks(queued);
  const firstName = entry.displayName.split(" ")[0] ?? entry.displayName;
  const at = heightLabel(height);
  const box = boxAt();

  function boxAt(): Box {
    if (statusTyped) return { kind: "status" };
    if (attempt < cell.stored.length) return { kind: "server", mark: cell.stored[attempt]! };
    const item = deviceItemsAt(queued?.get(heightKey(height)))[attempt - cell.stored.length];
    if (item && pending.includes(item))
      return { kind: "queued", mark: (item.payload as ResultJumpPayload).mark, item };
    return { kind: "next" };
  }

  const waiting = box.kind === "server" && pending.length > 0;
  const editable =
    (box.kind === "server" && correcting !== null && !waiting) || box.kind === "queued";

  function autoReason(replacement: JumpMark | null): string {
    const current = box.kind === "server" || box.kind === "queued" ? box.mark : "";
    const what =
      replacement === null
        ? "removed"
        : `changed ${inNotation(current)} → ${inNotation(replacement)}`;
    const auto = `Attempt ${attempt + 1} at ${at} ${what}`;
    return reason.trim() === "" ? auto : `${auto} — ${reason.trim()}`;
  }

  async function run(action: () => Promise<boolean>) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      if (await action()) onClose();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  async function mark(value: JumpMark) {
    if (busy || !actorSubject) return;
    setBusy(true);
    setError(null);
    try {
      await submitJumpViaOutbox({
        carnivalId,
        carnivalEventId: event.id,
        entryId: entry.id,
        entryDisplayName: entry.displayName,
        height,
        mark: value,
        measureType: event.measureType,
        actorSubject,
        ...(actorDisplayName !== undefined ? { actorDisplayName } : {}),
      });
      onClose(value);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  }

  async function correct(jumps: JumpCorrection[], why: string): Promise<boolean> {
    if (!correcting || !actorSubject) return false;
    const outcome = await submitCorrection({
      carnivalId,
      carnivalEventId: entry.carnivalEventId,
      entryId: entry.id,
      entryDisplayName: entry.displayName,
      resultId: correcting.resultId,
      baseVersion: correcting.baseVersion,
      value: null,
      status: null,
      measureType: event.measureType,
      jumps,
      reason: why,
      actorSubject,
      ...(actorDisplayName !== undefined ? { actorDisplayName } : {}),
    });
    if (outcome.kind === "stale") {
      setError("This card changed elsewhere — reload before changing it again.");
      return false;
    }
    if (outcome.kind === "corrected") onCorrected();
    return true;
  }

  async function requeue(target: Plan): Promise<boolean> {
    const [edited, ...later] = target.cancel ?? [];
    if (!edited || !(await cancelIfPending(edited.id))) {
      setError(DRAINED_TEXT);
      return false;
    }
    for (const item of later) await cancelIfPending(item.id);
    if (target.replacement !== null && actorSubject)
      await submitJumpViaOutbox({
        carnivalId,
        carnivalEventId: event.id,
        entryId: entry.id,
        entryDisplayName: entry.displayName,
        height,
        mark: target.replacement,
        measureType: event.measureType,
        actorSubject,
        ...(actorDisplayName !== undefined ? { actorDisplayName } : {}),
      });
    return true;
  }

  function apply(target: Plan) {
    void run(() =>
      target.cancel ? requeue(target) : correct(target.card, autoReason(target.replacement)),
    );
  }

  function edit(replacement: JumpMark | null) {
    if (busy) return;
    let next: Plan;
    if (box.kind === "queued") {
      const later = pending.filter((item) => item.id > box.item.id);
      next = {
        replacement,
        card: [],
        dropped: later.map((item) => droppedFor(item)),
        cancel: [box.item, ...later],
      };
    } else {
      next = { replacement, ...editCard(stored, height, attempt, replacement, perHeight) };
    }
    if (next.dropped.length > 0) {
      setError(null);
      setPlan(next);
    } else apply(next);
  }

  /** Where a later queued mark sits on the grid, so the notice can name its box. */
  function droppedFor(item: OutboxItem): DroppedMark {
    const payload = item.payload as ResultJumpPayload;
    const items = deviceItemsAt(queued?.get(heightKey(payload.height)));
    const storedThere = cellAt(stored, undefined, payload.height).stored.length;
    return {
      height: payload.height,
      attempt: storedThere + items.indexOf(item) + 1,
      mark: payload.mark,
    };
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === "Escape" && !busy) {
      e.stopPropagation();
      onClose();
    }
  }

  const eliminates = box.kind === "next" && nextFailEliminates(cells, height, perHeight);
  const current = box.kind === "server" || box.kind === "queued" ? box.mark : null;

  const reasonField = (
    <label className="field wide" htmlFor={`jump-reason-${entry.id}`}>
      <span>Reason (optional)</span>
      <input
        id={`jump-reason-${entry.id}`}
        value={reason}
        placeholder="Filled in for you"
        onChange={(e) => setReason(e.target.value)}
        disabled={busy}
      />
    </label>
  );

  function targets(onPick: (value: JumpMark) => void) {
    return (
      <div className="jump-marks">
        {MARKS.map(({ mark: value, glyph, label, tone }) => {
          const now = value === current;
          const chosen = plan !== null && plan.replacement === value;
          return (
            <button
              key={value}
              type="button"
              className={[`jump-mark-button ${tone}`, now && "current", chosen && "chosen"]
                .filter(Boolean)
                .join(" ")}
              disabled={busy || actorSubject === null || now || plan !== null}
              aria-current={now ? "true" : undefined}
              aria-pressed={plan !== null ? chosen : undefined}
              aria-label={`${glyph} ${label} for ${entry.displayName} at ${at}${
                now ? " (current mark)" : ""
              }`}
              onClick={() => onPick(value)}
            >
              <span aria-hidden="true" className="jump-mark-glyph">
                {glyph}
              </span>{" "}
              <span aria-hidden="true">{now ? "now" : label}</span>
            </button>
          );
        })}
      </div>
    );
  }

  return (
    <div className="jump-panel" onKeyDown={onKeyDown}>
      <p className="jump-panel-head">
        <span className="jump-panel-who">{entry.displayName}</span>
        <span className="mono jump-panel-height">{at}</span>
        {box.kind === "next" && markable && (
          <span className="jump-panel-attempt">
            attempt {attemptNumber(cells, height)} of {perHeight}
          </span>
        )}
        {current !== null && (
          <span className="jump-panel-attempt">
            attempt {attempt + 1} — now {inNotation(current)}
          </span>
        )}
      </p>

      {box.kind === "next" && markable && targets((value) => void mark(value))}
      {eliminates && markable && (
        <p className="alert notice" role="status">
          {perHeight === 2
            ? `A second fail at this height puts ${firstName} out.`
            : `A third fail here puts ${firstName} out. Fails only carry to the next height ` +
              "when they pass rather than clear."}
        </p>
      )}
      {box.kind === "next" && !markable && (
        <p className="row-sub">Nothing can land at {at} on this card.</p>
      )}

      {box.kind === "status" && correcting !== null && (
        <>
          <p className="row-sub">
            A mark here gives {firstName} a card back, and the typed status goes with it.
          </p>
          {targets((value) =>
            void run(() =>
              correct(
                [{ height, marks: value }],
                reason.trim() === ""
                  ? `Card restored with ${inNotation(value)} at ${at}`
                  : reason.trim(),
              ),
            ),
          )}
          {reasonField}
        </>
      )}

      {waiting && (
        <p className="alert notice" role="status">
          Waiting for {pending.length} {pending.length === 1 ? "mark" : "marks"} to sync before
          this one can change.
        </p>
      )}

      {editable && (
        <>
          {targets((value) => edit(value))}
          {box.kind === "server" && reasonField}
          <div className="button-row jump-panel-actions">
            <button
              type="button"
              className={`danger${plan !== null && plan.replacement === null ? " chosen" : ""}`}
              disabled={busy || plan !== null}
              onClick={() => edit(null)}
            >
              Remove this mark
            </button>
          </div>
        </>
      )}

      {plan !== null && (
        <>
          <p className="alert warn" role="status">
            This also removes {describeDropped(plan.dropped, dropSource())}.
          </p>
          <div className="button-row jump-panel-actions">
            <button type="button" className="primary" disabled={busy} onClick={() => apply(plan)}>
              {busy ? "Saving…" : "Confirm"}
            </button>
            <button type="button" disabled={busy} onClick={() => onClose()}>
              Cancel
            </button>
          </div>
        </>
      )}

      {error && (
        <p className="alert error" role="alert">
          {error}
        </p>
      )}
    </div>
  );

  /** The card the drops are counted against: the server's, plus this device's marks. */
  function dropSource(): Jump[] {
    return cells.map((c) => ({ height: c.height, marks: c.marks }));
  }
}
