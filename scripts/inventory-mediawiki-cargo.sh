#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
: "${MYSQL_CARGO_DEFAULTS_FILE:?set a private MariaDB option file}"
: "${MYSQL_CARGO_DATABASE:?set the authorized database name}"
: "${CARGO_INVENTORY_REPORT_PATH:?set a private output path}"
test -f "$MYSQL_CARGO_DEFAULTS_FILE"
test "$(stat -c '%a' "$MYSQL_CARGO_DEFAULTS_FILE")" = 600
client="${MYSQL_CLIENT:-mariadb}"
mkdir -p -- "$(dirname "$CARGO_INVENTORY_REPORT_PATH")"
"$client" --defaults-extra-file="$MYSQL_CARGO_DEFAULTS_FILE" --batch --raw --skip-column-names --database="$MYSQL_CARGO_DATABASE" \
  -e "SELECT table_name, engine, table_rows, create_time FROM information_schema.tables WHERE table_schema=DATABASE() AND (LOWER(table_name) LIKE '%cargo%' OR LOWER(table_name) LIKE '%_cargo%') ORDER BY table_name;" \
  >"$CARGO_INVENTORY_REPORT_PATH.tsv"
sha256sum "$CARGO_INVENTORY_REPORT_PATH.tsv" >"$CARGO_INVENTORY_REPORT_PATH.tsv.sha256"
chmod 600 "$CARGO_INVENTORY_REPORT_PATH.tsv" "$CARGO_INVENTORY_REPORT_PATH.tsv.sha256"
echo "Cargo metadata inventory written to $CARGO_INVENTORY_REPORT_PATH.tsv"
