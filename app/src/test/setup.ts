// Vitest setup: in-memory IndexedDB so the Dexie outbox works under Node.
// Must load before any module that constructs the Dexie database.
import "fake-indexeddb/auto";
import { afterEach } from "vitest";

// jsdom has no `scrollIntoView`; save-and-advance calls it on every focus hop, and a throw
// inside a passive effect surfaces as a false failure in an unrelated test.
if (typeof Element !== "undefined" && !Element.prototype.scrollIntoView)
  Element.prototype.scrollIntoView = () => {};

// Testing Library auto-registers cleanup only when `afterEach` is a global, and vitest runs
// without globals here; a still-mounted tree's poll would fire after the environment is gone.
if (typeof document !== "undefined") {
  const { cleanup } = await import("@testing-library/react");
  const { destroyTestQueryClients } = await import("./queryWrapper");
  afterEach(() => {
    cleanup();
    destroyTestQueryClients();
  });
}
