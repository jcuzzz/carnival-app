#!/usr/bin/env bash
# vm-deploy.sh — the mechanical core of a Proxmox VM deploy (`docs/admin/self-host.md`).
#
#   ssh deploy 'bash ~/work/carnival-app/scripts/deploy/vm-deploy.sh'
#   ssh deploy 'PREFLIGHT_OK=1 bash ~/work/carnival-app/scripts/deploy/vm-deploy.sh'   # see the gate below
#   (PREFLIGHT_OK must be set INSIDE the remote command — an env var on the ssh
#   client side never crosses to the VM.)
#   ssh deploy 'PREFLIGHT_OK=1 LOCAL_BUILD=1 bash ~/work/carnival-app/scripts/deploy/vm-deploy.sh'
#   (LOCAL_BUILD=1: no CI images exist — build them on this VM instead. See below.)
#
# Runs ON the VM, as the no-sudo deploy account. It automates only the parts that are
# the same on every deploy: refuse a dirty tree, dump the database first, show what is
# pending, pull fast-forward, resolve the CI-published image digests for the commit being
# deployed, pin them in .env, bootstrap the database roles, pull and start
# api+proxy+backup, and re-run the unauthenticated smoke checks that every
# "Last deploy" row in the runbook records.
#
# It BUILDS NOTHING (ADR-0049) unless LOCAL_BUILD=1 — the owner-approved fallback for a
# month with no GitHub free-tier minutes left, when CI has published no images at all
# (ADR-0049 amendment). Images otherwise come from ghcr.io, built by the CI `images` job
# from the commit whose tests passed. That needs a one-time, read-only
#   printf %s '<read:packages PAT>' | docker login ghcr.io -u <github-user> --password-stdin
# on this VM; the credential store keeps it (runbook §5.0).
#
# It deliberately does NOT do, and will never do:
#   - the Proxmox VM snapshot (§5.1 step 1) — host-side, not reachable from in here
#   - the change-specific pre-flight SQL (§5.2) — the gate below stops until a human ran it
#   - any edit to docker-compose.override.yml or .env (§5.1 steps 9/12) — config follows the
#     code deploy and is the one act with an ordering hazard; a script would get it wrong quietly
#   - the browser proofs (§5.1 step 8, §4.1 A/B/C) — /me is cookie-authenticated, so the alias
#     check and the role matrix are human, in a browser, and green CI is not evidence
#   - anything needing sudo, anything interactive, any rollback (it only tells you the command)

set -Eeuo pipefail   # -E so the ERR trap survives into functions and subshells

# Everything this script writes into $HOME is a full copy of the database
# (assessment C1). The deploy account's login umask is 022, which made the
# pre-deploy dumps world-readable.
umask 077

SUPPRESS_ROLLBACK=0
OLD_SHA="(not yet recorded)"
DUMP=""
PINS=""
PG_USER=""
PG_DB=""
PREV_PIN_LINE=""
ENV_PIN_PENDING=0
GHCR_OWNER="jcuzzz"
LOCAL_BUILD="${LOCAL_BUILD:-0}"

# The deployed-commit comment is the only on-VM record of what runs, so it must
# name a commit that passed the smoke checks and nothing else: written PENDING
# before the deploy, rewritten on success, put back on any exit before that.
set_pin_comment() {   # $1 = the whole comment line, or "" to drop it; keeps its position in .env
  local tmp
  tmp="$(mktemp)"
  PIN_LINE="$1" awk '
    /^# Digest pins written by vm-deploy/ { if (ENVIRON["PIN_LINE"] != "") print ENVIRON["PIN_LINE"]; next }
    { print }
  ' .env > "$tmp"
  cat "$tmp" > .env
  rm -f "$tmp"
  chmod 600 .env
}

restore_pin_comment() {
  [ "$ENV_PIN_PENDING" = "1" ] || return 0
  ENV_PIN_PENDING=0
  set_pin_comment "$PREV_PIN_LINE" || true
  echo "   .env's deployed-commit comment put back: $OLD_SHA is still the last commit that passed smoke"
}
trap restore_pin_comment EXIT

on_err() {
  [ "$SUPPRESS_ROLLBACK" = "1" ] && exit 1
  echo
  echo "!! DEPLOY FAILED. Rollback:"
  restore_pin_comment
  echo "     cd $(pwd) && git checkout $OLD_SHA"
  if [ -n "$PINS" ] && [ -f "$PINS" ]; then
    echo "     put the previous digest pins back in .env (they are in $PINS), then:"
  fi
  echo "     docker compose up -d db api proxy backup"
  if [ "$LOCAL_BUILD" = "1" ]; then
    echo "     (local build: if the previous images are gone, re-run with LOCAL_BUILD=1 at $OLD_SHA)"
  fi
  [ -n "$DUMP" ] && echo "     database (only if a migration ran and you must go back):"
  [ -n "$DUMP" ] && echo "     docker compose exec -T db pg_restore -U $PG_USER -d $PG_DB --clean --if-exists --no-owner < $DUMP"
  exit 1
}
trap on_err ERR

step() { echo; echo "== $*"; }

FAILURES=0
pass() { echo "   PASS  $1"; }
fail() { echo "   FAIL  $1"; FAILURES=$((FAILURES + 1)); }

env_value() { sed -n "s/^$1=//p" .env 2>/dev/null | tail -1 | tr -d "\r" | sed -e "s/^[\"']//" -e "s/[\"']\$//"; }

# ADR-0047 §3, and the ordering is load-bearing: the pass BEFORE the api starts
# creates the role its connection string already names; the pass AFTER migrations
# grants on new tables and revokes UPDATE/DELETE on AuditEvents, which until then
# may not exist.
bootstrap_roles() {
  docker compose exec -T db psql -v ON_ERROR_STOP=1 -U "$PG_USER" -d "$PG_DB" \
    -v app_user="$APP_USER" -v app_password="$APP_PASSWORD" \
    < deploy/db/init-roles.sql | sed 's/^/     /'
}

# cwd is not trustworthy (ssh lands in $HOME, interactive shells land anywhere): the repo
# clone is wherever this script lives, two levels up.
COMPOSE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$COMPOSE_DIR"
[ -f docker-compose.yml ] || { echo "No docker-compose.yml in $COMPOSE_DIR — wrong clone?" >&2; exit 1; }
step "Compose directory: $COMPOSE_DIR"

# .env holds both database passwords and the Entra client secret; the deploy
# account's login umask (022) left it world-readable (assessment C1).
step "Securing .env"
if [ -f .env ]; then
  ENV_MODE_BEFORE="$(stat -c %a .env)"
  chmod 600 .env
  echo "   .env mode $ENV_MODE_BEFORE -> $(stat -c %a .env)"
else
  echo "   no .env in $COMPOSE_DIR — nothing to secure"
fi

# The application role's credentials (ADR-0047 §3). Absent, compose would
# interpolate an empty password and the api would fail to connect after the
# rebuild — so this is a gate, not a warning.
PG_DB="$(env_value POSTGRES_DB)"
PG_USER="$(env_value POSTGRES_USER)"
APP_USER="$(env_value APP_DB_USER)"; [ -n "$APP_USER" ] || APP_USER="carnival_app"
APP_PASSWORD="$(env_value APP_DB_PASSWORD)"
if [ -z "$APP_PASSWORD" ] || [ -z "$PG_DB" ] || [ -z "$PG_USER" ]; then
  echo "POSTGRES_DB, POSTGRES_USER and APP_DB_PASSWORD must all be set in .env (see .env.example)." >&2
  echo "APP_DB_PASSWORD is new in ADR-0047: add it (any strong value) before deploying." >&2
  SUPPRESS_ROLLBACK=1; exit 1
fi
echo "   database $PG_DB: owner '$PG_USER', application role '$APP_USER'"

# The backup sidecar exits rather than write plaintext dumps (ADR-0047 §4), so a
# missing recipient is a crash-loop this script would otherwise walk past.
if [ -z "$(env_value BACKUP_AGE_RECIPIENT)" ] && [ "$(env_value BACKUP_ENCRYPTION)" != "off" ]; then
  echo "BACKUP_AGE_RECIPIENT is empty in .env. Set the school's age public key, or set" >&2
  echo "BACKUP_ENCRYPTION=off for a synthetic test VM (never with real data)." >&2
  SUPPRESS_ROLLBACK=1; exit 1
fi

# Caddy host-matches {$SITE_ADDRESS}; a localhost handshake gets no cert and dies with
# curl (35) even under -k (§4.1). So resolve the FQDN to the loopback rather than asking
# for localhost. Falls back to localhost only when SITE_ADDRESS is unset (pre-TLS stand-up).
SITE="$(env_value SITE_ADDRESS)"
if [ -n "$SITE" ] && [ "$SITE" != "localhost" ]; then
  BASE="https://$SITE"; CURL=(curl -sk --resolve "$SITE:443:127.0.0.1")
else
  BASE="https://localhost"; CURL=(curl -sk)
fi
echo "   smoke target: $BASE"

# (a) A dirty tree means someone edited files on the VM by hand. Pulling over that either
# conflicts or silently ships an un-reviewed change — either way, not a deploy.
# Bash keeps the pre-pull inode open, so a script that pulls a newer copy of itself
# keeps running the OLD steps (seen 2026-09-04). Pull first when this file changed.
step "Checking this script is current"
git fetch --quiet origin
if ! git diff --quiet HEAD origin/main -- "${BASH_SOURCE[0]}"; then
  echo "vm-deploy.sh changed upstream. Run 'git pull --ff-only' first, then re-run this script." >&2
  SUPPRESS_ROLLBACK=1; exit 3
fi
echo "   script matches origin/main"

step "Checking the working tree is clean"
if [ -n "$(git status --porcelain)" ]; then
  git status --short
  echo "Working tree is dirty. Commit, stash or revert on the VM before deploying." >&2
  SUPPRESS_ROLLBACK=1; exit 1
fi

# (b) Written down before anything moves, because after a failed migration the shell history
# is the only place this number would otherwise live.
# The operator must pull before running whenever this script changed, so HEAD here is
# already the NEW commit. The pin comment the previous deploy wrote is the only on-VM
# record of what is actually running.
step "Recording the currently deployed commit"
PREV_PIN_LINE="$(grep '^# Digest pins written by vm-deploy' .env 2>/dev/null | tail -1 || true)"
PIN_SHA=""
case "$PREV_PIN_LINE" in
  *PENDING*) echo "   the pin comment is PENDING: the last deploy never reached its smoke checks" ;;
  *)         PIN_SHA="$(printf '%s\n' "$PREV_PIN_LINE" | sed -n 's/.*commit \([0-9a-f]\{40\}\).*/\1/p')" ;;
esac

# A LOCAL_BUILD pin carries the short sha as its tag; a digest pin carries nothing.
image_tag_sha() {
  case "$1" in *@sha256:*) return 0 ;; esac
  case "${1##*:}" in
    [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) git rev-parse --verify --quiet "${1##*:}^{commit}" || true ;;
  esac
}

if [ -n "$PIN_SHA" ] && git cat-file -e "${PIN_SHA}^{commit}" 2>/dev/null; then
  OLD_SHA="$PIN_SHA"
  echo "   source: the digest-pin comment in .env, written by the previous deploy"
else
  TAG_SHA="$(image_tag_sha "$(env_value API_IMAGE)")"
  if [ -n "$TAG_SHA" ]; then
    OLD_SHA="$TAG_SHA"
    echo "   source: the API_IMAGE tag still in .env (no usable pin comment)"
  elif [ -n "$PIN_SHA" ]; then
    OLD_SHA="$(git rev-parse HEAD)"
    echo "   .env names commit $PIN_SHA, unknown to this clone - assuming HEAD is what runs"
  else
    OLD_SHA="$(git rev-parse HEAD)"
    echo "   no usable pin comment in .env - assuming HEAD is what runs"
  fi
fi
git --no-pager log --oneline -1 "$OLD_SHA"
echo "   ROLLBACK TARGET: $OLD_SHA"

# (c) Before the pull, not after: APPLY_MIGRATIONS=true means a bad migration can leave the
# api container unable to start, and there is no taking a dump from a stack that is down (§5.2).
step "Dumping the database (service is 'db', not 'postgres')"
DUMP="$HOME/pre-deploy-$(date -u +%Y%m%d)-${OLD_SHA:0:7}.dump"
docker compose exec -T db pg_dump -U "$PG_USER" -Fc "$PG_DB" > "$DUMP"
[ -s "$DUMP" ] || { echo "Dump $DUMP is empty — the stack may be down or the credentials wrong." >&2; SUPPRESS_ROLLBACK=1; exit 1; }
chmod 600 "$DUMP"   # belt-and-braces: the umask above already does this
echo "   $DUMP ($(du -h "$DUMP" | cut -f1), $(stat -c %a "$DUMP"))"

# (c2) These dumps are unencrypted student data with no retention policy of
# their own (assessment C1). Newest 5 only — the pattern covers both the
# script's pre-deploy-* names and hand-made pre-<change>.dump ones.
step "Pruning old pre-deploy dumps (keeping the newest 5)"
for OLD in $(ls -1t "$HOME"/pre-*.dump 2>/dev/null | tail -n +6 || true); do
  rm -f "$OLD"; echo "   removed $OLD"
done
KEPT="$(ls -1 "$HOME"/pre-*.dump 2>/dev/null | wc -l || true)"
echo "   $KEPT dump(s) retained in $HOME"

# (d) The before-count is half of a pair; the after-count at the end is meaningless alone.
step "Migrations: state before, and what is pending"
HIST_BEFORE="$(docker compose exec -T db psql -U "$PG_USER" -d "$PG_DB" -tA -c 'SELECT count(*) FROM "__EFMigrationsHistory";' | tr -d '\r')"
echo "   __EFMigrationsHistory before: $HIST_BEFORE"
git fetch --quiet origin
echo "   pending migration commits ($OLD_SHA..origin/main):"
git --no-pager log --oneline "$OLD_SHA..origin/main" -- api/src/CarnivalApp.Api/Migrations/ | sed 's/^/     /'
echo "   commits behind main: $(git rev-list --count "$OLD_SHA..origin/main")"

# (d2) The gates the script CAN answer. A migration that refuses on data it cannot carry
# (ADR-0058 OneAgePerDivision) must be refused HERE, before the images are swapped:
# discovered at api start, it is a crash loop behind a 502 (2026-09-17). Each entry is
# a migration id and the query whose non-zero count that migration would refuse on.
step "Data gates for pending destructive migrations"
migration_applied() {
  docker compose exec -T db psql -U "$PG_USER" -d "$PG_DB" -tA \
    -c "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '$1';" | tr -d '\r'
}
data_gate() {  # $1 migration id, $2 what the count means, $3 SQL returning one count
  if [ "$(migration_applied "$1")" = "0" ]; then
    COUNT="$(docker compose exec -T db psql -U "$PG_USER" -d "$PG_DB" -tA -c "$3" | tr -d '\r')"
    if [ "$COUNT" != "0" ]; then
      echo "   $1: $COUNT $2 — the migration will refuse. Fix the data (runbook §5.2), then re-run." >&2
      SUPPRESS_ROLLBACK=1; exit 1
    fi
    echo "   $1: 0 $2"
  fi
}
MERGED_AGES="SELECT (SELECT count(*) FROM \"CarnivalEvents\" WHERE \"AgeGroupKey\" LIKE '%-%')
  + (SELECT count(*) FROM \"CarnivalDivisions\" WHERE \"AgeGroupKey\" LIKE '%-%')
  + (SELECT count(*) FROM \"SchoolRecords\" WHERE \"AgeGroup\" LIKE '%-%');"
data_gate 20260916233014_OneAgePerDivision "rows with a merged age key" "$MERGED_AGES"

# (e) The human gate. Every deploy so far has had a change-specific pre-flight — the
# index-tightening duplicate check, the HeatLaneSettingsJson non-null count — and each one
# was a question ("is this zero?") that only a person could answer. The script cannot know
# which query this deploy needs, so it stops here rather than pretending.
#
# Env var, not a marker file: a PREFLIGHT_OK file survives the deploy that created it and
# would silently pre-authorise the NEXT one, which is precisely the failure this gate exists
# to prevent. An env var cannot go stale.
if [ "${PREFLIGHT_OK:-}" != "1" ]; then
  echo
  echo "== PRE-FLIGHT GATE (runbook §5.2)"
  echo "   Stop here. Run the pre-flight queries this change needs, against the counts above,"
  echo "   and read the pending-migration table in §5.2 for the gate that applies:"
  echo "     docker compose exec -T db psql -U $PG_USER -d $PG_DB -c '<the check for this migration>'"
  echo "   A non-zero result on a destructive migration's gate is a conversation, not a deployment."
  echo "   Then re-run:  PREFLIGHT_OK=1 bash $0"
  SUPPRESS_ROLLBACK=1; exit 2
fi
if [ -e "$(dirname "${BASH_SOURCE[0]}")/PREFLIGHT_OK" ]; then
  echo "   note: a PREFLIGHT_OK file exists and is IGNORED (env var only). Delete it."
fi
echo "   pre-flight acknowledged by the operator (PREFLIGHT_OK=1)"

# (f) --ff-only: a merge commit created on the VM is a divergence nobody will ever see again.
step "Pulling (fast-forward only)"
git pull --ff-only
NEW_SHA="$(git rev-parse HEAD)"
git --no-pager log --oneline -1 HEAD

# (f3) The machine gate that replaces the by-hand "confirm CI is green for this
# commit" (ADR-0049 §5). A missing sha-<short> tag means the images job has not
# finished, or failed, for the commit just pulled — so a failed build can no longer
# leave a stale frontend serving quietly: there is simply nothing to deploy.
SHORT="${NEW_SHA:0:7}"
if [ "$LOCAL_BUILD" = "1" ]; then
  # ADR-0049 amendment: no CI minutes means no sha-<short> images to resolve, so pin the
  # tags docker-compose.build.yml will produce (its build: tags with the service's image:).
  step "LOCAL BUILD for $SHORT — no CI digests are resolved"
  echo
  echo "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
  echo "  !! LOCAL BUILD — images built on this VM, NOT the CI-tested images,"
  echo "  !! NOT pinned by digest."
  echo '  !! Record "built on VM (LOCAL_BUILD)" in the runbook row.'
  echo "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
  echo
  API_IMAGE="carnival-app-local/api:$SHORT"
  WEB_IMAGE="carnival-app-local/web:$SHORT"
  BACKUP_IMAGE="carnival-app-local/backup:$SHORT"
  echo "   carnival-app-api     $API_IMAGE"
  echo "   web            $WEB_IMAGE  (VITE_DEMO_MODE=$(env_value VITE_DEMO_MODE || true), the overlay's build arg reads .env)"
  echo "   carnival-app-backup  $BACKUP_IMAGE"
else
  step "Resolving CI image digests for ${NEW_SHA:0:7}"
  if [ "$(env_value VITE_DEMO_MODE)" = "true" ]; then
    WEB_REPO="carnival-app-web-demo"
    echo "   VITE_DEMO_MODE=true -> pinning $WEB_REPO (the DEMO MODE banner is baked into the image)"
  else
    WEB_REPO="carnival-app-web"
  fi

  HAVE_IMAGETOOLS=0
  docker buildx version >/dev/null 2>&1 && HAVE_IMAGETOOLS=1 || true

  resolve_digest() {   # $1 = repository without tag; prints sha256:... or nothing
    local d=""
    if [ "$HAVE_IMAGETOOLS" = "1" ]; then
      d="$(docker buildx imagetools inspect "$1:sha-$SHORT" --format '{{json .Manifest.Digest}}' 2>/dev/null | tr -d '"' || true)"
    fi
    if [ -z "$d" ]; then
      # No buildx plugin: pulling the tag is the portable way to learn its digest,
      # and this deploy is about to pull anyway.
      docker pull -q "$1:sha-$SHORT" >/dev/null 2>&1 || true
      d="$(docker image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$1:sha-$SHORT" 2>/dev/null | sed -n "s|^$1@||p" | head -1 || true)"
    fi
    case "$d" in sha256:*) printf %s "$d" ;; esac
  }

  MISSING=""
  for REPO in carnival-app-api "$WEB_REPO" carnival-app-backup; do
    D="$(resolve_digest "ghcr.io/$GHCR_OWNER/$REPO")"
    if [ -z "$D" ]; then
      MISSING="$MISSING ghcr.io/$GHCR_OWNER/$REPO:sha-$SHORT"
      continue
    fi
    echo "   $REPO  $D"
    case "$REPO" in
      carnival-app-api)    API_IMAGE="ghcr.io/$GHCR_OWNER/$REPO@$D" ;;
      carnival-app-backup) BACKUP_IMAGE="ghcr.io/$GHCR_OWNER/$REPO@$D" ;;
      *)             WEB_IMAGE="ghcr.io/$GHCR_OWNER/$REPO@$D" ;;
    esac
  done
  if [ -n "$MISSING" ]; then
    echo
    echo "CI has not published images for $NEW_SHA; wait for the images job." >&2
    echo "  missing:$MISSING" >&2
    echo "  If the images job is still running, wait and re-run this script. If it FAILED," >&2
    echo "  this commit is not deployable — fix it and let CI publish before trying again." >&2
    echo "  If GitHub's free minutes are exhausted, re-run with LOCAL_BUILD=1 to build here (ADR-0049 amendment)." >&2
    echo "  If EVERY tag is missing, suspect the GHCR credential instead:" >&2
    echo "    printf %s '<read:packages PAT>' | docker login ghcr.io -u <github-user> --password-stdin" >&2
    echo "  NOTHING HAS BEEN DEPLOYED: the stack still runs the previous images. The working" >&2
    echo "  tree is now at $NEW_SHA ('git checkout $OLD_SHA' puts it back)." >&2
    SUPPRESS_ROLLBACK=1; exit 4
  fi
fi

# The previous pins are written before .env is touched, so a rollback needs neither
# the registry nor the source tree in any particular state.
step "Recording the pre-deploy pins, then pinning $SHORT in .env"
PINS="$HOME/pins-$(date -u +%Y%m%d)-${OLD_SHA:0:7}.env"
{
  echo "# Digest pins in .env BEFORE deploying $NEW_SHA (from $OLD_SHA), $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "# Rollback: put these lines back into .env, then"
  echo "#   cd $COMPOSE_DIR && docker compose up -d db api proxy backup"
  # The comment line goes back with the pins: pasted in, it restores the truth
  # about which commit is serving, which the reader below derives from it.
  if [ -n "$PREV_PIN_LINE" ]; then
    echo "$PREV_PIN_LINE"
  else
    echo "# (.env carried no deployed-commit comment before this deploy)"
  fi
  for K in API_IMAGE WEB_IMAGE BACKUP_IMAGE; do
    V="$(env_value "$K")"
    if [ -n "$V" ]; then echo "$K=$V"; else echo "# $K was unset (compose default: the mutable :main tag)"; fi
  done
} > "$PINS"
chmod 600 "$PINS"
echo "   previous pins -> $PINS"

TMP_ENV="$(mktemp)"
grep -v -E '^(API_IMAGE=|WEB_IMAGE=|BACKUP_IMAGE=|# Digest pins written by vm-deploy)' .env > "$TMP_ENV" || true
PIN_NOTE=""
[ "$LOCAL_BUILD" = "1" ] && PIN_NOTE="LOCAL BUILD (unpinned), "
{
  echo "# Digest pins written by vm-deploy.sh — ${PIN_NOTE}commit $NEW_SHA PENDING, $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "API_IMAGE=$API_IMAGE"
  echo "WEB_IMAGE=$WEB_IMAGE"
  echo "BACKUP_IMAGE=$BACKUP_IMAGE"
} >> "$TMP_ENV"
cat "$TMP_ENV" > .env     # rewrite in place: keeps the inode and the 600 mode
rm -f "$TMP_ENV"
chmod 600 .env
ENV_PIN_PENDING=1
echo "   deployed commit recorded as PENDING until the smoke checks pass"
if [ "$LOCAL_BUILD" = "1" ]; then
  echo "   .env names the LOCAL tags built below — no digests, no CI images"
else
  echo "   .env pins $WEB_REPO, carnival-app-api and carnival-app-backup by digest"
fi

# (f2) The api and proxy containers run unprivileged (assessment C3), but their
# named volumes were created while both ran as root, so every file in them is
# root-owned and unreadable to the new users. The API would silently generate a
# fresh DataProtection keyring — signing every official out mid-carnival — and
# Caddy could not read its own CA. Chown before the containers start, never
# after. Runs on every deploy: it is a no-op once the ownership is right, and
# an empty/absent volume is skipped (a fresh one inherits from the image).
step "Volume ownership for the unprivileged containers"
PROJECT="$(env_value COMPOSE_PROJECT_NAME)"
[ -n "$PROJECT" ] || PROJECT="$(basename "$COMPOSE_DIR" | tr '[:upper:]' '[:lower:]')"
chown_volume() {   # $1 = compose volume name, $2 = uid:gid
  local vol="${PROJECT}_$1"
  if ! docker volume inspect "$vol" >/dev/null 2>&1; then
    echo "   $vol: absent — skipped"
    return 0
  fi
  docker run --rm -v "$vol:/mnt" alpine:3 chown -R "$2" /mnt
  echo "   $vol -> $2"
}
chown_volume dp-keys "1654:1654"        # api/Dockerfile: $APP_UID
chown_volume caddy-data "10001:10001"   # deploy/Dockerfile.web: caddy
chown_volume caddy-config "10001:10001"

step "Database roles: creating/refreshing '$APP_USER' before the api restarts"
bootstrap_roles

# (f4) Nothing is built here (ADR-0049) unless LOCAL_BUILD=1. The pull needs the one-time
# `docker login ghcr.io`; an absent or expired read token fails loudly at this
# line, before any container is recreated.
if [ "$LOCAL_BUILD" = "1" ]; then
  # The overlay's build: on a service that already has image: makes compose tag the result
  # with that value — the local tags just written to .env, so a plain `up -d` finds them.
  # An explicit -f list suppresses compose's automatic docker-compose.override.yml, which on
  # the VM holds every Identity__GroupToRoleMappings__ entry (api started with none, 2026-09-08).
  # The build overlay goes LAST so its build: blocks win.
  COMPOSE_FILES=(-f docker-compose.yml)
  if [ -f docker-compose.override.yml ]; then
    COMPOSE_FILES+=(-f docker-compose.override.yml)
  fi
  COMPOSE_FILES+=(-f docker-compose.build.yml)
  step "Building the images on this VM, then starting db + api + proxy + backup"
  echo "   compose files: ${COMPOSE_FILES[*]}"
  docker compose "${COMPOSE_FILES[@]}" up -d --build db api proxy backup
else
  step "Pulling the pinned images"
  docker compose pull api proxy backup

  step "Starting db + api + proxy + backup"
  docker compose up -d db api proxy backup
fi

# (g/h) Rebuilt containers answer the port before they answer correctly; poll rather than sleep.
step "Waiting for the api container (up to 120s)"
API_CID="$(docker compose ps -q api)"
[ -n "$API_CID" ] || { echo "No api container after 'up -d' — check 'docker compose logs api'." >&2; exit 1; }
for i in $(seq 1 60); do
  STATE="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$API_CID")"
  case "$STATE" in healthy|running) echo "   api: $STATE (${i}x2s)"; break ;; esac
  [ "$i" = 60 ] && { echo "api never became healthy (last state: $STATE)" >&2; docker compose logs --tail 40 api; exit 1; }
  sleep 2
done

# (g2) No compose healthcheck on api, so "running" above is true before Kestrel listens and
# Caddy answers 502 meanwhile. Wait for the endpoint, not the process.
step "Waiting for /health to answer (up to 120s)"
for i in $(seq 1 60); do
  BODY="$("${CURL[@]}" "$BASE/health" || true)"
  case "$BODY" in Healthy*) echo "   /health: answered in ${i}x2s"; break ;; esac
  [ "$i" = 60 ] && { echo "api never answered $BASE/health (last body: ${BODY:-<nothing>})" >&2; docker compose logs --tail 40 api; exit 1; }
  sleep 2
done

# (h0) Second pass, now that migrations have run: grants on any new table, and the
# AuditEvents revoke that needs the table to exist.
step "Database roles: second pass (post-migration grants + AuditEvents revoke)"
bootstrap_roles

# (h1) The privilege split is only real if it is checked. Both queries run AS the
# application role, over the same internal network the api uses.
step "Privilege checks (as '$APP_USER')"
app_psql() {
  docker compose exec -T -e PGPASSWORD="$APP_PASSWORD" db \
    psql -h 127.0.0.1 -U "$APP_USER" -d "$PG_DB" -tA -c "$1" 2>&1
}
AUDIT_WRITE="$(app_psql 'UPDATE "AuditEvents" SET "Action" = "Action";' || true)"
case "$AUDIT_WRITE" in
  *"permission denied"*) pass "$APP_USER cannot UPDATE AuditEvents (permission denied)" ;;
  *"append-only"*)       fail "$APP_USER reached the append-only TRIGGER — the REVOKE did not apply" ;;
  *)                     fail "UPDATE \"AuditEvents\" as $APP_USER returned: ${AUDIT_WRITE:-<nothing>}" ;;
esac
STUDENT_READ="$(app_psql 'SELECT count(*) FROM "Students";' || true)"
case "$STUDENT_READ" in
  ''|*[!0-9]*) fail "SELECT from \"Students\" as $APP_USER returned: ${STUDENT_READ:-<nothing>}" ;;
  *)           pass "$APP_USER can SELECT from Students ($STUDENT_READ rows)" ;;
esac

# (h1b) The class of failure a dropped docker-compose.override.yml causes: the rendered config
# declares the group-to-role mappings and the running api has none. Cheap in both modes.
step "Role mappings: rendered config vs the running api"
CFG_MAPS="$(docker compose config 2>/dev/null | grep -c 'Identity__GroupToRoleMappings__' || true)"
RUN_MAPS="$(docker compose exec -T api env 2>/dev/null | grep -c 'Identity__GroupToRoleMappings__' || true)"
if [ "$CFG_MAPS" = "$RUN_MAPS" ]; then
  pass "api has $RUN_MAPS Identity__GroupToRoleMappings__ entries (config declares $CFG_MAPS)"
else
  fail "api has $RUN_MAPS Identity__GroupToRoleMappings__ entries but config declares $CFG_MAPS - a compose file (docker-compose.override.yml?) did not reach the api"
fi

# (h2) The digest comparison that replaces §5.0's served-bytes marker grep: each
# container must BE the image just pinned, or `up -d` reused an older one.
step "Deployed image digests"
digest_check() {   # $1 = compose service, $2 = pinned ref
  local cid img want
  cid="$(docker compose ps -q "$1" || true)"
  img="$(docker inspect -f '{{.Image}}' "$cid" 2>/dev/null || true)"
  want="$(docker image inspect -f '{{.Id}}' "$2" 2>/dev/null || true)"
  if [ -n "$img" ] && [ "$img" = "$want" ]; then
    pass "$1 runs ${2##*/}"
  else
    fail "$1 is NOT running $2 (container image: ${img:-<none>})"
  fi
}
digest_check api "$API_IMAGE"
digest_check proxy "$WEB_IMAGE"
digest_check backup "$BACKUP_IMAGE"

# (h) The same four checks every "Last deploy" row records. Unauthenticated only — anything
# behind the cookie is a browser job.
step "Smoke checks"
HEALTH="$("${CURL[@]}" "$BASE/health" || true)"
case "$HEALTH" in Healthy*) pass "/health contains Healthy" ;; *) fail "/health returned: ${HEALTH:-<nothing>}" ;; esac

ME="$("${CURL[@]}" -o /dev/null -w '%{http_code}' -H 'Accept: */*' "$BASE/api/v1/me" || true)"
[ "$ME" = "401" ] && pass "/api/v1/me XHR → 401 (expected 401)" || fail "/api/v1/me XHR → $ME (expected 401)"

LOGIN="$("${CURL[@]}" -o /dev/null -w '%{http_code}' -H 'Accept: text/html' "$BASE/api/v1/auth/login" || true)"
[ "$LOGIN" = "302" ] && pass "/api/v1/auth/login nav → 302 (expected 302)" || fail "/api/v1/auth/login nav → $LOGIN (expected 302)"

SPA="$("${CURL[@]}" -w '\n%{http_code}' "$BASE/" || true)"
SPA_CODE="$(printf '%s' "$SPA" | tail -1)"
if [ "$SPA_CODE" = "200" ] && printf '%s' "$SPA" | grep -qi '<html\|<!doctype html'; then
  pass "SPA served at / → 200 with HTML"
else
  fail "SPA served at / → $SPA_CODE (expected 200 + HTML)"
fi

HIST_AFTER="$(docker compose exec -T db psql -U "$PG_USER" -d "$PG_DB" -tA -c 'SELECT count(*) FROM "__EFMigrationsHistory";' | tr -d '\r')"
echo "   __EFMigrationsHistory: $HIST_BEFORE → $HIST_AFTER"

[ "$FAILURES" -eq 0 ] || { echo; echo "$FAILURES smoke check(s) failed." >&2; exit 1; }

step "Recording $NEW_SHA as the deployed commit"
set_pin_comment "# Digest pins written by vm-deploy.sh — ${PIN_NOTE}commit $NEW_SHA, $(date -u +%Y-%m-%dT%H:%M:%SZ)"
ENV_PIN_PENDING=0
echo "   .env now names $NEW_SHA, and only because every check above passed"

IMAGES_BLOCK="$API_IMAGE
                  $WEB_IMAGE
                  $BACKUP_IMAGE"
ROLLBACK_NOTE="(no rebuild, and no dependence on the state of the source tree)"
if [ "$LOCAL_BUILD" = "1" ]; then
  # Local tags, not digests: the runbook row is copied from this block.
  IMAGES_BLOCK="LOCAL BUILD (unpinned)
                  $API_IMAGE
                  $WEB_IMAGE
                  $BACKUP_IMAGE"
  ROLLBACK_NOTE="(the pins in $PINS are what the previous deploy ran: 'docker compose up -d'
    pulls them when they are digests. If they are local tags whose images are gone, re-run
    this script with LOCAL_BUILD=1 at $OLD_SHA to rebuild them here.)"
fi

cat <<SUMMARY

========================= DEPLOY SUMMARY =========================
  commit          $OLD_SHA  ->  $NEW_SHA
  images          $IMAGES_BLOCK
  previous pins   $PINS
  pre-deploy dump $DUMP
  migrations      $HIST_BEFORE -> $HIST_AFTER
  db roles        owner '$PG_USER' (migrations) | app '$APP_USER' (requests, no audit writes)
  smoke           /health Healthy | /me 401 | /auth/login 302 | SPA 200   (all PASS)

  NOT DONE BY THIS SCRIPT — do these by hand now:
   - §5.1 step 8: sign in in a BROWSER as CARNIVAL-SysAdmins and as a collapsed
     group (CARNIVAL-Verifiers is the sharpest) and read /api/v1/me. Empty roles []
     means stop and roll the code back, cleanly, before touching any config.
   - §5.1 step 9: only then, if wanted, edit docker-compose.override.yml values
     (keys unchanged) and 'docker compose up -d api'. Config never precedes code.
   - §4.1: the allow/refuse matrix, and the lifecycle 404 checks.
   - §5.1 step 13: record commit, the IMAGE DIGESTS above, migrations, history
     count and /me output in the runbook instance table.

  Rollback if any of that goes wrong:
    cd $COMPOSE_DIR && git checkout $OLD_SHA
    put the pins from $PINS back into .env, then
    docker compose up -d db api proxy backup
    $ROLLBACK_NOTE
    (config first, code second, if the override file was already edited — §5.1 step 12)
==================================================================
SUMMARY
