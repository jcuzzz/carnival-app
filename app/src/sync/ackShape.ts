// Runs inside the Dexie upgrade, so it must import neither db.ts nor the transport;
// kind strings are literals here for that reason.

/** What a later correction bases itself on. */
export interface ResultAck {
  id: string;
  currentRevision?: number;
  warnings?: undefined;
  error?: undefined;
  detail?: undefined;
}

/** The implausibility warnings, read nowhere else. */
export interface MaterialiseAck {
  warnings: string[];
  id?: undefined;
  currentRevision?: undefined;
  error?: undefined;
  detail?: undefined;
}

export interface RefusalAck {
  error?: string;
  detail?: string;
  id?: undefined;
  currentRevision?: undefined;
  warnings?: undefined;
}

export type OutboxAck = ResultAck | MaterialiseAck | RefusalAck;

type AckRule = (body: Record<string, unknown>) => OutboxAck | undefined;

const resultAck: AckRule = (body) => {
  if (typeof body.id !== "string") return undefined;
  const revision = body.currentRevision;
  if (typeof revision !== "number") return { id: body.id };
  return { id: body.id, currentRevision: revision };
};

const materialiseAck: AckRule = (body) => {
  if (!Array.isArray(body.warnings)) return undefined;
  return { warnings: body.warnings.filter((w): w is string => typeof w === "string") };
};

/** The item id already is the created entity's id. */
const nothing: AckRule = () => undefined;

const ACK_BY_KIND = new Map<string, AckRule>([
  ["result.submit", resultAck],
  ["result.correct", resultAck],
  ["result.attempt", resultAck],
  ["result.jump", resultAck],
  ["race.materialise", materialiseAck],
  ["entry.create", nothing],
  ["team.create", nothing],
  ["race.create", nothing],
  ["entry.assign", nothing],
  ["adjustment.create", nothing],
]);

/** Every kind with a rule; a new outbox kind must be added to the map above. */
export const ACK_KINDS: readonly string[] = [...ACK_BY_KIND.keys()];

function refusalAck(body: Record<string, unknown>): RefusalAck | undefined {
  const ack: RefusalAck = {};
  if (typeof body.error === "string") ack.error = body.error;
  if (typeof body.detail === "string") ack.detail = body.detail;
  return ack.error === undefined && ack.detail === undefined ? undefined : ack;
}

/** A refusal keeps the same two fields whatever the kind; an unknown kind's ack keeps nothing. */
export function narrowAck(kind: string, status: number, body: unknown): OutboxAck | undefined {
  if (typeof body !== "object" || body === null) return undefined;
  const record = body as Record<string, unknown>;
  if (status < 200 || status >= 300) return refusalAck(record);
  return ACK_BY_KIND.get(kind)?.(record);
}
