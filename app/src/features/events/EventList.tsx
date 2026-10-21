import { useState } from "react";
import type { CarnivalEvent } from "../../api/types";
import { EventRow } from "./EventRow";
import {
  ageScopeLabel,
  ageScopeOrder,
  categoryValueLabel,
  facetLabel,
  parseTag,
} from "./eventLabels";

/**
 * The uncategorised option's value: a null category has no wire code, and ""
 * already means "any", so it needs one the wire cannot produce.
 */
const NO_CATEGORY = "(none)";

/** The order categories are offered in; anything else sorts after them. */
const CATEGORY_ORDER = ["M", "F", "S"];

/** Filterable event list — 100+ events across divisions need narrowing. */
export function EventList({
  events,
  canManage,
  busy,
  onAction,
}: {
  events: CarnivalEvent[];
  canManage: boolean;
  busy: boolean;
  onAction: (action: () => Promise<unknown>) => Promise<void>;
}) {
  const [q, setQ] = useState("");
  const [age, setAge] = useState("");
  const [cat, setCat] = useState("");
  const [facetSel, setFacetSel] = useState<Record<string, string>>({});
  const [type, setType] = useState("");
  const [status, setStatus] = useState("");

  // Filtering keys off the division's own age code; the option text is what staff read it as.
  const ageOptionLabels = new Map<string, string>();
  for (const e of events)
    if (!ageOptionLabels.has(e.ageGroup))
      ageOptionLabels.set(e.ageGroup, ageScopeLabel(e) ?? "All ages");
  const ageOptions = [...ageOptionLabels.entries()].sort(
    ([a], [b]) => ageScopeOrder(a) - ageScopeOrder(b) || a.localeCompare(b),
  );
  // Categories come from the events present, not from an enumerated M/F: S is a
  // first-class category and an uncategorised division is a value of its own.
  const catOptions = [...new Set(events.map((e) => e.category ?? NO_CATEGORY))].sort(
    (a, b) => categoryRank(a) - categoryRank(b) || a.localeCompare(b),
  );
  const statusOptions = [...new Set(events.map((e) => e.status))];

  // One dropdown per facet with at least two distinct values: a single-value dropdown filters
  // nothing.
  const facetValues = new Map<string, Set<string>>();
  for (const e of events)
    for (const t of e.tags ?? []) {
      const { facet, value } = parseTag(t);
      if (!facetValues.has(facet)) facetValues.set(facet, new Set());
      facetValues.get(facet)!.add(value);
    }
  const facets = [...facetValues.entries()]
    .filter(([, v]) => v.size >= 2)
    .map(([facet, v]) => ({ facet, values: [...v].sort((a, b) => a.localeCompare(b)) }))
    .sort((a, b) => a.facet.localeCompare(b.facet));

  const filtered = events.filter((e) => {
    if (age && e.ageGroup !== age) return false;
    if (cat && (e.category ?? NO_CATEGORY) !== cat) return false;
    for (const { facet } of facets) {
      const sel = facetSel[facet];
      if (sel && !(e.tags ?? []).some((t) => t === `${facet}:${sel}`)) return false;
    }
    if (type && (e.format ?? "Individual") !== type) return false;
    if (status && e.status !== status) return false;
    if (q.trim() !== "") {
      const needle = q.trim().toLowerCase();
      const hay = `${e.definitionName ?? ""} ${e.definitionCode ?? ""}`.toLowerCase();
      if (!hay.includes(needle)) return false;
    }
    return true;
  });

  const active =
    q.trim() !== "" || age || cat || type || status || Object.values(facetSel).some((v) => v);

  return (
    <>
      <div className="filter-bar">
        <input
          className="filter-search"
          placeholder="Filter events by name…"
          value={q}
          onChange={(e) => setQ(e.target.value)}
          aria-label="Filter events by name"
        />
        <select value={age} onChange={(e) => setAge(e.target.value)} aria-label="Age group">
          <option value="">Any age</option>
          {ageOptions.map(([key, label]) => (
            <option key={key} value={key}>{label}</option>
          ))}
        </select>
        <select value={cat} onChange={(e) => setCat(e.target.value)} aria-label="Category">
          <option value="">Any category</option>
          {catOptions.map((value) => (
            <option key={value} value={value}>
              {categoryValueLabel(value === NO_CATEGORY ? null : value)}
            </option>
          ))}
        </select>
        {facets.map(({ facet, values }) => (
          <select
            key={facet}
            value={facetSel[facet] ?? ""}
            onChange={(e) => setFacetSel((prev) => ({ ...prev, [facet]: e.target.value }))}
            aria-label={facetLabel(facet)}
          >
            <option value="">Any {facetLabel(facet).toLowerCase()}</option>
            {values.map((v) => (
              <option key={v} value={v}>{v}</option>
            ))}
          </select>
        ))}
        <select value={type} onChange={(e) => setType(e.target.value)} aria-label="Event type">
          <option value="">All types</option>
          <option value="Individual">Individual</option>
          <option value="Team">Relays</option>
        </select>
        <select value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status">
          <option value="">Any status</option>
          {statusOptions.map((s) => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>
        {active && (
          <button type="button" onClick={() => { setQ(""); setAge(""); setCat(""); setFacetSel({}); setType(""); setStatus(""); }}>
            Clear
          </button>
        )}
      </div>
      <p className="row-sub" aria-live="polite">
        Showing {filtered.length} of {events.length} events
      </p>
      {filtered.length === 0 ? (
        <p>No events match these filters.</p>
      ) : (
        <ul className="row-list card">
          {filtered.map((event) => (
            <EventRow
              key={event.id}
              event={event}
              canManage={canManage}
              busy={busy}
              onAction={onAction}
            />
          ))}
        </ul>
      )}
    </>
  );
}

function categoryRank(category: string): number {
  const i = CATEGORY_ORDER.indexOf(category);
  return i === -1 ? CATEGORY_ORDER.length : i;
}
