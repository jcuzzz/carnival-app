import { uuidv7 } from "../sync/uuidv7";

/** Sent as X-Device-Id so the server can attribute queue traffic per device. Not a credential. */
const STORAGE_KEY = "sports.deviceId";

let cached: string | null = null;

export function getDeviceId(): string {
  if (cached) return cached;
  try {
    const existing = localStorage.getItem(STORAGE_KEY);
    if (existing) {
      cached = existing;
      return existing;
    }
    const fresh = uuidv7();
    localStorage.setItem(STORAGE_KEY, fresh);
    cached = fresh;
    return fresh;
  } catch {
    cached = uuidv7();
    return cached;
  }
}
