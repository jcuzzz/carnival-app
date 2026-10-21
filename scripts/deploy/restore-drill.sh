#!/usr/bin/env bash
# restore-drill.sh — prove the newest encrypted backup actually restores
# (ADR-0047 §4). Runs ON the VM as the deploy account, once a term:
#
#   ssh deploy 'bash ~/work/carnival-app/scripts/deploy/restore-drill.sh ~/school-backup.key'
#
# The argument is the age IDENTITY (private key) file. It normally lives OFF
# this VM — copy it on for the drill, run this, delete it. Record the date, the
# dump name, the row counts and the wall time in the "Backups and restore drill"
# table in `docs/admin/self-host.md`.
#
# Everything happens inside the backup container (it has age, pg_restore and the
# owner credentials) and against a throwaway database; the live one is untouched.
#
# It FAILS (exit 1) when the newest dump is older than 2 x BACKUP_INTERVAL_SECONDS,
# when any of the four tables restores 0 rows, or when a restored count is under
# MIN_LIVE_PCT of the same table's live count read in the same run.

set -Eeuo pipefail
umask 077

DRILL_DB="sports_drill"

IDENTITY="${1:-}"
[ -n "$IDENTITY" ] || { echo "usage: $0 <age-identity-file>" >&2; exit 1; }
[ -f "$IDENTITY" ] || { echo "No such identity file: $IDENTITY" >&2; exit 1; }

COMPOSE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$COMPOSE_DIR"
[ -f docker-compose.yml ] || { echo "No docker-compose.yml in $COMPOSE_DIR — wrong clone?" >&2; exit 1; }

env_value() { sed -n "s/^$1=//p" .env 2>/dev/null | tail -1 | tr -d "\r" | sed -e "s/^[\"']//" -e "s/[\"']\$//"; }
PG_DB="$(env_value POSTGRES_DB)"; PG_USER="$(env_value POSTGRES_USER)"
[ -n "$PG_DB" ] && [ -n "$PG_USER" ] || { echo "POSTGRES_DB/POSTGRES_USER missing from .env." >&2; exit 1; }

INTERVAL="$(env_value BACKUP_INTERVAL_SECONDS)"
case "$INTERVAL" in ''|*[!0-9]*) INTERVAL=86400 ;; esac   # the compose default
MAX_DUMP_AGE=$(( INTERVAL * 2 ))
MIN_LIVE_PCT=90   # a restored table this far under its live count is a bad dump, not a drill

in_backup() { docker compose exec -T backup sh -c "$1"; }
docker compose ps -q backup | grep -q . || { echo "The backup container is not running." >&2; exit 1; }

cleanup() {
  in_backup 'rm -f /tmp/drill-identity; command -v shred >/dev/null && shred -u /tmp/drill.dump 2>/dev/null || rm -f /tmp/drill.dump' || true
  docker compose exec -T backup dropdb --if-exists "$DRILL_DB" >/dev/null 2>&1 || true
}
trap cleanup EXIT

NEWEST="$(in_backup 'ls -1t /backups/*.dump.age 2>/dev/null | head -1' | tr -d '\r')"
[ -n "$NEWEST" ] || { echo "No *.dump.age in the db-backups volume — nothing to drill." >&2; exit 1; }
echo "Newest encrypted dump: $NEWEST"

# An old "newest" dump means the sidecar stopped writing; restoring it would pass
# a drill about a backup regime that is no longer running.
NOW_TS="$(in_backup 'date +%s' | tr -d '\r')"
DUMP_TS="$(in_backup "stat -c %Y '$NEWEST'" | tr -d '\r')"
case "$NOW_TS$DUMP_TS" in
  ''|*[!0-9]*) echo "FAIL: could not read the dump's timestamp from the backup container." >&2; exit 1 ;;
esac
DUMP_AGE=$(( NOW_TS - DUMP_TS ))
if [ "$DUMP_AGE" -gt "$MAX_DUMP_AGE" ]; then
  echo "FAIL: the newest dump is ${DUMP_AGE}s old — more than 2 x BACKUP_INTERVAL_SECONDS (${MAX_DUMP_AGE}s)." >&2
  echo "      The backup sidecar has stopped writing; fix that before drilling. Its last good" >&2
  echo "      cycle is stamped in /backups/.last-success." >&2
  exit 1
fi
LAST_SUCCESS="$(in_backup 'cat /backups/.last-success 2>/dev/null' | tr -d '\r' || true)"

START="$(date +%s)"

docker compose cp "$IDENTITY" backup:/tmp/drill-identity
in_backup 'chmod 600 /tmp/drill-identity'
echo "Decrypting..."
in_backup "age -d -i /tmp/drill-identity -o /tmp/drill.dump '$NEWEST'"
in_backup 'rm -f /tmp/drill-identity'

echo "Restoring into $DRILL_DB..."
docker compose exec -T backup dropdb --if-exists "$DRILL_DB"
docker compose exec -T backup createdb "$DRILL_DB"
in_backup "pg_restore --no-owner --exit-on-error -d '$DRILL_DB' /tmp/drill.dump"

ELAPSED=$(( $(date +%s) - START ))

# Explicit exit codes: under set -e a bare psql failure would kill the script
# before it could say which table failed, and the drill would look like a crash.
count_rows() {   # $1 = database, $2 = table
  docker compose exec -T backup psql -tA -d "$1" -c "SELECT count(*) FROM \"$2\";" 2>/dev/null | tr -d '\r'
}

FAILURES=0
echo
echo "===================== RESTORE DRILL ====================="
printf '  dump          %s (%ss old)\n' "$NEWEST" "$DUMP_AGE"
[ -n "$LAST_SUCCESS" ] && printf '  last success  %s\n' "$LAST_SUCCESS"
printf '  scratch db    %s (dropped on exit)\n' "$DRILL_DB"
for TABLE in Students Carnivals Results AuditEvents; do
  COUNT=""; RC=0
  COUNT="$(count_rows "$DRILL_DB" "$TABLE")" || RC=$?
  case "$COUNT" in
    ''|*[!0-9]*)
      printf '  %-13s FAIL — psql returned "%s" (exit %s)\n' "$TABLE" "${COUNT:-<nothing>}" "$RC"
      FAILURES=$((FAILURES + 1)); continue ;;
  esac
  if [ "$COUNT" = "0" ]; then
    printf '  %-13s FAIL — 0 rows restored\n' "$TABLE"
    FAILURES=$((FAILURES + 1)); continue
  fi
  LIVE="$(count_rows "$PG_DB" "$TABLE")" || LIVE=""
  case "$LIVE" in ''|*[!0-9]*) LIVE="" ;; esac
  if [ -z "$LIVE" ]; then
    printf '  %-13s %s rows (live count unavailable)\n' "$TABLE" "$COUNT"
  elif [ "$((COUNT * 100))" -lt "$((LIVE * MIN_LIVE_PCT))" ]; then
    printf '  %-13s FAIL — %s rows, under %s%% of the live %s\n' "$TABLE" "$COUNT" "$MIN_LIVE_PCT" "$LIVE"
    FAILURES=$((FAILURES + 1))
  else
    printf '  %-13s %s rows (live %s)\n' "$TABLE" "$COUNT" "$LIVE"
  fi
done
printf '  wall time     %ss\n' "$ELAPSED"
if [ "$FAILURES" -eq 0 ]; then
  echo "  RESULT        PASS — record this row in the runbook §5.5 table."
else
  echo "  RESULT        FAIL — $FAILURES check(s) failed. Record the row, then fix the backups."
fi
echo "========================================================="
[ "$FAILURES" -eq 0 ] || exit 1
