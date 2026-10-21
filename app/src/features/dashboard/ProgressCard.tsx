import { href } from "../../router";
import { NOT_STARTED, stillToRecord, type OverlaidProgress } from "./overlay";

/** Enough to see what the afternoon owes without the card running the page. */
const LISTED = 8;

/** Every row links to its deck; the Dashboard captures nothing itself. */
export function ProgressCard({
  carnivalId,
  progress,
  provisional,
}: {
  carnivalId: string;
  progress: OverlaidProgress;
  provisional: boolean;
}) {
  const { totals } = progress;
  const percent =
    totals.expected > 0 ? Math.round((totals.recorded / totals.expected) * 100) : 0;
  const outstanding = stillToRecord(progress.events);
  const listed = outstanding.slice(0, LISTED);
  const more = outstanding.length - listed.length;

  return (
    <section className="card" aria-labelledby="progress-heading">
      <h2 id="progress-heading" className="flush-top">
        Progress {provisional && <span className="badge warn">provisional</span>}
      </h2>
      <p className="dash-big">
        <strong className="mono">{totals.recorded}</strong> of{" "}
        <strong className="mono">{totals.expected}</strong> results recorded
      </p>
      <span className="deck-bar dash-bar" aria-hidden="true">
        <span className="deck-bar-fill" style={{ width: `${percent}%` }} />
      </span>
      <p className="row-sub mono">
        {totals.eventsComplete} of {totals.events} events complete
      </p>

      <h3>Still to record</h3>
      {outstanding.length === 0 ? (
        <p className="row-sub">Every event is recorded.</p>
      ) : (
        <ul className="row-list dash-list">
          {listed.map((event) => (
            <li key={event.carnivalEventId}>
              <a
                className="row-link"
                href={href(`/carnivals/${carnivalId}/events/${event.carnivalEventId}`)}
              >
                <span className="row-main">
                  <span className="row-title">{event.definitionName}</span>
                  <span className="row-sub">
                    {event.divisionLabel}
                    {event.slotLabel ? ` · ${event.slotLabel}` : ""}
                  </span>
                </span>
                {event.status === NOT_STARTED ? (
                  <span className="badge muted">not started</span>
                ) : (
                  <span className="badge warn mono">
                    {event.recorded} of {event.expected}
                  </span>
                )}
              </a>
            </li>
          ))}
        </ul>
      )}
      {more > 0 && (
        <p>
          <a href={href(`/carnivals/${carnivalId}`)}>and {more} more</a>
        </p>
      )}
    </section>
  );
}

