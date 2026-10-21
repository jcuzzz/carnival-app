import { QueryClient } from "@tanstack/react-query";
import { subscribeSession } from "../auth/session";

/** Reads only: writes never use useMutation; they invalidate keys once they land. */
export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        // gcTime spans a carnival day: navigating away and back must render from cache, not empty.
        gcTime: 24 * 60 * 60 * 1000,
        // With the AP up and the uplink dead a fetch fails and must fall through to cache.
        networkMode: "offlineFirst",
        // A failed read shows stale data and an honest error at once, never a silent spinner.
        retry: false,
        refetchOnWindowFocus: true,
        refetchOnReconnect: true,
      },
    },
  });
}

/** Reads are role-scoped server-side and `gcTime` spans a day, so a handover must clear them. */
export function clearCacheOnSessionChange(client: QueryClient): () => void {
  return subscribeSession(() => client.clear());
}

export const queryClient = createQueryClient();
