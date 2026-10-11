#!/usr/bin/env bash
set -Eeuo pipefail

# Runs the source-copy migration portion of CW-M2-006C inside the one
# disposable container owned by mysql-recovery-drill.sh. It deliberately
# refuses to infer paths or accept a missing report.
: "${CW006C_SOURCE_ROOT:?Set CW006C_SOURCE_ROOT to the isolated repaired SQLite copy}"
: "${CW006C_TRANSLATIONS:?Set CW006C_TRANSLATIONS to the isolated translation store}"
: "${CW006C_DEFAULTS:?Set CW006C_DEFAULTS to the isolated defaults store}"
: "${CW006C_REPORT_DIR:?Set CW006C_REPORT_DIR to the checked-in evidence directory}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
utility=(dotnet "$repo_root/SqliteToMariaDb/bin/Debug/net10.0/ContinuumWiki.SqliteToMariaDb.dll")
destination="Server=127.0.0.1;Port=${MYSQL_PORT};Database=${MYSQL_SOURCE_DATABASE};User ID=${MYSQL_TEST_USER};Password=${MYSQL_TEST_PASSWORD};Allow User Variables=True;"
app_log="${CW006C_APP_LOG:-/tmp/cw-m2-006c-app.log}"
mkdir -p "$CW006C_REPORT_DIR"

set +e
timeout 30s env \
  ASPNETCORE_ENVIRONMENT=Development \
  Database__Provider=mysql \
  Database__MySql__DevelopmentOnly=true \
  Database__MySql__DevelopmentAdminPassword=RecoveryPass!123 \
  Database__MySql__DevelopmentAdminUser=recovery-admin \
  ConnectionStrings__MySql="$destination" \
  ASPNETCORE_URLS="http://127.0.0.1:${CW006C_SCHEMA_PORT:-5118}" \
  "$repo_root/TightWiki/bin/Debug/net10.0/TightWiki" >"$app_log" 2>&1
status=$?
set -e
if [[ "$status" -ne 124 ]]; then
  echo "Schema initialization app exited with status $status; expected timeout 124." >&2
  exit 1
fi

while IFS= read -r table; do
  [[ -n "$table" ]] || continue
  podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_SOURCE_DATABASE" \
    -e "SET FOREIGN_KEY_CHECKS=0; TRUNCATE TABLE \`$table\`; SET FOREIGN_KEY_CHECKS=1;" >/dev/null
done < <(podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -N -B -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_SOURCE_DATABASE" \
  -e "SELECT TABLE_NAME FROM information_schema.tables WHERE TABLE_SCHEMA=DATABASE() ORDER BY TABLE_NAME;")

run_report() {
  local operation="$1"
  local report="$CW006C_REPORT_DIR/repaired-$operation.json"
  local output="/tmp/cw-m2-006c-$operation.out"
  local error="/tmp/cw-m2-006c-$operation.err"
  set +e
  "${utility[@]}" "$operation" \
    --source-root "$CW006C_SOURCE_ROOT" \
    --translations "$CW006C_TRANSLATIONS" \
    --defaults "$CW006C_DEFAULTS" \
    --destination "$destination" \
    --report "$report" >"$output" 2>"$error"
  status=$?
  set -e
  if [[ "$status" -ne 0 || ! -s "$report" ]]; then
    echo "CW-M2-006C $operation failed: exit=$status report=$report" >&2
    tail -40 "$error" >&2 || true
    exit 1
  fi
  jq -e --arg operation "$operation" '.operation == $operation and .succeeded == true and (.discrepancies | length) == 0' "$report" >/dev/null
}

run_report integrity
run_report preflight
run_report dry-run
run_report execute

jq -e '.completionMarker == "sqlite-to-mariadb-v1" and .destinationMutated == true and (.mappings | length) == 55' \
  "$CW006C_REPORT_DIR/repaired-execute.json" >/dev/null

for database in "$MYSQL_SOURCE_DATABASE" "$MYSQL_RESTORE_DATABASE"; do
  [[ "$database" =~ ^[A-Za-z0-9_]+$ ]] || { echo "Unsafe disposable database name." >&2; exit 1; }
done

dump="${CW006C_DUMP_PATH:-/tmp/cw-m2-006c-mariadb.sql}"
source_page_count=$(podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -N -B -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_SOURCE_DATABASE" -e 'SELECT COUNT(*) FROM Page;')
source_revision_count=$(podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -N -B -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_SOURCE_DATABASE" -e 'SELECT COUNT(*) FROM PageRevision;')
podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_DUMP_CLIENT" -uroot -p"$MYSQL_ROOT_PASSWORD" \
  --single-transaction --routines --events --triggers --hex-blob --default-character-set=utf8mb4 --skip-comments \
  "$MYSQL_SOURCE_DATABASE" >"$dump"
dump_sha256=$(sha256sum "$dump" | cut -d' ' -f1)
dump_size=$(stat -c '%s' "$dump")

podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -uroot -p"$MYSQL_ROOT_PASSWORD" -e \
  "CREATE DATABASE IF NOT EXISTS \`$MYSQL_RESTORE_DATABASE\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;" >/dev/null
podman exec -i "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_RESTORE_DATABASE" <"$dump"
rollback_page_count=$(podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -N -B -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_RESTORE_DATABASE" -e 'SELECT COUNT(*) FROM Page;')
rollback_revision_count=$(podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -N -B -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_RESTORE_DATABASE" -e 'SELECT COUNT(*) FROM PageRevision;')
[[ "$source_page_count" == "$rollback_page_count" && "$source_revision_count" == "$rollback_revision_count" ]]

app_log="${CW006C_HTTP_APP_LOG:-/tmp/cw-m2-006c-http-app.log}"
app_port="${CW006C_HTTP_PORT:-5119}"
set +e
env ASPNETCORE_ENVIRONMENT=Development \
  Database__Provider=mysql \
  Database__MySql__DevelopmentOnly=true \
  Database__MySql__DevelopmentAdminPassword=RecoveryPass!123 \
  Database__MySql__DevelopmentAdminUser=recovery-admin \
  ConnectionStrings__MySql="$destination" \
  ASPNETCORE_URLS="http://127.0.0.1:${app_port}" \
  "$repo_root/TightWiki/bin/Debug/net10.0/TightWiki" >"$app_log" 2>&1 &
app_pid=$!
echo "$app_pid" >"${MYSQL_DRILL_APP_PID_FILE:-/tmp/cw-m2-006c-http.pid}"
set -e
for _ in {1..60}; do
  code=$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/Identity/Account/Login" || true)
  [[ "$code" == 200 ]] && break
  sleep 1
done
[[ "${code:-}" == 200 ]]

podman exec -i "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_SOURCE_DATABASE" <"$repo_root/scripts/mysql-migration-http-fixture.sql"
export RECOVERY_BASE_URL="http://127.0.0.1:${app_port}"
export RECOVERY_USERNAME=recovery-admin
export RECOVERY_PASSWORD=RecoveryPass!123
export RECOVERY_PAGE_NAVIGATION=recovery-article
export RECOVERY_PAGE_ID=900001
export RECOVERY_HISTORICAL_REVISION=0
export RECOVERY_FILE_NAVIGATION=recovery-bin
export RECOVERY_FILE_SHA256=f31f6e882548760d827c06cb1af9871f1fc0762d5f19fdb8cccfe2c88d258b17
export RECOVERY_CURRENT_FILE_SHA256=f31f6e882548760d827c06cb1af9871f1fc0762d5f19fdb8cccfe2c88d258b17
export RECOVERY_HISTORICAL_FILE_SHA256=df6b383223c3616989564fcd78d21c10bdea11dbb45dafdc5abe71e4a931b0fa
export RECOVERY_RESULTS_JSON="$CW006C_REPORT_DIR/migrated-http.json"
"$repo_root/scripts/mysql-recovery-http.sh"

post_fixture_page_count=$(podman exec "$MYSQL_DRILL_CONTAINER" "$MYSQL_CLIENT" -N -B -uroot -p"$MYSQL_ROOT_PASSWORD" -D "$MYSQL_SOURCE_DATABASE" -e 'SELECT COUNT(*) FROM Page;')
jq -n \
  --arg dump "$dump" --arg sha256 "$dump_sha256" --argjson size "$dump_size" \
  --arg sourceDatabase "$MYSQL_SOURCE_DATABASE" --arg rollbackDatabase "$MYSQL_RESTORE_DATABASE" \
  --argjson sourcePageCount "$source_page_count" --argjson sourceRevisionCount "$source_revision_count" \
  --argjson rollbackPageCount "$rollback_page_count" --argjson rollbackRevisionCount "$rollback_revision_count" \
  --argjson postFixturePageCount "$post_fixture_page_count" \
  '{backup:{artifact:$dump,size:$size,sha256:$sha256,sourceDatabase:$sourceDatabase},rollback:{targetDatabase:$rollbackDatabase,restored:true,pageCountBeforeSmoke:$sourcePageCount,pageCountAfterRestore:$rollbackPageCount,revisionCountBeforeSmoke:$sourceRevisionCount,revisionCountAfterRestore:$rollbackRevisionCount,countsReconciled:($sourcePageCount == $rollbackPageCount and $sourceRevisionCount == $rollbackRevisionCount),postCutoverFixturePageCount:$postFixturePageCount,postCutoverWritesAutomaticallyPreserved:false},http:{artifact:(env.CW006C_REPORT_DIR + "/migrated-http.json"),checks:17}}' \
  >"$CW006C_REPORT_DIR/backup-rollback-rehearsal.json"
echo "CW-M2-006C isolated repaired-copy migration, HTTP smoke, backup, and rollback rehearsal passed."
