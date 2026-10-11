#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M3-003B disposable MariaDB, offline media, HTTP, and recovery harness.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
container_name="cw-m3-003b-verify"
database_name="cw_m3_003b"
restore_database="cw_m3_003b_restore"
port="${CW_M3_003B_PORT:-3313}"
app_port="${CW_M3_003B_APP_PORT:-5096}"
password="cw-m3-003b-disposable"
work="$(mktemp -d "${TMPDIR:-/tmp}/cw-m3-003b.XXXXXX")"
root="$work/media-root"
mkdir -p "$root/media"
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

printf 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=' | base64 -d >"$root/media/example-v1.bin"
printf 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+Q8AAQUBAScY42YAAAAASUVORK5CYII=' | base64 -d >"$root/media/example-v2.bin"
printf '/9j/4AAQSkZJRgABAQAAAQABAAD/2w==' | base64 -d >"$root/media/map.bin"
printf 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=' | base64 -d >"$root/media/unicode.bin"
sha() { sha256sum "$1" | cut -d' ' -f1; }
size() { stat -c '%s' "$1"; }
example1_sha="$(sha "$root/media/example-v1.bin")"; example2_sha="$(sha "$root/media/example-v2.bin")"; map_sha="$(sha "$root/media/map.bin")"; unicode_sha="$(sha "$root/media/unicode.bin")"
example1_size="$(size "$root/media/example-v1.bin")"; example2_size="$(size "$root/media/example-v2.bin")"; map_size="$(size "$root/media/map.bin")"; unicode_size="$(size "$root/media/unicode.bin")"
manifest="$work/media-manifest.json"
cat >"$manifest" <<EOF
{
  "ManifestVersion": "cw-m3-003b-fixture-1",
  "SourceWikiIdentity": "Continuum Synthetic Media Wiki|https://media.synthetic.invalid/wiki|MediaWiki 1.42",
  "Entries": [
    {"OriginalFileName":"Example.png","SourceFilePageId":"600","MimeType":"image/png","Size":$example2_size,"Sha256":"$example2_sha","RelativeBinaryPath":"media/example-v2.bin","OriginalTimestamp":"2025-03-01T01:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"21","DisplayName":"Media author"},"Revisions":[{"SourceMediaRevisionId":"6001","RevisionOrdinal":1,"Timestamp":"2025-03-01T01:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"21","DisplayName":"Media author"},"MimeType":"image/png","Size":$example1_size,"Sha256":"$example1_sha","RelativeBinaryPath":"media/example-v1.bin"},{"SourceMediaRevisionId":"6002","RevisionOrdinal":2,"Timestamp":"2025-03-02T01:00:00Z","Uploader":{"Kind":"AnonymousIp","IpAddress":"192.0.2.51"},"MimeType":"image/png","Size":$example2_size,"Sha256":"$example2_sha","RelativeBinaryPath":"media/example-v2.bin"}]},
    {"OriginalFileName":"地图.jpg","SourceFilePageId":"601","MimeType":"image/jpeg","Size":$map_size,"Sha256":"$map_sha","RelativeBinaryPath":"media/map.bin","OriginalTimestamp":"2025-03-01T02:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"22","DisplayName":"Map author"},"Revisions":[{"SourceMediaRevisionId":"6011","RevisionOrdinal":1,"Timestamp":"2025-03-01T02:00:00Z","Uploader":{"Kind":"NamedUser","SourceId":"22","DisplayName":"Map author"},"MimeType":"image/jpeg","Size":$map_size,"Sha256":"$map_sha","RelativeBinaryPath":"media/map.bin"}]},
    {"OriginalFileName":"ファイル.png","SourceFilePageId":"602","MimeType":"image/png","Size":$unicode_size,"Sha256":"$unicode_sha","RelativeBinaryPath":"media/unicode.bin","OriginalTimestamp":"2025-03-01T03:00:00Z","Uploader":{"Kind":"AnonymousIp","IpAddress":"192.0.2.52"},"Revisions":[{"SourceMediaRevisionId":"6021","RevisionOrdinal":1,"Timestamp":"2025-03-01T03:00:00Z","Uploader":{"Kind":"AnonymousIp","IpAddress":"192.0.2.52"},"MimeType":"image/png","Size":$unicode_size,"Sha256":"$unicode_sha","RelativeBinaryPath":"media/unicode.bin"}]}
  ]
}
EOF

connection="Server=127.0.0.1;Port=${port};Database=${database_name};User ID=root;Password=${password};"
target_config="$work/target.json"
cat >"$target_config" <<EOF
{"ConnectionString":"$connection","DatabaseName":"$database_name","Purpose":"disposable-mariadb","RequireEmptyTarget":true}
EOF
plan="$work/media-plan.json"
report="$work/media-report.json"
xml="$repo_root/MediaWikiInventory/Fixtures/media-fixture.xml"
dotnet run --project "$repo_root/MediaWikiInventory/MediaWikiInventory.csproj" -- plan --input "$xml" --output "$plan" --media-manifest "$manifest" --media-root "$root"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- provision --target-config "$target_config"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- preflight --input "$xml" --plan "$plan" --target-config "$target_config" --media-manifest "$manifest" --media-root "$root"
dotnet run --project "$repo_root/MediaWikiImport/MediaWikiImport.csproj" -- import --input "$xml" --plan "$plan" --target-config "$target_config" --media-manifest "$manifest" --media-root "$root" --reconciliation-output "$report"
jq -e '.Verdict == "VERIFIED" and .SourceMediaCount == 3 and .ImportedMediaRevisionCount == 4' "$report" >/dev/null

dump="$work/media.sql"
podman exec "$container_name" mariadb-dump -uroot -p"$password" --single-transaction --routines --events --triggers --hex-blob --default-character-set=utf8mb4 --skip-comments "$database_name" >"$dump"
podman exec "$container_name" mariadb -uroot -p"$password" -e "CREATE DATABASE IF NOT EXISTS \`$restore_database\` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;" >/dev/null
podman exec -i "$container_name" mariadb -uroot -p"$password" "$restore_database" <"$dump"
source_hash="$(podman exec "$container_name" mariadb -N -B -uroot -p"$password" "$database_name" -e "SELECT LOWER(SHA2(Data,256)) FROM PageFileRevision PFR JOIN PageFile PF ON PF.Id=PFR.PageFileId WHERE PF.Name='Example.png' AND PFR.Revision=1;")"
restore_hash="$(podman exec "$container_name" mariadb -N -B -uroot -p"$password" "$restore_database" -e "SELECT LOWER(SHA2(Data,256)) FROM PageFileRevision PFR JOIN PageFile PF ON PF.Id=PFR.PageFileId WHERE PF.Name='Example.png' AND PFR.Revision=1;")"
[[ "$source_hash" == "$example2_sha" && "$restore_hash" == "$example2_sha" ]]

podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) SELECT R.Id, P.Id, NULL, '*', PD.Id FROM Role R JOIN Permission P ON P.Name='Read' JOIN PermissionDisposition PD ON PD.Name='Allow' WHERE R.Name='Anonymous';" >/dev/null
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:${app_port}" Database__Provider=mysql ConnectionStrings__MySql="$connection" Database__MySql__DevelopmentAdminUser=m3-admin Database__MySql__DevelopmentAdminEmail=m3-admin@example.invalid Database__MySql__DevelopmentAdminPassword='M3-003B-Disposable-Password!' dotnet run --project "$repo_root/TightWiki/TightWiki.csproj" --no-build >"$work/app.log" 2>&1 &
app_pid=$!
for _ in $(seq 1 60); do curl -fsS "http://127.0.0.1:${app_port}/health/ready" >/dev/null 2>&1 && break; sleep 1; done
curl -fsS "http://127.0.0.1:${app_port}/File/Binary/File%3A%3AExample_png/example_png/1" -o "$work/download.bin"
[[ "$(sha "$work/download.bin")" == "$example2_sha" ]]
curl -fsS "http://127.0.0.1:${app_port}/File/Binary/File%3A%3AExample_png/example_png/0" -o "$work/download-historical.bin"
[[ "$(sha "$work/download-historical.bin")" == "$example1_sha" ]]

# CW-M3-004A dispatch checks: the imported current and historical article
# revisions use the MediaWiki renderer and resolve staged media references.
curl -fsS "http://127.0.0.1:${app_port}/media_gallery" -o "$work/article-current.html"
grep -q '<img ' "$work/article-current.html"
curl -fsS "http://127.0.0.1:${app_port}/media_gallery/1" -o "$work/article-historical.html"
grep -q '<img ' "$work/article-historical.html"

authorized_status="$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/File/Binary/File%3A%3AExample_png/example_png/1")"
[[ "$authorized_status" == "200" ]]
podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "DELETE RP FROM RolePermission RP JOIN Role R ON R.Id=RP.RoleId JOIN Permission P ON P.Id=RP.PermissionId WHERE R.Name='Anonymous' AND P.Name='Read' AND RP.PageId='*';" >/dev/null
denied_status="$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/File/Binary/File%3A%3ANoSuch/no_such/0")"
[[ "$denied_status" == "302" ]]
podman exec "$container_name" mariadb -uroot -p"$password" "$database_name" -e "INSERT IGNORE INTO RolePermission (RoleId, PermissionId, Namespace, PageId, PermissionDispositionId) SELECT R.Id, P.Id, NULL, '*', PD.Id FROM Role R JOIN Permission P ON P.Name='Read' JOIN PermissionDisposition PD ON PD.Name='Allow' WHERE R.Name='Anonymous';" >/dev/null
authorized_status="$(curl -sS -o /dev/null -w '%{http_code}' "http://127.0.0.1:${app_port}/File/Binary/File%3A%3AExample_png/example_png/1")"
[[ "$authorized_status" == "200" ]]

printf 'CW-M3-003B/004A VERIFIED: media plan, secure import, 3 files/4 revisions, binary restore, current/historical MediaWiki rendering, download hash, and anonymous permission denial passed.\n'

