import type { ResultDto } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { Freshness } from "../../shell/Freshness";

/** Counts the whole division, whichever heat is on screen. */
export function DeckProgress({
  total,
  resultsByEntry,
  outboxByEntry,
  attemptsByEntry,
  jumpsByEntry,
  resultsUnavailable,
  updatedAt,
  isRefetching,
}: {
  total: number;
  resultsByEntry: Map<string, ResultDto>;
  outboxByEntry: Map<string, OutboxItem>;
  /** This device's undelivered attempt cells, by entry. */
  attemptsByEntry?: ReadonlyMap<string, ReadonlyMap<number, OutboxItem>>;
  /** This device's undelivered card marks, by entry then height. */
  jumpsByEntry?: ReadonlyMap<string, ReadonlyMap<string, OutboxItem[]>>;
  resultsUnavailable: boolean;
  updatedAt?: number;
  isRefetching?: boolean;
}) {
  // Counted per ATHLETE, not per write: a card is many marks for one result, and
  // a row with a refusal on it needs a person, not a wait.
  const pendingRows = new Set<string>();
  const attentionRows = new Set<string>();
  const recorded = new Set(resultsByEntry.keys());
  const note = (entryId: string, item: OutboxItem) => {
    if (item.status === "queued" || item.status === "syncing") pendingRows.add(entryId);
    else if (item.status === "conflict" || item.status === "rejected") attentionRows.add(entryId);
  };
  for (const [entryId, item] of outboxByEntry) {
    if (item.status === "accepted") recorded.add(entryId);
    else note(entryId, item);
  }
  // Marks never move "recorded" (the result is derived), but a queued or parked one is work this
  // deck still holds.
  for (const [entryId, cells] of attemptsByEntry ?? []) {
    for (const item of cells.values()) note(entryId, item);
  }
  for (const [entryId, heights] of jumpsByEntry ?? []) {
    for (const marks of heights.values()) for (const item of marks) note(entryId, item);
  }
  const attention = attentionRows.size;
  const pending = [...pendingRows].filter((id) => !attentionRows.has(id)).length;
  const tone = attention > 0 || resultsUnavailable ? "trouble" : pending > 0 ? "pending" : "ok";
  // Nothing to claim about progress while the results read is down: the bar
  // would draw a completeness this device cannot see.
  const percent =
    resultsUnavailable || total === 0 ? 0 : Math.round((recorded.size / total) * 100);
  return (
    <div className={`deck-progress ${tone}`}>
      {/* The freshness stamp is its own live region and ticks every poll; inside
          the status it would re-announce the whole count each time. */}
      <span role="status">
        <strong className="mono">{resultsUnavailable ? "?" : recorded.size}</strong> of{" "}
        <strong className="mono">{total}</strong> recorded
        {pending > 0 && <span> · {pending} pending</span>}
        {attention > 0 && (
          <span className="attn"> · {attention} need{attention === 1 ? "s" : ""} attention</span>
        )}
      </span>
      <span className="deck-fresh">
        <Freshness updatedAt={updatedAt} isRefetching={isRefetching} />
      </span>
      <span className="deck-bar" aria-hidden="true">
        <span className="deck-bar-fill" style={{ width: `${percent}%` }} />
      </span>
    </div>
  );
}
