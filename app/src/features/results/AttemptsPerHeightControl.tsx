import { useState } from "react";
import { ApiError } from "../../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { setAttemptsPerHeight } from "../../api/resources";
import type { AttemptsPerHeight, CarnivalEvent } from "../../api/types";
import { attemptsPerHeightOf } from "../../results/jumpCard";

/** A direct call like the raise: the value is the division's shared state. */

/** Another deck's mark landed first: the division this control read is out of date. */
const FROZEN = "ATTEMPTS_PER_HEIGHT_FROZEN";

export function AttemptsPerHeightControl({
  event,
  switchable,
  reachable,
  onChanged,
}: {
  event: CarnivalEvent;
  /** No mark in the division, here or on the server, and the bar can be raised. */
  switchable: boolean;
  reachable: boolean;
  onChanged: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const perHeight = attemptsPerHeightOf(event);

  if (!switchable)
    return <p className="row-sub jump-attempts-text">{perHeight} attempts per height</p>;

  async function change(next: AttemptsPerHeight) {
    if (busy || next === perHeight) return;
    setBusy(true);
    setError(null);
    try {
      await setAttemptsPerHeight(event.carnivalId, event.id, next);
      onChanged();
    } catch (err) {
      if (err instanceof ApiError && err.code === FROZEN) onChanged();
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <label className="jump-attempts">
        <span className="visually-hidden">Attempts per height</span>
        <select
          value={perHeight}
          disabled={busy || !reachable}
          onChange={(e) => void change(Number(e.target.value) as AttemptsPerHeight)}
        >
          <option value={3}>3 attempts per height</option>
          <option value={2}>2 attempts per height</option>
        </select>
      </label>
      {error && (
        <p className="alert error" role="alert">
          {error}
        </p>
      )}
    </>
  );
}
