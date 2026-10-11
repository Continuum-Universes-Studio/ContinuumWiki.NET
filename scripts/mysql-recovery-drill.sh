#!/usr/bin/env bash
set -Eeuo pipefail

# Disposable lifecycle wrapper. MariaDB is the primary target. The caller
# supplies phase commands as arguments while one official MariaDB container
# remains alive. Set MYSQL_IMAGE only for a separately labeled historical
# Oracle MySQL rehearsal.
# Credentials are passed through the process environment and never echoed.
: "${MYSQL_ROOT_PASSWORD:=cw-disposable-root}"
: "${MYSQL_TEST_USER:=continuumwiki}"
: "${MYSQL_TEST_PASSWORD:=cw-disposable-user}"
: "${MYSQL_SOURCE_DATABASE:=continuumwiki_source}"
: "${MYSQL_RESTORE_DATABASE:=continuumwiki_restore}"
: "${MYSQL_PORT:=13308}"
: "${MYSQL_IMAGE:=docker.io/library/mariadb:11.8}"
: "${MYSQL_CLIENT:=mariadb}"
: "${MYSQL_ADMIN_CLIENT:=mariadb-admin}"
: "${MYSQL_DUMP_CLIENT:=mariadb-dump}"
export MYSQL_ROOT_PASSWORD MYSQL_TEST_USER MYSQL_TEST_PASSWORD MYSQL_SOURCE_DATABASE MYSQL_RESTORE_DATABASE MYSQL_PORT MYSQL_IMAGE MYSQL_CLIENT MYSQL_ADMIN_CLIENT MYSQL_DUMP_CLIENT

case "$MYSQL_SOURCE_DATABASE:$MYSQL_RESTORE_DATABASE" in
  *production*|*prod*|*live*) echo "Refusing production-like database name." >&2; exit 2;;
esac
if [[ "$MYSQL_SOURCE_DATABASE" == "$MYSQL_RESTORE_DATABASE" ]]; then
  echo "Source and restore databases must differ." >&2
  exit 2
fi

container="continuumwiki-recovery-${RANDOM}-${RANDOM}"
cleanup() {
  status=$?
  set +e
  if [[ -n "${MYSQL_DRILL_APP_PID_FILE:-}" && -f "$MYSQL_DRILL_APP_PID_FILE" ]]; then
    kill "$(<"$MYSQL_DRILL_APP_PID_FILE")" >/dev/null 2>&1
    rm -f "$MYSQL_DRILL_APP_PID_FILE"
  fi
  if [[ -n "${container:-}" ]]; then
    podman logs "$container" >"${MYSQL_DRILL_LOG:-/tmp/${container}.log}" 2>&1
    podman rm -f "$container" >/dev/null 2>&1
  fi
  exit "$status"
}
trap cleanup EXIT INT TERM

podman run -d --name "$container" \
  -e MARIADB_ROOT_PASSWORD="$MYSQL_ROOT_PASSWORD" \
  -e MARIADB_DATABASE="$MYSQL_SOURCE_DATABASE" \
  -e MARIADB_USER="$MYSQL_TEST_USER" \
  -e MARIADB_PASSWORD="$MYSQL_TEST_PASSWORD" \
  -e MYSQL_ROOT_PASSWORD="$MYSQL_ROOT_PASSWORD" \
  -e MYSQL_DATABASE="$MYSQL_SOURCE_DATABASE" \
  -e MYSQL_USER="$MYSQL_TEST_USER" \
  -e MYSQL_PASSWORD="$MYSQL_TEST_PASSWORD" \
  -p "${MYSQL_PORT}:3306" "$MYSQL_IMAGE" >/dev/null

for _ in {1..60}; do
  if podman exec "$container" "$MYSQL_ADMIN_CLIENT" ping -h127.0.0.1 -P3306 -uroot -p"$MYSQL_ROOT_PASSWORD" --silent >/dev/null 2>&1; then break; fi
  sleep 1
done
podman exec "$container" "$MYSQL_ADMIN_CLIENT" ping -h127.0.0.1 -P3306 -uroot -p"$MYSQL_ROOT_PASSWORD" --silent >/dev/null
podman exec "$container" "$MYSQL_CLIENT" -h127.0.0.1 -P3306 -uroot -p"$MYSQL_ROOT_PASSWORD" -e \
  "GRANT ALL PRIVILEGES ON *.* TO '${MYSQL_TEST_USER}'@'%'; FLUSH PRIVILEGES;" >/dev/null

export MYSQL_SERVER_VERSION="$(podman exec "$container" "$MYSQL_CLIENT" -N -B -h127.0.0.1 -P3306 -uroot -p"$MYSQL_ROOT_PASSWORD" -e 'SELECT VERSION();')"
export MYSQL_SERVER_COMMENT="$(podman exec "$container" "$MYSQL_CLIENT" -N -B -h127.0.0.1 -P3306 -uroot -p"$MYSQL_ROOT_PASSWORD" -e 'SELECT @@version_comment;')"

export CONTINUUMWIKI_RUN_MYSQL_TESTS=1
export CONTINUUMWIKI_MYSQL_TEST_CONNECTION="Server=127.0.0.1;Port=${MYSQL_PORT};Database=${MYSQL_SOURCE_DATABASE};User ID=${MYSQL_TEST_USER};Password=${MYSQL_TEST_PASSWORD};Allow User Variables=True;"
export MYSQL_DRILL_CONTAINER="$container"
export MYSQL_DRILL_LOG="${MYSQL_DRILL_LOG:-/tmp/${container}.log}"

if (($# == 0)); then
  echo "At least one recovery phase is required; refusing a provisioning-only/no-op run." >&2
  exit 2
fi

for phase in "$@"; do
  if [[ "$phase" == "true" || "$phase" == ":" || -z "$phase" ]]; then
    echo "Refusing no-op recovery phase: $phase" >&2
    exit 2
  fi
  bash -lc "$phase"
done

if [[ -n "${MYSQL_HTTP_DRIVER:-}" ]]; then
  "$MYSQL_HTTP_DRIVER"
fi
