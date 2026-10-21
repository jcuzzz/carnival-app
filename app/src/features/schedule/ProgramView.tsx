import type { Carnival, CarnivalEvent, Schedule } from "../../api/types";
import { buildProgram, type ProgramSlot } from "./program";

export function ProgramView({
  carnival,
  schedule,
  events,
}: {
  carnival: Carnival;
  schedule: Schedule | undefined;
  events: CarnivalEvent[];
}) {
  const columns = buildProgram(schedule, events);

  return (
    <div className="program">
      <header className="program-head">
        <h1>{carnival.name}</h1>
        <p className="program-meta">
          {carnival.date}
          {carnival.venue ? ` · ${carnival.venue}` : ""}
        </p>
      </header>

      {/* Part of the program, not chrome: it survives the print stylesheet, because paper carries
          no status. */}
      {carnival.status === "Draft" && (
        <p className="program-draft">
          DRAFT — this carnival has not opened yet. Times, blocks and divisions may still change.
        </p>
      )}

      {columns.length === 0 ? (
        <p>No program has been built for this carnival yet.</p>
      ) : (
        <div className="program-grid">
          {columns.map((column) => (
            <section className="program-stream" key={column.id} aria-label={column.name}>
              <h2 className="program-stream-name">{column.name}</h2>
              {column.slots.length === 0 ? (
                <p className="program-empty">Nothing scheduled.</p>
              ) : (
                column.slots.map((slot) => <Slot key={slot.id} slot={slot} />)
              )}
            </section>
          ))}
        </div>
      )}

      <p className="program-footnote">
        Times are indicative — events may run out of order.
      </p>
    </div>
  );
}

function Slot({ slot }: { slot: ProgramSlot }) {
  return (
    <div className={`program-slot ${slot.kind === "Item" ? "item" : ""}`}>
      <div className="program-slot-head">
        {slot.time && <span className="program-time mono">{slot.time}</span>}
        <span className="program-label">{slot.label}</span>
      </div>
      {slot.entries.map((entry) => (
        <div className="program-entry" key={entry.key}>
          <span className="program-entry-name">{entry.name}</span>{" "}
          {/* A semicolon, not the labels' "·": "12 · Boys · 12 · Girls" would not show where a
              division ends. */}
          <span className="program-divisions">{entry.divisions.join("; ")}</span>
        </div>
      ))}
      {/* Screen only: organiser bookkeeping the program need not print. */}
      {slot.adjustmentCategory && (
        <div className="program-entry no-print">
          <span className="program-divisions">novelty points: {slot.adjustmentCategory}</span>
        </div>
      )}
    </div>
  );
}
