import type { ReactElement, ReactNode } from "react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, renderHook, type RenderHookOptions } from "@testing-library/react";

// A fresh QueryClient per render, so one test's cache never satisfies the next test's fetch.
const liveClients = new Set<QueryClient>();

export function createTestQueryClient(): QueryClient {
  const client = new QueryClient({
    defaultOptions: {
      queries: {
        // Mirrors the production defaults that affect behaviour under test
        // (a cached resource is not refetched on remount inside the window).
        staleTime: 30_000,
        networkMode: "offlineFirst",
        retry: false,
        // Focus/reconnect revalidation is production behaviour under test only
        // in the hook's own tests, where it is opted into explicitly.
        refetchOnWindowFocus: false,
        gcTime: Infinity,
      },
    },
  });
  liveClients.add(client);
  return client;
}

/**
 * Unmounting drops the observers, but a fetch in flight or a poll's next tick would still
 * notify after the test environment is gone.
 */
export function destroyTestQueryClients() {
  for (const client of liveClients) {
    void client.cancelQueries();
    client.unmount();
    client.clear();
  }
  liveClients.clear();
}

export function QueryWrapper({
  client,
  children,
}: {
  client: QueryClient;
  children: ReactNode;
}) {
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

export function renderWithQuery(ui: ReactElement, client = createTestQueryClient()) {
  return {
    client,
    ...render(<QueryWrapper client={client}>{ui}</QueryWrapper>),
  };
}

export function renderHookWithQuery<Result, Props>(
  hook: (props: Props) => Result,
  options?: Omit<RenderHookOptions<Props>, "wrapper"> & { client?: QueryClient },
) {
  const client = options?.client ?? createTestQueryClient();
  return {
    client,
    ...renderHook(hook, {
      ...options,
      wrapper: ({ children }) => <QueryWrapper client={client}>{children}</QueryWrapper>,
    } as RenderHookOptions<Props>),
  };
}
