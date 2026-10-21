import { useState, type FormEvent } from "react";
import {
  deleteScheduleSlot,
  placeScheduleEvent,
  removeScheduleEvent,
  updateScheduleSlot,
} from "../../api/resources";
import type {
  CarnivalEvent,
  ScheduleSlot,
  ScheduleSlotKind,
  SlotRequest,
} from "../../api/types";
import { divisionChipLabel } from "../events/divisions";
import { eventName } from "../events/eventLabels";
import { moveWithin, slotEventOrder, slotRequest, timeInputValue, toWireTime } from "./builder";
import { slotTime } from "./program";

export function SlotPanel({
  carnivalId,
  slot,
  eventsById,
  unscheduled,
  adjustmentCategories,
  canManage,
  busy,
  canMoveUp,
  canMoveDown,
  startsBeforeSlotAbove,
  onMove,
  onRun,
}: {
  carnivalId: string;
  slot: ScheduleSlot;
  eventsById: Map<string, CarnivalEvent>;
  /** Events on no slot at all — what the picker can place here. */
  unscheduled: CarnivalEvent[];
  adjustmentCategories: string[];
  canManage: boolean;
  busy: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  /** Starts earlier than the timed slot above — a hint only. */
  startsBeforeSlotAbove?: boolean;
  onMove: (direction: -1 | 1) => void;
  onRun: (action: () => Promise<unknown>) => Promise<boolean>;
}) {
  const [editing, setEditing] = useState(false);
  const [confirmingRemove, setConfirmingRemove] = useState(false);
  const [pick, setPick] = useState("");

  // An id the cache cannot resolve yet is dropped here, so the move and its buttons reason over the
  // same rows; otherwise a tap at the edge does nothing visible.
  const listed = slot.eventIds.flatMap((id) => eventsById.get(id) ?? []);
  const isBlock = slot.kind === "Events";

  function moveEvent(eventId: string, direction: -1 | 1) {
    const writes = moveWithin(slotEventOrder(listed), eventId, direction);
    void onRun(async () => {
      for (const write of writes)
        await placeScheduleEvent(carnivalId, slot.id, {
          eventId: write.id,
          slotOrder: write.sortOrder,
        });
    });
  }

  return (
    <li>
      <div className="entry-row">
        <span className="row-main">
          <span className="row-title">
            <span className="mono">{slotTime(slot) ?? "—"}</span> {slot.label}
          </span>
          <span className="row-sub">
            {isBlock
              ? `${listed.length} ${listed.length === 1 ? "event" : "events"}`
              : "Non-competition item"}
            {slot.adjustmentCategory ? ` · novelty points: ${slot.adjustmentCategory}` : ""}
            {/* A hint only: nothing is disabled or re-sorted, but 09:45 typed as 19:45 is
                otherwise invisible until the program is printed. */}
            {startsBeforeSlotAbove && (
              <>
                {" "}
                <span className="badge warn">starts before the slot above</span>
              </>
            )}
          </span>
        </span>
        {canManage && (
          <>
            <button
              type="button"
              disabled={busy || !canMoveUp}
              aria-label={`Move ${slot.label} earlier`}
              onClick={() => onMove(-1)}
            >
              ↑
            </button>
            <button
              type="button"
              disabled={busy || !canMoveDown}
              aria-label={`Move ${slot.label} later`}
              onClick={() => onMove(1)}
            >
              ↓
            </button>
            <button type="button" disabled={busy} onClick={() => setEditing((v) => !v)}>
              {editing ? "Close" : "Edit"}
            </button>
            {!confirmingRemove && (
              <button
                type="button"
                className="danger"
                disabled={busy}
                onClick={() => setConfirmingRemove(true)}
              >
                Remove
              </button>
            )}
          </>
        )}
      </div>

      {/* The server unschedules the slot's events rather than touching them. */}
      {canManage && confirmingRemove && (
        <div className="entry-form row-edit">
          <p className="row-sub flush-top">
            Removes this line from the program.
            {listed.length > 0
              ? ` Its ${listed.length} ${listed.length === 1 ? "event" : "events"} become unscheduled — they keep every entry and result, and stay fully enterable.`
              : ""}
          </p>
          <button
            type="button"
            className="danger"
            disabled={busy}
            onClick={() =>
              void onRun(() => deleteScheduleSlot(carnivalId, slot.id)).then((saved) => {
                // Only a landed write closes the confirmation, or a failed delete reads as removed.
                if (saved) setConfirmingRemove(false);
              })
            }
          >
            {busy ? "Removing…" : "Confirm remove"}
          </button>
          <button type="button" disabled={busy} onClick={() => setConfirmingRemove(false)}>
            Cancel
          </button>
        </div>
      )}

      {canManage && editing && (
        <SlotForm
          // Keyed on the stored editable fields: a refetch re-seeds the form, but adding an event
          // to the block does not discard a half-typed label.
          key={`${slot.label}|${slot.startTime}|${slot.endTime}|${slot.kind}|${slot.adjustmentCategory}`}
          slot={slot}
          hasEvents={listed.length > 0}
          adjustmentCategories={adjustmentCategories}
          busy={busy}
          // Closes only when the write landed: closing on a 409 would discard what was retyped.
          onSave={(request) =>
            void onRun(() => updateScheduleSlot(carnivalId, slot.id, request)).then((saved) => {
              if (saved) setEditing(false);
            })
          }
        />
      )}

      {isBlock && listed.length > 0 && (
        <ul className="row-list slot-events">
          {listed.map((event, index) => (
            <li key={event.id}>
              <div className="entry-row">
                <span className="row-main">
                  <span className="row-title">{eventName(event)}</span>
                  <span className="row-sub">{divisionChipLabel(event)}</span>
                </span>
                {canManage && (
                  <>
                    <button
                      type="button"
                      disabled={busy || index === 0}
                      aria-label={`Move ${eventName(event)} ${divisionChipLabel(event)} earlier in this block`}
                      onClick={() => moveEvent(event.id, -1)}
                    >
                      ↑
                    </button>
                    <button
                      type="button"
                      disabled={busy || index === listed.length - 1}
                      aria-label={`Move ${eventName(event)} ${divisionChipLabel(event)} later in this block`}
                      onClick={() => moveEvent(event.id, 1)}
                    >
                      ↓
                    </button>
                    {/* Not destructive: the event leaves the program and keeps
                        everything else, so it takes no confirmation. */}
                    <button
                      type="button"
                      disabled={busy}
                      onClick={() =>
                        void onRun(() => removeScheduleEvent(carnivalId, slot.id, event.id))
                      }
                    >
                      Unschedule
                    </button>
                  </>
                )}
              </div>
            </li>
          ))}
        </ul>
      )}

      {canManage && isBlock && (
        <form
          className="entry-form row-edit"
          onSubmit={(e) => {
            e.preventDefault();
            if (pick === "") return;
            // Keep the chosen event selected if the placement failed — it is
            // still what the organiser wants placed.
            void onRun(() => placeScheduleEvent(carnivalId, slot.id, { eventId: pick })).then(
              (saved) => {
                if (saved) setPick("");
              },
            );
          }}
        >
          <label className="field wide flush-bottom">
            <span>Add an event to this block</span>
            <select value={pick} onChange={(e) => setPick(e.target.value)} disabled={busy}>
              <option value="">
                {unscheduled.length === 0
                  ? "— every event is on the program —"
                  : `— ${unscheduled.length} unscheduled —`}
              </option>
              {unscheduled.map((event) => (
                <option key={event.id} value={event.id}>
                  {eventName(event)} — {divisionChipLabel(event)}
                </option>
              ))}
            </select>
          </label>
          <button type="submit" disabled={busy || pick === ""}>
            Add
          </button>
        </form>
      )}
    </li>
  );
}

function SlotForm({
  slot,
  hasEvents,
  adjustmentCategories,
  busy,
  onSave,
}: {
  slot: ScheduleSlot;
  hasEvents: boolean;
  adjustmentCategories: string[];
  busy: boolean;
  onSave: (request: SlotRequest) => void;
}) {
  const [label, setLabel] = useState(slot.label ?? "");
  const [start, setStart] = useState(timeInputValue(slot.startTime));
  const [end, setEnd] = useState(timeInputValue(slot.endTime));
  const [kind, setKind] = useState<ScheduleSlotKind>(slot.kind);
  const [category, setCategory] = useState(slot.adjustmentCategory ?? "");

  function submit(e: FormEvent) {
    e.preventDefault();
    onSave(
      slotRequest(slot, {
        label: label.trim(),
        startTime: toWireTime(start),
        endTime: toWireTime(end),
        kind,
        // Only an Item carries a category; switching kind drops it rather than leave an invisible
        // value the server would still validate.
        adjustmentCategory: kind === "Item" && category !== "" ? category : null,
      }),
    );
  }

  return (
    <form className="entry-form row-edit" onSubmit={submit}>
      <div className="form-grid">
        <label className="field wider">
          <span>Printed label</span>
          <input
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            placeholder="FREESTYLE EVENTS"
            autoFocus
          />
        </label>
        <label className="field">
          <span>Start</span>
          <input type="time" value={start} onChange={(e) => setStart(e.target.value)} />
        </label>
        <label className="field">
          <span>End (optional)</span>
          <input type="time" value={end} onChange={(e) => setEnd(e.target.value)} />
        </label>
        <label className="field">
          <span>Kind</span>
          {/* A block that lists events cannot become an item — the server
              refuses it rather than strand them, so the control says so. */}
          <select
            value={kind}
            onChange={(e) => setKind(e.target.value as ScheduleSlotKind)}
            disabled={hasEvents}
          >
            <option value="Events">Events — a block of divisions</option>
            <option value="Item">Item — novelty, logistics, announcement</option>
          </select>
        </label>
        {kind === "Item" && (
          <label className="field wide">
            <span>Novelty points category (optional, ADR-0005)</span>
            <select value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">— none —</option>
              {adjustmentCategories.map((c) => (
                <option key={c} value={c}>
                  {c}
                </option>
              ))}
            </select>
          </label>
        )}
      </div>
      <button className="primary" type="submit" disabled={busy || label.trim() === ""}>
        {busy ? "Saving…" : "Save slot"}
      </button>
    </form>
  );
}
