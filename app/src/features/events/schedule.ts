// A suggestion only: nothing here may gate, warn or block.

import type { Schedule } from "../../api/types";

/** Null when the event is off the program or last in its stream: both mean show nothing. */
export function nextScheduledEventId(
  schedule: Schedule | undefined,
  eventId: string,
): string | null {
  if (!schedule) return null;
  for (const stream of schedule.streams) {
    const slotIndex = stream.slots.findIndex((slot) => slot.eventIds.includes(eventId));
    if (slotIndex === -1) continue;

    const slot = stream.slots[slotIndex]!;
    const position = slot.eventIds.indexOf(eventId);
    if (position + 1 < slot.eventIds.length) return slot.eventIds[position + 1]!;

    // The next block in the same stream: streams run concurrently, so an official is never sent to
    // another station's next event.
    for (const later of stream.slots.slice(slotIndex + 1))
      if (later.eventIds.length > 0) return later.eventIds[0]!;
    return null;
  }
  return null;
}
