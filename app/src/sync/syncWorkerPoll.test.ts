// @vitest-environment jsdom
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { enqueue } from "./outbox";
import { createSyncWorker, type Transport } from "./syncWorker";
import { _resetUuidv7StateForTests } from "./uuidv7";

/**
 * The poll chain itself (ADR-0029): it survives a pass that threw, it stays ONE
 * chain across connectivity flaps, and `stop()` ends it. jsdom, because
 * `online` is a window event the node environment has nowhere to dispatch.
 */

const POLL_MS = 15_000;

/** Only setTimeout: fake-indexeddb drives Dexie off setImmediate (retention.test.ts). */
beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
});

afterEach(() => {
  vi.useRealTimers();
});

/** The real timer, captured before any faking, so Dexie's macrotasks can run. */
const realSetTimeout = globalThis.setTimeout;

/** Let the unfaked macrotasks Dexie runs on drain. */
async function settle(): Promise<void> {
  for (let i = 0; i < 10; i++) await new Promise((resolve) => realSetTimeout(resolve, 0));
}

function counting(): { transport: Transport; sent: string[] } {
  const sent: string[] = [];
  return {
    sent,
    transport: async (item) => {
      sent.push(item.id);
      return { status: 200 };
    },
  };
}

it("re-arms the poll after a pass that threw, and drains on the next one", async () => {
  const item = await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" });
  let passes = 0;
  const throwsOnce = () => {
    passes += 1;
    if (passes === 1) throw new Error("DatabaseClosedError");
    return true;
  };
  const server = counting();
  const worker = createSyncWorker({
    transport: server.transport,
    isOnline: throwsOnce,
    pollIntervalMs: POLL_MS,
  });

  worker.start();
  await settle();
  expect(server.sent).toEqual([]);
  expect(vi.getTimerCount()).toBe(1);

  vi.advanceTimersByTime(POLL_MS);
  await settle();

  expect(server.sent).toEqual([item.id]);
  expect((await db.outbox.get(item.id))?.status).toBe("accepted");
  worker.stop();
});

it("waits out a 429 longer than an ordinary poll, then drains", async () => {
  const item = await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" });
  const sent: string[] = [];
  const worker = createSyncWorker({
    transport: async (outgoing) => {
      sent.push(outgoing.id);
      return sent.length === 1 ? { status: 429, retryAfterMs: POLL_MS * 3 } : { status: 200 };
    },
    isOnline: () => true,
    pollIntervalMs: POLL_MS,
  });

  worker.start();
  await settle();
  expect(sent).toEqual([item.id]);
  expect((await db.outbox.get(item.id))?.status).toBe("queued");

  vi.advanceTimersByTime(POLL_MS);
  await settle();
  expect(sent).toHaveLength(1);

  vi.advanceTimersByTime(POLL_MS * 2);
  await settle();
  expect(sent).toEqual([item.id, item.id]);
  expect((await db.outbox.get(item.id))?.status).toBe("accepted");
  worker.stop();
});

it("keeps ONE poll chain however many times connectivity flaps", async () => {
  const server = counting();
  const worker = createSyncWorker({
    transport: server.transport,
    isOnline: () => true,
    pollIntervalMs: POLL_MS,
  });

  worker.start();
  await settle();
  expect(vi.getTimerCount()).toBe(1);

  for (let i = 0; i < 3; i++) window.dispatchEvent(new Event("online"));
  await settle();

  // Four chains would drain four times a poll interval, each one holding the
  // device's queue open at a different offset.
  expect(vi.getTimerCount()).toBe(1);
  worker.stop();
});

it("stop() cancels the live timer", async () => {
  const server = counting();
  const worker = createSyncWorker({
    transport: server.transport,
    isOnline: () => true,
    pollIntervalMs: POLL_MS,
  });

  worker.start();
  await settle();
  expect(vi.getTimerCount()).toBe(1);

  worker.stop();
  expect(vi.getTimerCount()).toBe(0);

  await enqueue({ kind: "result.submit", payload: 1, actorSubject: "a" });
  vi.advanceTimersByTime(POLL_MS * 4);
  await settle();
  expect(server.sent).toEqual([]);
});
