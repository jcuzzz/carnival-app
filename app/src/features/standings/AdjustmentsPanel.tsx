import { useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { keys } from "../../api/queryKeys";
import { listHouses, voidAdjustment } from "../../api/resources";
import { useApiQuery, useInvalidate, type ApiState } from "../../api/useApiQuery";
import type { AdjustmentCreatePayload } from "../../api/outboxTransport";
import type { CarnivalSettings, HousePointsAdjustment } from "../../api/types";
import { submitAdjustmentViaOutbox } from "../../adjustments/submitAdjustment";
import { showToast } from "../../shell/toast";
import type { OutboxItem } from "../../db/db";

export function AdjustmentsPanel({
  carnivalId,
  canManage,
  settings,
  adjustments,
  pending,
  actorSubject,
  actorDisplayName,
}: {
  carnivalId: string;
  canManage: boolean;
  settings: CarnivalSettings | undefined;
  adjustments: ApiState<HousePointsAdjustment[]>;
  pending: OutboxItem[];
  actorSubject: string | null;
  actorDisplayName?: string;
}) {
  const houses = useApiQuery(keys.houses(), () => listHouses());
  const invalidate = useInvalidate();
  const [house, setHouse] = useState("");
  const [category, setCategory] = useState("");
  const [points, setPoints] = useState("");
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [awarding, setAwarding] = useState(false);
  const [voidingId, setVoidingId] = useState<string | null>(null);
  const [voidReason, setVoidReason] = useState("");
  const [voidBusy, setVoidBusy] = useState(false);

  const rows = adjustments.data ?? [];

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    if (!actorSubject) {
      setError("Sign in to award bonus points — the actor is captured on every award (ADR-0005).");
      return;
    }
    const pts = Number(points);
    if (!Number.isInteger(pts) || pts === 0) {
      setError("Enter a whole, non-zero point value (negative to deduct).");
      return;
    }
    if (house === "" || category === "" || reason.trim() === "") {
      setError("House, category and a reason are all required.");
      return;
    }
    // One tap must not become two on a laggy device.
    setAwarding(true);
    try {
      await submitAdjustmentViaOutbox({
        carnivalId,
        houseCode: house,
        points: pts,
        category,
        reason: reason.trim(),
        actorSubject,
        ...(actorDisplayName !== undefined ? { actorDisplayName } : {}),
      });
      showToast(`Queued ${pts > 0 ? "+" : ""}${pts} for ${house} — syncing.`);
      setPoints("");
      setReason("");
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not queue the adjustment.");
    } finally {
      setAwarding(false);
    }
  }

  // Inline reason + confirm rather than window.prompt, which iOS suppresses in
  // some standalone (installed-PWA) contexts — the void would silently no-op.
  async function doVoid(id: string) {
    const why = voidReason.trim();
    if (why === "") return;
    setError(null);
    setVoidBusy(true);
    try {
      await voidAdjustment(carnivalId, id, why);
      setVoidingId(null);
      setVoidReason("");
      // A void removes points, so the standings are stale too.
      invalidate(keys.adjustments(carnivalId), keys.standings(carnivalId));
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Could not void the adjustment.");
    } finally {
      setVoidBusy(false);
    }
  }

  return (
    <section className="subsection">
      <h2>Novelty &amp; bonus points</h2>
      <p className="row-sub">
        Discretionary house awards — novelty events, best dressed, spirit (ADR-0005). Separate from
        competition results; never a student record.
      </p>

      {canManage && (
        <form className="card" onSubmit={submit}>
          {error && (
            <div className="alert error" role="alert">
              {error}
            </div>
          )}
          <div className="form-grid">
            <label className="field">
              <span>House</span>
              <select value={house} onChange={(e) => setHouse(e.target.value)}>
                <option value="">— pick —</option>
                {(houses.data ?? []).map((h) => (
                  <option key={h} value={h}>{h}</option>
                ))}
              </select>
            </label>
            <label className="field">
              <span>Category</span>
              <select value={category} onChange={(e) => setCategory(e.target.value)}>
                <option value="">— pick —</option>
                {(settings?.adjustmentCategories ?? []).map((c) => (
                  <option key={c} value={c}>{c}</option>
                ))}
              </select>
            </label>
            <label className="field">
              <span>Points (negative to deduct)</span>
              <input
                type="number"
                inputMode="numeric"
                value={points}
                onChange={(e) => setPoints(e.target.value)}
                placeholder="e.g. 10 or -5"
              />
            </label>
            <label className="field wide">
              <span>Reason</span>
              <input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Best dressed — Wattle" />
            </label>
          </div>
          <button className="primary" type="submit" disabled={awarding}>
            {awarding ? "Queueing…" : "Award points"}
          </button>
        </form>
      )}

      {pending.length > 0 && (
        <ul className="row-list card">
          {pending.map((i) => {
            const p = i.payload as AdjustmentCreatePayload;
            return (
              <li key={i.id}>
                <div className="entry-row">
                  <span className="row-main">
                    <span className="row-title">
                      {p.houseCode} {p.points > 0 ? "+" : ""}{p.points} · {p.category}
                    </span>
                    <span className="row-sub">{p.reason}</span>
                  </span>
                  <span className="badge muted">syncing…</span>
                </div>
              </li>
            );
          })}
        </ul>
      )}

      {adjustments.loading && <p>Loading adjustments…</p>}
      {rows.length === 0 && !adjustments.loading && pending.length === 0 && (
        <p className="row-sub">No bonus points recorded yet.</p>
      )}
      {rows.length > 0 && (
        <ul className="row-list card">
          {rows.map((a) => (
            <li key={a.id}>
              <div className="entry-row">
                <span className="row-main">
                  <span className={`row-title${a.voided ? " voided" : ""}`}>
                    {a.houseCode} {a.points > 0 ? "+" : ""}{a.points} · {a.category}
                  </span>
                  <span className="row-sub">
                    {a.reason}
                    {a.voided ? ` · voided${a.voidReason ? `: ${a.voidReason}` : ""}` : ""}
                  </span>
                </span>
                {canManage && !a.voided && voidingId !== a.id && (
                  <button
                    onClick={() => {
                      setVoidingId(a.id);
                      setVoidReason("");
                    }}
                  >
                    Void
                  </button>
                )}
              </div>
              {canManage && voidingId === a.id && (
                <form
                  className="entry-form row-edit"
                  onSubmit={(e) => {
                    e.preventDefault();
                    void doVoid(a.id);
                  }}
                >
                  <label className="field wide flush-bottom">
                    <span>Reason for voiding (recorded on the audit trail)</span>
                    <input
                      value={voidReason}
                      onChange={(e) => setVoidReason(e.target.value)}
                      placeholder="e.g. awarded to the wrong house"
                      autoFocus
                    />
                  </label>
                  <button
                    className="danger"
                    type="submit"
                    disabled={voidBusy || voidReason.trim() === ""}
                  >
                    {voidBusy ? "Voiding…" : "Confirm void"}
                  </button>
                  <button type="button" disabled={voidBusy} onClick={() => setVoidingId(null)}>
                    Cancel
                  </button>
                </form>
              )}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
