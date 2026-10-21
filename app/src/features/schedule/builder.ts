import type { CarnivalEvent, ScheduleSlot, SlotRequest } from "../../api/types";

export interface Ordered {
  id: string;
  sortOrder: number;
}

/**
 * Order writes that move one item up (-1) or down (+1). Stored orders may have drifted from
 * 0..n-1, so new orders are list positions and items already in place are skipped.
 */
export function moveWithin<T extends Ordered>(
  items: readonly T[],
  id: string,
  direction: -1 | 1,
): Ordered[] {
  const index = items.findIndex((item) => item.id === id);
  const target = index + direction;
  if (index === -1 || target < 0 || target >= items.length) return [];

  const moved = [...items];
  moved[index] = items[target]!;
  moved[target] = items[index]!;
  return moved.flatMap((item, position) =>
    item.sortOrder === position ? [] : [{ id: item.id, sortOrder: position }],
  );
}

/**
 * Carries the stored slotOrder, never the screen position: a block can hold 2, 3, 4 after an
 * unschedule, and positions would skip the write that repairs it. A null (-1) is always rewritten.
 */
export function slotEventOrder(events: readonly CarnivalEvent[]): Ordered[] {
  return events.map((event) => ({ id: event.id, sortOrder: event.slotOrder ?? -1 }));
}

export function unscheduledEvents(events: readonly CarnivalEvent[]): CarnivalEvent[] {
  return events.filter((event) => event.scheduleSlotId === null);
}

/**
 * A typo net, never a rule: flags a slot earlier than the previous timed slot. Untimed slots are
 * skipped and equal times (concurrent blocks) do not flag.
 */
export function outOfOrderSlotIds(
  slots: readonly Pick<ScheduleSlot, "id" | "startTime">[],
): Set<string> {
  const flagged = new Set<string>();
  let previous = "";
  for (const slot of slots) {
    // "09:45" vs "14:30": zero-padded 24-hour clock times compare as strings.
    const start = timeInputValue(slot.startTime);
    if (start === "") continue;
    if (previous !== "" && start < previous) flagged.add(slot.id);
    previous = start;
  }
  return flagged;
}

/** Slot times as `<input type="time">` wants them: "09:45:00" → "09:45". */
export function timeInputValue(time: string | null | undefined): string {
  return time ? time.slice(0, 5) : "";
}

/**
 * With the seconds the server's `TimeOnly` writes back, so an untouched slot round-trips
 * identically and the audit trail records an edit only where one happened.
 */
export function toWireTime(value: string): string | null {
  const trimmed = value.trim();
  return trimmed === "" ? null : `${trimmed.slice(0, 5)}:00`;
}

/**
 * The server's slot PUT replaces rather than patches: `{startTime}` alone would clear the label,
 * so every write from this screen goes through here.
 */
export function slotRequest(slot: ScheduleSlot, changes: Partial<SlotRequest> = {}): SlotRequest {
  return {
    startTime: slot.startTime,
    endTime: slot.endTime,
    kind: slot.kind,
    label: slot.label,
    adjustmentCategory: slot.adjustmentCategory,
    sortOrder: slot.sortOrder,
    ...changes,
  };
}
