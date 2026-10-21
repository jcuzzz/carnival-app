import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { ensurePersistentStorage, STORAGE_PERSIST_KEY } from "./storagePersistence";

beforeEach(async () => {
  await db.syncMeta.clear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("ensurePersistentStorage", () => {
  it("requests navigator.storage.persist() and records the grant", async () => {
    const persist = vi.fn().mockResolvedValue(true);
    vi.stubGlobal("navigator", { storage: { persist } });

    const result = await ensurePersistentStorage();

    expect(persist).toHaveBeenCalledOnce();
    expect(result).toMatchObject({ persisted: true, unsupported: false });
    const meta = await db.syncMeta.get(STORAGE_PERSIST_KEY);
    expect(meta?.value).toMatchObject({ persisted: true, unsupported: false });
  });

  it("records an unsupported Storage API as evictable", async () => {
    vi.stubGlobal("navigator", {});
    const result = await ensurePersistentStorage();
    expect(result).toMatchObject({ persisted: false, unsupported: true });
    const meta = await db.syncMeta.get(STORAGE_PERSIST_KEY);
    expect(meta?.value).toMatchObject({ persisted: false, unsupported: true });
  });

  it("never rejects when the Storage API throws", async () => {
    const persist = vi.fn().mockRejectedValue(new Error("denied"));
    vi.stubGlobal("navigator", { storage: { persist } });
    await expect(ensurePersistentStorage()).resolves.toMatchObject({
      persisted: false,
      unsupported: true,
    });
  });
});
