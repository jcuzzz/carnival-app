import { db } from "../db/db";

export const STORAGE_PERSIST_KEY = "storage.persisted";

export interface StoragePersistState {
  persisted: boolean;
  unsupported: boolean;
  requestedAt: string;
}

export async function requestPersistentStorage(): Promise<{
  persisted: boolean;
  unsupported: boolean;
}> {
  if (typeof navigator === "undefined" || !navigator.storage?.persist) {
    return { persisted: false, unsupported: true };
  }
  const persisted = await navigator.storage.persist();
  return { persisted, unsupported: false };
}

/**
 * Called at startup, not only at package download: the outbox can hold unsynced results before
 * any package exists, and iOS IndexedDB is evictable until granted. Never rejects.
 */
export async function ensurePersistentStorage(): Promise<StoragePersistState> {
  const requestedAt = new Date().toISOString();
  let state: StoragePersistState = { persisted: false, unsupported: true, requestedAt };
  try {
    const { persisted, unsupported } = await requestPersistentStorage();
    state = { persisted, unsupported, requestedAt };
  } catch {
    // A throwing Storage API: grant unknown, assume evictable.
  }
  try {
    await db.syncMeta.put({ key: STORAGE_PERSIST_KEY, value: state });
  } catch {
    // Advisory record only.
  }
  return state;
}
