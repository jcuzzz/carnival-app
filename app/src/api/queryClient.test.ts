import { afterEach, describe, expect, it } from "vitest";
import { clearCacheOnSessionChange, createQueryClient } from "./queryClient";
import { keys } from "./queryKeys";
import { signIn, signOut } from "../auth/session";

/**
 * Handover is sign out → sign in on the same device (ADR-0006), and reads are
 * role-scoped server-side. With a day-long `gcTime` the cache would otherwise
 * outlive the official who fetched it.
 */

let unsubscribe: (() => void) | undefined;

afterEach(() => {
  unsubscribe?.();
  unsubscribe = undefined;
  signOut();
});

describe("clearCacheOnSessionChange", () => {
  it("drops cached reads when the signed-in official changes", () => {
    const client = createQueryClient();
    unsubscribe = clearCacheOnSessionChange(client);
    client.setQueryData(keys.standings("c-1"), { houses: [] });
    expect(client.getQueryData(keys.standings("c-1"))).toBeDefined();

    signIn({ subject: "dev-official-2", displayName: "Omar", roles: ["Recorder"] });

    expect(client.getQueryData(keys.standings("c-1"))).toBeUndefined();
  });

  it("drops them on sign-out too — the next official starts from empty", () => {
    const client = createQueryClient();
    unsubscribe = clearCacheOnSessionChange(client);
    signIn({ subject: "dev-official-1", displayName: "Olivia", roles: ["Recorder"] });
    client.setQueryData(keys.carnivals(), []);

    signOut();

    expect(client.getQueryData(keys.carnivals())).toBeUndefined();
  });

  it("stops clearing once unsubscribed", () => {
    const client = createQueryClient();
    const stop = clearCacheOnSessionChange(client);
    stop();
    client.setQueryData(keys.houses(), ["BANK"]);

    signIn({ subject: "dev-admin", displayName: "Alex", roles: ["CarnivalAdministrator"] });

    expect(client.getQueryData(keys.houses())).toEqual(["BANK"]);
  });
});
