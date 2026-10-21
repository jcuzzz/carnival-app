import type { APIRequestContext } from "@playwright/test";
// The app's own label helper, imported rather than re-implemented: these
// strings are asserted against the rendered UI, so a second copy would drift
// (its first draft already dropped the OPEN case). e2e/ shares the app's
// tsconfig, and the module is pure — its only
// import is a type.
import { ageScopeOrder, divisionLabel, type DivisionScope } from "../src/features/events/eventLabels";
import { seedName } from "./fixed";

/**
 * Seeding through the real dev API (docs/development/local-dev.md): dev header auth,
 * no fixtures, no direct database access — if the API refuses, the journey was
 * never going to pass anyway and the failure says so immediately.
 *
 * Each spec seeds its OWN carnival rather than reusing the dev seeder's one.
 * "Already entered" is scoped to the event (ADR-0003), so a fresh carnival makes
 * the whole synthetic roster eligible again and the suite is re-runnable
 * without scripts/dev-reset.ps1 between runs.
 */

const API = "http://localhost:5080";

/** The seeding actor: CarnivalAdministrator runs the carnival, SystemAdministrator imports. */
const HEADERS = {
  "X-Dev-Subject": "e2e-seed",
  "X-Dev-Roles": "CarnivalAdministrator,SystemAdministrator",
};

/**
 * The dev-login persona the journeys sign in as (DevLoginScreen presets). The
 * seed assigns it as an official so the "My events" block (ADR-0014 §3) has
 * something to show — an ordering the deck journeys walk through.
 */
export const SIGNED_IN_SUBJECT = "dev-admin";

interface Carnival {
  id: string;
  name: string;
  status: string;
}

/** `DivisionScope` carries the age/category fields (ADR-0014 §4: a division
 *  scopes to a SET of age groups; null = open) exactly as the app types them. */
interface CarnivalEvent extends DivisionScope {
  id: string;
  eventDefinitionId: string;
  tier: string;
  definitionName: string | null;
  definitionCode: string | null;
  format: string | null;
}

interface Entry {
  id: string;
  displayName: string;
  houseCode: string;
  lane: number | null;
}

interface ScheduleStream {
  id: string;
  name: string;
}

interface ScheduleSlot {
  id: string;
  label: string | null;
}

export interface SeededCarnival {
  carnivalId: string;
  carnivalName: string;
  /** The event the entries are on — where a time gets captured. */
  eventId: string;
  eventName: string;
  /** The division as the UI writes it ("13 · Girls"), for the chip assertions. */
  divisionLabel: string;
  /** A sibling division of the SAME definition, with no entries of its own. */
  siblingEventId: string;
  siblingDivisionLabel: string;
  /**
   * The race the entries below are marshalled into (ADR-0015 §3) — the deck
   * unit. Named here so a journey can go straight to the race screen; without
   * it the only route in is the race list, which is itself under test.
   */
  raceId: string;
  /** What that race is called on screen ("Race 1"). */
  raceLabel: string;
  /** In lane order: `entries[i]` is in lane `i + 1`. */
  entries: Entry[];
}

export interface SeededSocialCarnival {
  carnivalId: string;
  carnivalName: string;
  eventId: string;
  eventName: string;
  divisionLabel: string;
  /** An eligible student the journey enters through the UI. */
  entrantName: string;
  /** What to type in the as-you-type search (surname — the API matches name parts). */
  entrantSearchTerm: string;
}

async function call<T>(
  api: APIRequestContext,
  method: "get" | "post" | "put" | "delete",
  path: string,
  body?: unknown,
  extraHeaders?: Record<string, string>,
): Promise<T> {
  const response = await api[method](`${API}/api/v1${path}`, {
    headers: { ...HEADERS, ...extraHeaders },
    ...(body !== undefined ? { data: body } : {}),
  });
  if (!response.ok()) {
    throw new Error(
      `${method.toUpperCase()} ${path} → ${response.status()}: ${await response.text()}`,
    );
  }
  return (await response.json()) as T;
}

/**
 * Divisions the synthetic roster actually populates. The dev seeder schedules
 * its event for the four most-populated age-group × category divisions, so
 * mirroring its events is the only way to pick divisions with students behind
 * them without hard-coding the fixture's demographics.
 */
async function populatedDivisions(api: APIRequestContext): Promise<CarnivalEvent[]> {
  const carnivals = await call<Carnival[]>(api, "get", "/carnivals");
  const seeded = carnivals.find((c) => c.name.includes("(synthetic)"));
  if (!seeded) {
    throw new Error(
      "The dev API has no synthetic seed carnival. Start it with the `http` launch profile " +
        "(scripts/dev-run.ps1); scripts/dev-reset.ps1 forces a reseed.",
    );
  }
  const events = await call<CarnivalEvent[]>(api, "get", `/carnivals/${seeded.id}/events`);
  const individual = events.filter(
    (e) => (e.format ?? "Individual") === "Individual" && e.tier === "Competitive",
  );
  if (individual.length < 2) {
    throw new Error(
      `Seed carnival ${seeded.id} has ${individual.length} individual Competitive events; ` +
        "the division-switch journey needs two divisions of one definition to mirror.",
    );
  }
  return individual;
}

export interface FreshCarnival {
  carnivalId: string;
  carnivalName: string;
}

/** Created and left alone (ADR-0054): the empty state, so nothing else may
 *  touch it. */
export async function seedFreshCarnival(
  api: APIRequestContext,
  label: string,
  type = "Swimming",
): Promise<FreshCarnival> {
  const carnivalName = seedName(label);
  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type,
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
    laneCount: 8,
    autoFillLanes: true,
  });
  return { carnivalId: carnival.id, carnivalName };
}

/**
 * A carnival of its own with two divisions of one event definition — `entryCount`
 * swimmers in heat 1 of the first, none in the second — listed in one program
 * block so the deck journeys can exercise tiles, the division chips and
 * "Next event →" the way an official does.
 */
export async function seedCarnival(
  api: APIRequestContext,
  label: string,
  entryCount = 3,
): Promise<SeededCarnival> {
  const [division, siblingDivision] = await populatedDivisions(api);
  const carnivalName = seedName(label);

  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type: "Swimming",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
  });

  // Scheduling needs an editable carnival, capture needs an Open one, and
  // results need BOTH open (CarnivalGuards) — hence this order.
  const event = await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
    eventDefinitionId: division!.eventDefinitionId,
    ageGroup: division!.ageGroup,
    category: division!.category,
  });
  const sibling = await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
    eventDefinitionId: siblingDivision!.eventDefinitionId,
    ageGroup: siblingDivision!.ageGroup,
    category: siblingDivision!.category,
  });

  // The program (ADR-0014 §1): one stream, one block listing both divisions in
  // order, so "Next event →" has something to follow. Purely advisory — every
  // event below is enterable and resultable regardless.
  const stream = await call<ScheduleStream>(
    api,
    "post",
    `/carnivals/${carnival.id}/schedule/streams`,
    { name: "Pool" },
  );
  const slot = await call<ScheduleSlot>(
    api,
    "post",
    `/carnivals/${carnival.id}/schedule/streams/${stream.id}/slots`,
    { label: "FREESTYLE EVENTS", startTime: "09:45:00", kind: "Events" },
  );
  await call(api, "post", `/carnivals/${carnival.id}/schedule/slots/${slot.id}/events`, {
    eventId: event.id,
    slotOrder: 0,
  });
  await call(api, "post", `/carnivals/${carnival.id}/schedule/slots/${slot.id}/events`, {
    eventId: sibling.id,
    slotOrder: 1,
  });

  // One assignment, so the signed-in persona has a "My events" block (ADR-0014
  // §3) — and so the block is demonstrably an ORDERING: the unassigned sibling
  // stays reachable through the chips and the grid below.
  await call(api, "put", `/carnivals/${carnival.id}/events/${event.id}/officials`, {
    officials: [{ subjectId: SIGNED_IN_SUBJECT, displayName: "Alex Admin" }],
  });

  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Open" });
  for (const id of [event.id, sibling.id])
    await call(api, "post", `/carnivals/${carnival.id}/events/${id}/status`, { status: "Open" });

  const eligible = await call<{ students: { studentId: string; displayName: string }[] }>(
    api,
    "get",
    `/carnivals/${carnival.id}/events/${event.id}/eligible-students`,
  );
  if (eligible.students.length < entryCount) {
    throw new Error(
      `Division ${division!.ageGroup}/${division!.category} has ${eligible.students.length} eligible ` +
        `students; the journey needs ${entryCount}.`,
    );
  }

  // The division's heat is MATERIALISED first and every create NAMES it
  // (ADR-0039 §2) — the order the deck produces on the day, and the only one
  // that leaves a single race: since ADR-0039 §1 the server places every create
  // it is given no heat for, so a bare create followed by a placement mints a
  // race, empties it again, and leaves the division standing at a stray empty
  // heat. Lanes still go through the placement endpoint: a lane is a position
  // in a race (ADR-0015 §3) and the create endpoint refuses one.
  //
  // Materialise, not `race.create`: it stamps the race with its division —
  // which is what lets a create name it — and claims the program block created
  // above, where a recorder looks for it (races group by block, ADR-0015 §4).
  const raceLabel = "Race 1";
  const { race } = await call<{ race: { id: string } }>(
    api,
    "post",
    `/carnivals/${carnival.id}/races/materialise`,
    { eventId: event.id, label: raceLabel, scheduleSlotId: slot.id },
  );
  const entries: Entry[] = [];
  for (const [i, student] of eligible.students.slice(0, entryCount).entries()) {
    const created = await call<Entry>(
      api,
      "post",
      `/carnivals/${carnival.id}/events/${event.id}/entries`,
      { studentId: student.studentId, raceId: race.id },
    );
    entries.push(
      await call<Entry>(
        api,
        "put",
        `/carnivals/${carnival.id}/events/${event.id}/entries/${created.id}/placement`,
        { raceId: race.id, lane: i + 1 },
      ),
    );
  }

  return {
    carnivalId: carnival.id,
    carnivalName,
    eventId: event.id,
    eventName: division!.definitionName ?? division!.definitionCode ?? "event",
    divisionLabel: divisionLabel(division!),
    siblingEventId: sibling.id,
    siblingDivisionLabel: divisionLabel(siblingDivision!),
    raceId: race.id,
    raceLabel,
    entries,
  };
}

export interface SeededAthleticsCarnival {
  carnivalId: string;
  carnivalName: string;
  /** A Competitive Long Jump instance — a `Distance` event (ADR-0052 §3). */
  eventId: string;
  eventName: string;
  divisionLabel: string;
  /**
   * The three attempt-series states (ADR-0053 §6): [0] a foul among three jumps
   * with a best of 4.32, [1] all fouls so the server derives NM, [2] one round
   * taken with two cells still open.
   */
  entries: Entry[];
  /** The Competitive High Jump of the same division — a laddered event (ADR-0056). */
  highJumpEventId: string;
  highJumpEventName: string;
  /**
   * The card states the grid is reviewed in, in seed order: [0] in and at the
   * bar with a fail standing, [1] in and over it, [2] in but not yet at it,
   * [3] out with no height at all, [4] out with 1.20 behind them (if the
   * division had a fifth eligible student).
   */
  highJumpEntries: Entry[];
  /**
   * A second High Jump division whose definition carries NO suggestions
   * (ADR-0056 amendment 2026-09-17): the type alone makes it the card grid, and
   * the bar has not been set, so the raise control has nothing to pre-fill.
   */
  bareHighJumpEventId: string;
  /**
   * A third High Jump division, at two attempts per height (ADR-0056 amendment
   * 2026-09-25): [0] cleared the opening height and failed once at the bar,
   * [1] out on XX at the opening height, [2] a fail, a pass, then over the bar.
   */
  twoAttemptEventId: string;
  twoAttemptEntries: Entry[];
}

/**
 * The shipped athletics template applied to a carnival of its own (ADR-0052 §8):
 * seven program streams, and one field division whose three athletes sit in the
 * three states of an attempt series — the strip, the derived best mark and the
 * derived NM the deck is reviewed in. Long Jump ships `attempts: 3`, so
 * `result.submit` is refused here (ATTEMPTS_REQUIRED) and the seed records
 * attempt by attempt, exactly as the deck does.
 *
 * Entries are created with no race named: since ADR-0039 §1 the server places
 * each one, and a field entry joins its heat unlaned, which is the row this
 * capture is of.
 */
export async function seedAthleticsCarnival(
  api: APIRequestContext,
  label: string,
): Promise<SeededAthleticsCarnival> {
  // The Long Jump division is chosen the way every other seed here chooses one
  // — off the dev seeder's own picks — because the synthetic roster puts only a
  // student or two in most single-age divisions and this capture needs three.
  const [division] = await populatedDivisions(api);
  const carnivalName = seedName(label);
  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type: "Athletics",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Athletics Track",
  });
  await call(api, "post", `/carnivals/${carnival.id}/apply-template`, { template: "athletics" });

  const events = await call<CarnivalEvent[]>(api, "get", `/carnivals/${carnival.id}/events`);
  const event = events.find(
    (e) =>
      e.definitionCode === "AT-LONG-JUMP" &&
      e.tier === "Competitive" &&
      e.category === division!.category &&
      e.ageGroup === division!.ageGroup,
  );
  if (!event)
    throw new Error(
      `The athletics template scheduled no Competitive ${division!.ageGroup}/` +
        `${division!.category} Long Jump; config/event-templates/athletics.json decides the ` +
        "divisions, and the two templates' ages have to line up.",
    );

  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Open" });
  await call(api, "post", `/carnivals/${carnival.id}/events/${event.id}/status`, { status: "Open" });

  const eligible = await call<{ students: { studentId: string; displayName: string }[] }>(
    api,
    "get",
    `/carnivals/${carnival.id}/events/${event.id}/eligible-students`,
  );
  if (eligible.students.length < 3)
    throw new Error(
      `Long Jump ${division!.ageGroup}/${division!.category} has ` +
        `${eligible.students.length} eligible students; the capture needs 3.`,
    );

  const entries: Entry[] = [];
  for (const student of eligible.students.slice(0, 3))
    entries.push(
      await call<Entry>(api, "post", `/carnivals/${carnival.id}/events/${event.id}/entries`, {
        studentId: student.studentId,
      }),
    );

  const attempt = (entry: Entry, ordinal: number, body: Record<string, unknown>) =>
    call(
      api,
      "put",
      `/carnivals/${carnival.id}/events/${event.id}/entries/${entry.id}/attempts/${ordinal}`,
      body,
      { "Idempotency-Key": crypto.randomUUID(), "X-Device-Id": "e2e-seed-device" },
    );
  await attempt(entries[0]!, 1, { value: "4.10", foul: false });
  await attempt(entries[0]!, 2, { foul: true });
  await attempt(entries[0]!, 3, { value: "4.32", foul: false });
  for (const ordinal of [1, 2, 3]) await attempt(entries[1]!, ordinal, { foul: true });
  await attempt(entries[2]!, 1, { value: "3.95", foul: false });

  // ADR-0056: the same division's High Jump, seeded as the card the grid draws.
  const highJump = events.find(
    (e) =>
      e.definitionCode === "AT-HIGH-JUMP" &&
      e.tier === "Competitive" &&
      e.category === division!.category &&
      e.ageGroup === division!.ageGroup,
  );
  if (!highJump)
    throw new Error(
      `The athletics template scheduled no Competitive ${division!.ageGroup}/` +
        `${division!.category} High Jump; config/event-templates/athletics.json decides them.`,
    );
  await call(api, "post", `/carnivals/${carnival.id}/events/${highJump.id}/status`, {
    status: "Open",
  });

  const jumpEligible = await call<{ students: { studentId: string; displayName: string }[] }>(
    api,
    "get",
    `/carnivals/${carnival.id}/events/${highJump.id}/eligible-students`,
  );
  if (jumpEligible.students.length < 3)
    throw new Error(
      `High Jump ${division!.ageGroup}/${division!.category} has ` +
        `${jumpEligible.students.length} eligible students; the card grid needs 3.`,
    );

  const highJumpEntries: Entry[] = [];
  for (const student of jumpEligible.students.slice(0, 5))
    highJumpEntries.push(
      await call<Entry>(api, "post", `/carnivals/${carnival.id}/events/${highJump.id}/entries`, {
        studentId: student.studentId,
      }),
    );

  // The ladder first: a mark at a height the division has not raised to is
  // refused (HEIGHT_NOT_ON_LADDER), exactly as it is on the deck.
  for (const height of [1.2, 1.25, 1.3])
    await call(api, "post", `/carnivals/${carnival.id}/events/${highJump.id}/heights`, { height });

  const jump = (entry: Entry, height: number, mark: string) =>
    call(
      api,
      "put",
      `/carnivals/${carnival.id}/events/${highJump.id}/entries/${entry.id}/jumps/${height}`,
      { mark },
      { "Idempotency-Key": crypto.randomUUID(), "X-Device-Id": "e2e-seed-device" },
    );
  /** One card, height by height, in the order an official would have marked it. */
  const card = async (entry: Entry, marks: [number, string][]) => {
    for (const [height, at] of marks) for (const mark of at) await jump(entry, height, mark);
  };
  // In, at the bar, one fail standing there.
  await card(highJumpEntries[0]!, [[1.2, "O"], [1.25, "XO"], [1.3, "X"]]);
  // In, and over the bar — a pass at the opening height, which the card records.
  await card(highJumpEntries[1]!, [[1.2, "-"], [1.25, "O"], [1.3, "O"]]);
  // In, but not yet at the bar: two fails then a clearance at 1.25.
  await card(highJumpEntries[2]!, [[1.2, "O"], [1.25, "XXO"]]);
  // Out with nothing: three fails at the opening height, so the server says NM.
  if (highJumpEntries[3]) await card(highJumpEntries[3], [[1.2, "XXX"]]);
  // Out with 1.20 behind them.
  if (highJumpEntries[4]) await card(highJumpEntries[4], [[1.2, "O"], [1.25, "XXX"]]);

  const bare = await seedEventDefinition(api, {
    code: "AT-HIGH-JUMP-OPEN-BAR",
    name: "High Jump (bar set on the day)",
    sport: "Athletics",
    measureType: "Height",
    units: "metres",
  });
  const bareEvent = await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
    eventDefinitionId: bare.id,
    ageGroup: division!.ageGroup,
    category: division!.category,
  });
  await call(api, "post", `/carnivals/${carnival.id}/events/${bareEvent.id}/status`, {
    status: "Open",
  });
  for (const student of jumpEligible.students.slice(0, 3))
    await call<Entry>(api, "post", `/carnivals/${carnival.id}/events/${bareEvent.id}/entries`, {
      studentId: student.studentId,
    });

  const twoAttempts = await seedEventDefinition(api, {
    code: "AT-HIGH-JUMP-TWO",
    name: "High Jump (two attempts)",
    sport: "Athletics",
    measureType: "Height",
    units: "metres",
    attemptsPerHeight: 2,
  });
  const twoAttemptEvent = await call<CarnivalEvent>(
    api,
    "post",
    `/carnivals/${carnival.id}/events`,
    {
      eventDefinitionId: twoAttempts.id,
      ageGroup: division!.ageGroup,
      category: division!.category,
    },
  );
  await call(api, "post", `/carnivals/${carnival.id}/events/${twoAttemptEvent.id}/status`, {
    status: "Open",
  });
  const twoAttemptEntries: Entry[] = [];
  for (const student of jumpEligible.students.slice(0, 3))
    twoAttemptEntries.push(
      await call<Entry>(
        api,
        "post",
        `/carnivals/${carnival.id}/events/${twoAttemptEvent.id}/entries`,
        { studentId: student.studentId },
      ),
    );
  const twoPath = `/carnivals/${carnival.id}/events/${twoAttemptEvent.id}`;
  for (const height of [1.2, 1.25]) await call(api, "post", `${twoPath}/heights`, { height });
  const twoCard = async (entry: Entry, marks: [number, string][]) => {
    for (const [height, at] of marks)
      for (const mark of at)
        await call(
          api,
          "put",
          `${twoPath}/entries/${entry.id}/jumps/${height}`,
          { mark },
          { "Idempotency-Key": crypto.randomUUID(), "X-Device-Id": "e2e-seed-device" },
        );
  };
  await twoCard(twoAttemptEntries[0]!, [[1.2, "O"], [1.25, "X"]]);
  await twoCard(twoAttemptEntries[1]!, [[1.2, "XX"]]);
  await twoCard(twoAttemptEntries[2]!, [[1.2, "X-"], [1.25, "O"]]);

  return {
    carnivalId: carnival.id,
    carnivalName,
    eventId: event.id,
    eventName: event.definitionName ?? event.definitionCode ?? "Long Jump",
    divisionLabel: divisionLabel(event),
    entries,
    highJumpEventId: highJump.id,
    highJumpEventName: highJump.definitionName ?? highJump.definitionCode ?? "High Jump",
    highJumpEntries,
    bareHighJumpEventId: bareEvent.id,
    twoAttemptEventId: twoAttemptEvent.id,
    twoAttemptEntries,
  };
}

export interface SchoolSettingsSeed {
  name: string;
  brandColour: string | null;
  houses: { code: string; name: string; colour: string | null }[];
  updatedAtUtc: string | null;
}

/** The school identity row (ADR-0045 §2) — read so a journey can put it back. */
export function readSchoolSettings(api: APIRequestContext): Promise<SchoolSettingsSeed> {
  return call<SchoolSettingsSeed>(api, "get", "/school/settings");
}

/** Restore it, carrying whatever stamp the row holds now rather than the read one. */
export async function restoreSchoolSettings(
  api: APIRequestContext,
  original: SchoolSettingsSeed,
): Promise<void> {
  const current = await readSchoolSettings(api);
  await call(api, "put", "/school/settings", { ...original, updatedAtUtc: current.updatedAtUtc });
}

export interface EventDefinitionSeed {
  id: string;
  code: string;
  name: string;
  sport: string;
  measureType: string;
  units: string;
  /** ADR-0053 §1: an omitted attemptCount CLEARS the series on a PUT. */
  attemptCount: number | null;
}

/** The shared catalogue (ADR-0050 reconciles this), active rows only. */
export function listEventDefinitions(api: APIRequestContext): Promise<EventDefinitionSeed[]> {
  return call<EventDefinitionSeed[]>(api, "get", "/event-definitions");
}

/**
 * A catalogue definition no seeded carnival contests, so Event setup has a row
 * to Add (ADR-0059 §1). Idempotent by code: the dev database outlives a run.
 */
export async function seedEventDefinition(
  api: APIRequestContext,
  request: Omit<EventDefinitionSeed, "id" | "attemptCount"> & { attemptsPerHeight?: number },
): Promise<EventDefinitionSeed> {
  const existing = (await listEventDefinitions(api)).find((d) => d.code === request.code);
  return existing ?? call<EventDefinitionSeed>(api, "post", "/event-definitions", request);
}

/**
 * Rename one catalogue row, leaving its configuration blobs alone (an absent
 * blob means "unchanged" on this endpoint). Used to create — and then undo —
 * a known drift for the template-reconcile preview.
 */
export function renameEventDefinition(
  api: APIRequestContext,
  definition: EventDefinitionSeed,
  name: string,
): Promise<EventDefinitionSeed> {
  return call<EventDefinitionSeed>(api, "put", `/event-definitions/${definition.id}`, {
    code: definition.code,
    name,
    sport: definition.sport,
    measureType: definition.measureType,
    units: definition.units,
    attemptCount: definition.attemptCount,
  });
}

export interface RoleAssignmentSeed {
  id: string | null;
  upn: string;
  role: string;
  bootstrap: boolean;
  boundToSubject: boolean;
  revokedAtUtc: string | null;
}

export function listRoleAssignments(api: APIRequestContext): Promise<RoleAssignmentSeed[]> {
  return call<RoleAssignmentSeed[]>(api, "get", "/admin/role-assignments");
}

/**
 * A grant, or the one that is already there (ADR-0048 §7 refuses a duplicate).
 * Rows are never deleted, so every seed here has to be idempotent or a second
 * run of the suite fails on its own first run's data.
 */
export async function seedRoleAssignment(
  api: APIRequestContext,
  upn: string,
  role: string,
  reason: string,
): Promise<RoleAssignmentSeed> {
  const existing = (await listRoleAssignments(api)).find(
    (row) => !row.bootstrap && row.upn === upn.toLowerCase() && row.role === role,
  );
  if (existing) return existing;
  return call<RoleAssignmentSeed>(api, "post", "/admin/role-assignments", { upn, role, reason });
}

export function revokeRoleAssignment(
  api: APIRequestContext,
  id: string,
  reason: string,
): Promise<RoleAssignmentSeed> {
  return call<RoleAssignmentSeed>(api, "delete", `/admin/role-assignments/${id}`, { reason });
}

/** A row already revoked, for the screen state that shows its history. */
export async function seedRevokedRoleAssignment(
  api: APIRequestContext,
  upn: string,
  role: string,
): Promise<RoleAssignmentSeed> {
  const row = await seedRoleAssignment(api, upn, role, "Helped at last year's carnival.");
  if (row.revokedAtUtc !== null || row.id === null) return row;
  return revokeRoleAssignment(api, row.id, "Left the school at the end of last year.");
}

/**
 * A recorded swim, seeded through the real endpoint: the standings need points
 * behind them for a journey that is not about capturing one.
 */
export async function seedResult(
  api: APIRequestContext,
  carnival: SeededCarnival,
  index: number,
  value: string,
): Promise<void> {
  const entry = carnival.entries[index]!;
  await call(
    api,
    "post",
    `/carnivals/${carnival.carnivalId}/events/${carnival.eventId}/entries/${entry.id}/results`,
    { value },
    { "Idempotency-Key": crypto.randomUUID(), "X-Device-Id": "e2e-seed-device" },
  );
}

export interface SeededDenseCarnival {
  carnivalId: string;
  carnivalName: string;
  eventName: string;
  /** Divisions of the TARGET definition — the number its tile reports. */
  divisionCount: number;
  /** Where the journey starts. */
  fromEventId: string;
  fromDivisionLabel: string;
  /** A division of ANOTHER category: reachable only by narrowing first. */
  toEventId: string;
  toDivisionLabel: string;
}

/**
 * A carnival dense enough for the selector's AXIS mode (ADR-0018 §2): every
 * populated division of ONE definition plus a Social twin, which is past the
 * flat row's threshold and spans both categories and both tiers.
 *
 * Everything below is scoped to that one definition rather than to the whole
 * seed: the dev seeder happens to instantiate a single definition today, and a
 * second one would otherwise silently put two tiles' divisions in one count.
 *
 * The shape is asserted rather than hoped for — a single-category seed would
 * leave the narrowing journey passing without ever narrowing anything.
 */
export async function seedDenseCarnival(
  api: APIRequestContext,
  label: string,
): Promise<SeededDenseCarnival> {
  const populated = await populatedDivisions(api);
  const from = populated[0]!;
  const definitionId = from.eventDefinitionId;
  const divisions = populated.filter((d) => d.eventDefinitionId === definitionId);
  const to = divisions.find((d) => d.category !== from.category);
  if (!to)
    throw new Error(
      `Every populated division of ${from.definitionName ?? definitionId} is category ` +
        `${from.category}; the narrowing journey needs two. The seed is deterministic, so ` +
        "re-running it changes nothing: regenerate the synthetic roster " +
        "(py scripts/generate_synthetic_roster.py) or widen DevelopmentSeeder's division picks.",
    );

  const carnivalName = seedName(label);
  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type: "Swimming",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
  });

  const created: CarnivalEvent[] = [];
  for (const d of divisions)
    created.push(
      await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
        eventDefinitionId: definitionId,
        ageGroup: d.ageGroup,
        category: d.category,
      }),
    );
  // The Social twin: same division, relaxed tier, so the tier control renders
  // and the two must not read alike (ADR-0014 §5).
  created.push(
    await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
      eventDefinitionId: definitionId,
      ageGroup: from.ageGroup,
      category: from.category,
      tier: "Social",
    }),
  );
  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Open" });

  const ofDefinition = created.filter((e) => e.eventDefinitionId === definitionId);
  const competitive = (d: CarnivalEvent) => {
    const match = ofDefinition.find(
      (e) => e.ageGroup === d.ageGroup && e.category === d.category && e.tier === "Competitive",
    );
    if (!match) throw new Error(`Seeded no Competitive ${d.ageGroup}/${d.category} to start from.`);
    return match;
  };
  return {
    carnivalId: carnival.id,
    carnivalName,
    eventName: from.definitionName ?? from.definitionCode ?? "event",
    divisionCount: ofDefinition.length,
    fromEventId: competitive(from).id,
    fromDivisionLabel: divisionLabel(from),
    toEventId: competitive(to).id,
    toDivisionLabel: divisionLabel(to),
  };
}

export interface SeededProgramCarnival {
  carnivalId: string;
  carnivalName: string;
  /** Two events, both unscheduled — the organiser's worklist on arrival. */
  eventName: string;
  divisionLabel: string;
  siblingDivisionLabel: string;
}

/**
 * A carnival with events and NO PROGRAM AT ALL (ADR-0014 §2). The builder
 * journey starts from the state a new carnival is actually in and constructs the
 * program through the UI — stream, slot, placement — rather than asserting
 * against one the seed already made.
 */
export async function seedProgramCarnival(
  api: APIRequestContext,
  label: string,
): Promise<SeededProgramCarnival> {
  const [division, siblingDivision] = await populatedDivisions(api);
  const carnivalName = seedName(label);

  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type: "Swimming",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
  });
  for (const d of [division!, siblingDivision!])
    await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
      eventDefinitionId: d.eventDefinitionId,
      ageGroup: d.ageGroup,
      category: d.category,
    });

  return {
    carnivalId: carnival.id,
    carnivalName,
    eventName: division!.definitionName ?? division!.definitionCode ?? "event",
    divisionLabel: divisionLabel(division!),
    siblingDivisionLabel: divisionLabel(siblingDivision!),
  };
}

/**
 * A carnival holding one SOCIAL-tier event and no entries at all (ADR-0014 §4).
 * A Social event scores a flat point per ENTRY and needs no time — the journey
 * enters a student through the UI and watches the point land in the standings.
 */
export async function seedSocialCarnival(
  api: APIRequestContext,
  label: string,
): Promise<SeededSocialCarnival> {
  const [division] = await populatedDivisions(api);
  const carnivalName = seedName(label);

  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type: "Swimming",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
  });
  const event = await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
    eventDefinitionId: division!.eventDefinitionId,
    ageGroup: division!.ageGroup,
    category: division!.category,
    tier: "Social",
  });
  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Open" });
  await call(api, "post", `/carnivals/${carnival.id}/events/${event.id}/status`, { status: "Open" });

  const eligible = await call<{ students: { studentId: string; displayName: string }[] }>(
    api,
    "get",
    `/carnivals/${carnival.id}/events/${event.id}/eligible-students`,
  );
  const entrant = eligible.students[0];
  if (!entrant)
    throw new Error(
      `Social division ${division!.ageGroup}/${division!.category} has no eligible students.`,
    );

  return {
    carnivalId: carnival.id,
    carnivalName,
    eventId: event.id,
    eventName: division!.definitionName ?? division!.definitionCode ?? "event",
    divisionLabel: divisionLabel(division!),
    entrantName: entrant.displayName,
    // The display name is "preferred surname" (Snapshot.NameOf) and the server
    // matches name PARTS, not the whole string.
    entrantSearchTerm: entrant.displayName.split(" ").pop()!,
  };
}

export interface SeededMarshallingCarnival {
  carnivalId: string;
  carnivalName: string;
  eventName: string;
  /** Where the marshal stands. */
  primaryEventId: string;
  primaryDivisionLabel: string;
  /** A sibling division, so the selector has something to switch to. */
  otherEventId: string;
  otherDivisionLabel: string;
  primaryEntries: Entry[];
  otherEntries: Entry[];
}

/**
 * Two divisions of one definition, each with entries and NEITHER in a race
 * (ADR-0018 §4: race rows are materialised lazily). That is the state the
 * morning actually starts from, and the second division is what makes the
 * selector render at all.
 */
export async function seedMarshallingCarnival(
  api: APIRequestContext,
  label: string,
  entryCount = 2,
  /**
   * ADR-0038 §1. Written BEFORE the entries, because auto-fill happens as the
   * server creates them — set afterwards it would place nobody.
   */
  venueLanes?: { laneCount: number; autoFillLanes: boolean },
): Promise<SeededMarshallingCarnival> {
  const populated = await populatedDivisions(api);
  const from = populated[0]!;
  const to = populated.find(
    (d) => d.eventDefinitionId === from.eventDefinitionId && d.category !== from.category,
  );
  if (!to)
    throw new Error(
      `Every populated division of ${from.definitionName ?? from.eventDefinitionId} is category ` +
        `${from.category}; the selector needs a sibling to switch to.`,
    );

  // The PRIMARY is deliberately the one the selector sorts LAST (age, then
  // category — `siblingDivisions`), so a journey that only passes on plain list
  // order fails here.
  const [first, second] = [from, to].sort(
    (a, b) =>
      ageScopeOrder(a.ageGroup) - ageScopeOrder(b.ageGroup) ||
      a.ageGroup.localeCompare(b.ageGroup) ||
      (a.category ?? "").localeCompare(b.category ?? ""),
  );

  const carnivalName = seedName(label);
  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: carnivalName,
    type: "Swimming",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
  });

  const created: CarnivalEvent[] = [];
  for (const d of [second!, first!])
    created.push(
      await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
        eventDefinitionId: d.eventDefinitionId,
        ageGroup: d.ageGroup,
        category: d.category,
      }),
    );
  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Open" });
  for (const e of created)
    await call(api, "post", `/carnivals/${carnival.id}/events/${e.id}/status`, { status: "Open" });
  if (venueLanes) await call(api, "put", `/carnivals/${carnival.id}/settings`, venueLanes);

  const entriesOf = async (eventId: string): Promise<Entry[]> => {
    const eligible = await call<{ students: { studentId: string; displayName: string }[] }>(
      api,
      "get",
      `/carnivals/${carnival.id}/events/${eventId}/eligible-students`,
    );
    if (eligible.students.length < entryCount)
      throw new Error(`Event ${eventId} has ${eligible.students.length} eligible students.`);
    const made: Entry[] = [];
    for (const student of eligible.students.slice(0, entryCount))
      made.push(
        await call<Entry>(api, "post", `/carnivals/${carnival.id}/events/${eventId}/entries`, {
          studentId: student.studentId,
        }),
      );
    return made;
  };

  return {
    carnivalId: carnival.id,
    carnivalName,
    eventName: from.definitionName ?? from.definitionCode ?? "event",
    primaryEventId: created[0]!.id,
    primaryDivisionLabel: divisionLabel(second!),
    otherEventId: created[1]!.id,
    otherDivisionLabel: divisionLabel(first!),
    primaryEntries: await entriesOf(created[0]!.id),
    otherEntries: await entriesOf(created[1]!.id),
  };
}

/**
 * Give a carnival the division profile ADR-0022 §2 seeds from a template, so the
 * setup screens are captured configured rather than in their empty state.
 */
export async function seedDivisionProfile(
  api: APIRequestContext,
  carnivalId: string,
): Promise<void> {
  await call(api, "put", `/carnivals/${carnivalId}/settings`, {
    divisions: {
      ageGroups: ["12", "13", "14"],
      categories: ["M", "F"],
      tiers: ["Competitive", "Social"],
    },
  });
}

export interface SeededRecordBook {
  carnivalId: string;
  /** Every record the finalisation minted, in the order the custody read lists them. */
  divisions: string[];
  /** The one withdrawn through the real void endpoint. */
  voidedDivision: string;
}

/**
 * A school record book with something in it (ADR-0035). Records are minted by
 * finalisation and by NOTHING else, so the only way to a populated book is to
 * run a whole small carnival: three divisions, two swims each, closed, then
 * finalised. One record is then voided through the real endpoint.
 */
export async function seedRecordBook(
  api: APIRequestContext,
  label: string,
): Promise<SeededRecordBook> {
  const divisions = (await populatedDivisions(api)).slice(0, 3);
  await declareRecordCategories(api, divisions);
  const carnival = await call<Carnival>(api, "post", "/carnivals", {
    name: seedName(label),
    type: "Swimming",
    date: new Date().toISOString().slice(0, 10),
    venue: "E2E Aquatic Centre",
  });

  const events: CarnivalEvent[] = [];
  for (const division of divisions) {
    events.push(
      await call<CarnivalEvent>(api, "post", `/carnivals/${carnival.id}/events`, {
        eventDefinitionId: division.eventDefinitionId,
        ageGroup: division.ageGroup,
        category: division.category,
        tier: "Competitive",
      }),
    );
  }
  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Open" });
  for (const event of events)
    await call(api, "post", `/carnivals/${carnival.id}/events/${event.id}/status`, {
      status: "Open",
    });

  for (const [i, event] of events.entries()) {
    const eligible = await call<{ students: { studentId: string }[] }>(
      api,
      "get",
      `/carnivals/${carnival.id}/events/${event.id}/eligible-students`,
    );
    for (const [j, student] of eligible.students.slice(0, 2).entries()) {
      const entry = await call<Entry>(
        api,
        "post",
        `/carnivals/${carnival.id}/events/${event.id}/entries`,
        { studentId: student.studentId },
      );
      await call(
        api,
        "post",
        `/carnivals/${carnival.id}/events/${event.id}/entries/${entry.id}/results`,
        { value: `${31 + i}.${String(20 + j * 7).padStart(2, "0")}` },
        { "Idempotency-Key": crypto.randomUUID(), "X-Device-Id": "e2e-seed-device" },
      );
    }
  }

  await call(api, "post", `/carnivals/${carnival.id}/status`, { status: "Closed" });
  await call(api, "post", `/carnivals/${carnival.id}/status`, {
    status: "Finalised",
    reason: "E2E record book seed",
  });

  // Re-runnable: the book is SCHOOL-wide and outlives one run, so a second run
  // whose times beat nothing mints nothing — and must reuse the record a
  // previous run withdrew rather than withdrawing the last standing one.
  let book = await custodyList(api);
  if (!book.some((r) => r.voided)) {
    const standing = book.filter((r) => !r.voided);
    if (standing.length < 2) {
      throw new Error(
        `The record book holds ${standing.length} standing records; the capture needs two — one to ` +
          "withdraw and one to arm the confirm on. Run scripts/dev-reset.ps1 and try again.",
      );
    }
    await call(api, "post", `/records/${standing.at(-1)!.id}/void`, {
      reason: "Timekeeper's sheet misread — the swim was 33.40.",
    });
    book = await custodyList(api);
  }

  return {
    carnivalId: carnival.id,
    divisions: book.map((r) => r.division),
    voidedDivision: book.find((r) => r.voided)!.division,
  };
}

interface CustodyRecord {
  id: string;
  division: string;
  voided: boolean;
}

const custodyList = (api: APIRequestContext) =>
  call<CustodyRecord[]>(api, "get", "/records/administration");

/**
 * Records are minted only for divisions a definition DECLARES it holds records
 * for (ADR-0028 §3), and the synthetic seeder declares none — so the book stays
 * empty however many carnivals are finalised. Additive and idempotent: nothing
 * else in the dev loop mints outside finalisation.
 */
async function declareRecordCategories(
  api: APIRequestContext,
  divisions: CarnivalEvent[],
): Promise<void> {
  const byDefinition = new Map<string, Set<string>>();
  for (const division of divisions) {
    const ages = byDefinition.get(division.eventDefinitionId) ?? new Set<string>();
    ages.add(division.ageGroup);
    byDefinition.set(division.eventDefinitionId, ages);
  }
  const catalogue = await call<(Record<string, unknown> & { id: string })[]>(
    api,
    "get",
    "/event-definitions",
  );
  for (const [definitionId, ages] of byDefinition) {
    const definition = catalogue.find((d) => d.id === definitionId)!;
    await call(api, "put", `/event-definitions/${definitionId}`, {
      ...definition,
      recordCategoriesJson: JSON.stringify({
        ageGroups: [...ages],
        categories: ["M", "F"],
      }),
    });
  }
}

/**
 * Report a device's outbox depth the way the app's sync worker does (ADR-0034).
 * `lastSeenUtc` is stamped server-side, so a journey can seed WHAT a device is
 * carrying but never how long ago it went quiet. `X-Device-Id` is mandatory —
 * the server refuses a report that names a device other than the caller.
 */
export async function reportDeviceQueue(
  api: APIRequestContext,
  carnivalId: string,
  deviceId: string,
  queue: { pending: number; parked: number },
): Promise<void> {
  await call(
    api,
    "put",
    `/carnivals/${carnivalId}/devices/${deviceId}/sync-status`,
    {
      pending: queue.pending,
      parked: queue.parked,
      oldestPendingCapturedAt: queue.pending > 0 ? new Date().toISOString() : null,
      appVersion: "e2e",
    },
    { "X-Device-Id": deviceId },
  );
}
