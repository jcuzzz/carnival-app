import { useState } from "react";
import { ApiError } from "../../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { raiseBar } from "../../api/resources";
import type { CarnivalEvent } from "../../api/types";
import { readMeasureField, type MeasureFieldState } from "../../results/markMask";
import { MeasureInput } from "../../results/MeasureInput";
import { formatHeight, heightLabel, nextBarHeight, round2 } from "../../results/jumpCard";

/** A direct call, not an outbox kind: the ladder is shared state, so offline never queues. */

/** The server's refusal when the bar this control read is no longer the last one. */
const BAR_MOVED = "BAR_NEVER_LOWERED";

/** The offline answer: nothing was lost, nothing was sent. */
const OFFLINE_TEXT =
  "Can't reach the server, so the bar can't be raised from here yet. Marks you take keep queueing on this device; raise it once you're back on the venue's network.";

export function RaiseBarControl({
  event,
  reachable,
  disabled,
  onRaised,
  onStale,
}: {
  event: CarnivalEvent;
  reachable: boolean;
  /** No signed-in official, a division that can no longer take a bar, or a dead read. */
  disabled: boolean;
  onRaised: () => void;
  /** Another deck got there first: the ladder this control read is out of date. */
  onStale?: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [raw, setRaw] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const heights = event.heights ?? [];
  const opening = heights.length === 0;
  const last = opening ? null : heights[heights.length - 1]!;
  const suggested = nextBarHeight(heights, event.ladder);
  const field = readMeasureField("Height", raw);
  const typed = field.mode === "value" && field.valid && field.value ? Number(field.value) : null;
  const height = typed ?? suggested;
  const belowBar = height !== null && last !== null && round2(height) <= round2(last);
  // The metre mask right-fills (1-4-0 reads 0.01, 0.14, 1.40), so the alert waits for a complete
  // value; the button follows `belowBar`, since a live button that does nothing is worse.
  const tooLow = belowBar && (typed === null || complete(field));
  const unsuggested = opening ? "Set opening height" : "Raise the bar";
  const nameFor = (value: number | null) =>
    value === null
      ? unsuggested
      : opening
        ? `Set opening height ${heightLabel(value)}`
        : `Raise bar to ${heightLabel(value)}`;

  function close() {
    setRaw("");
    setError(null);
    setOpen(false);
  }

  async function save() {
    if (height === null || belowBar || busy) return;
    setBusy(true);
    setError(null);
    try {
      await raiseBar(event.carnivalId, event.id, round2(height));
      setRaw("");
      setOpen(false);
      onRaised();
    } catch (err) {
      if (err instanceof ApiError && err.code === BAR_MOVED) onStale?.();
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="jump-raise">
      {open ? (
        <div className="jump-raise-open">
          <MeasureInput
            id={`raise-${event.id}`}
            measureType="Height"
            raw={raw}
            onRawChange={setRaw}
            onEnter={() => void save()}
            disabled={busy}
            ariaLabel="New height"
            variant="bar"
            {...(suggested === null ? { placeholder: "e.g. 1.20" } : {})}
          />
          <button
            type="button"
            className="primary"
            disabled={busy || height === null || belowBar}
            onClick={() => void save()}
          >
            {busy ? "Saving…" : nameFor(height)}
          </button>
          <button type="button" disabled={busy} onClick={close}>
            Cancel
          </button>
        </div>
      ) : (
        <button
          type="button"
          className="primary"
          disabled={disabled || !reachable}
          onClick={() => {
            setError(null);
            setOpen(true);
          }}
        >
          {nameFor(suggested)}
        </button>
      )}
      {!reachable && <p className="row-sub jump-raise-note">{OFFLINE_TEXT}</p>}
      {open && (
        <p className="row-sub jump-raise-note">
          {opening
            ? "Every athlete's card starts at this height. The bar never comes down, so a lower one is refused."
            : `Athletes with attempts outstanding at ${heightLabel(last!)} keep them — the card reads the missing marks as passes.`}
          {suggested !== null && raw === "" && ` Leave it blank for ${heightLabel(suggested)}.`}
        </p>
      )}
      {tooLow && (
        <p className="alert error" role="alert">
          {formatHeight(height!)} m is not above {heightLabel(last!)}. The bar never comes down.
        </p>
      )}
      {error && (
        <p className="alert error" role="alert">
          {error}
        </p>
      )}
    </div>
  );
}

/** Enough digits typed for the mask to read the value the official meant. */
function complete(field: MeasureFieldState): boolean {
  return field.input.includes(".")
    ? field.ghost === ""
    : field.input.replace(/\D/g, "").length >= 3;
}
