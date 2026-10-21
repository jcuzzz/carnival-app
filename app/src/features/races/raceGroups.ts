import type { CarnivalEvent, Race, RaceEntry, Schedule } from "../../api/types";
import { divisionChipLabel } from "../events/divisions";
import { eventName } from "../events/eventLabels";
import { slotTime } from "../schedule/program";

export const OFF_PROGRAM_KEY = "off-program";

export interface RaceGroup {
  key: string;
  title: string;
  /** "Pool · 09:45", or the sentence explaining an off-program group. */
  subtitle: string | null;
  races: Race[];
}

export interface SlotOption {
  id: string;
  /** "FREESTYLE EVENTS" — what the block is called on the printed program. */
  label: string;
  streamName: string;
  time: string | null;
}

/** Races by program block in program order, off-program last; empty groups are dropped. */
export function groupRacesBySlot(
  races: readonly Race[],
  schedule: Schedule | undefined,
): RaceGroup[] {
  const groups = new Map<string, RaceGroup>();
  for (const stream of schedule?.streams ?? [])
    for (const slot of stream.slots)
      groups.set(slot.id, {
        key: slot.id,
        // Only rows predating the label rule lack one; same fallback as the printed program.
        title: slot.label ?? "(untitled)",
        subtitle: [stream.name, slotTime(slot)].filter((p) => p).join(" · "),
        races: [],
      });

  const offProgram: RaceGroup = {
    key: OFF_PROGRAM_KEY,
    ...offProgramNaming(schedule),
    races: [],
  };
  for (const race of races) {
    const group = race.scheduleSlotId ? groups.get(race.scheduleSlotId) : undefined;
    (group ?? offProgram).races.push(race);
  }

  const ordered = [...groups.values(), offProgram].filter((g) => g.races.length > 0);
  for (const group of ordered)
    group.races.sort((a, b) => a.sortOrder - b.sortOrder || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  return ordered;
}

/** With no program cached, calling every race "not on the program" would be false. */
function offProgramNaming(schedule: Schedule | undefined): { title: string; subtitle: string } {
  if (!schedule)
    return {
      title: "Races",
      subtitle: "The program has not loaded on this device, so races are not grouped by block.",
    };
  return {
    title: "Not on the program",
    subtitle: "Created on the day, or in a program block that has since been removed.",
  };
}

/** Its label, else its one-based position; a negative order is queued-only and gets no number. */
export function raceTitle(race: Pick<Race, "label" | "sortOrder">): string {
  const label = race.label?.trim();
  if (label) return label;
  return race.sortOrder < 0 ? "New race" : `Race ${race.sortOrder + 1}`;
}

export function laneSummary(entries: readonly RaceEntry[]): string {
  const lanes = entries.map((e) => e.lane).filter((l): l is number => l != null);
  if (lanes.length === 0) return "no lanes";
  const low = Math.min(...lanes);
  const high = Math.max(...lanes);
  return low === high ? `lane ${low}` : `lanes ${low}–${high}`;
}

/** A race may hold heats of several events, so every name is listed; unknown ids are skipped. */
export function raceSummary(race: Race, events: readonly CarnivalEvent[] | undefined): string {
  const parts: string[] = [];
  const count = race.entries.length;
  parts.push(count === 0 ? "No entries yet" : count === 1 ? "1 entry" : `${count} entries`);
  if (count > 0) parts.push(laneSummary(race.entries));

  const byId = new Map((events ?? []).map((e) => [e.id, e]));
  const named: string[] = [];
  for (const entry of race.entries) {
    const event = byId.get(entry.carnivalEventId);
    if (!event) continue;
    const name = `${eventName(event)} · ${divisionChipLabel(event)}`;
    if (!named.includes(name)) named.push(name);
  }
  if (named.length > 0) parts.push(named.join(", "));
  return parts.join(" · ");
}

/** Advisory: two offline devices may compute the same order, and nothing reads it as identity. */
export function nextSortOrder(races: readonly Race[], scheduleSlotId: string | null): number {
  const inSlot = races.filter((r) => (r.scheduleSlotId ?? null) === scheduleSlotId);
  return inSlot.reduce((max, r) => Math.max(max, r.sortOrder), 0) + 1;
}

export function slotOptions(schedule: Schedule | undefined): SlotOption[] {
  const options: SlotOption[] = [];
  for (const stream of schedule?.streams ?? [])
    for (const slot of stream.slots) {
      // The server refuses a race under a non-Events slot.
      if (slot.kind !== "Events") continue;
      options.push({
        id: slot.id,
        label: slot.label ?? "(untitled)",
        streamName: stream.name,
        time: slotTime(slot),
      });
    }
  return options;
}
