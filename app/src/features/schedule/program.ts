import type { CarnivalEvent, Schedule, ScheduleSlot, ScheduleSlotKind } from "../../api/types";
import { divisionChipLabel } from "../events/divisions";
import { eventName } from "../events/eventLabels";
import { timeInputValue } from "./builder";

/** Grouped by definition: one line per event listing its divisions, as the school prints it. */
export interface ProgramEntry {
  key: string;
  name: string;
  divisions: string[];
}

export interface ProgramSlot {
  id: string;
  /** "09:45" or "10:00–11:00"; null when the slot carries no time at all. */
  time: string | null;
  label: string;
  kind: ScheduleSlotKind;
  /** The category a novelty item corresponds to; not printed. */
  adjustmentCategory: string | null;
  entries: ProgramEntry[];
}

export interface ProgramColumn {
  id: string;
  name: string;
  slots: ProgramSlot[];
}

/** An end time only where the organiser set one; the leading zero lines the digits up. */
export function slotTime(slot: Pick<ScheduleSlot, "startTime" | "endTime">): string | null {
  const start = timeInputValue(slot.startTime);
  if (start === "") return null;
  const end = timeInputValue(slot.endTime);
  return end === "" ? start : `${start}–${end}`;
}

/** Server order throughout; an event id the list does not carry is skipped, never a placeholder. */
export function buildProgram(
  schedule: Schedule | undefined,
  events: readonly CarnivalEvent[],
): ProgramColumn[] {
  const byId = new Map(events.map((event) => [event.id, event]));
  return (schedule?.streams ?? []).map((stream) => ({
    id: stream.id,
    name: stream.name,
    slots: stream.slots.map((slot) => ({
      id: slot.id,
      time: slotTime(slot),
      // A slot without a label is refused on write, so this only covers rows
      // that predate the rule; "(untitled)" is at least visibly wrong on paper.
      label: slot.label ?? "(untitled)",
      kind: slot.kind,
      adjustmentCategory: slot.adjustmentCategory,
      entries: groupEntries(slot.eventIds.flatMap((id) => byId.get(id) ?? [])),
    })),
  }));
}

/** Instances → one line per definition, in first-appearance (listed) order. */
function groupEntries(events: readonly CarnivalEvent[]): ProgramEntry[] {
  const grouped = new Map<string, ProgramEntry>();
  for (const event of events) {
    const entry = grouped.get(event.eventDefinitionId);
    if (entry) entry.divisions.push(divisionChipLabel(event));
    else
      grouped.set(event.eventDefinitionId, {
        key: event.eventDefinitionId,
        name: eventName(event),
        divisions: [divisionChipLabel(event)],
      });
  }
  return [...grouped.values()];
}
