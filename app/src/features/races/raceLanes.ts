// The server's race read is authoritative but not current, so every helper folds in this
// device's undelivered placements.

import { ENTRY_ASSIGN_KIND, type EntryAssignPayload } from "../../api/outboxTransport";
import type { Race, Schedule } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { isAbandoned } from "../../sync/outbox";
import { slotTime } from "../schedule/program";

export interface RosterRow {
  entryId: string;
  carnivalEventId: string;
  lane: number | null;
  displayName: string;
  /** The queued placement speaking for this row, when one is still in flight. */
  item: OutboxItem | undefined;
}

/**
 * Accepted is not pending: an accepted item is retained for days, and as an overlay it would
 * override a newer move from another device. A refused one stays so its badge can show.
 */
function isPending(status: OutboxItem["status"]): boolean {
  return status === "queued" || status === "syncing";
}

/** An accepted placement acknowledged no earlier than the race read, so the read is stale. */
export type AcceptedFresh = (item: OutboxItem) => boolean;

const NEVER_FRESH: AcceptedFresh = () => false;

export function acceptedAfter(readAt: number | undefined): AcceptedFresh {
  return (item) => {
    if (item.status !== "accepted") return false;
    // No read, no overlay: a cold or failed read cannot make an old acknowledgement current.
    if (readAt === undefined) return false;
    // Not a strict >: on a tie the ack wins, because dropping it re-offers a mint for a race that
    // already exists.
    return item.syncedAt !== undefined && Date.parse(item.syncedAt) >= readAt;
  };
}

function isRefused(status: OutboxItem["status"]): boolean {
  return status === "rejected" || status === "conflict";
}

/**
 * The newest queued placement per entry (UUIDv7 ids order captures). Carnival-wide, because a
 * move or removal must reach the race the entry is leaving.
 */
export function placementsByEntry(
  items: OutboxItem[] | undefined,
  carnivalId: string,
): Map<string, OutboxItem> {
  const byEntry = new Map<string, OutboxItem>();
  if (!items) return byEntry;
  for (const item of items) {
    if (item.kind !== ENTRY_ASSIGN_KIND) continue;
    if (isAbandoned(item.status)) continue;
    const payload = item.payload as EntryAssignPayload;
    if (payload.carnivalId !== carnivalId) continue;
    const previous = byEntry.get(payload.entryId);
    if (!previous || item.id > previous.id) byEntry.set(payload.entryId, item);
  }
  return byEntry;
}

/** The server roster overlaid with undelivered placements, in the server's own order. */
export function raceRoster(
  race: Race,
  placements: Map<string, OutboxItem>,
  acceptedFresh: AcceptedFresh = NEVER_FRESH,
): RosterRow[] {
  const rows: RosterRow[] = [];
  const fromServer = new Set<string>();

  for (const entry of race.entries) {
    fromServer.add(entry.entryId);
    const item = placements.get(entry.entryId);
    const payload = item ? (item.payload as EntryAssignPayload) : undefined;
    // A refused placement is badged wherever the entry actually is: the source screen is the only
    // one that still has a row to say so.
    const concernsRace =
      payload !== undefined && (payload.raceId === race.id || payload.raceId === null);
    const badge =
      item !== undefined && item.status !== "accepted" && (concernsRace || isRefused(item.status))
        ? item
        : undefined;
    if (item && payload && (isPending(item.status) || acceptedFresh(item))) {
      if (payload.raceId !== race.id) continue;
      rows.push({
        entryId: entry.entryId,
        carnivalEventId: entry.carnivalEventId,
        lane: payload.lane,
        displayName: entry.displayName,
        item: badge,
      });
      continue;
    }
    rows.push({
      entryId: entry.entryId,
      carnivalEventId: entry.carnivalEventId,
      lane: entry.lane,
      displayName: entry.displayName,
      item: badge,
    });
  }

  // Placed into this race but not in the server's list yet — including a
  // late-added swimmer whose entry.create is queued directly behind.
  for (const [entryId, item] of placements) {
    if (fromServer.has(entryId)) continue;
    if (!isPending(item.status) && !acceptedFresh(item)) continue;
    const payload = item.payload as EntryAssignPayload;
    if (payload.raceId !== race.id) continue;
    // Retention strips display from aged accepted items; by then the races read owns the row.
    if (!payload.display) continue;
    rows.push({
      entryId,
      carnivalEventId: payload.carnivalEventId,
      lane: payload.lane,
      displayName: payload.display.entryDisplayName,
      item: item.status === "accepted" ? undefined : item,
    });
  }

  const key = (lane: number | null) => (lane == null ? Number.MAX_SAFE_INTEGER : lane);
  return rows.sort(
    (a, b) => key(a.lane) - key(b.lane) || a.displayName.localeCompare(b.displayName),
  );
}

const TYPICAL_LANE_COUNT = 8;

/** Always offers lanes (a mass start may become laned), reaching past the highest lane in use. */
export function laneOptions(
  rows: readonly Pick<RosterRow, "lane">[],
  laneCount: number | null | undefined,
): number[] {
  const lanes = rows.map((r) => r.lane).filter((l): l is number => l != null);
  const highest = lanes.length > 0 ? Math.max(...lanes) : 0;
  const n =
    laneCount != null && laneCount > 0
      ? Math.max(laneCount, highest)
      : Math.max(TYPICAL_LANE_COUNT, highest + 1);
  return Array.from({ length: n }, (_, i) => i + 1);
}

export function raceSlotContext(
  schedule: Schedule | undefined,
  scheduleSlotId: string | null | undefined,
): { stream: string; label: string | null; time: string | null } | null {
  if (!schedule || !scheduleSlotId) return null;
  for (const stream of schedule.streams)
    for (const slot of stream.slots)
      if (slot.id === scheduleSlotId)
        return { stream: stream.name, label: slot.label, time: slotTime(slot) };
  return null;
}
