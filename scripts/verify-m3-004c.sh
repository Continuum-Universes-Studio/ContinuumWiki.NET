#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M3-004C disposable MariaDB table, media, gallery, and HTTP harness.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="cw-m3-004c-verify"
database_name="cw_m3_004c"
port="${CW_M3_004C_PORT:-3315}"
app_port="${CW_M3_004C_APP_PORT:-5098}"
password="cw-m3-004c-disposable"
work="$(mktemp -d "${TMPDIR:-/tmp}/cw-m3-004c.XXXXXX")"
root="$work/media-root"
app_pid=""
mkdir -p "$root/media"

cleanup() {
    status=$?
    set +e
    if [[ "$status" -ne 0 && -f "$work/app.log" ]]; then
        echo '--- disposable application log ---' >&2
        tail -200 "$work/app.log" >&2
    fi
    if [[ "$status" -ne 0 && -f "$work/current.html" ]]; then
        echo '--- current response markers ---' >&2
        grep -oE 'cw-mediawiki-[A-Za-z0-9_-]+|Current[^<]{0,80}|Historical[^<]{0,80}' "$work/current.html" | head -80 >&2 || true
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

printf 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=' | base64 -d >"$root/media/map.bin"
printf 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+Q8AAQUBAScY42YAAAAASUVORK5CYII=' | base64 -d >"$root/media/unicode.bin"
sha() { sha256sum "$1" | cut -d' ' -f1; }
size() { stat -c '%s' "$1"; }
map_sha="$(sha "$root/media/map.bin")"; unicode_sha="$(sha "$root/media/unicode.bin")"
map_size="$(size "$root/media/map.bin")"; unicode_size="$(size "$root/media/unicode.bin")"
manifest="$work/media-manifest.json"
cat >"$manifest" <<EOF
{
  "ManifestVersion": "cw-m3-004c-fixture-1",
  "SourceWikiIdentity": "Continuum Synthetic Tables Wiki|https://media.synthetic.invalid/wiki|MediaWiki 1.42",
  "Entries": [
    {"OriginalFileName":"Map.png","SourceFilePageId":"600","MimeType":"image/png","Size":$map_size,"Sha256":"$map_sha","RelativeBinaryPath":"media/map.bin","OriginalTimestamp":"2025-01-01T01:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"5","DisplayName":"Media author"},"Revisions":[{"SourceMediaRevisionId":"6001","RevisionOrdinal":1,"Timestamp":"2025-01-01T01:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"5","DisplayName":"Media author"},"MimeType":"image/png","Size":$map_size,"Sha256":"$map_sha","RelativeBinaryPath":"media/map.bin"}]},
    {"OriginalFileName":"ファイル.png","SourceFilePageId":"601","MimeType":"image/png","Size":$unicode_size,"Sha256":"$unicode_sha","RelativeBinaryPath":"media/unicode.bin","OriginalTimestamp":"2025-01-01T02:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"5","DisplayName":"Media author"},"Revisions":[{"SourceMediaRevisionId":"6011","RevisionOrdinal":1,"Timestamp":"2025-01-01T02:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"5","DisplayName":"Media author"},"MimeType":"image/png","Size":$unicode_size,"Sha256":"$unicode_sha","RelativeBinaryPath":"media/unicode.bin"}]}
  ]
}
EOF

connection="Server=127.0.0.1;Port=${port};Database=${database_name};User ID=root;Password=${password};"
target_config="$work/target.json"
cat >"$target_config" <<EOF
{"ConnectionString":"$connection","DatabaseName":"$database_name","Purpose":"disposable-mariadb","RequireEmptyTarget":true}
EOF
plan="$work/table-media-plan.json"
report="$work/table-media-report.json"
xml="$repo_root/MediaWikiInventory/Fixtures/table-media-fixture.xml"

dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- plan --input "$xml" --output "$plan" --media-manifest "$manifest" --media-root "$root"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- provision --target-config "$target_config"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- preflight --input "$xml" --plan "$plan" --target-config "$target_config" --media-manifest "$manifest" --media-root "$root"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- import --input "$xml" --plan "$plan" --target-config "$target_config" --media-manifest "$manifest" --media-root "$root" --reconciliation-output "$report"
jq -e '.Verdict == "VERIFIED" and .ImportedPageCount == 6 and .ImportedRevisionCount == 8 and .SourceMediaCount == 2 and .ImportedMediaRevisionCount == 2' "$report" >/dev/null

podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) SELECT R.Id, P.Id, NULL, '*', PD.Id FROM Role R JOIN Permission P ON P.Name='Read' JOIN PermissionDisposition PD ON PD.Name='Allow' WHERE R.Name='Anonymous';" >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:${app_port}" Database__Provider=mysql ConnectionStrings__MySql="$connection" Database__MySql__DevelopmentAdminUser=m3-admin Database__MySql__DevelopmentAdminEmail=m3-admin@example.invalid Database__MySql__DevelopmentAdminPassword='M3-004C-Disposable-Password!' dotnet run --project "$repo_root/TightWiki/TightWiki.csproj" --no-build >"$work/app.log" 2>&1 &
app_pid=$!
for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null

curl -fsS "http://127.0.0.1:${app_port}/Wiki_About" -o "$work/native.html"
grep -q 'About' "$work/native.html"
! grep -q 'cw-mediawiki-' "$work/native.html"
curl -fsS "http://127.0.0.1:${app_port}/Table_Article" -o "$work/current.html"
grep -q 'cw-mediawiki-infobox' "$work/current.html"
grep -q 'Current template' "$work/current.html"
grep -q 'cw-mediawiki-table-wrap' "$work/current.html"
grep -q '<caption' "$work/current.html"
grep -q '<th' "$work/current.html"
grep -q 'cw-mediawiki-gallery' "$work/current.html"
grep -q 'Map gallery' "$work/current.html"
grep -q 'Unicode gallery' "$work/current.html"
[[ "$(grep -o '<img ' "$work/current.html" | wc -l)" -ge 3 ]]
grep -q 'cw-mediawiki-unresolved-media' "$work/current.html"
grep -q 'width="150"' "$work/current.html"
! grep -qi 'onclick' "$work/current.html"
! grep -qi 'javascript:' "$work/current.html"

curl -fsS "http://127.0.0.1:${app_port}/Table_Article/1" -o "$work/historical.html"
grep -q 'Historical' "$work/historical.html"
! grep -q 'Current template' "$work/historical.html"

podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "UPDATE PageRevision PR JOIN MediaWikiImportRevision IR ON IR.TargetPageId=PR.PageId AND IR.TargetRevision=PR.Revision JOIN MediaWikiImportPage IP ON IP.ImportBatchId=IR.ImportBatchId AND IP.SourcePageId=IR.SourcePageId SET PR.Body='{| class=\"infobox\"\n! Cache invalidated: {{{name|Unknown}}}\n|}', IR.WikitextSha256=LOWER(SHA2(CONVERT('{| class=\"infobox\"\n! Cache invalidated: {{{name|Unknown}}}\n|}' USING utf8mb4),256)) WHERE IP.SourcePageId='920' AND IR.SourceRevisionId='9202';" >/dev/null
curl -fsS "http://127.0.0.1:${app_port}/Table_Article" -o "$work/updated.html"
grep -q 'Cache invalidated: Current' "$work/updated.html"

printf 'CW-M3-004C VERIFIED: disposable MariaDB import, typed tables, template infoboxes, nested content, advanced media, galleries, historical rendering, native isolation, security filtering, and template cache invalidation passed.\n'
