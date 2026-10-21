import type { CarnivalEvent } from "../../api/types";
import { href } from "../../router";
import { EventClassification } from "./EventClassification";
import { divisionLabel } from "./eventLabels";
import { progressPercent, progressText, type QueuedProgress } from "./progress";
import { assignedEvents, buildEventTiles, type EventTile } from "./tiles";

export function EventTiles({
  events,
  subject,
  queued,
}: {
  events: CarnivalEvent[];
  subject: string | null;
  /** Undelivered captures from the device outbox. */
  queued?: Map<string, QueuedProgress>;
}) {
  const mine = buildEventTiles(assignedEvents(events, subject), queued);
  const all = buildEventTiles(events, queued);

  return (
    <>
      {mine.length > 0 && (
        <section aria-labelledby="my-events-heading">
          <h2 id="my-events-heading">My events</h2>
          <p className="row-sub flush-top">
            Events you are assigned to run, counted over your divisions only. Every other event is
            below.
          </p>
          <TileGrid tiles={mine} assignedOnly />
        </section>
      )}
      <section aria-labelledby="all-events-heading">
        <h2 id="all-events-heading">{mine.length > 0 ? "All events" : "Events"}</h2>
        <TileGrid tiles={all} />
      </section>
    </>
  );
}

function TileGrid({
  tiles,
  assignedOnly = false,
}: {
  tiles: EventTile[];
  /** These tiles count the signed-in official's assigned instances only. */
  assignedOnly?: boolean;
}) {
  return (
    <ul className="tile-grid">
      {tiles.map((tile) => (
        <li key={tile.key}>
          <Tile tile={tile} assignedOnly={assignedOnly} />
        </li>
      ))}
    </ul>
  );
}

function Tile({ tile, assignedOnly }: { tile: EventTile; assignedOnly: boolean }) {
  const { target, progress } = tile;
  return (
    <a className="tile" href={href(`/carnivals/${target.carnivalId}/events/${target.id}`)}>
      <span className="tile-head">
        <span className="tile-name">{tile.name}</span>
        <EventClassification event={tile.classification} />
      </span>
      <span className="row-sub">
        {tile.events.length === 1 ? divisionLabel(target) : `${tile.events.length} divisions`}
        {tile.discipline && tile.discipline !== tile.name ? ` · ${tile.discipline}` : ""}
      </span>
      <span className="tile-progress">
        <span className="tile-bar" aria-hidden="true">
          <span className="tile-bar-fill" style={{ width: `${progressPercent(progress)}%` }} />
        </span>
        <span className="tile-count mono">{progressText(progress)}</span>
        {/* Two fractions of one definition can share a screen, so the tile says which this is. */}
        {assignedOnly && <span className="tile-count">your divisions</span>}
      </span>
      <span className="tile-status">
        {tile.openCount > 0 && <span className="badge ok">{tile.openCount} open</span>}
        {tile.completeCount > 0 && (
          <span className="badge muted">{tile.completeCount} complete</span>
        )}
        {/* An event with no entries at all is the end-of-day straggler hunt's
            first question, so it says so rather than showing a bare "0/0". */}
        {progress.entryCount === 0 && <span className="badge muted">no entries yet</span>}
      </span>
    </a>
  );
}
