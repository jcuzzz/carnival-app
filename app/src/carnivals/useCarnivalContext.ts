import { useEffect } from "react";
import { ApiError } from "../api/client";
import { keys } from "../api/queryKeys";
import { getCarnival } from "../api/resources";
import { useApiQuery } from "../api/useApiQuery";
import { useSession } from "../auth/session";
import { clearSelectedCarnival, syncSelectedCarnival, useSelectedCarnival } from "./selectedCarnival";

/**
 * Drops the selection on a 404 only: a network failure or a 401 is not evidence of absence,
 * and the venue's dead uplink must not log officials out of their own carnival.
 */
export function useCarnivalContext(): void {
  const selected = useSelectedCarnival();
  const session = useSession();
  const carnivalId = selected?.id ?? null;
  // Unauthenticated, every read is a 401 — no point asking, and nothing to sync.
  const enabled = carnivalId !== null && session !== null;

  const carnival = useApiQuery(
    keys.carnival(carnivalId ?? "none"),
    () => getCarnival(carnivalId!),
    { enabled },
  );

  const loaded = carnival.data;
  useEffect(() => {
    if (loaded) syncSelectedCarnival(loaded);
  }, [loaded]);

  const error = carnival.error;
  useEffect(() => {
    if (error instanceof ApiError && error.status === 404) clearSelectedCarnival();
  }, [error]);
}
