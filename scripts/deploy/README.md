# VM deploy scripts

`vm-deploy.sh` is the **stable mechanical core** of a deploy to `carnival-app-host` — the part that is
identical every time. Everything that changes per deploy stays in
[`docs/admin/self-host.md`](../../docs/admin/self-host.md) and stays human.

## Invocation (from the workstation)

```bash
ssh deploy 'PREFLIGHT_OK=1 bash ~/work/carnival-app/scripts/deploy/vm-deploy.sh'
```

**One-time per VM (ADR-0049):** the images are pulled from a private GHCR, so the host needs a
read-only credential once — `printf %s '<read:packages PAT>' | docker login ghcr.io -u <user>
--password-stdin`. Docker's credential store keeps it. Without it every digest lookup fails and the
script stops before deploying anything.

Run it **first without** `PREFLIGHT_OK` — that pass takes the database dump and prints the counts and
pending migrations you need in order to answer the pre-flight, then stops at the gate. Re-run with
`PREFLIGHT_OK=1` once the pre-flight is satisfied. The script resolves the compose directory from its
own location, so the working directory does not matter and the repo path can move.

## What it does

1. Refuses to run on a dirty working tree.
2. Records and prints the current commit as the rollback target — read from the deployed-commit
   comment in `.env`, which names **only a commit that has passed the smoke checks** (a `PENDING`
   or absent comment falls back to the short sha in the `API_IMAGE` tag, then to `HEAD`).
3. `pg_dump -Fc` to `~/pre-deploy-<utcdate>-<shortsha>.dump` **before** anything changes (the service
   is `db`, not `postgres`), and aborts if the file comes out empty.
4. Prints `__EFMigrationsHistory` before, fetches, and lists the pending commits touching
   `api/src/CarnivalApp.Api/Migrations/`.
5. **Stops at the pre-flight gate** unless `PREFLIGHT_OK=1` (env var, not a marker file — a file
   would survive to silently pre-authorise the *next* deploy).
6. `git pull --ff-only`, then **the digest gate (ADR-0049)**: resolves
   `ghcr.io/jcuzzz/carnival-app-{api,web,backup}:sha-<short>` for the commit just pulled and **hard-fails
   if any tag is missing** — CI has not published for that commit, so it is not deployable. This
   replaces the by-hand "confirm CI is green", the step whose omission on 2026-08-18 put a broken
   build on the VM. **Images are built on demand, not on every push** (ADR-0049 amendment
   2026-09-08): run `gh workflow run images.yml -f ref=main` and `gh run watch` until green
   (~18 minutes) *before* deploying, or this gate will refuse the commit. `carnival-app-web-demo` is pinned instead of `carnival-app-web` when `.env` says
   `VITE_DEMO_MODE=true` (demo mode is a different image, not a runtime flag).
7. Saves the pins currently in `.env` — **and the deployed-commit comment line that belongs with
   them** — to `~/pins-<utcdate>-<oldsha>.env`, writes the new
   `API_IMAGE`/`WEB_IMAGE`/`BACKUP_IMAGE` digest pins into `.env` (mode 600) under a comment
   marked `PENDING`, runs
   `deploy/db/init-roles.sql` (ADR-0047 §3), then `docker compose pull` + `docker compose up -d db
   api proxy backup`. **It builds nothing.**
8. Polls the api container for up to 120s, then checks each container's image really is the digest
   just pinned — the comparison that replaced runbook §5.0's served-bytes marker grep — and runs the
   four unauthenticated smoke checks the
   runbook's "Last deploy" rows record — `/health` → `Healthy`, `/api/v1/me` XHR → 401,
   `/api/v1/auth/login` nav → 302, SPA `/` → 200 + HTML — plus the after-count of the migrations
   history. Any FAIL aborts and prints the rollback commands.
9. Runs `deploy/db/init-roles.sql` a second time (migrations have now created `AuditEvents` and any
   new table) and proves the split as the application role: `UPDATE "AuditEvents"` → permission
   denied, `SELECT` from `"Students"` → a count.
10. **Only once every check has passed**, rewrites the `PENDING` comment in `.env` to name the new
    commit — so the record of what is deployed can never name a commit that failed. Any earlier
    exit puts the previous comment line back.
11. Prints a summary: old → new commit, the three image digests, the previous-pins path, dump path,
    history before/after, roles, smoke results.

It also `chmod 600`s `.env` at the start and refuses to deploy when `APP_DB_PASSWORD` or
`BACKUP_AGE_RECIPIENT` is missing (ADR-0047 items 4 and 5).

## What stays manual, and where it lives

| Step | Where |
|---|---|
| Proxmox VM snapshot (host-side) | runbook §5.1 step 1 |
| The change-specific pre-flight SQL, and the gate values it must return | runbook §5.2 |
| `docker-compose.override.yml` / `.env` edits — **config follows the code deploy, never precedes it** | runbook §5.1 steps 9 and 12 |
| Browser sign-in proofs: `/me` for `CARNIVAL-SysAdmins` and a collapsed group (`CARNIVAL-Verifiers`), and the alias log line | runbook §5.1 step 8, §4.1 A |
| The four-role allow/refuse matrix and the lifecycle 404 checks | runbook §4.1 B and C |
| Recording the deploy in the instance table | runbook §5.1 step 13 |

`/me` is cookie-authenticated, so a bare `curl` gets a 401 by design — the role proofs cannot be
scripted from the VM and a green CI run is not a substitute (§4.1 A).

## The two backup scripts (ADR-0047 §4)

| Script | Runs | Does |
|---|---|---|
| `push-backups.sh` | on the VM, cron-able | rsyncs `*.dump.age` from the `db-backups` volume to `BACKUP_PUSH_TARGET`; clean no-op while that is unset. Never copies plaintext. A short stage copy or a zero-length ciphertext fails the run, and `--checksum` re-sends a file whose bytes differ from the target's copy. |
| `restore-drill.sh <age-identity-file>` | on the VM, once a term | decrypts the newest dump, restores into a scratch `carnival_drill`, prints row counts and wall time, then drops and shreds. Record the row in runbook §5.5. **FAILs** on a dump older than 2 × `BACKUP_INTERVAL_SECONDS`, on any of the four tables restoring 0 rows, and on a count under 90% of that table's live count read in the same run. |

## Rollback

The script never rolls back; it prints the commands. **Rollback is the previous digests** (ADR-0049):
`git checkout <old-sha>`, put the three lines from `~/pins-<utcdate>-<oldsha>.env` back into `.env`,
then `docker compose up -d db api proxy backup`. That file also holds the deployed-commit comment
line that went with those pins; the script puts it back in `.env` itself when a deploy fails, so the
*next* run's rollback target is still the last commit that actually served. No rebuild, no registry, and no dependence on the
state of the source tree. **After** a config edit, roll the config back first and the code second —
an old server plus new role names in config is the one state the ADR-0016 claim aliases cannot
rescue (§5.1 step 12). Database rollback is a separate, dump-based operation using the
`pre-deploy-*.dump` from step 3.
