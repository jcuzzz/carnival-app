import { useCallback } from "react";
import { useQuery, useQueryClient, type QueryKey } from "@tanstack/react-query";

/**
 * An action must never blank the screen under an official: `loading` means nothing to show yet,
 * not a request in flight, and a failed revalidation keeps the stale data on screen.
 */
export interface ApiState<T> {
  data: T | undefined;
  error: Error | undefined;
  loading: boolean;
  isRefetching: boolean;
  /** Screens stamp it: data minutes old looks identical to live data. */
  updatedAt: number | undefined;
}

export interface ApiQueryOptions {
  enabled?: boolean;
  /**
   * Only for reads whose value is that they age (the device fleet): a poll is a standing cost on
   * a venue uplink. 0 is off, so a caller can gate its own poll without dropping the option.
   */
  refetchIntervalMs?: number;
}

export function useApiQuery<T>(
  key: QueryKey,
  fetcher: () => Promise<T>,
  options: ApiQueryOptions = {},
): ApiState<T> {
  const query = useQuery({
    queryKey: key,
    queryFn: fetcher,
    enabled: options.enabled ?? true,
    ...(options.refetchIntervalMs !== undefined
      ? { refetchInterval: options.refetchIntervalMs }
      : {}),
  });
  return {
    data: query.data,
    error: query.error ?? undefined,
    loading: query.isLoading,
    // Not TanStack's `isRefetching`, which is also true for a retry with nothing on screen.
    isRefetching: query.isFetching && query.data !== undefined,
    updatedAt: query.dataUpdatedAt === 0 ? undefined : query.dataUpdatedAt,
  };
}

/** Writes never update the read cache, they announce the server changed; keys prefix-match. */
export function useInvalidate(): (...invalidated: QueryKey[]) => void {
  const client = useQueryClient();
  return useCallback(
    (...invalidated: QueryKey[]) => {
      for (const queryKey of invalidated) void client.invalidateQueries({ queryKey });
    },
    [client],
  );
}
