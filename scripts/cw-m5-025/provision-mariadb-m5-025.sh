#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

script_dir="$(cd "$(dirname "$BASH_SOURCE")" && pwd -P)"
source "$script_dir/common.sh"

usage() {
  cat <<'EOF'
Usage:
  scripts/cw-m5-025/provision-mariadb-m5-025.sh [options]
  scripts/cw-m5-025/provision-mariadb-m5-025.sh --provision --confirm-disposable-target [options]

Default mode is plan-only. --provision is operator-only and creates one new
rootless Podman target. It never runs migrations, certification, authorization,
or cleanup. The confirmation flag is required before live resource creation.
EOF
}

provision=0
confirm=0
suffix=""
port="${CW_M5_025_PORT:-}"
image="${CW_M5_025_IMAGE:-docker.io/library/mariadb:11.8}"
metadata=""
secrets_dir=""

while (($# > 0)); do
  case "$1" in
    --provision) provision=1 ;;
    --confirm-disposable-target) confirm=1 ;;
    --suffix) (($# >= 2)) || m5_die "--suffix requires a value"; suffix="$2"; shift ;;
    --port) (($# >= 2)) || m5_die "--port requires a value"; port="$2"; shift ;;
    --image) (($# >= 2)) || m5_die "--image requires a value"; image="$2"; shift ;;
    --secrets-dir) (($# >= 2)) || m5_die "--secrets-dir requires a path"; secrets_dir="$2"; shift ;;
    --metadata) (($# >= 2)) || m5_die "--metadata requires a path"; metadata="$2"; shift ;;
    -h|--help) usage; exit 0 ;;
    *) m5_die "unknown option: $1" ;;
  esac
  shift
done

m5_validate_image "$image"
if ((provision)); then
  ((confirm == 1)) || m5_die "--provision requires --confirm-disposable-target"
  m5_require_tool podman
  m5_require_tool openssl
  m5_require_tool python3
  m5_require_rootless_podman
  [[ -n "$suffix" ]] || suffix="$(date -u +%Y%m%d%H%M%S)-$(openssl rand -hex 4)"
else
  [[ -n "$suffix" ]] || suffix=dryrun
fi
[[ "$suffix" =~ ^[a-z0-9][a-z0-9-]{0,47}$ ]] || m5_die "invalid lowercase suffix"
[[ "$suffix" != "operator4" ]] || m5_die "refusing the existing operator4 target suffix"

if [[ -z "$port" ]]; then
  if ((provision)); then
    port="$(m5_find_available_port 13325 13424)" || m5_die "no available loopback port in 13325-13424"
  else
    port=13325
  fi
fi
m5_validate_port "$port"

database_suffix="$(tr '-' '_' <<<"$suffix")"
database_name="cw_m5_025_$database_suffix"
container_name="continuumwiki-m5-025-$suffix"
volume_name="continuumwiki-m5-025-$suffix-data"
m5_validate_names "$database_name" "$container_name" "$volume_name"

if [[ -z "$secrets_dir" ]]; then
  secrets_dir="$HOME/.local/state/continuumwiki/cw-m5-025/$suffix"
fi
m5_assert_secret_dir_outside_repo "$secrets_dir"
if [[ -z "$metadata" ]]; then
  metadata="$(m5_repo_root)/secure/evidence/cw-m5-025c-provisioning.json"
fi
endpoint="127.0.0.1:$port"

if (( ! provision )); then
  cat <<EOF
CW-M5-025C plan-only provisioning: no Podman, database, credentials, migrations, tests, authorization, or cleanup will run.
Planned container: $container_name
Planned volume: $volume_name
Planned database: $database_name
Planned application identity: $CW_M5_025_APP_IDENTITY
Planned endpoint: $endpoint
Planned image: $image
Planned secret directory: $secrets_dir
Planned metadata: $metadata
Live creation requires --provision --confirm-disposable-target and remains separately operator-authorized.
EOF
  exit 0
fi

podman info >/dev/null
podman container exists "$container_name" && m5_die "refusing to reuse container: $container_name" || true
podman volume exists "$volume_name" && m5_die "refusing to reuse volume: $volume_name" || true
m5_port_available "$port" || m5_die "loopback port is unavailable: $port"

mkdir -p "$secrets_dir"
chmod 700 "$secrets_dir"
environment_file="$secrets_dir/environment.env"
[[ ! -e "$environment_file" ]] || m5_die "refusing to overwrite existing credential file"
root_password="$(openssl rand -hex 32)"
app_password="$(openssl rand -hex 32)"
printf 'MARIADB_ROOT_PASSWORD=%s\nMYSQL_PWD=%s\nMARIADB_DATABASE=%s\nMARIADB_ROOT_HOST=localhost\n' "$root_password" "$root_password" "$database_name" >"$environment_file"
chmod 600 "$environment_file"
application_file="$secrets_dir/application.env"
printf 'CW_M5_APP_USER=%s\nCW_M5_APP_PASSWORD=%s\n' "$CW_M5_025_APP_IDENTITY" "$app_password" >"$application_file"
chmod 600 "$application_file"
unset root_password app_password

run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
resolved_digest=""
runtime_version=""
runtime_comment=""
app_schema_scoped=false
rootless=true
outcome=PROVISIONING_FAILED

on_exit() {
  status=$?
  set +e
  identity="container=$container_name;database=$database_name;endpoint=$endpoint;volume=$volume_name;image=$resolved_digest"
  m5_write_provision_metadata "$metadata" "$outcome" "$run_id" "$rootless" "$container_name" "$volume_name" "$database_name" "$endpoint" "$image" "$resolved_digest" "$runtime_version" "$runtime_comment" "$identity" "$environment_file" "$application_file" "$app_schema_scoped" >/dev/null 2>&1 || true
  exit "$status"
}
trap on_exit EXIT INT TERM

podman pull --quiet "$image" >/dev/null
resolved_digest="$(podman image inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$image" | head -n 1)"
[[ "$resolved_digest" =~ ^docker\.io/library/mariadb@sha256:[0-9a-f]{64}$ ]] || m5_die "image did not resolve to an immutable MariaDB digest"

podman volume create \
  --label "io.continuumwiki.m5-025.run-id=$run_id" \
  --label "io.continuumwiki.m5-025-database=$database_name" \
  "$volume_name" >/dev/null
podman run --detach --name "$container_name" \
  --label "io.continuumwiki.m5-025.run-id=$run_id" \
  --label "io.continuumwiki.m5-025-database=$database_name" \
  --label "io.continuumwiki.m5-025-volume=$volume_name" \
  --label "io.continuumwiki.m5-025-endpoint=$endpoint" \
  --label "io.continuumwiki.m5-025-image-digest=$resolved_digest" \
  --publish "$endpoint:3306" --env-file "$environment_file" \
  --volume "$volume_name:/var/lib/mysql" "$image" \
  --character-set-server=utf8mb4 --collation-server=utf8mb4_unicode_ci >/dev/null

ready=0
for _ in $(seq 1 120); do
  if podman exec --env-file "$environment_file" "$container_name" healthcheck.sh --connect --innodb_initialized >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
((ready == 1)) || m5_die "MariaDB health check failed; resource retained for explicit inspection/cleanup"
published_port="$(podman port "$container_name" 3306/tcp)"
grep -Fq "127.0.0.1:$port" <<<"$published_port" || m5_die "published binding is not loopback-only"

runtime="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -D "$database_name" -N -B -e 'SELECT DATABASE(), VERSION(), @@version_comment;')"
IFS=$'\t' read -r runtime_database runtime_version runtime_comment <<<"$runtime"
[[ "$runtime_database" == "$database_name" ]] || m5_die "runtime database identity mismatch"
[[ "$runtime_version" =~ ^11\.8\.[0-9]+-MariaDB ]] || m5_die "runtime is not MariaDB 11.8.x"
grep -qi MariaDB <<<"$runtime_comment" || m5_die "runtime vendor comment is not MariaDB"

bad_root_hosts="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT GROUP_CONCAT(Host ORDER BY Host SEPARATOR ',') FROM mysql.user WHERE User='root' AND Host NOT IN ('localhost','127.0.0.1','::1');")"
[[ -z "$bad_root_hosts" || "$bad_root_hosts" == "NULL" ]] || m5_die "root has an unexpected remote host entry"

sql_file="$secrets_dir/create-app-user.sql"
python3 - "$application_file" "$sql_file" "$database_name" <<'PY'
import pathlib
import sys

application_path, sql_path, database = sys.argv[1:]
values = {}
for line in pathlib.Path(application_path).read_text(encoding="utf-8").splitlines():
    key, separator, value = line.partition("=")
    if separator:
        values[key] = value
user = values["CW_M5_APP_USER"]
password = values["CW_M5_APP_PASSWORD"].replace("\\", "\\\\").replace("'", "''")
sql = (
    f"CREATE USER IF NOT EXISTS '{user}'@'%' IDENTIFIED BY '{password}';\n"
    f"ALTER USER '{user}'@'%' IDENTIFIED BY '{password}';\n"
    f"REVOKE ALL PRIVILEGES, GRANT OPTION FROM '{user}'@'%';\n"
    f"GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES ON `{database}`.* TO '{user}'@'%';\n"
    "FLUSH PRIVILEGES;\n"
)
pathlib.Path(sql_path).write_text(sql, encoding="utf-8")
PY
chmod 600 "$sql_file"
podman exec --env-file "$environment_file" -i "$container_name" mariadb --protocol=socket -uroot <"$sql_file" >/dev/null
rm -f "$sql_file"

app_grantee="'${CW_M5_025_APP_IDENTITY}'@'%'"
app_grantee_expression="CONCAT(CHAR(39),'${CW_M5_025_APP_IDENTITY}',CHAR(39),'@',CHAR(39),'%',CHAR(39))"
global_privileges="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT GROUP_CONCAT(DISTINCT PRIVILEGE_TYPE ORDER BY PRIVILEGE_TYPE SEPARATOR ',') FROM information_schema.USER_PRIVILEGES WHERE GRANTEE=$app_grantee_expression AND PRIVILEGE_TYPE <> 'USAGE';")"
[[ -z "$global_privileges" || "$global_privileges" == "NULL" ]] || m5_die "application identity has global privileges"
schema_names="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT GROUP_CONCAT(DISTINCT TABLE_SCHEMA ORDER BY TABLE_SCHEMA SEPARATOR ',') FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE=$app_grantee_expression;")"
[[ "$schema_names" == "$database_name" ]] || m5_die "application identity is not restricted to the target schema"
schema_privileges="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT GROUP_CONCAT(DISTINCT PRIVILEGE_TYPE ORDER BY PRIVILEGE_TYPE SEPARATOR ',') FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE=$app_grantee_expression AND TABLE_SCHEMA='$database_name';")"
[[ "$schema_privileges" == "ALTER,CREATE,DELETE,INDEX,INSERT,REFERENCES,SELECT,UPDATE" ]] || m5_die "application identity has an unexpected schema privilege set"
app_grants="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SHOW GRANTS FOR $app_grantee;")"
! grep -Fq 'WITH GRANT OPTION' <<<"$app_grants" || m5_die "application identity has grant option"
app_schema_scoped=true

table_count="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$database_name';")"
[[ "$table_count" == "0" ]] || m5_die "fresh target already contains schema tables"

outcome=DISPOSABLE_TARGET_PROVISIONED_NOT_AUTHORIZED
identity="container=$container_name;database=$database_name;endpoint=$endpoint;volume=$volume_name;image=$resolved_digest"
m5_write_provision_metadata "$metadata" "$outcome" "$run_id" "$rootless" "$container_name" "$volume_name" "$database_name" "$endpoint" "$image" "$resolved_digest" "$runtime_version" "$runtime_comment" "$identity" "$environment_file" "$application_file" "$app_schema_scoped"
trap - EXIT INT TERM
echo "CW-M5-025C provisioned a fresh rootless disposable target; migrations, certification, authorization, and cleanup remain disabled."
echo "Metadata: $metadata"
echo "Credentials are retained only at the owner-only path recorded in metadata."
