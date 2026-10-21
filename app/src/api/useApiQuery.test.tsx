import { describe, expect, it, vi } from "vitest";
import { act, waitFor } from "@testing-library/react";
import { renderHookWithQuery, createTestQueryClient } from "../test/queryWrapper";
import { keys } from "./queryKeys";
import { useApiQuery, useInvalidate, type ApiState } from "./useApiQuery";

/**
 * U1's guarantee, now enforced on the TanStack wrapper (ADR-0012): an action
 * mid-carnival must never blank the screen. `loading` means "nothing to show
 * yet", not "a request is in flight" — a revalidation keeps the previous data
 * rendered and reports `isRefetching` instead, and a failed revalidation
 * surfaces the error with the stale data still on screen.
 */

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (reason: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe("useApiQuery", () => {
  it("reports loading only until the first load lands", async () => {
    const first = deferred<string>();
    const { result } = renderHookWithQuery(() =>
      useApiQuery(keys.standings("c-1"), () => first.promise),
    );

    expect(result.current.loading).toBe(true);
    expect(result.current.isRefetching).toBe(false);
    expect(result.current.data).toBeUndefined();

    await act(async () => {
      first.resolve("standings-v1");
    });

    await waitFor(() => expect(result.current.data).toBe("standings-v1"));
    expect(result.current).toMatchObject({
      error: undefined,
      loading: false,
      isRefetching: false,
    });
  });

  it("keeps stale data rendered during a revalidation (isRefetching, not loading)", async () => {
    const first = deferred<string>();
    const second = deferred<string>();
    const fetcher = vi
      .fn<() => Promise<string>>()
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    const client = createTestQueryClient();
    const { result } = renderHookWithQuery(() => useApiQuery(keys.standings("c-1"), fetcher), {
      client,
    });
    await act(async () => {
      first.resolve("standings-v1");
    });
    await waitFor(() => expect(result.current.data).toBe("standings-v1"));

    await act(async () => {
      void client.invalidateQueries({ queryKey: keys.standings("c-1") });
    });

    await waitFor(() => expect(result.current.isRefetching).toBe(true));
    expect(result.current.data).toBe("standings-v1");
    expect(result.current.loading).toBe(false);

    await act(async () => {
      second.resolve("standings-v2");
    });

    await waitFor(() => expect(result.current.data).toBe("standings-v2"));
    expect(result.current.isRefetching).toBe(false);
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it("surfaces a failed revalidation while the stale data stays visible", async () => {
    const first = deferred<string>();
    const second = deferred<string>();
    const fetcher = vi
      .fn<() => Promise<string>>()
      .mockReturnValueOnce(first.promise)
      .mockReturnValueOnce(second.promise);
    const client = createTestQueryClient();
    const { result } = renderHookWithQuery(() => useApiQuery(keys.standings("c-1"), fetcher), {
      client,
    });
    await act(async () => {
      first.resolve("standings-v1");
    });
    await waitFor(() => expect(result.current.data).toBe("standings-v1"));

    await act(async () => {
      void client.invalidateQueries({ queryKey: keys.standings("c-1") });
    });
    await act(async () => {
      second.reject(new Error("Request failed: HTTP 503"));
    });

    await waitFor(() => expect(result.current.error?.message).toBe("Request failed: HTTP 503"));
    expect(result.current.data).toBe("standings-v1");
    expect(result.current.loading).toBe(false);
    expect(result.current.isRefetching).toBe(false);
  });

  it("reports loading again when the key changes — the data is a different resource", async () => {
    const a = deferred<string>();
    const b = deferred<string>();
    const fetcher = vi.fn((id: string) => (id === "c-a" ? a.promise : b.promise));
    const { result, rerender } = renderHookWithQuery<ApiState<string>, { id: string }>(
      ({ id }) => useApiQuery(keys.standings(id), () => fetcher(id)),
      { initialProps: { id: "c-a" } },
    );
    await act(async () => {
      a.resolve("carnival-a");
    });
    await waitFor(() => expect(result.current.data).toBe("carnival-a"));

    await act(async () => {
      rerender({ id: "c-b" });
    });

    expect(result.current.data).toBeUndefined();
    expect(result.current.loading).toBe(true);

    await act(async () => {
      b.resolve("carnival-b");
    });

    await waitFor(() => expect(result.current.data).toBe("carnival-b"));
    expect(result.current.loading).toBe(false);
  });

  it("leaves a failed first load in the error state with nothing to render", async () => {
    const first = deferred<string>();
    const { result } = renderHookWithQuery(() =>
      useApiQuery(keys.standings("c-1"), () => first.promise),
    );

    await act(async () => {
      first.reject(new Error("Not signed in (401)."));
    });

    await waitFor(() => expect(result.current.error?.message).toBe("Not signed in (401)."));
    expect(result.current.data).toBeUndefined();
    expect(result.current.loading).toBe(false);
    expect(result.current.isRefetching).toBe(false);
  });

  it("serves a second reader of the same resource from cache — one request, no empty state", async () => {
    const fetcher = vi.fn<() => Promise<string>>().mockResolvedValue("standings-v1");
    const client = createTestQueryClient();
    const { result: first } = renderHookWithQuery(
      () => useApiQuery(keys.standings("c-1"), fetcher),
      { client },
    );
    await waitFor(() => expect(first.current.data).toBe("standings-v1"));

    // Second screen (or a remount after navigating away) asks for the same
    // resource: it renders from cache immediately rather than from empty (U2).
    const { result: second } = renderHookWithQuery(
      () => useApiQuery(keys.standings("c-1"), fetcher),
      { client },
    );
    expect(second.current.data).toBe("standings-v1");
    expect(second.current.loading).toBe(false);
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it("useInvalidate refetches the affected key — the replacement for reload()", async () => {
    const fetcher = vi
      .fn<() => Promise<string>>()
      .mockResolvedValueOnce("standings-v1")
      .mockResolvedValueOnce("standings-v2");
    const client = createTestQueryClient();
    const { result } = renderHookWithQuery(
      () => ({
        state: useApiQuery(keys.standings("c-1"), fetcher),
        invalidate: useInvalidate(),
      }),
      { client },
    );
    await waitFor(() => expect(result.current.state.data).toBe("standings-v1"));

    act(() => result.current.invalidate(keys.carnival("c-1")));

    // Prefix match: the carnival key covers everything nested under it.
    await waitFor(() => expect(result.current.state.data).toBe("standings-v2"));
  });

  it("polls only when a caller opts in — every other read stays event-driven", async () => {
    const fetcher = vi.fn<() => Promise<string>>().mockResolvedValue("devices-v1");
    const { result } = renderHookWithQuery(() =>
      useApiQuery(keys.devices("c-1"), fetcher, { refetchIntervalMs: 40 }),
    );
    await waitFor(() => expect(result.current.data).toBe("devices-v1"));

    await waitFor(() => expect(fetcher.mock.calls.length).toBeGreaterThan(1));

    const quiet = vi.fn<() => Promise<string>>().mockResolvedValue("standings-v1");
    const other = renderHookWithQuery(() => useApiQuery(keys.standings("c-2"), quiet));
    await waitFor(() => expect(other.result.current.data).toBe("standings-v1"));
    await new Promise((resolve) => setTimeout(resolve, 120));
    expect(quiet).toHaveBeenCalledTimes(1);
  });

  it("treats a zero interval as off, so a caller can gate its own poll", async () => {
    // ADR-0036's results poll is gated on reachability this way: the option
    // stays passed, and the number it carries is what turns the poll off.
    const fetcher = vi.fn<() => Promise<string>>().mockResolvedValue("results-v1");
    const { result } = renderHookWithQuery(() =>
      useApiQuery(keys.results("c-1", "ev-1"), fetcher, { refetchIntervalMs: 0 }),
    );
    await waitFor(() => expect(result.current.data).toBe("results-v1"));
    await new Promise((resolve) => setTimeout(resolve, 120));
    expect(fetcher).toHaveBeenCalledTimes(1);
  });
});
