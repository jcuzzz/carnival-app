import { useLiveQuery } from "dexie-react-hooks";
import { db } from "../db/db";
import { useQueueStats } from "../sync/useOutboxStatus";
import { STORAGE_PERSIST_KEY, type StoragePersistState } from "../sync/storagePersistence";
import { href } from "../router";

const STORAGE_WARNING =
  "This device has not granted durable storage — the browser may clear queued work if it is left unsynced (iOS: after about 7 days or under storage pressure). Sync before the end of the day.";

// A denied storage grant means queued results are evictable: shown, but quieter than queue trouble.
export default function OutboxIndicator() {
  const stats = useQueueStats();
  const storage = useLiveQuery(() => db.syncMeta.get(STORAGE_PERSIST_KEY), []);
  const persistState = storage?.value as StoragePersistState | undefined;
  const storageAtRisk = persistState !== undefined && !persistState.persisted;

  if (!stats) {
    return (
      <a className="outbox-chip" href={href("/outbox")} aria-label="Outbox">
        <span className="dot" aria-hidden="true" />
        Outbox …
      </a>
    );
  }

  const pending = stats.byStatus.queued + stats.byStatus.syncing;
  const trouble = stats.byStatus.conflict + stats.byStatus.rejected;
  const discarded = stats.byStatus.discarded;
  const tone = stats.paused || trouble > 0 ? "trouble" : pending > 0 ? "pending" : "";

  const summary = stats.paused
    ? `paused · ${pending} waiting`
    : trouble > 0
      ? `${trouble} need attention · ${pending} pending`
      : pending > 0
        ? `${pending} pending`
        : discarded > 0
          ? `all synced (${stats.byStatus.accepted}) · ${discarded} discarded`
          : `all synced (${stats.byStatus.accepted})`;

  return (
    <a
      className={`outbox-chip ${tone}`}
      href={href("/outbox")}
      title={storageAtRisk ? STORAGE_WARNING : undefined}
      aria-label={
        storageAtRisk ? `Outbox: ${summary} · storage not durable` : `Outbox: ${summary}`
      }
    >
      <span className="dot" aria-hidden="true" />
      {summary}
      {storageAtRisk && <span aria-hidden="true">⚠</span>}
    </a>
  );
}
