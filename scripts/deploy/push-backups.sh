#!/usr/bin/env bash
# push-backups.sh — copy the encrypted database dumps off the VM (ADR-0047 §4,
# assessment B6). Runs ON the VM as the no-sudo deploy account; cron-able:
#
#   17 3 * * *  bash /home/deploy/work/carnival-app/scripts/deploy/push-backups.sh >> ~/push-backups.log 2>&1
#
# Reads BACKUP_PUSH_TARGET from the repo .env — any rsync-over-ssh destination
# the deploy account can reach without a passphrase, e.g.
# backups@nas.school.internal:/srv/sports/carnival-app-host. Unset = a clean no-op.
#
# Only *.dump.age files are pushed. Plaintext *.dump is never copied off the
# host: if you see any, BACKUP_ENCRYPTION is off and that is the thing to fix.

set -Eeuo pipefail
umask 077

COMPOSE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$COMPOSE_DIR"
[ -f docker-compose.yml ] || { echo "No docker-compose.yml in $COMPOSE_DIR — wrong clone?" >&2; exit 1; }

env_value() { sed -n "s/^$1=//p" .env 2>/dev/null | tail -1 | tr -d "\r" | sed -e "s/^[\"']//" -e "s/[\"']\$//"; }

TARGET="$(env_value BACKUP_PUSH_TARGET)"
if [ -z "$TARGET" ]; then
  echo "BACKUP_PUSH_TARGET is not set in $COMPOSE_DIR/.env — nothing to push."
  echo "The encrypted dumps stay on this one disk until a target exists (assessment B6)."
  exit 0
fi

PROJECT="$(env_value COMPOSE_PROJECT_NAME)"
[ -n "$PROJECT" ] || PROJECT="$(basename "$COMPOSE_DIR" | tr '[:upper:]' '[:lower:]')"
VOLUME="${PROJECT}_db-backups"
docker volume inspect "$VOLUME" >/dev/null 2>&1 || { echo "No volume $VOLUME." >&2; exit 1; }

# The volume is not mounted on the host, so stage a copy the deploy account can
# read. Cleared on every exit — the ciphertext is safe, the disk space is not.
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/sports-push.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT

COUNT="$(docker run --rm -v "$VOLUME:/backups:ro" alpine:3 \
  sh -c 'ls -1 /backups/*.dump.age 2>/dev/null | wc -l' | tr -d ' \r')"
if [ "$COUNT" = "0" ]; then
  echo "No *.dump.age in $VOLUME — is BACKUP_AGE_RECIPIENT set and the backup container running?" >&2
  exit 1
fi

# No '2>/dev/null || true' here: a half-extracted stage used to push a truncated
# ciphertext that --ignore-existing then never repaired. set -e plus the count
# and size checks below make a partial copy a failed run instead.
docker run --rm -v "$VOLUME:/backups:ro" -w /backups alpine:3 \
  sh -c 'tar -cf - *.dump.age' | tar -xf - -C "$STAGE"

STAGED="$(ls -1 "$STAGE"/*.dump.age 2>/dev/null | wc -l | tr -d ' ')"
if [ "$STAGED" != "$COUNT" ]; then
  echo "Staged $STAGED of $COUNT dump(s) from $VOLUME — the copy out of the volume was incomplete." >&2
  exit 1
fi
EMPTY="$(find "$STAGE" -name '*.dump.age' -size -1c)"
if [ -n "$EMPTY" ]; then
  echo "Zero-length ciphertext in the stage copy — refusing to push:" >&2
  echo "$EMPTY" >&2
  exit 1
fi

# --checksum, not --ignore-existing: a file already at the target whose bytes
# differ is re-sent. This proves the bytes match; it does not prove they decrypt
# (the age identity is off this host — that is the restore drill's job, §5.5).
echo "Pushing $COUNT encrypted dump(s) from $VOLUME to $TARGET"
rsync -av --checksum --chmod=F600 "$STAGE"/*.dump.age "$TARGET/"
echo "Push complete: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
