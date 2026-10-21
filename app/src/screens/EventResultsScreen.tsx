import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
  type RefObject,
} from "react";
import { ArrowLeft } from "lucide-react";
import { errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  getCarnivalEvent,
  getCarnivalSchedule,
  getCarnivalSettings,
  getRecords,
  listCarnivalEvents,
  listEntries,
  listRaces,
  listResults,
} from "../api/resources";
import { useReachable } from "../api/reachability";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import type { CarnivalEvent, Entry, ResultDto } from "../api/types";
import { hasRole, useSession } from "../auth/session";
import { type OutboxItem } from "../db/db";
import { discard, isAbandoned } from "../sync/outbox";
import { useOutboxItems } from "../sync/useOutboxStatus";
import { resultItemsForEvent } from "../results/submitResult";
import { attemptItemsForEvent } from "../results/submitAttempt";
import { jumpItemsForEvent } from "../results/submitJump";
import { cardCleared } from "../results/jumpCard";
import { entryItemsForEvent } from "../entries/submitEntry";
import { teamItemsForEvent } from "../entries/submitTeam";
import { submitAssignmentViaOutbox, submitSwapViaOutbox, type SwapSide } from "../races/assignEntry";
import { materialiseIntents, submitMaterialiseViaOutbox } from "../races/submitRace";
import {
  ENTRY_ASSIGN_KIND,
  RACE_MATERIALISE_KIND,
  type EntryCreatePayload,
} from "../api/outboxTransport";
import { DivisionSelector } from "../features/events/DivisionSelector";
import { EventClassification } from "../features/events/EventClassification";
import { NextEventLink } from "../features/events/NextEventLink";
import { OpenEventNotice } from "../features/events/OpenEventNotice";
import { divisionChipLabel } from "../features/events/divisions";
import { progressText, queuedProgressByEvent, sumProgress } from "../features/events/progress";
import { divisionKey, indexRecords } from "../features/records/standingRecords";
import { deckRaces } from "../features/races/divisionRaces";
import { DeckRaceBlock } from "../features/races/DeckRaceBlock";
import {
  EntryLaneControl,
  type LaneControlHandle,
  type LaneRaceOption,
  type SwapOffer,
} from "../features/races/EntryLaneControl";
import { HeatPicker, type HeatOption } from "../features/races/HeatPicker";
import { PROSPECTIVE_HEAT, heatsOfDivision, numberHeats, selectedHeat } from "../features/races/heats";
import { placementOf, vacantLanes } from "../features/races/deckPlacement";
import { raceTitle } from "../features/races/raceGroups";
import {
  acceptedAfter,
  laneOptions,
  placementsByEntry,
  raceRoster,
  type RosterRow,
} from "../features/races/raceLanes";
import { AddEntry } from "../features/results/AddEntry";
import { AddTeam } from "../features/results/AddTeam";
import { deckRows, type DeckRow } from "../features/results/deckRows";
import { EntryRow } from "../features/results/EntryRow";
import { HighJumpGrid, type JumpGridRow } from "../features/results/HighJumpGrid";
import { DeckProgress } from "../features/results/DeckProgress";
import { href, navigate, useRouteQuery } from "../router";
import { oldestUpdatedAt } from "../shell/Freshness";
import { scrollBehavior } from "../shell/motion";

// Only the read half is down here: every submission is outbox-backed, so the reassurance holds.
const READ_ERROR_NETWORK_TEXT =
  "Can't reach the server, so this event's entries and results may be out of date. Nothing you've captured is lost — it stays on this device and syncs when the server returns.";

// Every add behind a refused heat would name a race that will never exist.
const REFUSED_HEAT_TEXT = "The server refused this heat — clear it from the Outbox first";

/** Slow enough to be a background cost, fast enough to correct a wrong ribbon. */
const DECK_POLL_MS = 60_000;

const ICON = 20;

// Other devices' captures reach the deck only by re-reading. Off while unreachable: the SW answers
// from cache after a 5 s timeout and a cache hit reports the uplink alive, flipping the strip.
export function deckPollMs(reachable: boolean): number {
  return reachable ? DECK_POLL_MS : 0;
}

export default function EventResultsScreen({
  carnivalId,
  eventId,
}: {
  carnivalId: string;
  eventId: string;
}) {
  const session = useSession();
  const reachable = useReachable();
  const heatParam = new URLSearchParams(useRouteQuery()).get("heat");

  // A bar raised on another deck is this deck's heights axis, and nothing here invalidates it.
  const eventState = useApiQuery(
    keys.carnivalEvent(carnivalId, eventId),
    () => getCarnivalEvent(carnivalId, eventId),
    { refetchIntervalMs: deckPollMs(reachable) },
  );
  const entries = useApiQuery(
    keys.entries(carnivalId, eventId),
    () => listEntries(carnivalId, eventId),
    { refetchIntervalMs: deckPollMs(reachable) },
  );
  const results = useApiQuery(
    keys.results(carnivalId, eventId),
    () => listResults(carnivalId, eventId),
    { refetchIntervalMs: deckPollMs(reachable) },
  );
  // Additive: a failure hides the selector and the suggestion, so neither read joins `loadError`.
  const siblings = useApiQuery(keys.carnivalEvents(carnivalId), () =>
    listCarnivalEvents(carnivalId),
  );
  const schedule = useApiQuery(keys.schedule(carnivalId), () => getCarnivalSchedule(carnivalId));
  // `EntryDto` carries heat and lane but never the race; only this list knows an entry's race.
  const races = useApiQuery(keys.races(carnivalId), () => listRaces(carnivalId), {
    refetchIntervalMs: deckPollMs(reachable),
  });
  // A failure is not an error here: `laneOptions` falls back to a typical pool.
  const settings = useApiQuery(keys.carnivalSettings(carnivalId), () =>
    getCarnivalSettings(carnivalId),
  );
  const autoFillLanes = settings.data?.autoFillLanes === true;
  // Additive: it only badges rows, so a failure or an empty book badges nothing.
  const records = useApiQuery(keys.records(), () => getRecords());
  const invalidate = useInvalidate();
  const outboxItems = useOutboxItems();

  const event = eventState.data;
  // Offline, saved rows under a chip reading "0/6" would lie without the queued overlay.
  const queuedProgress = useMemo(() => queuedProgressByEvent(outboxItems), [outboxItems]);

  // The event list knows every division, so a failed per-division read still renders the deck.
  const known = event ?? siblings.data?.find((e) => e.id === eventId);
  const queuedEntryItems = useMemo(
    () => entryItemsForEvent(outboxItems, eventId),
    [outboxItems, eventId],
  );
  const queuedTeamItems = useMemo(
    () => teamItemsForEvent(outboxItems, eventId),
    [outboxItems, eventId],
  );
  const rows = useMemo(
    () => (known ? deckRows(entries.data, queuedEntryItems, queuedTeamItems) : []),
    [known, entries.data, queuedEntryItems, queuedTeamItems],
  );
  const standingRecord = useMemo(() => {
    if (!known) return undefined;
    return indexRecords(records.data).byDivision.get(
      divisionKey(known.eventDefinitionId, known.ageGroup, known.category),
    );
  }, [known, records.data]);
  const resultsByEntry = useMemo(() => {
    const byEntry = new Map<string, ResultDto>();
    for (const result of results.data ?? []) byEntry.set(result.entryId, result);
    return byEntry;
  }, [results.data]);
  // The grid still needs a cleared card's result, as the base of the next correction.
  const recordedByEntry = useMemo(
    () =>
      new Map(
        [...resultsByEntry].filter(([, result]) => !cardCleared(result, known?.measureType)),
      ),
    [resultsByEntry, known?.measureType],
  );
  const outboxByEntry = useMemo(
    () => resultItemsForEvent(outboxItems, eventId),
    [outboxItems, eventId],
  );
  // Apart from the row lookup so a queued cell never takes the derived best mark's slot.
  const attemptsByEntry = useMemo(
    () => attemptItemsForEvent(outboxItems, eventId),
    [outboxItems, eventId],
  );
  // Every mark at a height is kept: three taps at one bar are three marks.
  const jumpsByEntry = useMemo(
    () => jumpItemsForEvent(outboxItems, eventId),
    [outboxItems, eventId],
  );
  // An unknown result state must not offer a form: a capture queued over one
  // head-of-line blocks the drain on EVENT_NOT_OPEN.
  const resultsUnavailable = results.error !== undefined;

  // Derived, never a local "clicked" set: a refused add must hand the swimmer back, and offline
  // the saved roster cannot know who is already entered.
  const entryCreateStudentIds = useMemo(() => {
    const entered = new Set<string>();
    const failed = new Set<string>();
    for (const e of entries.data ?? []) if (e.studentId) entered.add(e.studentId);
    for (const item of queuedEntryItems) {
      const studentId = (item.payload as EntryCreatePayload).studentId;
      if (item.status === "rejected" || item.status === "conflict" || isAbandoned(item.status))
        failed.add(studentId);
      else entered.add(studentId);
    }
    return { entered, failed };
  }, [entries.data, queuedEntryItems]);

  const resultedEntryIds = useMemo(
    () => new Set<string>([...resultsByEntry.keys(), ...outboxByEntry.keys()]),
    [resultsByEntry, outboxByEntry],
  );
  const hasDisplay = (id: string) => resultedEntryIds.has(id);

  const inputRefs = useRef(new Map<string, RefObject<HTMLInputElement | null>>());
  const refFor = (id: string) => {
    let r = inputRefs.current.get(id);
    if (!r) {
      r = { current: null };
      inputRefs.current.set(id, r);
    }
    return r;
  };
  const [focusTarget, setFocusTarget] = useState<string | null>(null);
  const [queueError, setQueueError] = useState<string | null>(null);
  // One materialisation per division, however fast the taps come.
  const materialising = useRef(new Set<string>());
  const mintedHeat = useRef<{ eventId: string; raceId: string } | null>(null);
  const mintInFlight = useRef<Promise<string> | null>(null);
  // A refused or discarded mint names a race the server will never have. Absent from the outbox
  // means the live query has not caught up yet.
  const mintStands = useCallback(
    (raceId: string): boolean => {
      const item = (outboxItems ?? []).find((i) => i.id === raceId);
      return (
        item === undefined ||
        item.status === "queued" ||
        item.status === "syncing" ||
        item.status === "accepted"
      );
    },
    [outboxItems],
  );
  useEffect(() => {
    const held = mintedHeat.current;
    if (held && !mintStands(held.raceId)) mintedHeat.current = null;
  }, [mintStands]);
  const [busyDivisions, setBusyDivisions] = useState<ReadonlySet<string>>(() => new Set());
  useEffect(() => {
    if (!focusTarget) return;
    const el = inputRefs.current.get(focusTarget)?.current;
    if (el) {
      el.focus();
      el.scrollIntoView({ block: "center", behavior: scrollBehavior() });
      setFocusTarget(null);
    }
    // If the target row hasn't re-rendered as a form yet, this effect re-runs
    // when the data maps change and focuses it then.
  }, [focusTarget, rows, resultsByEntry, outboxByEntry]);
  // A division switch re-renders in place, so the previous division's queue error and focus
  // target would otherwise carry over.
  useEffect(() => {
    setQueueError(null);
    setFocusTarget(null);
  }, [eventId]);

  // Rendered order, not the flat sort, which skips fields where a heat holds unlaned rows.
  function handleSaved(savedEntryId: string) {
    const idx = renderedRows.findIndex((e) => e.entry.id === savedEntryId);
    const next = renderedRows.slice(idx + 1).find((e) => !hasDisplay(e.entry.id));
    setFocusTarget(next ? next.entry.id : null);
  }

  async function dismissCreate(item: OutboxItem) {
    // Never a delete: the outbox is append-only.
    if (!session) return;
    await discard(item.id, session.subject);
  }

  // An accepted drain is what announces the read cache stale.
  const acceptedCount = useMemo(
    () => [...outboxByEntry.values()].filter((i) => i.status === "accepted").length,
    [outboxByEntry],
  );
  // The event list is named explicitly: its key is a prefix of the results key, so prefix
  // matching never reaches it.
  useEffect(() => {
    if (acceptedCount > 0)
      invalidate(keys.results(carnivalId, eventId), keys.carnivalEvents(carnivalId));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [acceptedCount]);

  // A landed mark is the server's card now, so the grid re-reads it.
  const acceptedJumpCount = useMemo(
    () =>
      [...jumpsByEntry.values()].flatMap((heights) => [...heights.values()].flat())
        .filter((i) => i.status === "accepted").length,
    [jumpsByEntry],
  );
  useEffect(() => {
    if (acceptedJumpCount > 0)
      invalidate(keys.results(carnivalId, eventId), keys.carnivalEvents(carnivalId));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [acceptedJumpCount]);

  // Same for a queued add, so the authoritative row replaces the optimistic one.
  const acceptedEntryCount = useMemo(
    () => queuedEntryItems.filter((i) => i.status === "accepted").length,
    [queuedEntryItems],
  );
  useEffect(() => {
    if (acceptedEntryCount > 0)
      invalidate(
        keys.entries(carnivalId, eventId),
        keys.carnivalEvents(carnivalId),
        // Auto-fill may have placed the swimmer in a minted race. Unconditional: gating on the
        // flag loses this when the settings read has not landed.
        keys.races(carnivalId),
      );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [acceptedEntryCount]);

  // A landed materialisation (or a chained placement) moves entries between
  // races, and the races read is the only thing that can see it.
  const acceptedMarshallingCount = useMemo(
    () =>
      (outboxItems ?? []).filter(
        (i) =>
          i.status === "accepted" &&
          (i.kind === RACE_MATERIALISE_KIND || i.kind === ENTRY_ASSIGN_KIND) &&
          (i.payload as { carnivalId?: string }).carnivalId === carnivalId,
      ).length,
    [outboxItems, carnivalId],
  );
  useEffect(() => {
    if (acceptedMarshallingCount > 0)
      invalidate(
        keys.races(carnivalId),
        keys.entries(carnivalId, eventId),
        keys.carnivalEvents(carnivalId),
      );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [acceptedMarshallingCount]);

  // What this device itself has minted, which the server may not know yet.
  const intents = useMemo(
    () => materialiseIntents(outboxItems, carnivalId),
    [outboxItems, carnivalId],
  );
  // A mint or a lane from seconds ago is only in the outbox, not the races read.
  const placements = useMemo(
    () => placementsByEntry(outboxItems, carnivalId),
    [outboxItems, carnivalId],
  );
  // An acknowledgement keeps speaking for the row until the races read refetches: in that window
  // the server's answer is the stale one.
  const acceptedFresh = useMemo(() => acceptedAfter(races.updatedAt), [races.updatedAt]);
  // An acknowledged mint the races read has not caught up to still is the race; dropping it
  // reverts the block to "Not yet a race" and takes the swimmer just placed with it.
  const standingMaterialisations = useMemo(
    () =>
      intents
        .filter(
          (i) =>
            i.status === "queued" ||
            i.status === "syncing" ||
            // A refused mint keeps its block, or the refusal has nowhere to be read.
            i.status === "conflict" ||
            i.status === "rejected" ||
            // Only while the read cannot know about it, or a race an administrator deleted would
            // resurrect for the whole retention window.
            (i.status === "accepted" &&
              !(races.data ?? []).some((r) => r.id === i.id) &&
              acceptedFresh(i.item)),
        )
        .map((i) => i.item),
    [intents, races.data, acceptedFresh],
  );
  const parkedMaterialisations = useMemo(
    () =>
      new Set(
        intents.filter((i) => i.status === "conflict" || i.status === "rejected").map((i) => i.id),
      ),
    [intents],
  );
  // Minting needs a resolved races read: an unresolved one looks like no race, and the server has
  // no duplicate-race guard.
  const racesResolved = races.data !== undefined;
  const overlaid = useCallback(
    (entryId: string) => {
      const item = placements.get(entryId);
      return (
        item !== undefined &&
        (item.status === "queued" || item.status === "syncing" || acceptedFresh(item))
      );
    },
    [placements, acceptedFresh],
  );
  // The server's rows only: a queued add's stamp may name a mint that is later refused.
  const entryById = useMemo(
    () => new Map((entries.data ?? []).map((e) => [e.id, e])),
    [entries.data],
  );
  // Where the races read cannot see the entry, the entries read's own stamp speaks.
  const placeOf = useCallback(
    (entryId: string) => {
      const place = placementOf(races.data, placements, entryId, acceptedFresh);
      if (place.raceId !== null || !racesResolved || overlaid(entryId)) return place;
      const stamped = entryById.get(entryId);
      return stamped?.raceId ? { raceId: stamped.raceId, lane: stamped.lane ?? null } : place;
    },
    [races.data, placements, acceptedFresh, racesResolved, overlaid, entryById],
  );
  const laneShown = useCallback(
    (entry: Entry): number | null => {
      const place = placeOf(entry.id);
      return place.raceId !== null || overlaid(entry.id) ? place.lane : (entry.lane ?? null);
    },
    [placeOf, overlaid],
  );
  // Heats the entries read names that the races read may not.
  const entryRaceIds = useMemo(
    () => [...new Set(rows.map((row) => row.entry.raceId).filter((id): id is string => !!id))],
    [rows],
  );
  const divisionRaces = useMemo(
    () => deckRaces(eventId, races.data, standingMaterialisations, entryRaceIds),
    [eventId, races.data, standingMaterialisations, entryRaceIds],
  );
  // The carnival-wide "Race N", not the chip's heat number: the outbox is read away from the deck.
  const raceTitleOf = useCallback(
    (raceId: string): string | undefined => {
      const race = divisionRaces.find((r) => r.id === raceId);
      return race ? raceTitle(race) : undefined;
    },
    [divisionRaces],
  );
  // This device's undelivered placements folded in: the source for lane vacancy on every picker.
  const rostersByRace = useMemo(() => {
    const byRace = new Map<string, RosterRow[]>();
    for (const race of divisionRaces)
      byRace.set(race.id, raceRoster(race, placements, acceptedFresh));
    return byRace;
  }, [divisionRaces, placements, acceptedFresh]);
  const serverRaceByEntry = useMemo(() => {
    const byEntry = new Map<string, string>();
    for (const race of races.data ?? [])
      for (const member of race.entries) byEntry.set(member.entryId, race.id);
    return byEntry;
  }, [races.data]);
  // The entry's own stamp carries an optimistic add and covers a refetch where the entries read
  // has landed and the races read has not.
  const heatOf = useCallback(
    (row: DeckRow): string | null => {
      const place = placeOf(row.entry.id);
      if (place.raceId !== null) return place.raceId;
      return overlaid(row.entry.id) ? null : (row.entry.raceId ?? null);
    },
    [placeOf, overlaid],
  );
  // A row's stamp may name a race in neither read nor the outbox (a discarded mint, a deleted
  // race); such a row belongs under the first chip, never on no chip.
  const knownHeatIds = useMemo(() => new Set(divisionRaces.map((r) => r.id)), [divisionRaces]);
  const knownHeat = useCallback(
    (raceId: string | null): string | null =>
      raceId !== null && knownHeatIds.has(raceId) ? raceId : null,
    [knownHeatIds],
  );
  // The server's heat counts too, so a moved swimmer stays visible until the read lands. The
  // prospective chip (null) takes rows in no heat, never one whose heat this device cannot see.
  const showsInHeat = useCallback(
    (row: DeckRow, heatId: string | null, first: boolean): boolean => {
      const server = knownHeat(serverRaceByEntry.get(row.entry.id) ?? null);
      const device = knownHeat(heatOf(row));
      if (server === null && device === null) return first;
      return heatId !== null && (server === heatId || device === heatId);
    },
    [serverRaceByEntry, heatOf, knownHeat],
  );
  // This device's answer first, so a swimmer mid-move counts in one heat, not both.
  const countsInHeat = useCallback(
    (row: DeckRow): string | null =>
      knownHeat(heatOf(row)) ?? knownHeat(serverRaceByEntry.get(row.entry.id) ?? null),
    [heatOf, serverRaceByEntry, knownHeat],
  );
  // Members of one race share a scoring heat, so the first carrying one speaks for the chip; only
  // legacy data makes it differ from the ordinal, relabelling the chip without moving it.
  const scoringHeatOf = useCallback(
    (raceId: string | null): number | null => {
      if (raceId === null) return null;
      for (const row of rows)
        if (row.entry.heat != null && countsInHeat(row) === raceId) return row.entry.heat;
      return null;
    },
    [rows, countsInHeat],
  );
  // The server still gives a field entry a heat with no lane; the deck says nothing about it.
  const heatsShown = (known?.measureType ?? "Time") === "Time";
  const ladderMode = known?.measureType === "Height";
  // The prospective chip needs resolved reads: a lone "Heat 1" over heats that failed to load is
  // a claim nothing on this device supports.
  const heats = useMemo(() => {
    const found = numberHeats(heatsOfDivision(divisionRaces), scoringHeatOf);
    if (found.length > 0) return found;
    return racesResolved && entries.data !== undefined ? [PROSPECTIVE_HEAT] : [];
  }, [divisionRaces, scoringHeatOf, racesResolved, entries.data]);
  const selected = selectedHeat(heats, heatParam);
  const selectedRace = divisionRaces.find((r) => r.id === selected?.raceId) ?? null;
  const heatLabelOf = (raceId: string): string | undefined =>
    heats.find((h) => h.raceId === raceId)?.label;
  const firstHeatId = heats[0]?.raceId ?? null;
  const isFirstHeat = (raceId: string | null) => heats.length === 0 || firstHeatId === raceId;
  // One unresolved mint per division: `claimMaterialisation` fences only the same tick, and a
  // standing refusal must not re-offer the mint.
  const mintPending = intents.some(
    (i) =>
      i.eventId === eventId &&
      (i.status === "queued" ||
        i.status === "syncing" ||
        i.status === "conflict" ||
        i.status === "rejected"),
  );
  const mintRefused = intents.some(
    (i) => i.eventId === eventId && (i.status === "conflict" || i.status === "rejected"),
  );

  const selectedRaceId = selected?.raceId ?? null;
  // `+` pushes the heat it mints; without the default heat in the address, Back off a new heat
  // re-derives the same default and appears to do nothing.
  useEffect(() => {
    if (!heatsShown || heatParam !== null || selectedRaceId === null) return;
    navigate(heatPath(selectedRaceId), { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [heatsShown, heatParam, selectedRaceId]);

  // Sorted on the lane this device believes in: offline the entries read never refreshes. Outbox
  // ids are UUIDv7, so comparing them compares capture time.
  const heatRows = useMemo(() => {
    const key = (n: number | null) => (n == null ? Number.MAX_SAFE_INTEGER : n);
    const first = heats.length === 0 || heats[0]!.raceId === selectedRaceId;
    // With no heat axis there is nothing to switch with, so the deck shows the whole division.
    return rows
      .filter((row) => !heatsShown || showsInHeat(row, selectedRaceId, first))
      .sort(
        (a, b) =>
          key(laneShown(a.entry)) - key(laneShown(b.entry)) ||
          (a.entry.id < b.entry.id ? -1 : a.entry.id > b.entry.id ? 1 : 0),
      );
  }, [rows, heats, heatsShown, selectedRaceId, showsInHeat, laneShown]);

  // Heat numbers change mid-list only in a legacy race carrying two heats of one division.
  const heatDividers = useMemo(() => {
    const byEntry = new Map<string, number>();
    let previous: number | null = null;
    heatRows.forEach((row, i) => {
      const heat = row.entry.heat;
      if (i > 0 && heat != null && heat !== previous) byEntry.set(row.entry.id, heat);
      previous = heat;
    });
    return byEntry;
  }, [heatRows]);

  // Offline a queued capture is the progress, as on the division chips; a refused one is not.
  const recordedHere = (entryId: string): boolean => {
    if (resultsByEntry.has(entryId)) return true;
    const status = outboxByEntry.get(entryId)?.status;
    return status === "queued" || status === "syncing" || status === "accepted";
  };
  // The division selector's own arithmetic and wording, so "8/8" on the division cannot sit above
  // "0/8" on its only heat.
  const heatOptions: HeatOption[] = heats.map((heat) => {
    const members = rows.filter((row) => {
      const counted = countsInHeat(row);
      return counted === null ? isFirstHeat(heat.raceId) : counted === heat.raceId;
    });
    const recorded = members.filter((row) => recordedHere(row.entry.id)).length;
    return {
      ...heat,
      // With the results read down the numerator is unknown, not zero, and no
      // heat can be called complete off this device's own captures.
      count: known
        ? progressText(
            sumProgress([{ ...known, entryCount: members.length, resultCount: recorded }]),
            resultsUnavailable,
          )
        : `${resultsUnavailable ? "?" : recorded}/${members.length}`,
      complete: !resultsUnavailable && members.length > 0 && recorded === members.length,
    };
  });

  const unplacedTarget: { raceId: string } | "mint" | "unknown" | null = !racesResolved
    ? "unknown"
    : selectedRaceId === null
      ? "mint"
      : parkedMaterialisations.has(selectedRaceId)
        ? null
        : { raceId: selectedRaceId };

  // A row's own lane may sit above the venue's count and stays selectable; nothing else above it is
  // offered, as the server refuses it and the item parks for good.
  function laneChoices(raceId: string | null, lane: number | null, entryId: string): number[] {
    const roster = raceId ? (rostersByRace.get(raceId) ?? []) : [];
    const taken = new Set(roster.map((r) => r.lane).filter((l): l is number => l != null));
    // A lane the entries read fills is taken too: the races read may be behind it.
    for (const { entry: other } of raceId === null ? [] : rows)
      if (other.raceId === raceId && other.id !== entryId && !overlaid(other.id))
        if (other.lane != null) taken.add(other.lane);
    const cap = settings.data?.laneCount;
    const held = [...taken, ...(lane === null ? [] : [lane])].map((n) => ({ lane: n }));
    return vacantLanes(laneOptions(held, cap), taken, lane).filter(
      (n) => n === lane || cap == null || n <= cap,
    );
  }

  // A refused race is offered only to rows already parked in it. A swimmer who has swum is offered
  // only their own race: the placing is scored against its pool, so leaving goes through Remove.
  function laneRaceOptions(
    entryId: string,
    place: { raceId: string | null; lane: number | null },
    target: { raceId: string } | "mint" | null,
    resulted: boolean,
  ): LaneRaceOption[] {
    if (target === "mint")
      return [
        { raceId: null, title: null, lanes: laneChoices(null, place.lane, entryId), home: true },
      ];
    const homeId = place.raceId ?? (target === null ? null : target.raceId);
    const swum = resulted && place.raceId !== null;
    const groups: LaneRaceOption[] = [];
    for (const race of divisionRaces) {
      const raceId = race.id;
      const home = raceId === homeId;
      if (
        !home &&
        (swum ||
          parkedMaterialisations.has(raceId) ||
          laneChoices(raceId, null, entryId).length === 0)
      )
        continue;
      groups.push({
        raceId,
        title: heatLabelOf(raceId) ?? null,
        lanes: laneChoices(raceId, home ? place.lane : null, entryId),
        home,
      });
    }
    // A race this division's chips do not carry — another division's, or one
    // only the entries read knows of. The row still has to describe itself.
    if (homeId !== null && !groups.some((g) => g.home))
      groups.unshift({
        raceId: homeId,
        title: heatLabelOf(homeId) ?? raceTitleOf(homeId) ?? null,
        lanes: laneChoices(homeId, place.lane, entryId),
        home: true,
      });
    return groups;
  }

  // Empty for a row holding no lane: with nothing to give in exchange it is a displacement.
  function swapChoices(raceId: string | null, lane: number | null, entryId: string): SwapOffer[] {
    if (raceId === null || lane === null) return [];
    // A lane above the venue's count is where somebody is, never somewhere the
    // server will let anyone go: a swap touching one is two writes it refuses.
    const cap = settings.data?.laneCount;
    if (cap != null && lane > cap) return [];
    return (rostersByRace.get(raceId) ?? [])
      // `r.lane !== lane` guards a roster that momentarily shows two rows in one
      // lane: "swap" with them would be A out, B in, A back into the same lane.
      .filter(
        (r) =>
          r.entryId !== entryId &&
          r.lane != null &&
          r.lane !== lane &&
          (cap == null || r.lane <= cap),
      )
      .map((r) => ({ lane: r.lane!, entryId: r.entryId, displayName: r.displayName }))
      .sort((a, b) => a.lane - b.lane);
  }

  // A ref set before the first await: a state flag lets two taps in one tick both mint, orphaning
  // a race only an administrator can delete.
  function claimMaterialisation(divisionId: string): boolean {
    if (materialising.current.has(divisionId)) return false;
    materialising.current.add(divisionId);
    setBusyDivisions(new Set(materialising.current));
    return true;
  }

  function releaseMaterialisation(divisionId: string) {
    materialising.current.delete(divisionId);
    setBusyDivisions(new Set(materialising.current));
  }

  // Clears the heat too: the placement that decided it is withdrawn.
  async function removeFromRace(input: { entry: Entry; division: CarnivalEvent }) {
    if (!session) return;
    const from = placeOf(input.entry.id).raceId;
    try {
      await submitAssignmentViaOutbox({
        carnivalId,
        carnivalEventId: input.division.id,
        entryId: input.entry.id,
        raceId: null,
        lane: null,
        heat: null,
        entryDisplayName: input.entry.displayName,
        ...(from && raceTitleOf(from) ? { raceTitle: raceTitleOf(from)! } : {}),
        actorSubject: session.subject,
        ...(session.displayName ? { actorDisplayName: session.displayName } : {}),
      });
      setQueueError(null);
    } catch {
      setQueueError(`Couldn't queue ${input.entry.displayName}'s removal — try again.`);
    }
  }

  async function assignLane(input: {
    race: { id: string };
    entry: Entry;
    division: CarnivalEvent;
    /** null parks the swimmer in the race without a lane — an ordinary state. */
    lane: number | null;
    /** The race's name for a race this device has only just minted. */
    raceTitle?: string;
  }) {
    if (!session) return;
    const title = input.raceTitle ?? raceTitleOf(input.race.id);
    // A placement the device queue refuses is a swimmer who is not in the race
    // anyone can see — silence there is the worst outcome on the deck.
    try {
      await submitAssignmentViaOutbox({
        carnivalId,
        carnivalEventId: input.division.id,
        entryId: input.entry.id,
        raceId: input.race.id,
        lane: input.lane,
        entryDisplayName: input.entry.displayName,
        ...(title ? { raceTitle: title } : {}),
        actorSubject: session.subject,
        ...(session.displayName ? { actorDisplayName: session.displayName } : {}),
      });
      setQueueError(null);
    } catch {
      setQueueError(
        input.lane === null
          ? `Couldn't queue ${input.entry.displayName}'s park without a lane on this device — try again.`
          : `Couldn't queue lane ${input.lane} on this device — try again.`,
      );
    }
  }

  // Three ordered placements: each holds the lane the other wants, so two writes are unsatisfiable.
  async function swapLanes(input: { raceId: string; moving: SwapSide; occupant: SwapSide }) {
    if (!session) return;
    const title = raceTitleOf(input.raceId);
    try {
      await submitSwapViaOutbox({
        carnivalId,
        raceId: input.raceId,
        ...(title ? { raceTitle: title } : {}),
        moving: input.moving,
        occupant: input.occupant,
        actorSubject: session.subject,
        ...(session.displayName ? { actorDisplayName: session.displayName } : {}),
      });
      setQueueError(null);
    } catch {
      setQueueError(
        `Couldn't queue the swap of ${input.moving.entryDisplayName} and ` +
          `${input.occupant.entryDisplayName} on this device — try again.`,
      );
    }
  }

  // Never `race.create`, which is administrator-only.
  async function materialiseAndAssign(input: {
    division: CarnivalEvent;
    entry: Entry;
    lane: number;
  }) {
    if (!session) return;
    // Two picks in one tick are a fumbled gesture, not a second heat.
    if (materialising.current.has(input.division.id)) {
      setQueueError("Another lane tap is already creating this race — retry in a moment.");
      return;
    }
    try {
      const raceId = await mintOrJoin(input.division);
      // The race may be on no read yet, so the division it was minted for is
      // what names it in the queue.
      await assignLane({
        race: { id: raceId },
        raceTitle: divisionChipLabel(input.division),
        ...input,
      });
    } catch {
      setQueueError("Couldn't queue the lane on this device — try again.");
    }
  }

  // The refs answer in the window the outbox live query cannot; without them a second pick mints a
  // second race for one division.
  async function mintOrJoin(division: CarnivalEvent): Promise<string> {
    const held = mintedHeat.current;
    if (held && held.eventId === eventId && mintStands(held.raceId)) return held.raceId;
    if (mintInFlight.current) return mintInFlight.current;
    if (!claimMaterialisation(division.id))
      throw new Error("A heat is already being created here.");
    const pending = (async () => {
      try {
        return (await mintRace(division)).id;
      } finally {
        releaseMaterialisation(division.id);
      }
    })();
    mintInFlight.current = pending;
    try {
      return await pending;
    } finally {
      mintInFlight.current = null;
    }
  }

  async function mintRace(division: CarnivalEvent) {
    const race = await submitMaterialiseViaOutbox({
      carnivalId,
      eventId: division.id,
      scheduleSlotId: division.scheduleSlotId,
      title: divisionChipLabel(division),
      actorSubject: session!.subject,
      ...(session!.displayName ? { actorDisplayName: session!.displayName } : {}),
    });
    if (division.id === eventId) mintedHeat.current = { eventId, raceId: race.id };
    return race;
  }

  function selectHeat(raceId: string) {
    navigate(heatPath(raceId));
  }

  function heatPath(raceId: string): string {
    return `/carnivals/${carnivalId}/events/${eventId}?heat=${raceId}`;
  }

  async function addHeat(division: CarnivalEvent) {
    if (!session) return;
    if (!claimMaterialisation(division.id)) {
      setQueueError("Another tap is already creating this heat — retry in a moment.");
      return;
    }
    try {
      const race = await mintRace(division);
      selectHeat(race.id);
    } catch {
      setQueueError("Couldn't queue the new heat on this device — try again.");
    } finally {
      releaseMaterialisation(division.id);
    }
  }

  // Resolved at tap time so any mint is enqueued in front of the create. Off an unresolved races
  // read it names nothing and the server places the swimmer.
  async function raceIdForAdd(): Promise<string | null> {
    // Not into a refused heat: the server places the swimmer instead.
    if (selectedRaceId !== null)
      return parkedMaterialisations.has(selectedRaceId) ? null : selectedRaceId;
    if (!racesResolved || !known || !session) return null;
    return mintOrJoin(known);
  }

  // A failed listResults must never read as "no result yet": officials would re-enter times that
  // exist and each comes back a 409. The navigation reads are nothing an official can act on.
  const loadError = eventState.error ?? entries.error ?? results.error;
  const hasRaces = divisionRaces.length > 0;
  const selectedRefused = selectedRaceId !== null && parkedMaterialisations.has(selectedRaceId);
  // What save-and-advance walks.
  const renderedRows = heatRows;
  const settingsLoading = settings.loading;
  // Said in words: a grey control reads as a missing capability, and a `title` is unreachable on
  // a tablet.
  const addHeatBlocked = !racesResolved
    ? "Waiting for this division's heats — a heat added now could duplicate one that exists."
    : rows.length === 0
      ? // A heat minted for nobody is litter the deck has no way to clear.
        "Add a swimmer first — an empty heat can't be removed from the deck."
      : busyDivisions.has(eventId)
        ? "This heat is still being created on this device."
        : mintRefused
          ? REFUSED_HEAT_TEXT
          : mintPending
            ? "A heat for this division is already queued on this device."
            : undefined;

  function laneControlFor(
    row: DeckRow,
  ): ((handle: RefObject<LaneControlHandle | null>) => ReactNode) | undefined {
    const { entry, createItem } = row;
    const addRefused = createItem?.status === "rejected" || createItem?.status === "conflict";
    if (!session || addRefused || !known) return undefined;
    const place = placeOf(entry.id);
    const target = place.raceId !== null ? { raceId: place.raceId } : unplacedTarget;
    const placement = placements.get(entry.id);
    // Drawn and dead while the races read is unresolved: hiding it would say the row has no lane.
    const marshallingPaused = target === "unknown";
    const laneRaces = marshallingPaused
      ? []
      : laneRaceOptions(entry.id, place, target, hasDisplay(entry.id));
    const swaps = marshallingPaused ? [] : swapChoices(place.raceId, place.lane, entry.id);
    return (handle) => (
      <EntryLaneControl
        entryId={entry.id}
        displayName={entry.displayName}
        handle={handle}
        lane={place.lane}
        inRace={place.raceId !== null}
        races={laneRaces}
        swaps={swaps}
        disabled={busyDivisions.has(eventId) || marshallingPaused || settingsLoading}
        item={placement && placement.status !== "accepted" ? placement : undefined}
        onPick={(lane) => {
          if (marshallingPaused || target === null) return;
          if (target === "mint") {
            if (lane !== null) void materialiseAndAssign({ division: known, entry, lane });
            return;
          }
          void assignLane({ race: { id: target.raceId }, entry, division: known, lane });
        }}
        onPickInRace={(raceId, lane) => {
          if (marshallingPaused) return;
          void assignLane({ race: { id: raceId }, entry, division: known, lane });
        }}
        onRemove={() => void removeFromRace({ entry, division: known })}
        onSwap={(offer) => {
          const other = (rostersByRace.get(place.raceId!) ?? []).find(
            (r) => r.entryId === offer.entryId,
          );
          if (!other || other.lane == null || place.lane == null) return;
          void swapLanes({
            raceId: place.raceId!,
            moving: {
              entryId: entry.id,
              carnivalEventId: known.id,
              entryDisplayName: entry.displayName,
              lane: place.lane,
            },
            occupant: {
              entryId: other.entryId,
              carnivalEventId: other.carnivalEventId,
              entryDisplayName: other.displayName,
              lane: other.lane,
            },
          });
        }}
      />
    );
  }

  const gridRows: JumpGridRow[] = ladderMode
    ? heatRows.map((row) => {
        const control = laneControlFor(row);
        return {
          ...row,
          ...(control ? { laneControl: control } : {}),
          inRace: placeOf(row.entry.id).raceId !== null,
        };
      })
    : [];

  return (
    <>
      <header className="deck-head">
        <a
          className="back-button"
          href={href(`/carnivals/${carnivalId}`)}
          aria-label="Back to carnival"
        >
          <ArrowLeft size={ICON} aria-hidden="true" />
        </a>
        {event ? (
          <>
            <h1>
              {event.definitionName ?? event.definitionCode}{" "}
              <EventClassification event={event} />{" "}
              <span className={`badge ${event.status === "Open" ? "ok" : "muted"}`}>
                {event.status}
              </span>
            </h1>
            {/* Gated: "0 of 0 recorded" on a deck still loading is a claim. */}
            {rows.length > 0 && (
              <DeckProgress
                total={rows.length}
                resultsByEntry={recordedByEntry}
                outboxByEntry={outboxByEntry}
                attemptsByEntry={attemptsByEntry}
                jumpsByEntry={jumpsByEntry}
                resultsUnavailable={resultsUnavailable}
                updatedAt={oldestUpdatedAt(entries.updatedAt, results.updatedAt)}
                isRefetching={entries.isRefetching || results.isRefetching}
              />
            )}
          </>
        ) : (
          <h1>{eventState.loading ? "Loading event…" : "Event"}</h1>
        )}
      </header>
      {event && (
        <>
          <div className="deck-pickers">
            <DivisionSelector
              definitionId={event.eventDefinitionId}
              events={siblings.data}
              current={event}
              queued={queuedProgress}
              subject={session?.subject ?? null}
            />
            {known && heatsShown && (
              <HeatPicker
                heats={heatOptions}
                selectedRaceId={selectedRaceId}
                hrefOf={(raceId) => href(heatPath(raceId))}
                onAdd={session ? () => void addHeat(known) : null}
                addBlocked={addHeatBlocked}
              />
            )}
            <NextEventLink event={event} events={siblings.data} schedule={schedule.data} />
          </div>
          {event.status !== "Open" && (
            <OpenEventNotice
              event={event}
              canManage={hasRole(session, "CarnivalAdministrator", "SystemAdministrator")}
              onOpened={() => invalidate(keys.carnival(carnivalId))}
            />
          )}
        </>
      )}
      {loadError && (
        <div className="alert error" role="alert">
          {errorText(loadError, READ_ERROR_NETWORK_TEXT)}
        </div>
      )}
      {queueError && (
        <div className="alert error" role="alert">
          {queueError}
        </div>
      )}
      {!session && (
        <div className="alert notice">
          <a href={href("/login")}>Sign in</a> to record results — the actor is captured on every
          entry (ADR-0006).
        </div>
      )}

      {!racesResolved && (
        <p className="row-sub">
          {races.error
            ? "Marshalling is paused until the race list loads — lanes can't be changed while placements can't be checked. Reload the page to try again; late adds will need placing later."
            : "Checking race placements…"}
        </p>
      )}
      {entries.loading && <p>Loading entries…</p>}

      <div className="deck">
        {settingsLoading && <p className="row-sub">Loading the venue's lanes…</p>}
        {entries.data && rows.length === 0 && <p>No entries yet — add students below.</p>}
        {selectedRace && session && (
          <DeckRaceBlock
            race={selectedRace}
            laneCountUnknown={settings.error !== undefined}
            schedule={schedule.data}
            outboxItems={outboxItems}
            refused={parkedMaterialisations.has(selectedRace.id)}
          />
        )}
        {/* Never under the refusal banner, which has just refused the add. */}
        {entries.data && rows.length > 0 && heatRows.length === 0 && !selectedRefused && (
          <p className="row-sub">No swimmers yet — add one below.</p>
        )}
        {known && heatRows.length > 0 && ladderMode && (
          <HighJumpGrid
            carnivalId={carnivalId}
            event={known}
            rows={gridRows}
            resultsByEntry={resultsByEntry}
            jumpsByEntry={jumpsByEntry}
            outboxByEntry={outboxByEntry}
            resultsUnavailable={resultsUnavailable}
            reachable={reachable}
            actorSubject={session?.subject ?? null}
            {...(session?.displayName ? { actorDisplayName: session.displayName } : {})}
            onChanged={() =>
              invalidate(
                keys.results(carnivalId, eventId),
                keys.carnivalEvent(carnivalId, eventId),
                keys.carnivalEvents(carnivalId),
              )
            }
          />
        )}
        {known && heatRows.length > 0 && !ladderMode && (
          <section className="deck-group">
            <ul className="row-list card">
              {heatRows.map((row) => {
                      const { entry, createItem } = row;
                      const place = placeOf(entry.id);
                      const addRefused =
                        createItem?.status === "rejected" || createItem?.status === "conflict";
                      const laneControl = laneControlFor(row);
                      return (
                        <EntryRow
                          key={entry.id}
                          carnivalId={carnivalId}
                          event={known}
                          entry={entry}
                          heatDivider={heatDividers.get(entry.id) ?? null}
                          createItem={createItem}
                          onDismissCreate={
                            createItem && session
                              ? () => void dismissCreate(createItem)
                              : undefined
                          }
                          serverResult={resultsByEntry.get(entry.id)}
                          outboxItem={outboxByEntry.get(entry.id)}
                          attemptItems={attemptsByEntry.get(entry.id)}
                          resultsUnavailable={resultsUnavailable}
                          standingRecord={standingRecord}
                          // The server lanes this one at drain time.
                          lanePending={autoFillLanes && createItem !== undefined && !addRefused}
                          // No heat while the division has races is outside the ranked field,
                          // a raced-but-heatless row included.
                          unplaced={racesResolved && hasRaces && entry.heat == null}
                          // In no race at all: legacy, or a removal that landed.
                          raceless={racesResolved && hasRaces && heatOf(row) === null}
                          lane={laneShown(entry)}
                          inRace={place.raceId !== null}
                          {...(laneControl ? { laneControl } : {})}
                          actorSubject={session?.subject ?? null}
                          inputRef={refFor(entry.id)}
                          onSaved={() => handleSaved(entry.id)}
                        />
                      );
              })}
            </ul>
          </section>
        )}
      </div>

      {/* Keyed by the event: a division switch re-renders in place, and a kept roster and
          search would offer the previous division's students. */}
      {event?.format === "Team" ? (
        <AddTeam
          key={eventId}
          carnivalId={carnivalId}
          eventId={eventId}
          teamSize={event.teamSize ?? 4}
          legs={event.legs}
          ageGroup={event.ageGroup}
          actorSubject={session?.subject ?? null}
          {...(session?.displayName ? { actorDisplayName: session.displayName } : {})}
          disabled={event.status !== "Open"}
          // A heat only queued as a mint is fine: the item waits behind it.
          raceId={selectedRefused ? null : selectedRaceId}
          blocked={mintRefused ? REFUSED_HEAT_TEXT : undefined}
          onAdded={() =>
            invalidate(
              keys.entries(carnivalId, eventId),
              keys.carnivalEvents(carnivalId),
              // Under auto-fill the create may have placed the relay in a newly minted race.
              keys.races(carnivalId),
            )
          }
        />
      ) : (
        <AddEntry
          key={eventId}
          carnivalId={carnivalId}
          eventId={eventId}
          actorSubject={session?.subject ?? null}
          enteredStudentIds={entryCreateStudentIds.entered}
          failedStudentIds={entryCreateStudentIds.failed}
          onAdded={() => invalidate(keys.entries(carnivalId, eventId))}
          raceIdForAdd={raceIdForAdd}
          blocked={mintRefused ? REFUSED_HEAT_TEXT : undefined}
        />
      )}
    </>
  );
}
