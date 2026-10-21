import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { useOutboxItems, useQueueStats, useRecentlyAccepted } from "../sync/useOutboxStatus";
import { ACCEPTED_RETENTION_DAYS, markPruneSeen, unseenPrune } from "../sync/retention";
import {
  InvalidTransitionError,
  dependentsOf,
  discard,
  editAndRequeue,
  heldCardKeys,
  isCardCorrection,
  isHeldBehindParkedMark,
  isResultCapture,
  needsRebase,
  requeue,
} from "../sync/outbox";
import { syncWorker, resumeAfterSignIn } from "../api/syncSetup";
import { getDeviceId } from "../auth/deviceId";
import { useSession } from "../auth/session";
import { measureNoun, readMeasureField } from "../results/markMask";
import { formatCapturedValue, formatMeasure } from "../results/measureFormat";
import { MeasureInput } from "../results/MeasureInput";
import { refusalDetail } from "../features/results/resultDisplay";
import { STATUS_FROM_TYPED, statusLabel } from "../features/results/statusLabels";
import {
  ENTRY_ASSIGN_KIND,
  ENTRY_CREATE_KIND,
  RACE_CREATE_KIND,
  RACE_MATERIALISE_KIND,
  RESULT_ATTEMPT_KIND,
  RESULT_CORRECT_KIND,
  RESULT_JUMP_KIND,
  RESULT_SUBMIT_KIND,
  TEAM_CREATE_KIND,
  type EntryAssignPayload,
  type RaceMaterialisePayload,
  type RaceCreatePayload,
  type ResultAttemptPayload,
  type ResultCapturePayload,
  type ResultJumpPayload,
  type TeamCreatePayload,
} from "../api/outboxTransport";
import type { OutboxItem, OutboxStatus } from "../db/db";
import StatusBadge from "../sync/StatusBadge";
import { ARMING_LOCKOUT_MS } from "../features/ui/timing";

// A reassurance list, not a log.
const RECENT_ACCEPTED_LIMIT = 50;

const ACTIVE_STATUSES: readonly OutboxStatus[] = [
  "queued",
  "syncing",
  "conflict",
  "rejected",
  "cancelled",
];

const DISCARDED_STATUSES: readonly OutboxStatus[] = ["discarded"];

// Captured · What · Status · Recover — the span an expanded strip has to fill.
const ACTIVE_COLUMNS = 4;

const CONFIRM_WINDOW_MS = 4_000;

export default function OutboxScreen() {
  const stats = useQueueStats();
  const active = useOutboxItems(ACTIVE_STATUSES);
  const resolved = useResolvedItems();
  const session = useSession();
  const [syncing, setSyncing] = useState(false);
  const [lastOutcome, setLastOutcome] = useState<string | null>(null);
  const tidied = useTidiedNotice();

  async function syncNow() {
    setSyncing(true);
    try {
      const result = await syncWorker.syncNow();
      setLastOutcome(
        `Drain: ${result.stopReason} — ${result.accepted} accepted, ${result.conflicts} conflict(s), ${result.rejected} rejected`,
      );
    } catch (err) {
      setLastOutcome(`Drain could not finish: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      setSyncing(false);
    }
  }

  return (
    <>
      <h1>Outbox</h1>
      {/* The id the organiser's Devices panel names. */}
      <p className="row-sub">
        This device: <span className="mono">{getDeviceId().slice(-8)}</span>
      </p>
      {stats?.paused && (
        <div className="alert error" role="alert">
          Queue paused: {stats.pausedReason ?? "authentication required"}. Nothing is lost —
          sign in again, then resume.
          <div className="button-row">
            <button onClick={() => void resumeAfterSignIn()}>Resume queue</button>
          </div>
        </div>
      )}
      {stats && (
        <p>
          {stats.byStatus.queued} queued · {stats.byStatus.syncing} syncing ·{" "}
          {stats.byStatus.accepted} accepted · {stats.byStatus.conflict} conflict ·{" "}
          {stats.byStatus.rejected} rejected · {stats.byStatus.discarded} discarded
        </p>
      )}
      <div className="button-row">
        <button className="primary" onClick={() => void syncNow()} disabled={syncing}>
          {syncing ? "Syncing…" : "Sync now"}
        </button>
        {lastOutcome && <span className="row-sub">{lastOutcome}</span>}
      </div>
      {tidied !== null && (
        <p className="row-sub" role="status">
          {tidied} older finished item{tidied === 1 ? "" : "s"} tidied up.
        </p>
      )}

      <h2>Waiting or needing attention</h2>
      {active === undefined ? (
        <p>Loading…</p>
      ) : active.length === 0 ? (
        <p>Nothing waiting — every capture on this device has reached the server.</p>
      ) : (
        <ItemTable
          items={active}
          caption="Outbox items in capture order"
          resolvedBy={session?.subject ?? null}
        />
      )}

      <h2>Synced or discarded</h2>
      <p className="row-sub">
        The last {RECENT_ACCEPTED_LIMIT} accepted on this device, plus everything let go here.
        Both are tidied up after {ACCEPTED_RETENTION_DAYS} days.
      </p>
      <p className="row-sub">
        A synced item is not the full record — the server keeps that. A discarded one never
        reached the server, so this row is the only evidence it was ever captured.
      </p>
      {resolved === undefined ? (
        <p>Loading…</p>
      ) : resolved.length === 0 ? (
        <p>Nothing from this device has synced or been discarded yet.</p>
      ) : (
        <ItemTable items={resolved} caption="Synced and discarded items, newest first" />
      )}
    </>
  );
}

// Accepted is read bounded (hundreds by hour six); discards are read whole.
function useResolvedItems(): OutboxItem[] | undefined {
  const accepted = useRecentlyAccepted(RECENT_ACCEPTED_LIMIT);
  const discarded = useOutboxItems(DISCARDED_STATUSES);
  return useMemo(() => {
    if (accepted === undefined || discarded === undefined) return undefined;
    return [...accepted, ...discarded].sort((a, b) => {
      const x = finishedAt(a);
      const y = finishedAt(b);
      return x < y ? 1 : x > y ? -1 : 0;
    });
  }, [accepted, discarded]);
}

function finishedAt(item: OutboxItem): string {
  return item.resolvedAt ?? item.syncedAt ?? item.capturedAt;
}

function ItemTable({
  items,
  caption,
  resolvedBy,
}: {
  items: OutboxItem[];
  caption: string;
  resolvedBy?: string | null;
}) {
  const recovery = resolvedBy !== undefined;
  const held = useMemo(() => heldBehindParkedMark(items), [items]);
  return (
    <div className="card table-wrap outbox-table">
      {/* Roles are spelled out because the phone layout displays these as blocks. */}
      <table role="table">
        <caption className="visually-hidden">{caption}</caption>
        <thead role="rowgroup">
          <tr role="row">
            <th scope="col">Captured</th>
            <th scope="col">What</th>
            <th scope="col">Status</th>
            {recovery && <th scope="col">Recover</th>}
          </tr>
        </thead>
        <tbody role="rowgroup">
          {items.map((item) =>
            recovery ? (
              <RecoverableRow
                key={item.id}
                item={item}
                resolvedBy={resolvedBy ?? null}
                held={held.has(item.id)}
              />
            ) : (
              <tr key={item.id} role="row">
                <ItemCells item={item} />
              </tr>
            ),
          )}
        </tbody>
      </table>
    </div>
  );
}

// Derived the way the drain derives them, so this screen and the worker cannot disagree.
function heldBehindParkedMark(items: readonly OutboxItem[]): ReadonlySet<string> {
  const parked = heldCardKeys(items);
  const held = new Set<string>();
  for (const item of items) {
    if (item.status !== "queued" && item.status !== "syncing") continue;
    if (isHeldBehindParkedMark(item, parked)) held.add(item.id);
  }
  return held;
}

const HELD_TEXT = "Waiting behind a parked mark on this athlete's card.";

// Actor and refusal are lines of What, not columns: columns push Recover off an iPad in portrait.
function ItemCells({ item, held }: { item: OutboxItem; held?: boolean }) {
  const resolution = discardNote(item);
  const detail = refusalDetail(item);
  return (
    <>
      <td className="mono cell-when" role="cell">
        {new Date(item.capturedAt).toLocaleTimeString()}
      </td>
      <td className="cell-what" role="cell">
        <span className="what-title">{describe(item)}</span>
        <span className="row-sub">By {item.actorSubject}</span>
        {/* The capture keeps its original official; the edit is shown, not merged. */}
        {item.editedBySubject && (
          <span className="row-sub">
            Edited by {item.editedByDisplayName ?? item.editedBySubject}
          </span>
        )}
        {detail && <span className="row-sub">{detail}</span>}
        {held && <span className="row-sub warn">{HELD_TEXT}</span>}
        {materialiseWarnings(item).map((warning, i) => (
          <span key={i} className="row-sub warn">
            {warning}
          </span>
        ))}
        {resolution && <span className="row-sub">{resolution}</span>}
      </td>
      <td className="cell-status" role="cell">
        <StatusBadge status={item.status} />
      </td>
    </>
  );
}

function RecoverableRow({
  item,
  resolvedBy,
  held,
}: {
  item: OutboxItem;
  resolvedBy: string | null;
  held: boolean;
}) {
  const [confirming, setConfirming] = useState(false);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [waiting, setWaiting] = useState({ adds: 0, results: 0 });
  const confirmTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const armedAt = useRef(0);
  const confirmRef = useRef<HTMLButtonElement | null>(null);
  const lastStatus = useRef(item.status);

  useEffect(() => () => clearTimeout(confirmTimer.current), []);
  useEffect(() => {
    if (confirming) confirmRef.current?.focus();
  }, [confirming]);

  // Read at arming time, not live: the sentence describes what this confirm
  // will do, and the queue behind a parked mint cannot move while it is parked.
  useEffect(() => {
    if (!confirming) return;
    let cancelled = false;
    void dependentsOf(item.id).then((items) => {
      if (cancelled) return;
      setWaiting({
        adds: items.filter((i) => i.kind === ENTRY_CREATE_KIND || i.kind === TEAM_CREATE_KIND)
          .length,
        results: items.filter(isResultCapture).length,
      });
    });
    return () => {
      cancelled = true;
    };
  }, [confirming, item.id]);

  // Disarm only on a real status change: what was armed is no longer what the
  // confirm would discard.
  useEffect(() => {
    if (lastStatus.current === item.status) return;
    lastStatus.current = item.status;
    clearTimeout(confirmTimer.current);
    setConfirming(false);
    setEditing(false);
  }, [item.status]);

  function disarm() {
    clearTimeout(confirmTimer.current);
    setConfirming(false);
  }

  async function requeueNow() {
    await requeue(item.id);
    void syncWorker.syncNow().catch(() => undefined);
  }

  function armTimer() {
    clearTimeout(confirmTimer.current);
    confirmTimer.current = setTimeout(() => setConfirming(false), CONFIRM_WINDOW_MS);
  }

  async function act(run: () => Promise<unknown>) {
    setError(null);
    try {
      await run();
    } catch (err) {
      setError(
        err instanceof InvalidTransitionError
          ? "This capture moved on before that could happen — read its status again."
          : err instanceof Error
            ? err.message
            : String(err),
      );
    }
  }

  const parked = item.status === "conflict" || item.status === "rejected";
  const what = describe(item);
  // Gated on the status too: a strip must never outlive the state that made
  // its action legal.
  const armed = confirming && parked;
  // No results read here to re-base against, so a lost concurrency race can
  // only be retyped from the deck.
  const stale = needsRebase(item);
  const correcting = editing && item.status === "conflict";
  const expanded = armed || correcting;

  return (
    <Fragment>
      <tr className={expanded ? "row-expanded" : undefined} role="row">
        <ItemCells item={item} held={held} />
        <td className="row-actions" role="cell">
          {/* Every act needs a named official: a requeue drains as the original capturer,
              so an anonymous holder must not fire one. */}
          {parked && resolvedBy === null && (
            <span className="row-sub">Sign in to recover this capture</span>
          )}
          {item.status === "conflict" && !stale && resolvedBy !== null && (
            <button type="button" onClick={() => void act(requeueNow)}>
              Requeue
            </button>
          )}
          {item.status === "conflict" && !stale && resolvedBy !== null && canEditCapture(item) && (
            <button type="button" disabled={correcting} onClick={() => setEditing(true)}>
              Edit and requeue
            </button>
          )}
          {item.status === "conflict" && stale && (
            <span className="row-sub">
              Only the event's deck can retype this one — it needs the recorded result to
              correct against. Otherwise, let it go.
            </span>
          )}
          {item.status === "conflict" && !stale && isCardCorrection(item) && (
            <span className="row-sub">
              The marks are corrected from the card — open the height's cell on the event's
              deck and use “Correct this height”. Otherwise, let it go.
            </span>
          )}
          {parked && resolvedBy !== null && (
            <button
              type="button"
              className="danger"
              disabled={armed}
              onClick={() => {
                setEditing(false);
                setConfirming(true);
                armedAt.current = Date.now();
                armTimer();
              }}
            >
              Discard…
            </button>
          )}
          {error && (
            <div className="row-error" role="alert">
              {error}
            </div>
          )}
        </td>
      </tr>
      {armed && (
        <tr className="row-strip" role="row">
          <td colSpan={ACTIVE_COLUMNS} role="cell">
            <span
              className="row-armed"
              role="alert"
              // The auto-disarm is for a strip left armed on a bench, never for
              // one somebody is standing in.
              onFocus={() => clearTimeout(confirmTimer.current)}
              onBlur={(e) => {
                if (!e.currentTarget.contains(e.relatedTarget as Node | null)) armTimer();
              }}
            >
              <span>
                Discard “{what}”? This capture never reached the server — letting it go leaves
                no record of it anywhere but this device.
                {waiting.adds > 0 &&
                  ` It also sends ${waiting.adds} queued add${waiting.adds === 1 ? "" : "s"} to the server without a heat.`}
                {waiting.results > 0 &&
                  ` ${waiting.results} captured result${waiting.results === 1 ? "" : "s"} behind it ${waiting.results === 1 ? "has" : "have"} nowhere to go and will be held here for retyping.`}
              </span>
              <button
                type="button"
                className="danger armed"
                ref={confirmRef}
                aria-label={`Confirm discarding ${what}`}
                onClick={() => {
                  if (resolvedBy === null) return;
                  if (Date.now() - armedAt.current < ARMING_LOCKOUT_MS) return;
                  disarm();
                  void act(() => discard(item.id, resolvedBy));
                }}
              >
                Confirm discard
              </button>
              <button type="button" onClick={disarm}>
                Cancel
              </button>
            </span>
          </td>
        </tr>
      )}
      {correcting && (
        <tr className="row-strip" role="row">
          <td colSpan={ACTIVE_COLUMNS} role="cell">
            <CaptureEditor item={item} onCancel={() => setEditing(false)} onError={setError} />
          </td>
        </tr>
      )}
    </Fragment>
  );
}

// A foul has no value to retype, a high-jump mark is a tap at a fixed bar, and the server refuses
// a whole card beside a typed value.
function canEditCapture(item: OutboxItem): boolean {
  if (item.kind === RESULT_CORRECT_KIND) return !isCardCorrection(item);
  if (item.kind === RESULT_SUBMIT_KIND) return true;
  return item.kind === RESULT_ATTEMPT_KIND && !(item.payload as ResultAttemptPayload).foul;
}

function CaptureEditor({
  item,
  onCancel,
  onError,
}: {
  item: OutboxItem;
  onCancel: () => void;
  onError: (message: string | null) => void;
}) {
  const payload = item.payload as ResultCapturePayload;
  const who = payload.display?.entryDisplayName ?? payload.entryId;
  // An item queued by an older build carries no measure; every queued item then was a time.
  const measureType = payload.display?.measureType ?? "Time";
  const session = useSession();
  const [raw, setRaw] = useState(
    () => payload.value ?? (payload.status ? statusLabel(payload.status) : ""),
  );
  const [busy, setBusy] = useState(false);
  const field = readMeasureField(measureType, raw);
  // A cell takes a mark, never a status: NM is derived from the series and the
  // other three belong to the whole result.
  const attempt = item.kind === RESULT_ATTEMPT_KIND;
  const savable = field.valid && (!attempt || field.mode === "value");

  async function save() {
    if (!savable || busy) return;
    setBusy(true);
    onError(null);
    const next = attempt
      ? { ...(item.payload as ResultAttemptPayload), value: field.value, foul: false }
      : field.statusCode
        ? { ...payload, value: null, status: STATUS_FROM_TYPED[field.statusCode] }
        : { ...payload, value: field.value, status: null };
    try {
      await editAndRequeue(item.id, next, {
        ...(session
          ? { editedBy: { subject: session.subject, displayName: session.displayName } }
          : {}),
      });
      void syncWorker.syncNow().catch(() => undefined);
    } catch (err) {
      onError(err instanceof Error ? err.message : String(err));
      setBusy(false);
      onCancel();
    }
  }

  return (
    <span className="outbox-time-edit">
      <span className="row-sub">
        Corrected {measureNoun(measureType)} for {who}
      </span>
      <MeasureInput
        id={`outbox-time-${item.id}`}
        measureType={measureType}
        raw={raw}
        onRawChange={setRaw}
        onEnter={() => void save()}
        disabled={busy}
        ariaLabel={`Corrected ${measureNoun(measureType)} for ${who}`}
      />
      <button
        type="button"
        className="primary"
        disabled={busy || !savable}
        onClick={() => void save()}
      >
        Save and requeue
      </button>
      <button type="button" disabled={busy} onClick={onCancel}>
        Cancel
      </button>
    </span>
  );
}

// Captured into state before it is acknowledged, so acknowledging does not erase the message.
function useTidiedNotice(): number | null {
  const [tidied, setTidied] = useState<number | null>(null);
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const prune = await unseenPrune();
      if (cancelled || !prune) return;
      setTidied(prune.prunedCount);
      await markPruneSeen();
    })();
    return () => {
      cancelled = true;
    };
  }, []);
  return tidied;
}

// The server warns rather than refuses an implausible race, so this row is the only place the
// warning is read. Free copy: rendered, never branched on.
function materialiseWarnings(item: OutboxItem): string[] {
  if (item.kind !== RACE_MATERIALISE_KIND) return [];
  return item.serverResponse?.warnings ?? [];
}

// The only trace the discarded capture ever existed.
function discardNote(item: OutboxItem): string | null {
  if (item.status !== "discarded" || !item.resolvedBy) return null;
  const at = item.resolvedAt ? new Date(item.resolvedAt).toLocaleTimeString() : null;
  return at ? `Discarded by ${item.resolvedBy} at ${at}` : `Discarded by ${item.resolvedBy}`;
}

// An accepted row has shed its `display`; drop the person rather than show a raw entry id.
function subjectOf(item: OutboxItem, name: string | undefined, entryId: string): string | null {
  return name ?? (item.status === "accepted" ? null : entryId);
}

// A parked item is read cold by someone deciding whether it happened, so each names exactly what
// the tap was: which attempt, bar, division, house or race.
function describe(item: OutboxItem): string {
  if (item.kind === RESULT_SUBMIT_KIND || item.kind === RESULT_CORRECT_KIND) {
    const payload = item.payload as ResultCapturePayload;
    const value = payload.status ?? payload.value ?? "?";
    const what = item.kind === RESULT_CORRECT_KIND ? "Correction to" : "Result";
    const who = subjectOf(item, payload.display?.entryDisplayName, payload.entryId);
    return who ? `${what} ${value} — ${who}` : `${what} ${value}`;
  }
  if (item.kind === RESULT_ATTEMPT_KIND) {
    const payload = item.payload as ResultAttemptPayload;
    const measureType = payload.display?.measureType ?? "Distance";
    const what = payload.foul
      ? "X"
      : payload.value == null
        ? "?"
        : formatCapturedValue(measureType, payload.value);
    const who = subjectOf(item, payload.display?.entryDisplayName, payload.entryId);
    // Attempt 2 of six rounds is a different thing from attempt 2 of two.
    const count = payload.display?.attemptCount;
    const which = count ? `Attempt ${payload.ordinal} of ${count}` : `Attempt ${payload.ordinal}`;
    const attempt = `${which} · ${what}`;
    return who ? `${attempt} — ${who}` : attempt;
  }
  if (item.kind === RESULT_JUMP_KIND) {
    const payload = item.payload as ResultJumpPayload;
    const who = subjectOf(item, payload.display?.entryDisplayName, payload.entryId);
    const jump = `Jump ${formatMeasure("Height", payload.height)} · ${payload.mark}`;
    return who ? `${jump} — ${who}` : jump;
  }
  if (item.kind === RACE_CREATE_KIND) {
    const payload = item.payload as RaceCreatePayload;
    const label = payload.label?.trim();
    return label ? `Create race "${label}"` : "Create race";
  }
  if (item.kind === RACE_MATERIALISE_KIND) {
    const payload = item.payload as RaceMaterialisePayload;
    const title = payload.display?.title?.trim();
    return title ? `Create race for ${title}` : "Create race for a division";
  }
  if (item.kind === TEAM_CREATE_KIND) {
    const payload = item.payload as TeamCreatePayload;
    return `Relay team — ${payload.houseCode}`;
  }
  if (item.kind === ENTRY_ASSIGN_KIND) {
    const payload = item.payload as EntryAssignPayload;
    const who = subjectOf(item, payload.display?.entryDisplayName, payload.entryId);
    // A null race is a cleared placement, not a lane-less one.
    if (payload.raceId === null) return who ? `Clear placement for ${who}` : "Clear placement";
    const where = payload.display?.raceTitle ? ` — ${payload.display.raceTitle}` : "";
    const lane = payload.lane == null ? "" : ` in lane ${payload.lane}`;
    return `${who ? `Place ${who}` : "Placement"}${lane}${where}`;
  }
  return item.kind;
}
