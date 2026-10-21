import type { Race, Schedule } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { requestedSlotId } from "./deckPlacement";
import { raceSlotContext } from "./raceLanes";

export function DeckRaceBlock({
  race,
  laneCountUnknown,
  schedule,
  outboxItems,
  refused,
}: {
  race: Race;
  /** The settings read failed: lanes are a guess, and the deck says so. */
  laneCountUnknown: boolean;
  schedule: Schedule | undefined;
  outboxItems: OutboxItem[] | undefined;
  /** The server refused this heat's materialisation. */
  refused: boolean;
}) {
  const asked = requestedSlotId(outboxItems, race);
  const askedFor = asked ? raceSlotContext(schedule, asked) : null;
  if (!refused && !askedFor && !laneCountUnknown) return null;

  return (
    <div className="deck-race-block">
      {refused && (
        <div className="alert error" role="alert">
          The server refused this heat, so nothing here has been marshalled — see the Outbox.
        </div>
      )}
      {askedFor && (
        <div className="alert notice">
          This race asked for {[askedFor.label, askedFor.time].filter((p) => p).join(" · ")} and is
          not on the program — the block was removed while the race was still queued here. It runs
          exactly as it is; an administrator can put it back on a block.
        </div>
      )}
      {laneCountUnknown && (
        <p className="row-sub">
          The venue's lane count couldn't be read, so this shows a typical pool.
        </p>
      )}
    </div>
  );
}
