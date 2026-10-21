/**
 * A key is the resource path split into segments. Invalidation is prefix-matched, so
 * `carnival(id)` covers everything a lifecycle transition changes.
 */
export const keys = {
  imports: () => ["/imports"] as const,
  importDefaults: () => ["/imports", "defaults"] as const,
  importValidation: (batchId: string) => ["/imports", batchId, "validation"] as const,
  importReconciliation: (batchId: string) => ["/imports", batchId, "reconciliation"] as const,

  eventDefinitions: (includeInactive = false) =>
    ["/event-definitions", { includeInactive }] as const,

  carnivals: () => ["/carnivals"] as const,
  carnival: (carnivalId: string) => ["/carnivals", carnivalId] as const,
  carnivalEvents: (carnivalId: string) => ["/carnivals", carnivalId, "events"] as const,
  carnivalEvent: (carnivalId: string, eventId: string) =>
    ["/carnivals", carnivalId, "events", eventId] as const,
  entries: (carnivalId: string, eventId: string) =>
    ["/carnivals", carnivalId, "events", eventId, "entries"] as const,
  results: (carnivalId: string, eventId: string) =>
    ["/carnivals", carnivalId, "events", eventId, "results"] as const,

  schedule: (carnivalId: string) => ["/carnivals", carnivalId, "schedule"] as const,

  /** A division's races are a filter over this read, never a request of their own. */
  races: (carnivalId: string) => ["/carnivals", carnivalId, "races"] as const,

  /** Nested under the carnival, so a lifecycle transition re-reads it. */
  devices: (carnivalId: string) => ["/carnivals", carnivalId, "devices"] as const,

  standings: (carnivalId: string) => ["/carnivals", carnivalId, "standings"] as const,

  progress: (carnivalId: string) => ["/carnivals", carnivalId, "progress"] as const,

  resultsReport: (carnivalId: string) => ["/carnivals", carnivalId, "reports", "results"] as const,
  carnivalSettings: (carnivalId: string) => ["/carnivals", carnivalId, "settings"] as const,
  adjustments: (carnivalId: string) => ["/carnivals", carnivalId, "adjustments"] as const,

  /** School-wide: never nested under a carnival, so a lifecycle transition keeps it. */
  records: (sport?: string) => ["/records", { sport: sport ?? null }] as const,

  recordsAdministration: () => ["/records", "administration"] as const,

  /** `records(sport)` is keyed by sport, so only this prefix covers the book and custody list. */
  recordsAll: () => ["/records"] as const,

  houses: () => ["/school/houses"] as const,

  schoolSettings: () => ["/school/settings"] as const,

  /** Not nested under school settings: saving the school row must not re-read the roles. */
  roleAssignments: () => ["/admin/role-assignments"] as const,

  /** The preview nests under the list so an apply invalidates both with one key. */
  eventTemplates: () => ["/admin/event-templates"] as const,
  templateReconcile: (templateId: string) =>
    ["/admin/event-templates", templateId, "reconcile"] as const,
};
