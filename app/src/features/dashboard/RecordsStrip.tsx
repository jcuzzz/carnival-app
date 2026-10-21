import { href } from "../../router";
import { HouseMark } from "../houses/HouseMark";
import type { RecordToday } from "../../api/types";

/** Candidates until finalisation mints them (a correction can still undo one); after it, the
 * carnival's own records. */
export function RecordsStrip({
  carnivalId,
  records,
  provisional,
}: {
  carnivalId: string;
  records: RecordToday[];
  provisional: boolean;
}) {
  return (
    <section className="card" aria-labelledby="records-today-heading">
      <h2 id="records-today-heading" className="flush-top">
        Records today
      </h2>
      {records.length === 0 ? (
        <p className="row-sub">
          {provisional ? "No record candidates yet" : "No records set at this carnival"}
        </p>
      ) : (
        <ul className="records-strip">
          {records.map((record) => (
            <li key={record.entryId} className="record-candidate">
              <a
                className="record-head"
                href={href(`/carnivals/${carnivalId}/events/${record.carnivalEventId}`)}
              >
                <strong className="record-value mono">{record.value}</strong>
                {provisional ? (
                  <span className="badge warn">
                    candidate — {record.kind === "equalled" ? "equalled" : "new"}
                  </span>
                ) : (
                  <span className="badge ok">
                    {record.kind === "equalled" ? "record equalled" : "new record"}
                  </span>
                )}
              </a>
              <span className="row-sub">
                {record.definitionName} · {record.divisionLabel}
              </span>
              <span>
                {record.displayName} · <HouseMark code={record.houseCode} />
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
