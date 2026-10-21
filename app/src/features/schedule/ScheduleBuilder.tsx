import { useState, type FormEvent } from "react";
import { createScheduleStream, updateScheduleStream } from "../../api/resources";
import type { CarnivalEvent, Schedule } from "../../api/types";
import { divisionChipLabel } from "../events/divisions";
import { eventName } from "../events/eventLabels";
import { moveWithin, unscheduledEvents } from "./builder";
import { StreamPanel } from "./StreamPanel";

/** Direct writes, not the outbox: building the program is a desk task. */
export function ScheduleBuilder({
  carnivalId,
  schedule,
  events,
  adjustmentCategories,
  canManage,
  busy,
  onRun,
}: {
  carnivalId: string;
  schedule: Schedule | undefined;
  events: CarnivalEvent[];
  adjustmentCategories: string[];
  canManage: boolean;
  busy: boolean;
  /** Runs one write and reports whether it LANDED — see ScheduleBuilderScreen. */
  onRun: (action: () => Promise<unknown>) => Promise<boolean>;
}) {
  const streams = schedule?.streams ?? [];
  const eventsById = new Map(events.map((event) => [event.id, event]));
  const unscheduled = unscheduledEvents(events);

  function moveStream(streamId: string, direction: -1 | 1) {
    const writes = moveWithin(streams, streamId, direction);
    void onRun(async () => {
      for (const write of writes) {
        const stream = streams.find((s) => s.id === write.id)!;
        // The name rides along because the server's stream PUT requires it —
        // re-ordering must not be able to blank a stream's name.
        await updateScheduleStream(carnivalId, stream.id, {
          name: stream.name,
          sortOrder: write.sortOrder,
        });
      }
    });
  }

  return (
    <>
      <section aria-labelledby="streams-heading">
        <h2 id="streams-heading">Streams</h2>
        <p className="row-sub flush-top">
          Each stream runs its own sequence, and streams run at the same time — one “Pool” for
          swimming, a track beside each field site for athletics. Times are indicative: nothing on
          the day is gated on them.
        </p>
        {streams.length === 0 ? (
          <p>
            No program yet. Add a stream to start building one — every event stays fully enterable
            in the meantime.
          </p>
        ) : (
          <TemplateReapplyNote />
        )}
        {streams.map((stream, index) => (
          <StreamPanel
            key={stream.id}
            carnivalId={carnivalId}
            stream={stream}
            eventsById={eventsById}
            unscheduled={unscheduled}
            adjustmentCategories={adjustmentCategories}
            canManage={canManage}
            busy={busy}
            canMoveUp={index > 0}
            canMoveDown={index < streams.length - 1}
            onMove={(direction) => moveStream(stream.id, direction)}
            onRun={onRun}
          />
        ))}
        {canManage && (
          <AddStreamForm
            busy={busy}
            onAdd={(name) => onRun(() => createScheduleStream(carnivalId, { name }))}
          />
        )}
      </section>

      <UnscheduledPanel events={unscheduled} total={events.length} />
    </>
  );
}

function AddStreamForm({
  busy,
  onAdd,
}: {
  busy: boolean;
  onAdd: (name: string) => Promise<boolean>;
}) {
  const [name, setName] = useState("");

  function submit(e: FormEvent) {
    e.preventDefault();
    // The field empties only once the stream exists; a refused write leaves the
    // typed name there to retry with.
    void onAdd(name.trim()).then((saved) => {
      if (saved) setName("");
    });
  }

  return (
    <form className="entry-form" onSubmit={submit}>
      <label className="field wide flush-bottom">
        <span>New stream</span>
        <input
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="Pool / Track / Long Jump"
        />
      </label>
      <button className="primary" type="submit" disabled={busy || name.trim() === ""}>
        {busy ? "Adding…" : "Add stream"}
      </button>
    </form>
  );
}

/**
 * apply-template matches slots by label, so a seeded block renamed or removed here comes back on
 * the next apply. A note, not a guard: renaming and removing slots is what this screen is for.
 */
function TemplateReapplyNote() {
  return (
    <p className="row-sub">
      If this program was seeded from a template, re-applying that template puts back any slot
      renamed or removed here — it matches slots by their printed label. Entries and results are
      never affected.
    </p>
  );
}

/** Worded as a state, not a fault: an unscheduled event is fully usable. */
function UnscheduledPanel({ events, total }: { events: CarnivalEvent[]; total: number }) {
  return (
    <section className="subsection" aria-labelledby="unscheduled-heading">
      <h2 id="unscheduled-heading">Not on the program ({events.length})</h2>
      {events.length === 0 ? (
        <p className="row-sub">
          {total === 0
            ? "This carnival has no events yet."
            : `All ${total} events are placed in a slot.`}
        </p>
      ) : (
        <>
          <p className="row-sub flush-top">
            These are not printed on the program. They are still open, enterable and resultable —
            place them from a block’s “Add an event” picker above when you want them listed.
          </p>
          <ul className="row-list card">
            {events.map((event) => (
              <li key={event.id}>
                <div className="row-text">
                  <span className="row-title">{eventName(event)}</span>{" "}
                  <span className="row-sub">{divisionChipLabel(event)}</span>
                </div>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}
