import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { updateCarnivalSettings } from "../../api/resources";
import type { CarnivalSettings } from "../../api/types";
import { showToast } from "../../shell/toast";

/**
 * Re-seeds on the stored content, not `dataUpdatedAt`, which moves on every poll and would drop a
 * half-typed edit.
 */
export function ScoringRulesEditor({
  carnivalId,
  settings,
  onSaved,
}: {
  carnivalId: string;
  settings: CarnivalSettings;
  onSaved: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [placePoints, setPlacePoints] = useState(settings.scoring.placePoints.join(", "));
  const [floor, setFloor] = useState(String(settings.scoring.participationFloor));
  const [dnf, setDnf] = useState(String(settings.scoring.dnfPoints));
  const [dns, setDns] = useState(String(settings.scoring.dnsPoints));
  const [dq, setDq] = useState(String(settings.scoring.dqPoints));
  const [socialPerEntry, setSocialPerEntry] = useState(
    String(settings.scoring.socialPerEntryPoints),
  );
  const [categories, setCategories] = useState(settings.adjustmentCategories.join(", "));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Re-seeded in place, not by a remount: a remount also closes the disclosure under whoever saved.
  const stored = JSON.stringify(settings);
  const [seeded, setSeeded] = useState(stored);
  if (seeded !== stored) {
    setSeeded(stored);
    setPlacePoints(settings.scoring.placePoints.join(", "));
    setFloor(String(settings.scoring.participationFloor));
    setDnf(String(settings.scoring.dnfPoints));
    setDns(String(settings.scoring.dnsPoints));
    setDq(String(settings.scoring.dqPoints));
    setSocialPerEntry(String(settings.scoring.socialPerEntryPoints));
    setCategories(settings.adjustmentCategories.join(", "));
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setError(null);
    const parsed = placePoints
      .split(",")
      .map((s) => s.trim())
      .filter((s) => s !== "")
      .map(Number);
    if (parsed.some((n) => !Number.isInteger(n) || n < 0)) {
      setError("Place points must be a comma list of non-negative whole numbers, e.g. 4, 3, 2.");
      return;
    }
    const social = Number(socialPerEntry);
    if (!Number.isInteger(social) || social < 0) {
      setError("Social points per entry must be a non-negative whole number.");
      return;
    }
    setSaving(true);
    try {
      await updateCarnivalSettings(carnivalId, {
        scoring: {
          placePoints: parsed,
          participationFloor: Number(floor) || 0,
          dnfPoints: Number(dnf) || 0,
          dnsPoints: Number(dns) || 0,
          dqPoints: Number(dq) || 0,
          socialPerEntryPoints: social,
        },
        adjustmentCategories: categories
          .split(",")
          .map((s) => s.trim())
          .filter((s) => s !== ""),
      });
      showToast("Scoring rules saved — standings recomputed.");
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not save the scoring rules.");
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="subsection">
      <button type="button" onClick={() => setOpen((v) => !v)}>
        {open ? "Hide scoring rules" : "Scoring rules…"}
      </button>
      {open && (
        <form className="card disclosure-body" onSubmit={save}>
          <p className="row-sub flush-top">
            Per-carnival scoring (ADR-0007). Blank overrides keep the ratified default. Relays always
            score double (event weight, ADR-0008). Social-scored events earn the flat per-entry
            points instead of places (ADR-0014).
          </p>
          {error && (
            <div className="alert error" role="alert">
              {error}
            </div>
          )}
          <div className="form-grid">
            <label className="field wide">
              <span>Place points (1st, 2nd, 3rd, …)</span>
              <input value={placePoints} onChange={(e) => setPlacePoints(e.target.value)} placeholder="4, 3, 2" />
            </label>
            <label className="field">
              <span>Participation floor (next places)</span>
              <input type="number" value={floor} onChange={(e) => setFloor(e.target.value)} />
            </label>
            <label className="field">
              <span>DNF</span>
              <input type="number" value={dnf} onChange={(e) => setDnf(e.target.value)} />
            </label>
            <label className="field">
              <span>DNS</span>
              <input type="number" value={dns} onChange={(e) => setDns(e.target.value)} />
            </label>
            <label className="field">
              <span>DQ</span>
              <input type="number" value={dq} onChange={(e) => setDq(e.target.value)} />
            </label>
            <label className="field">
              <span>Social points per entry</span>
              {/* No `min`: native validation would block the submit silently, and the alert
                  above is the one error channel. */}
              <input
                type="number"
                value={socialPerEntry}
                onChange={(e) => setSocialPerEntry(e.target.value)}
              />
            </label>
            <label className="field wider">
              <span>Bonus categories (comma-separated)</span>
              <input value={categories} onChange={(e) => setCategories(e.target.value)} />
            </label>
          </div>
          <button className="primary" type="submit" disabled={saving}>
            {saving ? "Saving…" : "Save scoring rules"}
          </button>
        </form>
      )}
    </section>
  );
}
