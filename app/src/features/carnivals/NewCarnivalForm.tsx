import { useState, type FormEvent } from "react";
import { AUTO_FILL_NEEDS_LANE_COUNT_TEXT, errorText } from "../../api/errorText";
import { keys } from "../../api/queryKeys";
import { createCarnival } from "../../api/resources";
import type { CarnivalType } from "../../api/types";
import { useInvalidate } from "../../api/useApiQuery";
import { navigate } from "../../router";
import { LANE_COUNT_INVALID_TEXT, parseLaneCount } from "../races/laneCountField";

export const CREATED_WITHOUT_ID_TEXT =
  "The server accepted the carnival but did not say which one it made — find it in the list before creating it again.";

/** Create a carnival, then go straight to its detail screen. */
export function NewCarnivalForm({ onCancel }: { onCancel: () => void }) {
  const [name, setName] = useState("");
  const [type, setType] = useState<CarnivalType>("Swimming");
  const [date, setDate] = useState("");
  const [venue, setVenue] = useState("");
  // Eight lanes is the standard pool and track, so every carnival type creates on the first press.
  const [laneCount, setLaneCount] = useState("8");
  const [autoFill, setAutoFill] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const invalidate = useInvalidate();

  async function submit(e: FormEvent) {
    e.preventDefault();
    if (name.trim() === "" || date === "") {
      setError("Name and date are required.");
      return;
    }
    const count = parseLaneCount(laneCount);
    if (count === "invalid") {
      setError(LANE_COUNT_INVALID_TEXT);
      return;
    }
    if (autoFill && count === null) {
      setError(AUTO_FILL_NEEDS_LANE_COUNT_TEXT);
      return;
    }
    setSaving(true);
    setError(null);
    try {
      const created = await createCarnival({
        name: name.trim(),
        type,
        date,
        ...(venue.trim() !== "" ? { venue: venue.trim() } : {}),
        ...(count === null ? {} : { laneCount: count }),
        autoFillLanes: autoFill,
      });
      // Without this, going back inside the stale window shows a list missing the new carnival.
      invalidate(keys.carnivals());
      // A 2xx with no readable body (a proxy in the way) names no carnival to open.
      if (typeof created?.id !== "string" || created.id === "") {
        setError(CREATED_WITHOUT_ID_TEXT);
        return;
      }
      navigate(`/carnivals/${created.id}`);
    } catch (err) {
      setError(errorText(err));
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="card" onSubmit={submit}>
      <h2 className="flush-top">New carnival</h2>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      <div className="form-grid">
        <label className="field">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </label>
        <label className="field">
          <span>Type</span>
          <select value={type} onChange={(e) => setType(e.target.value as CarnivalType)}>
            <option>Swimming</option>
            <option>Athletics</option>
            <option>CrossCountry</option>
            <option>Other</option>
          </select>
        </label>
        <label className="field">
          <span>Date</span>
          <input type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
        </label>
        <label className="field">
          <span>Venue (optional)</span>
          <input value={venue} onChange={(e) => setVenue(e.target.value)} />
        </label>
        <label className="field">
          <span>Lanes per venue</span>
          {/* No `min`/`max`: native validation would block the submit silently,
              and this form's one error channel is the alert above. */}
          <input
            type="number"
            inputMode="numeric"
            value={laneCount}
            onChange={(e) => setLaneCount(e.target.value)}
          />
        </label>
        <label className="check-row beside-field">
          <input
            type="checkbox"
            checked={autoFill}
            onChange={(e) => setAutoFill(e.target.checked)}
          />
          Auto-fill lanes
        </label>
      </div>
      <p className="row-sub">
        New entries in timed events are given the next free lane; a full race starts the next one.
      </p>
      <div className="button-row">
        <button className="primary" type="submit" disabled={saving}>
          {saving ? "Creating…" : "Create"}
        </button>
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </form>
  );
}
