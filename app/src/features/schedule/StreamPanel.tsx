import { useState, type FormEvent } from "react";
import {
  createScheduleSlot,
  deleteScheduleStream,
  updateScheduleSlot,
  updateScheduleStream,
} from "../../api/resources";
import type { CarnivalEvent, ScheduleSlotKind, ScheduleStream } from "../../api/types";
import { moveWithin, outOfOrderSlotIds, slotRequest, toWireTime } from "./builder";
import { SlotPanel } from "./SlotPanel";

export function StreamPanel({
  carnivalId,
  stream,
  eventsById,
  unscheduled,
  adjustmentCategories,
  canManage,
  busy,
  canMoveUp,
  canMoveDown,
  onMove,
  onRun,
}: {
  carnivalId: string;
  stream: ScheduleStream;
  eventsById: Map<string, CarnivalEvent>;
  unscheduled: CarnivalEvent[];
  adjustmentCategories: string[];
  canManage: boolean;
  busy: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  onMove: (direction: -1 | 1) => void;
  onRun: (action: () => Promise<unknown>) => Promise<boolean>;
}) {
  const [renaming, setRenaming] = useState(false);
  const [name, setName] = useState(stream.name);
  const [confirmingRemove, setConfirmingRemove] = useState(false);

  const placedCount = stream.slots.reduce((n, slot) => n + slot.eventIds.length, 0);
  // Marked, never acted on: a stream that runs backwards is legitimate, so the
  // times stay exactly as typed and the slots stay exactly where they were put.
  const outOfOrder = outOfOrderSlotIds(stream.slots);

  function moveSlot(slotId: string, direction: -1 | 1) {
    const writes = moveWithin(stream.slots, slotId, direction);
    void onRun(async () => {
      for (const write of writes) {
        const slot = stream.slots.find((s) => s.id === write.id)!;
        await updateScheduleSlot(
          carnivalId,
          slot.id,
          slotRequest(slot, { sortOrder: write.sortOrder }),
        );
      }
    });
  }

  return (
    <section className="card" aria-label={`Stream ${stream.name}`}>
      <div className="entry-row">
        <span className="row-main">
          <span className="row-title">{stream.name}</span>
          <span className="row-sub">
            {stream.slots.length} {stream.slots.length === 1 ? "slot" : "slots"} · {placedCount}{" "}
            {placedCount === 1 ? "event" : "events"} placed
          </span>
        </span>
        {canManage && (
          <>
            <button
              type="button"
              disabled={busy || !canMoveUp}
              aria-label={`Move ${stream.name} left`}
              onClick={() => onMove(-1)}
            >
              ↑
            </button>
            <button
              type="button"
              disabled={busy || !canMoveDown}
              aria-label={`Move ${stream.name} right`}
              onClick={() => onMove(1)}
            >
              ↓
            </button>
            <button type="button" disabled={busy} onClick={() => setRenaming((v) => !v)}>
              Rename
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

      {canManage && renaming && (
        <form
          className="entry-form row-edit"
          onSubmit={(e) => {
            e.preventDefault();
            // A refused rename keeps the field open with the typed name in it —
            // closing over it would leave the organiser retyping from memory.
            void onRun(() =>
              updateScheduleStream(carnivalId, stream.id, { name: name.trim() }),
            ).then((saved) => {
              if (saved) setRenaming(false);
            });
          }}
        >
          <label className="field wide flush-bottom">
            <span>Stream name</span>
            <input value={name} onChange={(e) => setName(e.target.value)} autoFocus />
          </label>
          <button className="primary" type="submit" disabled={busy || name.trim() === ""}>
            {busy ? "Saving…" : "Save name"}
          </button>
          <button type="button" disabled={busy} onClick={() => setRenaming(false)}>
            Cancel
          </button>
        </form>
      )}

      {canManage && confirmingRemove && (
        <div className="entry-form row-edit">
          <p className="row-sub flush-top">
            Removes this stream and its {stream.slots.length}{" "}
            {stream.slots.length === 1 ? "slot" : "slots"} from the program.
            {placedCount > 0
              ? ` Its ${placedCount} ${placedCount === 1 ? "event becomes" : "events become"} unscheduled — every entry and result is kept, and they stay fully enterable.`
              : ""}
          </p>
          <button
            type="button"
            className="danger"
            disabled={busy}
            onClick={() =>
              void onRun(() => deleteScheduleStream(carnivalId, stream.id)).then((saved) => {
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

      {stream.slots.length === 0 ? (
        <p className="row-sub">Nothing scheduled in this stream yet.</p>
      ) : (
        <ul className="row-list">
          {stream.slots.map((slot, index) => (
            <SlotPanel
              key={slot.id}
              carnivalId={carnivalId}
              slot={slot}
              eventsById={eventsById}
              unscheduled={unscheduled}
              adjustmentCategories={adjustmentCategories}
              canManage={canManage}
              busy={busy}
              canMoveUp={index > 0}
              canMoveDown={index < stream.slots.length - 1}
              startsBeforeSlotAbove={outOfOrder.has(slot.id)}
              onMove={(direction) => moveSlot(slot.id, direction)}
              onRun={onRun}
            />
          ))}
        </ul>
      )}

      {canManage && (
        <AddSlotForm
          streamName={stream.name}
          busy={busy}
          onAdd={(label, start, end, kind) =>
            onRun(() =>
              createScheduleSlot(carnivalId, stream.id, {
                label,
                startTime: toWireTime(start),
                endTime: toWireTime(end),
                kind,
              }),
            )
          }
        />
      )}
    </section>
  );
}

/** New slots append to the end of the stream; the up/down controls move them. */
function AddSlotForm({
  streamName,
  busy,
  onAdd,
}: {
  streamName: string;
  busy: boolean;
  onAdd: (
    label: string,
    start: string,
    end: string,
    kind: ScheduleSlotKind,
  ) => Promise<boolean>;
}) {
  const [open, setOpen] = useState(false);
  const [label, setLabel] = useState("");
  const [start, setStart] = useState("");
  const [end, setEnd] = useState("");
  const [kind, setKind] = useState<ScheduleSlotKind>("Events");

  function submit(e: FormEvent) {
    e.preventDefault();
    // Clearing the fields says the slot is on the program, so only a landed write clears them.
    void onAdd(label.trim(), start, end, kind).then((saved) => {
      if (!saved) return;
      setLabel("");
      setStart("");
      setEnd("");
    });
  }

  if (!open)
    return (
      <button type="button" disabled={busy} onClick={() => setOpen(true)}>
        Add a slot to {streamName}…
      </button>
    );

  return (
    <form className="entry-form row-edit" onSubmit={submit}>
      <div className="form-grid">
        <label className="field wider">
          <span>Printed label</span>
          <input
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            placeholder="FREESTYLE EVENTS / Buses depart"
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
          <select value={kind} onChange={(e) => setKind(e.target.value as ScheduleSlotKind)}>
            <option value="Events">Events — a block of divisions</option>
            <option value="Item">Item — novelty, logistics, announcement</option>
          </select>
        </label>
      </div>
      <button className="primary" type="submit" disabled={busy || label.trim() === ""}>
        {busy ? "Adding…" : "Add slot"}
      </button>
      <button type="button" disabled={busy} onClick={() => setOpen(false)}>
        Cancel
      </button>
    </form>
  );
}
