#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M3-004B disposable MariaDB template expansion and HTTP harness.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="cw-m3-004b-verify"
database_name="cw_m3_004b"
port="${CW_M3_004B_PORT:-3314}"
app_port="${CW_M3_004B_APP_PORT:-5097}"
password="cw-m3-004b-disposable"
work="$(mktemp -d "${TMPDIR:-/tmp}/cw-m3-004b.XXXXXX")"
app_pid=""

cleanup() {
    status=$?
    set +e
    if [[ "$status" -ne 0 && -f "$work/app.log" ]]; then
        echo '--- disposable application log ---' >&2
        tail -200 "$work/app.log" >&2
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
cat >"$target_config" <<EOF
{"ConnectionString":"$connection","DatabaseName":"$database_name","Purpose":"disposable-mariadb","RequireEmptyTarget":true}
EOF
plan="$work/template-plan.json"
report="$work/template-report.json"
xml="$repo_root/MediaWikiInventory/Fixtures/template-expansion-fixture.xml"

dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- plan --input "$xml" --output "$plan"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- provision --target-config "$target_config"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- preflight --input "$xml" --plan "$plan" --target-config "$target_config"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- import --input "$xml" --plan "$plan" --target-config "$target_config" --reconciliation-output "$report"
jq -e '.Verdict == "VERIFIED" and .ImportedPageCount == 6 and .ImportedRevisionCount == 8' "$report" >/dev/null

podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) SELECT R.Id, P.Id, NULL, '*', PD.Id FROM Role R JOIN Permission P ON P.Name='Read' JOIN PermissionDisposition PD ON PD.Name='Allow' WHERE R.Name='Anonymous';" >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:${app_port}" Database__Provider=mysql ConnectionStrings__MySql="$connection" Database__MySql__DevelopmentAdminUser=m3-admin Database__MySql__DevelopmentAdminEmail=m3-admin@example.invalid Database__MySql__DevelopmentAdminPassword='M3-004B-Disposable-Password!' dotnet run --project "$repo_root/TightWiki/TightWiki.csproj" --no-build >"$work/app.log" 2>&1 &
app_pid=$!
for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null
curl -fsS "http://127.0.0.1:${app_port}/Wiki%20About" -o "$work/native.html"
grep -q 'About' "$work/native.html"

curl -fsS "http://127.0.0.1:${app_port}/TemplateArticle" -o "$work/current.html"
grep -q 'Updated name=Current' "$work/current.html"
grep -q 'Inner Current' "$work/current.html"
grep -q 'Only current' "$work/current.html"
grep -q 'Current arithmetic' "$work/current.html"
! grep -q 'Documentation' "$work/current.html"

curl -fsS "http://127.0.0.1:${app_port}/TemplateArticle/1" -o "$work/historical.html"
grep -q 'Name=Koozarii' "$work/historical.html"
grep -q 'Inner Koozarii' "$work/historical.html"
grep -q 'Only historical' "$work/historical.html"
grep -q 'Arithmetic yes' "$work/historical.html"
grep -q '14' "$work/historical.html"
! grep -q 'Documentation' "$work/historical.html"

# Change the persisted imported template body and its provenance hash. The
# template cache version must change even though the article revision does not.
podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "UPDATE PageRevision PR JOIN MediaWikiImportRevision IR ON IR.TargetPageId=PR.PageId AND IR.TargetRevision=PR.Revision JOIN MediaWikiImportPage IP ON IP.ImportBatchId=IR.ImportBatchId AND IP.SourcePageId=IR.SourcePageId SET PR.Body='Cache name={{{name|Unknown}}}; Origin={{{origin|Unknown}}}; {{Inner|value={{{name|Unknown}}}}}', IR.WikitextSha256=LOWER(SHA2(CONVERT('Cache name={{{name|Unknown}}}; Origin={{{origin|Unknown}}}; {{Inner|value={{{name|Unknown}}}}}' USING utf8mb4),256)) WHERE IP.SourcePageId='701' AND IR.SourceRevisionId='7012';" >/dev/null
curl -fsS "http://127.0.0.1:${app_port}/TemplateArticle" -o "$work/updated.html"
grep -q 'Cache name=Current' "$work/updated.html"

printf 'CW-M3-004B VERIFIED: disposable MariaDB import, current/historical template expansion, parameters, inclusion tags, parser functions, safe output, and template cache invalidation passed.\n'
