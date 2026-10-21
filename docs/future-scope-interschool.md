# Future scope — Interschool Sports Management

Recorded 2026-07-24 from the project owner. **Not in current scope**: the immediate priority remains delivery and validation of the swimming and athletics carnival platform. This document exists so near-term design decisions don't foreclose the future module.

## Potential future capabilities

- Competition setup for sports such as basketball, football and netball
- School and team registration
- Fixture and venue scheduling
- Teacher, coach and official assignments
- Score entry, confirmation and dispute handling
- Automatic ladders, finals and progression pathways
- District, division, zone, region and state competition structures
- Public fixtures, results, ladders and announcements website
- Controlled player statistics and representative-history records
- Data exchange between school-hosted instances and competition organisers

## Boundary principle

Interschool competition is a **separate future module** built on shared platform services: authentication, organisations, audit history and publishing. It must not blur the per-school data-isolation model (ADR-0001) — cross-school exchange happens through explicit, controlled data-exchange interfaces between instances and organisers, not shared databases.

## Implications for current design (keep cheap, don't build yet)

1. **Organisation concept**: today "the school" is implicit in a deployment. Avoid hard-coding single-school assumptions into identifiers or report headers; `SchoolConfiguration` already carries the school identity — route all school references through it.
2. **Shared services stay generic**: auth (role assignment scoping), audit events, and publishing/export code should not assume swimming/athletics semantics.
3. **Stable external identifiers**: results and records carry stable IDs (UUIDv7) — safe to exchange later without renumbering.
4. **Sport-agnostic core where free**: `EventDefinition` already separates sport/measure type; team-based scoring is a future extension, not a v1 concern.
5. **Publishing boundary**: the "public website" capability reinforces keeping report/export generation separate from the transactional core (proposal §6).

Any concrete work on this module requires its own proposal, governance review and ADRs.
