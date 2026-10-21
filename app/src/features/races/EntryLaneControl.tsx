import {
  Fragment,
  useEffect,
  useImperativeHandle,
  useRef,
  useState,
  type RefObject,
} from "react";
import type { OutboxItem } from "../../db/db";
import StatusBadge from "../../sync/StatusBadge";
import { ARMING_LOCKOUT_MS } from "../ui/timing";

const CONFIRM_WINDOW_MS = 4_000;

/** Keys that move a select's highlight without choosing anything by them. */
const TRAVERSAL_KEYS = [
  "ArrowUp",
  "ArrowDown",
  "ArrowLeft",
  "ArrowRight",
  "Home",
  "End",
  "PageUp",
  "PageDown",
];

const REMOVE = "__remove";
const SWAP = "__swap:";
/** A free lane in a race this row is NOT in: picking it moves the row there. */
const MOVE = "__move:";

export interface SwapOffer {
  lane: number;
  entryId: string;
  displayName: string;
}

export interface LaneRaceOption {
  /** Null for a division with no race yet — the pick mints one. */
  raceId: string | null;
  /** The group label; unused where the division offers a single race. */
  title: string | null;
  lanes: readonly number[];
  /** The race a plain pick goes to: this row's own, or the one it will mint. */
  home: boolean;
}

type Armed =
  | { kind: "remove" }
  | { kind: "swap"; offer: SwapOffer }
  | { kind: "move"; raceId: string; lane: number; title: string | null };

export interface LaneControlHandle {
  openPicker: () => void;
  armRemove: () => void;
}

function moveText(title: string | null, lane: number): string {
  return title ? `Move to ${title}, lane ${lane}` : `Move to lane ${lane}`;
}

export function EntryLaneControl({
  entryId,
  displayName,
  lane,
  inRace,
  races,
  swaps = [],
  disabled,
  item,
  handle,
  onPick,
  onPickInRace,
  onRemove,
  onSwap,
}: {
  entryId: string;
  displayName: string;
  /** Where this entry is now, as this device believes it; null = no lane. */
  lane: number | null;
  /** False for an entry in no race at all — the first pick mints one. */
  inRace: boolean;
  races: readonly LaneRaceOption[];
  /** The taken lanes, offered as swaps. Empty unless this row holds a lane. */
  swaps?: readonly SwapOffer[];
  disabled: boolean;
  /** An undelivered placement for this entry — its badge belongs on the row. */
  item: OutboxItem | undefined;
  handle?: RefObject<LaneControlHandle | null>;
  onPick: (lane: number | null) => void;
  onPickInRace?: (raceId: string, lane: number) => void;
  onRemove: () => void;
  onSwap?: (offer: SwapOffer) => void;
}) {
  // Remove and swap arm an in-place strip, never a window.confirm: a modal at the marshalling
  // point is a dialog to dismiss one-handed.
  const [armed, setArmed] = useState<Armed | null>(null);
  const [open, setOpen] = useState(false);
  const selectRef = useRef<HTMLSelectElement | null>(null);
  const confirmTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const armedAt = useRef(0);
  const confirmRef = useRef<HTMLButtonElement | null>(null);
  const lastPlace = useRef(`${lane}|${inRace}`);
  // Arrow traversal fires `change` per option on Windows Chrome/Firefox, so the value is held
  // until a commit key or blur. A mouse or touch pick never sets this.
  const [draft, setDraft] = useState<string | null>(null);
  const traversing = useRef(false);
  // A ref: Tab fires keydown and blur before React re-renders, so state would write twice.
  const pending = useRef<string | null>(null);
  useEffect(() => () => clearTimeout(confirmTimer.current), []);

  // Focus follows the interaction to the strip, or a keyboard user is left on the select.
  useEffect(() => {
    if (armed) confirmRef.current?.focus();
  }, [armed]);

  useEffect(() => {
    if (open) selectRef.current?.focus();
  }, [open]);

  useImperativeHandle(handle, () => ({
    openPicker: () => setOpen(true),
    armRemove: () => arm({ kind: "remove" }),
  }));

  // The row moved underneath the armed strip, so the confirm no longer does what it said.
  // The ref guard keeps the first run from disarming a strip the same task just armed.
  useEffect(() => {
    const place = `${lane}|${inRace}`;
    if (lastPlace.current === place) return;
    lastPlace.current = place;
    clearTimeout(confirmTimer.current);
    setArmed(null);
  }, [lane, inRace]);

  // A swap's other half moved: confirming would exchange lanes with a stranger.
  useEffect(() => {
    if (armed?.kind !== "swap") return;
    const target = armed.offer;
    if (swaps.some((s) => s.lane === target.lane && s.entryId === target.entryId)) return;
    clearTimeout(confirmTimer.current);
    setArmed(null);
  }, [swaps, armed]);

  // A move's lane was taken elsewhere: confirming would queue a placement the server refuses.
  useEffect(() => {
    if (armed?.kind !== "move") return;
    const target = armed;
    if (races.some((g) => g.raceId === target.raceId && g.lanes.includes(target.lane))) return;
    clearTimeout(confirmTimer.current);
    setArmed(null);
  }, [races, armed]);

  function disarm() {
    clearTimeout(confirmTimer.current);
    setArmed(null);
  }

  function armTimer() {
    clearTimeout(confirmTimer.current);
    confirmTimer.current = setTimeout(() => setArmed(null), CONFIRM_WINDOW_MS);
  }

  function arm(next: Armed) {
    setArmed(next);
    armedAt.current = Date.now();
    armTimer();
  }

  function commitDraft() {
    const value = pending.current;
    if (value === null) {
      traversing.current = false;
      return;
    }
    handleChange(value);
  }

  function handleChange(value: string) {
    pending.current = null;
    setDraft(null);
    traversing.current = false;
    setOpen(false);
    if (value === REMOVE) {
      arm({ kind: "remove" });
      return;
    }
    if (value.startsWith(SWAP)) {
      // Keyed by occupant: a stale read can momentarily show two rows in one lane.
      const offer = swaps.find((s) => s.entryId === value.slice(SWAP.length));
      if (offer) arm({ kind: "swap", offer });
      return;
    }
    if (value.startsWith(MOVE)) {
      // Another race changes the swimmer's heat, so it arms like Remove.
      // Another race changes the swimmer's heat, so it arms like Remove.
      const rest = value.slice(MOVE.length);
      const cut = rest.lastIndexOf(":");
      const raceId = rest.slice(0, cut);
      const lane = Number(rest.slice(cut + 1));
      const title = races.find((g) => g.raceId === raceId)?.title ?? null;
      arm({ kind: "move", raceId, lane, title });
      return;
    }
    disarm();
    if (value === "") {
      // An entry in no race is already unplaced, so picking none is not a write.
      if (inRace && lane !== null) onPick(null);
      return;
    }
    onPick(Number(value));
  }

  function confirm() {
    if (!armed) return;
    if (Date.now() - armedAt.current < ARMING_LOCKOUT_MS) return;
    const what = armed;
    disarm();
    if (what.kind === "remove") onRemove();
    else if (what.kind === "move") onPickInRace?.(what.raceId, what.lane);
    else onSwap?.(what.offer);
  }

  const grouped = races.length > 1 || races.some((g) => !g.home);

  return (
    <>
      <span className="lane-control">
        {open && (
          <>
            <label className="visually-hidden" htmlFor={`lane-${entryId}`}>
              Lane for {displayName}
            </label>
            <select
              id={`lane-${entryId}`}
              ref={selectRef}
              className="lane-select lane-inline"
              value={draft ?? (lane === null ? "" : String(lane))}
              disabled={disabled}
              onMouseDown={() => (traversing.current = false)}
              onKeyDown={(e) => {
                if (TRAVERSAL_KEYS.includes(e.key)) traversing.current = true;
                else if (e.key === "Enter") commitDraft();
                else if (e.key === "Escape") {
                  traversing.current = false;
                  pending.current = null;
                  setDraft(null);
                }
              }}
              onBlur={commitDraft}
              onChange={(e) => {
                if (!traversing.current) return handleChange(e.target.value);
                pending.current = e.target.value;
                setDraft(e.target.value);
              }}
            >
              <option value="">{inRace ? "No lane" : "Unplaced"}</option>
              {races.map((group) => {
                const key = group.raceId ?? "mint";
                // An empty group is a label over nothing; home keeps it when it has swaps.
                if (group.lanes.length === 0 && !(group.home && swaps.length > 0)) return null;
                const body = (
                  <>
                    {group.lanes.map((n) => (
                      <option key={n} value={group.home ? String(n) : `${MOVE}${group.raceId}:${n}`}>
                        Lane {n}
                      </option>
                    ))}
                    {group.home &&
                      swaps.map((s) => (
                        <option key={`swap-${s.entryId}`} value={`${SWAP}${s.entryId}`}>
                          Lane {s.lane} — swap with {s.displayName}
                        </option>
                      ))}
                  </>
                );
                return grouped && group.title ? (
                  <optgroup key={key} label={group.title}>
                    {body}
                  </optgroup>
                ) : (
                  <Fragment key={key}>{body}</Fragment>
                );
              })}
              {inRace && <option value={REMOVE}>Remove from race…</option>}
            </select>
          </>
        )}
        {item && <StatusBadge status={item.status} />}
      </span>
      {armed && (
        <span
          className="lane-confirm"
          role="alert"
          // Never withdraw a confirmation somebody is reading.
          onFocus={() => clearTimeout(confirmTimer.current)}
          onBlur={(e) => {
            if (!e.currentTarget.contains(e.relatedTarget as Node | null)) armTimer();
          }}
        >
          {armed.kind === "swap" && (
            <span className="row-sub">
              Lane {armed.offer.lane} · {armed.offer.displayName}
            </span>
          )}
          {armed.kind === "move" && (
            <span className="row-sub">{moveText(armed.title, armed.lane)}</span>
          )}
          <button
            type="button"
            className={armed.kind === "remove" ? "danger armed" : "armed-swap"}
            ref={confirmRef}
            disabled={disabled}
            aria-label={
              armed.kind === "swap"
                ? `Confirm swapping ${displayName} with ${armed.offer.displayName}`
                : armed.kind === "move"
                  ? `Confirm moving ${displayName} — ${moveText(armed.title, armed.lane)}`
                  : `Confirm removing ${displayName} from this race`
            }
            onClick={confirm}
          >
            {armed.kind === "remove"
              ? "Confirm remove"
              : armed.kind === "move"
                ? "Confirm move"
                : "Confirm swap"}
          </button>
          <button type="button" onClick={disarm}>
            Cancel
          </button>
        </span>
      )}
    </>
  );
}
