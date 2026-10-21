import { useState, type FormEvent } from "react";
import {
  AUTO_FILL_NEEDS_LANE_COUNT_TEXT,
  DIRECT_WRITE_NETWORK_ERROR_TEXT,
  errorText,
} from "../../api/errorText";
import { updateCarnivalSettings } from "../../api/resources";
import type { CarnivalSettings } from "../../api/types";
import { showToast } from "../../shell/toast";
import { LANE_COUNT_INVALID_TEXT, parseLaneCount } from "./laneCountField";

/**
 * Only this slice is sent: the settings write overlays each field, so a blank lane count leaves
 * the stored one rather than clearing it.
 */
export function VenueLanesEditor({
  carnivalId,
  settings,
  onSaved,
}: {
  carnivalId: string;
  settings: CarnivalSettings;
  onSaved: () => void;
}) {
  const [laneCount, setLaneCount] = useState(
    settings.laneCount == null ? "" : String(settings.laneCount),
  );
  const [autoFill, setAutoFill] = useState(settings.autoFillLanes);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Re-seeded on the stored content, never the read's timestamp: a poll must not overwrite a
  // half-typed count.
  const stored = `${settings.laneCount ?? ""}|${settings.autoFillLanes}`;
  const [seeded, setSeeded] = useState(stored);
  if (seeded !== stored) {
    setSeeded(stored);
    setLaneCount(settings.laneCount == null ? "" : String(settings.laneCount));
    setAutoFill(settings.autoFillLanes);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const count = parseLaneCount(laneCount);
    if (count === "invalid") {
      setError(LANE_COUNT_INVALID_TEXT);
      return;
    }
    // The resulting pair, as the server checks it: a blank field keeps what is stored.
    if (autoFill && (count ?? settings.laneCount) == null) {
      setError(AUTO_FILL_NEEDS_LANE_COUNT_TEXT);
      return;
    }
    setSaving(true);
    try {
      await updateCarnivalSettings(carnivalId, {
        ...(count === null ? {} : { laneCount: count }),
        autoFillLanes: autoFill,
      });
      showToast("Lane settings saved.");
      onSaved();
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="subsection">
      <h2>Lanes</h2>
      <form className="card" onSubmit={save}>
        {error && (
          <div className="alert error" role="alert">
            {error}
          </div>
        )}
        <div className="form-grid">
          <label className="field">
            <span>Lanes per venue</span>
            {/* No `min`/`max`: native constraint validation blocks the submit
                silently, and this form's one error channel is the alert above. */}
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
        <button className="primary" type="submit" disabled={saving}>
          {saving ? "Saving…" : "Save lane settings"}
        </button>
      </form>
    </section>
  );
}
