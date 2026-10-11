#!/usr/bin/env bash
set -euo pipefail

container_name="cw-m3-003a-verify"
database_name="cw_m3_003a"
port="${CW_M3_003A_PORT:-3308}"
password="cw-m3-003a-disposable"
connection="Server=127.0.0.1;Port=${port};Database=${database_name};User ID=root;Password=${password};"
plan="${TMPDIR:-/tmp}/cw-m3-003a-plan.json"
report="${TMPDIR:-/tmp}/cw-m3-003a-reconciliation.json"
target_config="MediaWikiImport/Fixtures/disposable-target.template.json"

cleanup() {
    if [[ -n "${app_pid:-}" ]]; then kill "$app_pid" 2>/dev/null || true; fi
    podman rm -f "$container_name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

podman rm -f "$container_name" >/dev/null 2>&1 || true
podman run -d --name "$container_name" -e MARIADB_ROOT_PASSWORD="$password" -e MARIADB_DATABASE="$database_name" -p "${port}:3306" docker.io/library/mariadb:11.8 --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci >/dev/null
for attempt in $(seq 1 60); do
    if podman exec "$container_name" healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1; then break; fi
    sleep 1
done

export CONTINUUMWIKI_M3_TARGET_CONNECTION="$connection"
dotnet run --project MediaWikiInventory/MediaWikiInventory.csproj -- plan --input MediaWikiInventory/Fixtures/persistence-fixture.xml --output "$plan"
dotnet run --project MediaWikiImport/MediaWikiImport.csproj -- provision --target-config "$target_config"
dotnet run --project MediaWikiImport/MediaWikiImport.csproj -- preflight --input MediaWikiInventory/Fixtures/persistence-fixture.xml --plan "$plan" --target-config "$target_config"
dotnet run --project MediaWikiImport/MediaWikiImport.csproj -- import --input MediaWikiInventory/Fixtures/persistence-fixture.xml --plan "$plan" --target-config "$target_config" --reconciliation-output "$report"

podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) SELECT R.Id, P.Id, NULL, '*', PD.Id FROM Role R JOIN Permission P ON P.Name='Read' JOIN PermissionDisposition PD ON PD.Name='Allow' WHERE R.Name='Anonymous';" >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5094 Database__Provider=mysql ConnectionStrings__MySql="$connection" Database__MySql__DevelopmentAdminUser=m3-admin Database__MySql__DevelopmentAdminEmail=m3-admin@example.invalid Database__MySql__DevelopmentAdminPassword='M3-003A-Disposable-Password!' dotnet run --project TightWiki/TightWiki.csproj --no-build >/tmp/cw-m3-003a-app.log 2>&1 &
app_pid=$!
for attempt in $(seq 1 60); do
    if curl -fsS http://127.0.0.1:5094/health/ready >/dev/null; then break; fi
    sleep 1
done
curl -fsS http://127.0.0.1:5094/health/ready >/dev/null
curl -fsS http://127.0.0.1:5094/%E4%B8%96%E7%95%8C | grep -F 'Second' >/dev/null
curl -fsS http://127.0.0.1:5094/%E4%B8%96%E7%95%8C/1 | grep -F 'First' >/dev/null
curl -fsS http://127.0.0.1:5094/%E4%B8%96%E7%95%8C%E6%97%A7%E5%90%8D | grep -F '#REDIRECT' >/dev/null
printf 'CW-M3-003A disposable MariaDB and HTTP verification passed: %s\n' "$report"
