#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M3-004D disposable MediaWiki magic-word/function and cache harness.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="cw-m3-004d-verify"
database_name="cw_m3_004d"
port="${CW_M3_004D_PORT:-3316}"
app_port="${CW_M3_004D_APP_PORT:-5099}"
password="cw-m3-004d-disposable"
work="$(mktemp -d "${TMPDIR:-/tmp}/cw-m3-004d.XXXXXX")"
app_pid=""

cleanup() {
    status=$?
    set +e
    if [[ "$status" -ne 0 && -f "$work/app.log" ]]; then
        echo '--- disposable application log ---' >&2
        tail -200 "$work/app.log" >&2
    fi
    if [[ "$status" -ne 0 && -f "$work/current.html" ]]; then
        echo '--- current response markers ---' >&2
        grep -oE 'Current[^<]{0,500}|page=[^<]{0,500}|ContinuumWiki[^<]{0,100}|Unsupported[^<]{0,100}' "$work/current.html" | head -80 >&2 || true
    fi
    [[ -n "$app_pid" ]] && kill "$app_pid" >/dev/null 2>&1 || true
    podman rm -f "$container_name" >/dev/null 2>&1 || true
    rm -rf "$work"
    exit "$status"
}
trap cleanup EXIT INT TERM

podman rm -f "$container_name" >/dev/null 2>&1 || true
podman run -d --name "$container_name" \
  -e MARIADB_ROOT_PASSWORD="$password" -e MARIADB_DATABASE="$database_name" \
  -p "${port}:3306" docker.io/library/mariadb:11.8 \
  --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci >/dev/null
for _ in $(seq 1 60); do
    podman exec "$container_name" healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1 && break
    sleep 1
done
podman exec "$container_name" healthcheck.sh --connect --innodb_initialized >/dev/null

connection="Server=127.0.0.1;Port=${port};Database=${database_name};User ID=root;Password=${password};"
target_config="$work/target.json"
printf '{"ConnectionString":"%s","DatabaseName":"%s","Purpose":"disposable-mariadb","RequireEmptyTarget":true}\n' "$connection" "$database_name" > "$target_config"
plan="$work/magic-plan.json"
report="$work/magic-report.json"
xml="$repo_root/MediaWikiInventory/Fixtures/magic-functions-fixture.xml"

dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- plan --input "$xml" --output "$plan"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- provision --target-config "$target_config"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- preflight --input "$xml" --plan "$plan" --target-config "$target_config"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- import --input "$xml" --plan "$plan" --target-config "$target_config" --reconciliation-output "$report"
jq -e '.Verdict == "VERIFIED" and .ImportedPageCount == 7 and .ImportedRevisionCount == 8' "$report" >/dev/null

podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) SELECT R.Id, P.Id, NULL, '*', PD.Id FROM Role R JOIN Permission P ON P.Name='Read' JOIN PermissionDisposition PD ON PD.Name='Allow' WHERE R.Name='Anonymous';" >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:${app_port}" Database__Provider=mysql ConnectionStrings__MySql="$connection" Database__MySql__DevelopmentAdminUser=m3-admin Database__MySql__DevelopmentAdminEmail=m3-admin@example.invalid Database__MySql__DevelopmentAdminPassword='M3-004D-Disposable-Password!' dotnet run --project "$repo_root/TightWiki/TightWiki.csproj" --no-build >"$work/app.log" 2>&1 &
app_pid=$!
for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null

curl -fsS "http://127.0.0.1:${app_port}/Wiki_About" -o "$work/native.html"
grep -q 'About' "$work/native.html"
! grep -q 'cw-mediawiki-' "$work/native.html"
curl -fsS "http://127.0.0.1:${app_port}/Magic_Article" -o "$work/current.html"
grep -q 'Magic_Article' "$work/current.html" || { echo 'current title marker failed' >&2; exit 1; }
grep -q 'page=Magic Article full=Magic Article ns= rev=10102 state=new' "$work/current.html" || { echo 'template marker failed' >&2; exit 1; }
grep -q 'ContinuumWiki.NET' "$work/current.html" || { echo 'site marker failed' >&2; exit 1; }
grep -q 'TightWiki en 10102' "$work/current.html" || { echo 'language marker failed' >&2; exit 1; }
grep -q '12,345.5' "$work/current.html" || { echo 'format marker failed' >&2; exit 1; }
grep -q '1970-01-01' "$work/current.html" || { echo 'time marker failed' >&2; exit 1; }
grep -q 'cw-mediawiki-references' "$work/current.html" || { echo 'reference marker failed' >&2; exit 1; }
! grep -qi 'javascript:' "$work/current.html"
curl -fsS "http://127.0.0.1:${app_port}/Magic_Article/1" -o "$work/historical.html"
grep -q 'Historical' "$work/historical.html"
grep -q 'state=old' "$work/historical.html"
! grep -q 'state=new' "$work/historical.html"
curl -fsS "http://127.0.0.1:${app_port}/Other_Article" -o "$work/other.html"
grep -q 'Other page' "$work/other.html"
grep -q 'Unsupported MediaWiki construct' "$work/other.html"
grep -q 'state=other' "$work/other.html"

printf 'CW-M3-004D VERIFIED: disposable MariaDB import, historical magic-word context, typed template page identity, controlled parser functions, unsupported extension diagnostics, native isolation, and HTTP cache-safe rendering passed.\n'
