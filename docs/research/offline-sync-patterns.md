# Offline-First Synchronisation Patterns for Event-Day Result Entry

Research note supporting proposal v0.1, sections 9 (Data and records integrity) and 10 (Event-day operation and offline resilience).

Status: research / design input. Not an implementation commitment. Prototype and field-pilot validation still required (proposal §12, §14).

---

## 1. Problem framing

Result entry at swim/athletics venues is characterised by:

- **Append-mostly workload.** The dominant operation is *creating* a new result for an (entry, event) pair. True concurrent *edits* of the same field by two officials are rare, but they do happen (two staff both enter the same heat; a verifier corrects while an official re-submits). This shapes the whole design: optimise for safe appends + idempotent retries, treat field-level conflict as the rare exception that must be surfaced, never silently resolved.
- **Shared, semi-trusted devices.** Tablets/laptops are pooled across staff and handed over mid-session. Auth and local-data ownership cannot assume one device = one person for a whole day.
- **Unreliable connectivity, not permanent offline.** The device is usually reachable eventually; the design target is *tolerating* multi-minute-to-multi-hour gaps, not running a fully disconnected replica indefinitely.
- **Records-grade auditability.** Every accepted mutation needs actor, timestamp, prior/new value, and reason (proposal §9.3). This is stronger than typical field apps and rules out fire-and-forget sync.

These properties make this a **queued-write / outbox** problem, *not* a general bidirectional CRDT replication problem. That distinction keeps the design tractable.

---

## 2. Core pattern: client outbox + idempotency key + server dedup

The proven, boring, correct baseline for offline writes.

### 2.1 Mechanics

1. **Every intent gets a client-generated UUID at capture time**, before any network attempt. Use **UUIDv7** (time-ordered) so keys are sortable and debuggable and so the server can reason about arrival order. This id is the *idempotency key* and also becomes (or maps to) the durable result id.
2. **The write is persisted locally first** into an append-only **outbox** table in IndexedDB, with status `queued`. The UI reflects the result immediately (optimistic) with a clear `pending` badge. Work is never lost to a failed request.
3. **A sync worker drains the outbox** in order, sending each item with its idempotency key (HTTP header `Idempotency-Key`, plus the id in the body).
4. **The server deduplicates.** On first receipt it processes the write and *stores the key together with the full response* (not just a boolean flag) inside the same DB transaction that commits the result. On any retry of the same key it returns the stored response verbatim — no second insert, no duplicate result. This is the industry-standard idempotency contract (Stripe-style).
5. **On ack the client marks the item `synced`** and records the server's authoritative id/version. On a definitive rejection it moves to `rejected`; on a version clash it moves to `conflict` (see §4).

### 2.2 Why client-generated ids (not server-assigned)

- Enables offline creation with a stable identity before the server has ever seen the row.
- Makes retries naturally idempotent — the key *is* the dedup token.
- Survives device loss/handover: the same logical result carries the same id everywhere.
- Avoids the classic double-submit duplicate (network ack lost, client retries).

### 2.3 Server dedup store notes

- Key the dedup table on `(idempotency_key)` with a uniqueness constraint; the unique-violation path returns the stored result.
- Store the request fingerprint (hash of payload) too: if the *same key* arrives with a *different* payload, that is a client bug or a key collision — reject with a distinct error rather than silently returning the old response.
- Retention: keep keys at least as long as a device could plausibly retry (a full carnival + recovery window; e.g. 30–90 days). They are cheap.
- Scope keys per school deployment (the platform is single-tenant per school, proposal §3.1) — no cross-tenant key space.

Sources: [EducBA – Offline-First: Outbox, Idempotency & Conflict Resolution](https://www.educba.com/offline-first/), [Designing Idempotent APIs in ASP.NET Core](https://developersvoice.com/blog/dotnet/designing-idempotent-apis-in-aspnet-core/), [Idempotency Patterns – BackendBytes](https://backendbytes.com/articles/idempotency-patterns-distributed-systems/), [System Design Sandbox – Idempotency & Deduplication](https://www.systemdesignsandbox.com/learn/idempotency-deduplication).

---

## 3. Client architecture: service worker + IndexedDB + sync

### 3.1 Layered model

- **Static shell (cache-first):** app assets precached by the service worker so the PWA launches with no network.
- **Reference data (pre-downloaded, read-mostly):** carnival config, event definitions, assigned events, and the *minimum* roster for the device (proposal §10 pre-event preparation). Stored in IndexedDB, downloaded and version-stamped during the "publish event-day package" step (proposal Appendix B). Keep the roster minimal for data-minimisation (proposal §11.1) and quota reasons.
- **Outbox (append-only writes):** IndexedDB object store of pending mutations — the local source of truth for anything not yet acknowledged.
- **Sync worker:** drains the outbox with exponential backoff + jitter, capped retries, and a dead-letter/`needs-attention` state rather than infinite looping.

### 3.2 Background Sync API — useful, but do not depend on it

The Background Sync API lets the service worker replay queued writes after connectivity returns *even if the tab is closed*. Attractive, but it has real caveats for a records-grade system:

- **Not Baseline / uneven browser support** — notably weak/absent on iOS Safari, a likely venue device. Cannot be the only mechanism.
- **Service workers get killed** by the browser to save resources; sync events are best-effort and time-boxed.
- **IndexedDB quota** is typically a percentage of free disk and can be evicted under storage pressure; large rosters or long-unsynced queues risk eviction. Mitigate with minimal roster, data-aging of synced items, and (where supported) `navigator.storage.persist()` to request durable storage.

**Recommendation:** treat Background Sync as a *progressive enhancement*. The reliable path is a **foreground sync loop** that runs whenever the app is open and online (triggered by `online` events, a visibility/interval poll, and an explicit "Sync now" control), backed by the outbox. Officials keep the app open during a carnival, so foreground sync covers the primary case; Background Sync opportunistically flushes when it can. Never report "finalised/synced" on the strength of a Background Sync registration alone — confirm against server acks.

Sources: [MDN – Background Synchronization API](https://developer.mozilla.org/en-US/docs/Web/API/Background_Synchronization_API), [LogRocket – Offline-first frontend apps in 2025](https://blog.logrocket.com/offline-first-frontend-apps-2025-indexeddb-sqlite/), [InfoWorld – Service worker background sync](https://www.infoworld.com/article/2515213/progressive-web-app-essentials-service-worker-background-sync.html), [Rohit Raj – Offline-First PWA Patterns](https://rohitraj.tech/en/notes/pwa-offline-sync).

---

## 4. Conflict detection and resolution (append-mostly)

### 4.1 Detection: optimistic concurrency via version/ETag

Attach a monotonically increasing **version** (or ETag/row-version) to each server-side result. Reads carry the version; writes assert the version they were based on (`If-Match` / body `base_version`). The server compares at commit:

- **Version matches →** apply, increment version, return new version.
- **Version differs →** conflict. Do **not** apply. Return the current server state + version so the client can present a resolution.

This is standard optimistic concurrency (EF Core `RowVersion`, HTTP ETags, AppSync `conflictDetection: VERSION`, Cosmos ETag). It holds no locks and assumes conflicts are rare — correct for this workload.

### 4.2 Resolution policy — differentiate by operation type

- **New result creation (the common case):** there is no prior version to clash with. Idempotency key handles duplicate-submit; a *second distinct* result for the same (entry, event) is a **business duplicate**, surfaced to a verifier, not auto-merged. Two officials entering the same swimmer's time is a real-world event that a human should reconcile.
- **Correction of an existing result:** use version check. On mismatch → **manual resolution**, never last-write-wins. Present both values (server vs incoming), actor and timestamp of each, and let a Verifier/Records manager choose, consistent with proposal §10 ("surfaced for resolution rather than silently overwriting") and the Verifier role (§8).

### 4.3 Why not last-write-wins

LWW is fine for low-stakes, self-correcting data (presence, draft notes). It is explicitly wrong for official competition results and records: it silently discards one official's data with no audit of the loss, violating proposal §9.3 audit and §16 "incorrect results/records" risk. Reserve LWW, if at all, only for genuinely non-authoritative local UI state.

### 4.4 Append-only revision model on the server

Model results as a current row + an **immutable `result_revision` log** (proposal Appendix A). Every accepted mutation writes a revision (prior value, new value, actor, role, timestamp, reason, app/rule version). Conflicts and their resolutions are themselves revisions. This gives the full audit trail and makes "prior/new values" a natural by-product rather than an add-on.

Sources: [AWS AppSync – Conflict detection and resolution](https://docs.aws.amazon.com/appsync/latest/devguide/conflict-detection-and-resolution.html), [EF Core RowVersion concurrency](https://rohitsakhare.medium.com/concurrency-handling-with-rowversion-in-ef-core-10fd0215d459), [Google Cloud – ETags for concurrency control](https://docs.cloud.google.com/gemini-enterprise-agent-platform/build/vector-search-2/data-objects/using-etags-for-concurrency-control), [Cosmos DB – Optimistic concurrency](https://learn.microsoft.com/en-us/azure/cosmos-db/database-transactions-optimistic-concurrency), [SQLite mobile sync engine conflict resolution](https://www.sqliteforum.com/p/building-a-mobile-sync-engine-with-1a2).

---

## 5. Shared-device auth and session expiry with a pending queue

This is the hardest requirement, because auth (per-person) and the outbox (per-device) have different lifetimes.

### 5.1 Separate device trust from user identity

- **The outbox belongs to the device/app install, not the logged-in user.** Each queued item records the **actor who created it** (captured at entry time and signed into the payload), so handover or re-login never re-attributes existing queued work. This directly satisfies "who" in the audit trail even when the person who eventually syncs differs from the person who entered.
- **Never block or discard queued writes because the current session expired.** Losing entered results on a token timeout is unacceptable at an event.

### 5.2 Token strategy

- Auth via the school OIDC provider (proposal §8). Obtain a **short-lived access token + longer-lived refresh token** (`offline_access` scope where applicable).
- **While offline:** treat the user as authenticated for *local entry* as long as a valid refresh token exists; do not re-validate against the IdP mid-entry. Allow **cached reads and local queued writes**; these are staged, not authoritative.
- **On reconnect, before draining the outbox:** silently refresh the access token. If refresh succeeds, replay the queue. If refresh fails (refresh token expired/revoked), **pause sync, keep the queue intact, and prompt for re-authentication** — the common "view/queue offline, but require fresh auth to commit" pattern used by field apps.
- The **server re-authorises every queued write on arrival** using the current token *and* checks the recorded actor still held the required role at entry time (role enforced at backend, proposal §8). The client token proves who is syncing; the payload actor proves who entered.

### 5.3 Handover, lock, and per-person attribution

- Enforce a **lock screen / session timeout** on the device (proposal §8) so an idle handed-over tablet does not sit in someone else's session.
- On staff change, a **fast re-login** rebinds the *current user* without touching the outbox. Newly created items get the new actor; previously queued items retain their original actor.
- Consider a lightweight "who are you?" actor selection for rapid deck entry, but the authoritative actor for authorisation must derive from the authenticated session, not a free pick, to avoid spoofed attribution.

Sources: [Cursa – Authentication, Token Refresh, and Offline Authorization Decisions](https://cursa.app/en/page/authentication-token-refresh-and-offline-authorization-decisions), [Fluix – Working on documents offline](https://fluix.io/blog/offline-data-collection), [Firebase – Offline capabilities & auth token persistence](https://firebase.google.com/docs/database/android/offline-capabilities), [Auth0 Community – PWA offline mode + rotating tokens](https://community.auth0.com/t/spa-pwa-offline-mode-localstorage-rotating-tokens/46576), [Auth Omnibus – Refresh Tokens](https://authomnibus.com/authorization/oauth/refresh-tokens/).

---

## 6. Device loss / handover / storage-clear recovery

- **Server is the durable record; the device is a cache + staging buffer.** Once a write is acked, its loss on-device is harmless. The risk window is *unsynced* items only.
- **Shrink the risk window:** aggressive foreground sync, a visible pending-count, and a "N results not yet synced — sync before leaving" guard when closing an event.
- **Device loss with unsynced items:** those specific entries are lost (they never reached the server). Mitigate operationally — encourage frequent sync, show per-device outstanding counts to coordinators, and prefer multiple devices over one device holding a large unsynced batch. This is an accepted, bounded risk, documented for the pilot (proposal §16 connectivity risk).
- **Storage clear / reinstall:** re-download the event-day package and resume; already-synced results are re-fetched from the server, so the device rebuilds cleanly. Idempotency keys mean any accidental re-send of a survived-but-uncertain item cannot duplicate.
- **Handover:** covered in §5.3 — outbox persists across users; attribution stays per-item.
- Because ids are client-generated UUIDv7, a result can be traced end-to-end (device → outbox → server → revision log) for forensic recovery.

---

## 7. How comparable products handle this

- **Hy-Tek Meet Manager (swimming, dominant incumbent):** a thick desktop app that is the local authority during a meet; it ingests times from timing consoles and exchanges data via **merge/results files** rather than live web sync. Lesson: meet-day systems historically prioritise a *locally authoritative* entry surface with deferred reconciliation — validating the "capture locally, reconcile later" posture — but its file-merge model lacks the fine-grained audit trail this proposal requires. ([ACTIVE/Hy-Tek](https://hytek.active.com/swim-meet-software.html))
- **SwimTopia Meet Maestro (modern web):** explicitly advertises an **offline safety net** — if the connection drops you keep running the meet and entering data with *no data lost*, and scores reconcile when connectivity resumes. This is exactly the queued-write posture recommended here, delivered as a PWA-style web app. ([SwimTopia Meet Maestro](https://www.swimtopia.com/tour/swim-meet-management/))
- **Field data-collection apps (Fluix, KipTraq, Alpha, Firebase-backed):** converge on the same primitives — built offline-first, **persist a disk-backed write queue**, **auto-sync on reconnect**, **require online login** (auth is the one thing not done offline), and **pause writes when tokens expire** rather than dropping them. ([KipTraq](https://kiptraq.com/product-demonstrations/working-offline/), [Alpha Software – Offline+Sync architecture](https://www.alphasoftware.com/blog/offline-sync-architecture-tutorial-examples-tools-for-field-operations), [Fluix](https://fluix.io/blog/offline-data-collection))
- **Payments/POS (Stripe-style idempotency):** the canonical source of the **client-key + stored-response dedup** contract adopted in §2. ([BackendBytes](https://backendbytes.com/articles/idempotency-patterns-distributed-systems/))

Convergent takeaways: local-authoritative capture, disk-backed queue, online-only auth with graceful expiry, idempotent replay, deferred reconciliation. The proposal's differentiator over incumbents is the **records-grade audit trail and explicit lifecycle/ratification**, which the file-merge tools do not provide.

---

## 8. Recommendations mapped to the result lifecycle

Aligning with proposal §9.2 (Draft/queued → Provisional → Verified → Final → Ratified record):

| Lifecycle state | Where it lives | Sync / integrity treatment |
|---|---|---|
| **Draft / queued** | Device IndexedDB outbox, status `pending` | Client UUIDv7 assigned at capture; optimistic UI; never lost; not yet authoritative. |
| **Provisional** | Server, after accepted sync | Idempotency-key dedup on insert; first server acceptance = transition; visible for operational review. |
| **Verified** | Server | Corrections use version/ETag optimistic concurrency; version clash → manual resolution by Verifier, never LWW. |
| **Final** | Server | Controlled correction only; each change = immutable revision (actor/time/prior/new/reason). **Finalisation gate:** event cannot finalise while any device reports outstanding queued items or unresolved conflicts (proposal §10, Appendix B coordinator journey). |
| **Ratified record** | Server | Fully audited supersede; unchanged by offline mechanics but depends on all upstream states being clean. |

### 8.1 Concrete design decisions

1. **Client-generated UUIDv7 as idempotency key = durable result id.** Assigned at capture, before first network attempt.
2. **Append-only outbox in IndexedDB**, foreground sync loop primary, Background Sync as enhancement only. Never trust Background Sync for finalisation.
3. **Server dedup table** storing key + payload-hash + full stored response, committed in the same transaction as the result.
4. **Optimistic concurrency (version/ETag) on corrections; business-duplicate surfacing on creates.** No last-write-wins for authoritative results.
5. **Immutable `result_revision` log** as the single source of the §9.3 audit trail; conflicts/resolutions are revisions.
6. **Per-item actor attribution** captured at entry, independent of the syncing session — the key to shared-device correctness.
7. **Online-only login; offline entry permitted on a valid refresh token; refresh-then-replay on reconnect; pause (never drop) the queue on refresh failure.** Server re-authorises every queued write and re-checks role-at-entry.
8. **Explicit per-device outstanding-queue visibility** surfaced to coordinators, and a **hard finalisation gate** blocking event close until all queues drained and conflicts resolved.
9. **Data minimisation for the offline roster** (proposal §11.1) — smaller cache = less quota/eviction risk and less data exposed on a shared/lost device.
10. **Validate in the field pilot** (proposal §14.2): disconnect/reconnect with queued results, duplicate submits, conflicting corrections, token expiry mid-queue, device loss with unsynced items, and handover mid-session.

### 8.2 Explicit non-goals

- No full bidirectional multi-master replication or CRDTs — the workload is queued writes, not collaborative document editing. Added complexity would not be justified and would weaken auditability.
- No silent automatic conflict resolution for authoritative results.
- No indefinite fully-disconnected operation — the target is tolerating temporary loss, not running a standalone replica for days.

---

## 9. Open questions for Phase 0 / prototype

- iOS Safari is a likely venue device — confirm Background Sync / storage-persistence behaviour on the actual hardware; assume foreground-only until proven.
- Refresh-token lifetime vs. a full carnival day: is the refresh token long enough to cover a whole event without a mid-day online re-auth? Choose lifetime with security review.
- Storage quota headroom for the largest realistic roster + a worst-case unsynced backlog on the target devices.
- Exact business rule for a genuine duplicate result on the same (entry, event): auto-flag to Verifier vs. reject-at-source with override.
- Where the authenticated actor list comes from for fast deck re-login without weakening attribution.

---

*Sources consolidated inline. Policy, privacy, security and records-management gates in proposal §12 remain prerequisites; this note informs design only.*
