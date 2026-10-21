# Assessment: native Android/iOS apps, and AWS hosting with CI auto-deploy

Two questions: what would it take to get an Android and iOS app running, and what about
hosting on AWS with automatic deploy via CI and Docker images? Both touch decided positions,
so the reasoning is recorded here.

## 1. Android and iOS

**The platform already ships an installable app on both.** ADR-0001/ADR-0006 chose the
installable PWA deliberately (BYO across mobile/tablet/laptop, no native app, no MDM — the
trade-offs are recorded there). Add to Home Screen on Android or iOS yields a standalone app
with offline capture and the outbox. ADR-0002's sync design is built around iOS PWA
constraints (foreground drain — iOS background sync is unreliable), so the PWA is not a
compromise bolted on; it is the design.

What a PWA does not give, and the cost of getting it:

| Gap | Path | Real cost |
|---|---|---|
| App-store presence | Wrap the existing PWA: Capacitor (iOS), Trusted Web Activity (Android). Never a rewrite. | Apple Developer org enrolment (~US$99/yr, needs a D-U-N-S identity for the school), Google Play (~US$25 once), code signing, store review (Apple's "minimum functionality" rule sometimes rejects thin wrappers; education-org apps usually pass), and a per-store release pipeline for every update — versus the PWA's deploy-once-every-device-has-it. |
| Guaranteed storage durability | The one solid technical argument for a wrapper: Safari can evict web storage under pressure. | Already mitigated — `navigator.storage.persist()` is requested at startup and installed PWAs are treated preferentially; no eviction observed in testing. A wrapper makes it unconditional. |
| Push notifications | Not currently needed. | Installed PWAs support web push on modern iOS/Android anyway. |

**Position: pilot on the PWA.** It is the decided, built, tested path. "Store-wrapped apps" is
a post-pilot decision to be taken only if the rehearsal or pilot surfaces a concrete gap —
reversing a ratified ADR wants evidence, and there is none yet.

## 2. AWS hosting, and CI auto-deploy via Docker images

Two separate things, deliberately split:

### 2a. CI image pipeline + pull-based deploy — hosting-agnostic, pure win

*This direction is now implemented: [ADR-0049](../adr/0049-images-are-built-in-ci-and-pulled-by-digest.md).*

Building images on the host is slow, non-reproducible and exposed to registry rate limits.
Better shape regardless of hosting:

- CI builds and publishes versioned images to GHCR on green `main` (tagged by SHA, pinned by
  digest).
- The deploy **pulls** a prebuilt image by digest instead of building on the VM.
- "Automatic" is **pull-based**: the VM checks for a new release on a schedule (or the ordinary
  `vm-deploy.sh` run pulls the latest green digest). Push-based deploy — GitHub Actions
  SSH-ing into a school-network host — is rejected: it puts credentials to school
  infrastructure into a third-party CI system and creates an inbound pathway a departmental
  security review will (rightly) dislike.
- Migrations, the pre-deploy dump, the keyring chown and the smoke checks keep their current
  runbook shape; only the build moves.

It keeps working unchanged wherever hosting lands.

### 2b. AWS as the host — technically easy, governance-significant

Technically the stack is portable today (Docker, env config, Postgres; ECS/Fargate + RDS or a
single EC2 running compose). AWS would close several self-hosting infrastructure concerns in
one move: encryption at rest (RDS/EBS), managed off-host backups, managed patching, trusted
TLS, and the end of the single-disk failure domain.

**But it inverts the platform's strongest compliance card.** The school-hosted story — student
data never leaves school control, identity never leaves school or department systems — is
load-bearing in the proposal, the PIA framing, and the departmental assessment pathway
([policy pathway](../governance/policy-pathway.md)). AWS makes the platform third-party cloud
processing of student data: the PIA must cover it, hosting must be the Sydney region, and the
DoE IT Security conversation gains a cloud-arrangements dimension. Not fatal — much of what
schools already use is AWS-hosted — but it is a *different assessment*, and switching
mid-pathway resets clocks.

**Position: pilot stays school-hosted** (it is what the governance track is shaped around).
AWS is held as a **per-school deployment option for the multi-school future** — it pairs
naturally with department-level identity and direct roster sync, because a school without its
own server is exactly the school that also lacks its own tenant. If and
when multi-school work starts, hosting-mode becomes part of that ADR set, and the DoE
engagement should mention it once rather than twice.
