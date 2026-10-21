import type { OutboxStatus } from "../db/db";

/** Glyph and word, not hue alone, so meaning survives glare and colour-blind vision. */
const GLYPH: Record<OutboxStatus, string> = {
  queued: "●",
  syncing: "↻",
  accepted: "✓",
  conflict: "!",
  rejected: "✕",
  cancelled: "⟲",
  discarded: "⊘",
};
const LABEL: Record<OutboxStatus, string> = {
  queued: "pending",
  syncing: "syncing",
  accepted: "synced",
  conflict: "conflict",
  rejected: "rejected",
  cancelled: "undone",
  discarded: "discarded",
};

export default function StatusBadge({ status }: { status: OutboxStatus }) {
  return (
    <span className={`sync-badge ${status}`}>
      <span aria-hidden="true" className="sync-glyph">
        {GLYPH[status]}
      </span>
      {LABEL[status]}
    </span>
  );
}
