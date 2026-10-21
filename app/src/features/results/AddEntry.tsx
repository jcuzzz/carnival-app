import { useEffect, useRef, useState } from "react";
import { ApiError } from "../../api/client";
import { searchEligibleStudentsPage } from "../../api/resources";
import type { EligibleStudent } from "../../api/types";
import { filterRoster, loadCachedRosterPage, saveCachedRoster } from "../../db/roster";
import { submitEntryViaOutbox } from "../../entries/submitEntry";
import { useOnline } from "../../shell/useOnline";

/**
 * The eligible roster is browsable and typing narrows it. What leaves the list is decided by the
 * server and the outbox, never a local "added" set, so a refused add puts the student back.
 */
export function AddEntry({
  carnivalId,
  eventId,
  actorSubject,
  enteredStudentIds,
  failedStudentIds,
  onAdded,
  raceIdForAdd,
  blocked,
}: {
  carnivalId: string;
  eventId: string;
  actorSubject: string | null;
  /** Students this event already holds — server entries + queued adds. */
  enteredStudentIds: ReadonlySet<string>;
  /** Queued adds the server refused (or that were withdrawn): offer them again. */
  failedStudentIds: ReadonlySet<string>;
  onAdded: () => void;
  /** Resolved at tap time: a heat minted here is enqueued first and the create rides behind it. */
  raceIdForAdd?: () => Promise<string | null>;
  /** Why nothing can be added: a refused heat, whose adds would queue behind a race never made. */
  blocked?: string | undefined;
}) {
  const online = useOnline();
  const [term, setTerm] = useState("");
  const [cache, setCache] = useState<{
    students: EligibleStudent[];
    truncated: boolean;
    limit: number | null;
  } | null>(null);
  const [page, setPage] = useState<{
    students: EligibleStudent[];
    truncated: boolean;
    limit: number;
  } | null>(null);
  const [loading, setLoading] = useState(true);
  // A double-tap guard until the outbox live query catches up; `enteredStudentIds` keeps students
  // out.
  const [justAdded, setJustAdded] = useState<Set<string>>(() => new Set());
  const [announced, setAnnounced] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  // navigator.onLine is not reachability: the pool AP is usually up with the uplink dead. A failed
  // search is treated exactly like offline.
  const [searchFailed, setSearchFailed] = useState(false);

  // Set unconditionally: no cached roster clears the list, never leaving another event's students.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      const cached = await loadCachedRosterPage(eventId);
      if (!cancelled) setCache(cached);
    })();
    return () => {
      cancelled = true;
    };
  }, [eventId]);

  // An empty box is a real request and the one that refreshes the offline snapshot. Opening the
  // panel is not debounced: that delay reads as a broken panel.
  useEffect(() => {
    if (!online) {
      setPage(null);
      setSearchFailed(false);
      setLoading(false);
      return;
    }
    let cancelled = false;
    const browsing = term.trim() === "";
    setLoading(true);
    const run = async () => {
      try {
        const fresh = await searchEligibleStudentsPage(carnivalId, eventId, term);
        if (cancelled) return;
        setPage(fresh);
        setSearchFailed(false);
        setError(null);
        if (browsing) {
          setCache({ students: fresh.students, truncated: fresh.truncated, limit: fresh.limit });
          await saveCachedRoster(eventId, fresh.students, {
            truncated: fresh.truncated,
            limit: fresh.limit,
          });
        }
      } catch (err) {
        if (cancelled) return;
        setPage(null);
        setSearchFailed(true);
        setError(
          err instanceof ApiError
            ? err.message
            : "Couldn't reach the server — showing the saved roster.",
        );
      } finally {
        if (!cancelled) setLoading(false);
      }
    };
    if (browsing) {
      void run();
      return () => {
        cancelled = true;
      };
    }
    const handle = setTimeout(() => void run(), 200);
    return () => {
      cancelled = true;
      clearTimeout(handle);
    };
  }, [term, online, carnivalId, eventId]);

  // Dismissing a refused add drops the id from both sets, so this prune must not put it back.
  useEffect(() => {
    setJustAdded((prev) => {
      if (prev.size === 0) return prev;
      const next = new Set(
        [...prev].filter((id) => enteredStudentIds.has(id) || !failedStudentIds.has(id)),
      );
      return next.size === prev.size ? prev : next;
    });
  }, [enteredStudentIds, failedStudentIds]);

  const searching = term.trim() !== "";
  const savedRoster = !online || searchFailed;
  const source = savedRoster ? filterRoster(cache?.students ?? [], term) : (page?.students ?? []);
  const students = source.filter(
    (s) => !enteredStudentIds.has(s.studentId) && !justAdded.has(s.studentId),
  );
  const truncated = savedRoster ? (cache?.truncated ?? false) : (page?.truncated ?? false);
  // Only the server page quotes a size: the saved-roster notice never does.
  const limit = page?.limit;
  // No roster on the device is not "everyone is entered"; saying the wrong one sends someone
  // hunting a bug instead of a hotspot.
  const noSnapshot = savedRoster && cache === null;
  const pending = loading && !savedRoster && students.length === 0;

  // Focus moves to the next Add; when the list empties, to the status line rather than the search
  // box, where an iPad keyboard would slide up over a finished list.
  const addRefs = useRef(new Map<string, HTMLButtonElement | null>());
  const statusRef = useRef<HTMLParagraphElement>(null);
  const [focusTarget, setFocusTarget] = useState<string | null>(null);
  useEffect(() => {
    if (!focusTarget) return;
    const el = addRefs.current.get(focusTarget);
    if (el) {
      el.focus();
      setFocusTarget(null);
    }
  }, [focusTarget, students]);

  async function add(student: EligibleStudent) {
    if (blocked) return;
    if (!actorSubject) {
      setError("Sign in to add entries — the actor is captured on every add (ADR-0006).");
      return;
    }
    if (justAdded.has(student.studentId) || enteredStudentIds.has(student.studentId)) return;
    setError(null);

    const index = students.findIndex((s) => s.studentId === student.studentId);
    const next = students[index + 1] ?? students[index - 1];
    setJustAdded((prev) => new Set(prev).add(student.studentId));
    setAnnounced(student.displayName);
    if (next) setFocusTarget(next.studentId);
    else statusRef.current?.focus();

    try {
      // Minting the heat first puts it in front of the create in the queue.
      const raceId = raceIdForAdd ? await raceIdForAdd() : null;
      await submitEntryViaOutbox({
        carnivalId,
        carnivalEventId: eventId,
        student,
        actorSubject,
        raceId,
      });
    } catch {
      // The queue itself refused the capture (storage evicted, quota, private
      // mode). Nothing was recorded anywhere, so the swimmer goes straight back.
      setJustAdded((prev) => {
        const back = new Set(prev);
        back.delete(student.studentId);
        return back;
      });
      setAnnounced(null);
      setError(`Couldn't queue ${student.displayName} on this device — try again.`);
      return;
    }
    onAdded();
  }

  return (
    <div className="card">
      <h2 className="flush-top">Add entry</h2>
      {blocked && (
        <div className="alert notice" role="status">
          {blocked}
        </div>
      )}
      <form className="entry-form" onSubmit={(e) => e.preventDefault()}>
        <label className="visually-hidden" htmlFor="student-search">
          Search eligible students
        </label>
        <input
          id="student-search"
          type="search"
          className="search-grow"
          placeholder="Search eligible students by name"
          enterKeyHint="search"
          autoComplete="off"
          autoCorrect="off"
          autoCapitalize="off"
          spellCheck={false}
          value={term}
          onChange={(e) => setTerm(e.target.value)}
        />
        {savedRoster && (
          <span className="badge muted" title="Can't reach the server — searching the saved roster">
            {online ? "saved roster" : "offline"}
          </span>
        )}
      </form>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      {/* -1, not focusable by tab: it is a landing place for focus after the
          last row goes, and the announcement of what just happened. */}
      <p className="row-sub" role="status" tabIndex={-1} ref={statusRef}>
        {announced ? `Added ${announced}.` : ""}
      </p>
      {truncated && (
        <p className="row-sub">
          {savedRoster
            ? // Offline the filter runs over a fixed snapshot: typing cannot reach
              // anyone this device never cached, whatever metadata it carries.
              "The saved roster may be incomplete — type to narrow."
            : searching
              ? `More matches than fit — showing the first ${limit}. Keep typing to narrow.`
              : `Showing the first ${limit} — type to narrow.`}
        </p>
      )}
      {pending && <p className="row-sub">Loading eligible students…</p>}
      {!pending && students.length === 0 && (
        <p className="row-sub">
          {searching
            ? `No eligible students match${savedRoster ? " in the saved roster." : "."}`
            : noSnapshot
              ? "No saved roster on this device for this event — connect once to load it."
              : `Everyone eligible is already entered in this event${
                  savedRoster ? ", according to the saved roster." : "."
                }`}
        </p>
      )}
      {students.length > 0 && (
        <ul className="row-list eligible-list">
          {students.map((student) => (
            <li key={student.studentId}>
              <div className="entry-row">
                <span className="row-main">
                  <span className="row-title">{student.displayName}</span>
                  <span className="row-sub">
                    {student.houseCode} · Yr {student.yearLevel} · {student.ageGroup}
                  </span>
                </span>
                <button
                  type="button"
                  className="add-eligible"
                  disabled={blocked !== undefined}
                  ref={(el) => {
                    addRefs.current.set(student.studentId, el);
                  }}
                  onClick={() => void add(student)}
                >
                  Add
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
