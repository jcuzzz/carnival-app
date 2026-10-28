import Dexie, { type Table } from "dexie";
import { narrowAck, type OutboxAck } from "../sync/ackShape";

export type OutboxStatus =
  | "queued"
  | "syncing"
  | "accepted"
  | "rejected"
  | "conflict"
  // Withdrawn in the undo window before the worker picked it up; the server never saw it.
  | "cancelled"
  // Parked, then let go by a person; the server did see it and refused it.
  | "discarded";

export interface OutboxItem {
  /** Client UUIDv7, the id the write mints; the Idempotency-Key for every kind but corrections. */
  id: string;
  kind: string;
  payload: unknown;
  /** Captured at entry, never at sync: a device handover never re-attributes work. */
  actorSubject: string;
  actorDisplayName?: string;
  capturedAt: string;
  status: OutboxStatus;
  attempts: number;
  lastError?: string;
  baseVersion?: number;
  /** Narrowed to what this kind's readers need. */
  serverResponse?: OutboxAck;
  syncedAt?: string;
  /** Tells a row an app killed mid-request left in `syncing` from one in flight. */
  syncingAt?: string;
  /** Not the capturer. */
  resolvedBy?: string;
  resolvedAt?: string;
  /** A different official who edited before requeueing; recorded beside `actorSubject`. */
  editedBySubject?: string;
  editedByDisplayName?: string;
}

export interface SyncMeta {
  key: string;
  value: unknown;
}

/** Not authoritative: the server re-checks eligibility on create. Cleared on sign-out. */
export interface EventRosterRow {
  carnivalEventId: string;
  students: unknown; // EligibleStudent[] as JSON, avoiding a db<->api dependency
  fetchedAt: string;
  /** Absent on older rows, which reads as "unknown" — exactly what they are. */
  truncated?: boolean;
  limit?: number;
}

/** Device-level settings, never student data: the last-seen branding for an offline cold start. */
export interface AppSetting {
  key: string;
  value: unknown;
}

// Unindexed optional fields (syncingAt, editedBy*, roster truncated) need no schema version bump.
export class CarnivalAppDb extends Dexie {
  outbox!: Table<OutboxItem, string>;
  syncMeta!: Table<SyncMeta, string>;
  eventRosters!: Table<EventRosterRow, string>;
  appSettings!: Table<AppSetting, string>;

  constructor() {
    super("carnival-app");
    // An upgrade never deletes, re-statuses or re-payloads an outbox row: a queued capture lost
    // in a schema upgrade would be the worst bug this product could ship.
    this.version(1).stores({
      outbox: "id, status, capturedAt",
      syncMeta: "key",
      carnivalConfig: "id",
    });
    this.version(2).stores({
      outbox: "id, status, capturedAt",
      syncMeta: "key",
      carnivalConfig: "id",
      eventRosters: "carnivalEventId",
    });
    this.version(3).stores({
      outbox: "id, status, capturedAt, [status+syncedAt]",
      syncMeta: "key",
      carnivalConfig: null,
      eventRosters: "carnivalEventId",
    });
    this.version(4).stores({
      outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
      syncMeta: "key",
      eventRosters: "carnivalEventId",
    });
    this.version(5).stores({
      outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
      syncMeta: "key",
      eventRosters: "carnivalEventId, fetchedAt",
    });
    // Sheds a per-student category an earlier build cached: a parked item is kept indefinitely,
    // so it never clears itself. `display` is never sent, so no captured intent changes.
    this.version(6)
      .stores({
        outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
        syncMeta: "key",
        eventRosters: "carnivalEventId, fetchedAt",
      })
      .upgrade(async (tx) => {
        await tx
          .table<EventRosterRow>("eventRosters")
          .toCollection()
          .modify((row) => {
            if (!Array.isArray(row.students)) return;
            row.students = row.students.map(withoutCategory);
          });
        await tx
          .table<OutboxItem>("outbox")
          .toCollection()
          .modify((item) => {
            if (item.kind !== ENTRY_CREATE_KIND) return;
            const payload = item.payload as { display?: unknown } | null;
            if (!payload || typeof payload !== "object" || !payload.display) return;
            item.payload = { ...payload, display: withoutCategory(payload.display) };
          });
      });
    this.version(7).stores({
      outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
      syncMeta: "key",
      eventRosters: "carnivalEventId, fetchedAt",
      appSettings: "key",
    });
    // Converges acks already kept: a `conflict` row is kept indefinitely.
    this.version(8)
      .stores({
        outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
        syncMeta: "key",
        eventRosters: "carnivalEventId, fetchedAt",
        appSettings: "key",
      })
      .upgrade(async (tx) => {
        await tx
          .table<OutboxItem>("outbox")
          .toCollection()
          .modify((item) => {
            if (item.serverResponse === undefined) return;
            // No HTTP status is stored; the row's own status says ack or refusal.
            const status = item.status === "accepted" ? 200 : 400;
            item.serverResponse = narrowAck(item.kind, status, item.serverResponse);
          });
      });
  }
}

// Literal, not the outboxTransport constant: db.ts is that module's dependency.
const ENTRY_CREATE_KIND = "entry.create";

function withoutCategory(value: unknown): unknown {
  if (value === null || typeof value !== "object") return value;
  const copy = { ...(value as Record<string, unknown>) };
  delete copy.category;
  return copy;
}

export const db = new CarnivalAppDb();
