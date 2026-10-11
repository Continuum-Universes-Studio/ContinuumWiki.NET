#!/usr/bin/env bash
set -Eeuo pipefail

# CW-M5-003B provisions one exact disposable schema. MySQL database-level
# grants cannot safely constrain arbitrary CREATE/DROP DATABASE operations by
# prefix, so root is used only through the container's private Unix socket.

usage() {
  cat >&2 <<'EOF'
Usage:
  scripts/run-m5-003b-certification.sh
  scripts/run-m5-003b-certification.sh --execute --approval PATH [options]

The default is a non-mutating dry run. --execute requires a CW-M4-005 Track B
approval file and an immutable docker.io/library/mysql@sha256:... image.
EOF
}

execute=0
approval_path=
container_name="${CW_M5_003B_CONTAINER:-continuumwiki-m5-003b-mysql}"
database_name="${CW_M5_003B_DATABASE:-cw_m5_003_certification}"
port="${CW_M5_003B_PORT:-13310}"
app_user="${CW_M5_003B_APP_USER:-continuumwiki_m5_003_app}"
image="${CW_M5_003B_MYSQL_IMAGE:-}"

while (($# > 0)); do
  case "$1" in
    --execute) execute=1 ;;
    --approval)
      (($# >= 2)) || { echo "--approval requires a path" >&2; exit 64; }
      approval_path=$2
      shift
      ;;
    --container)
      (($# >= 2)) || { echo "--container requires a name" >&2; exit 64; }
      container_name=$2
      shift
      ;;
    --database)
      (($# >= 2)) || { echo "--database requires a name" >&2; exit 64; }
      database_name=$2
      shift
      ;;
    --port)
      (($# >= 2)) || { echo "--port requires a number" >&2; exit 64; }
      port=$2
      shift
      ;;
    --image)
      (($# >= 2)) || { echo "--image requires an immutable image reference" >&2; exit 64; }
      image=$2
      shift
      ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 64 ;;
  esac
  shift
done

if [[ "$execute" != 1 ]]; then
  cat <<EOF
CW-M5-003B dry run: no container, database, credentials, or test process will be created.
Planned container: $container_name
Planned database: $database_name
Planned endpoint: 127.0.0.1:$port
Planned image: ${image:-<operator-supplied docker.io/library/mysql@sha256:...>}
Use --execute only after the exact disposable target and mutation scope are approved.
EOF
  exit 0
fi

command -v podman >/dev/null || { echo "podman is required" >&2; exit 127; }
command -v python3 >/dev/null || { echo "python3 is required" >&2; exit 127; }
command -v openssl >/dev/null || { echo "openssl is required" >&2; exit 127; }
[[ -n "$approval_path" ]] || { echo "--approval is required with --execute" >&2; exit 64; }
[[ -f "$approval_path" ]] || { echo "approval file does not exist" >&2; exit 66; }
[[ "$image" =~ ^docker\.io/library/mysql@sha256:[0-9a-f]{64}$ ]] || {
  echo "--image must be an immutable docker.io/library/mysql@sha256:<64 hex> reference" >&2
  exit 64
}
[[ "$database_name" =~ ^cw_m5_003_[a-z0-9_]+$ ]] || { echo "database is outside cw_m5_003_ scope" >&2; exit 64; }
[[ "$container_name" =~ ^continuumwiki-m5-003b-[a-z0-9-]+$ ]] || { echo "container name is outside CW-M5-003B scope" >&2; exit 64; }
[[ "$app_user" =~ ^continuumwiki_m5_003_app$ ]] || { echo "application identity is not the fixed least-privilege identity" >&2; exit 64; }
[[ "$port" =~ ^[0-9]+$ && "$port" -gt 1024 && "$port" -lt 65536 ]] || { echo "port must be an unprivileged high port" >&2; exit 64; }

target_identity="container=$container_name;database=$database_name;endpoint=127.0.0.1:$port;image=$image"
python3 - "$approval_path" "$database_name" "$target_identity" <<'PY'
import json
import pathlib
import sys

path, database, target_identity = sys.argv[1:]
try:
    document = json.loads(pathlib.Path(path).read_text(encoding="utf-8"))
    scope = document["RequestedScope"]
    evidence = document["Evidence"]
    approver = document["Approver"]
    boundaries = document["AuthorizationBoundaries"]
except (OSError, ValueError, KeyError, TypeError):
    raise SystemExit("approval file is missing, malformed, or incomplete")

if document.get("SchemaVersion") != "cw-m4-005-operator-approval-v1":
    raise SystemExit("approval file has an unsupported schema")
if document.get("ApprovalState") != "APPROVED" or document.get("Track") != "B":
    raise SystemExit("approval must be an APPROVED CW-M4-005 Track B record")
if scope.get("ReadOnly") is not False:
    raise SystemExit("approval must authorize synthetic database writes and cleanup")
mutation_scope = scope.get("MutationScope")
required_mutations = ("CreateDisposableDatabase", "CreateSyntheticIdentityRecords", "CreateSyntheticWikiContent", "MutateSyntheticPermissions", "DeleteDisposableDatabase", "CreateAndRemoveContainer", "RetainSanitizedEvidence")
if not isinstance(mutation_scope, dict) or any(mutation_scope.get(key) is not True for key in required_mutations):
    raise SystemExit("approval must authorize every required disposable mutation and evidence-retention operation")
if scope.get("DisposableTarget") != target_identity or evidence.get("TargetIdentity") != target_identity:
    raise SystemExit("approval does not authorize this exact disposable target identity")
if not evidence.get("RetentionAndCleanup"):
    raise SystemExit("approval must record retention and cleanup")
if not all(approver.get(key) for key in ("Name", "Role", "RecordedAtUtc")):
    raise SystemExit("approval must identify the approver and recorded time")
for key in ("NewMediaWikiApiAccess", "NewMediaWikiDatabaseAccess", "NewBackupAccess", "PrivateCargoRows", "ProductionMariaDb", "ProductionLua", "SystemdOrBubblewrapChanges", "WorkerDeployment", "CredentialMaterialIncluded"):
    if boundaries.get(key) is not False:
        raise SystemExit("approval contains a prohibited production or credential boundary")
if not database.startswith("cw_m5_003_"):
    raise SystemExit("database is outside CW-M5-003 scope")
PY

if podman container exists "$container_name"; then
  echo "refusing to reuse pre-existing container: $container_name" >&2
  exit 73
fi

run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
secret_env_file="$(mktemp "${TMPDIR:-/tmp}/cw-m5-003b-env.XXXXXX")"
log_file="${TMPDIR:-/tmp}/${container_name}.${run_id}.log"
chmod 600 "$secret_env_file"
root_password="$(openssl rand -hex 24)"
app_password="$(openssl rand -hex 24)"
printf 'MYSQL_ROOT_PASSWORD=%s\nMYSQL_PWD=%s\nMYSQL_DATABASE=%s\n' "$root_password" "$root_password" "$database_name" >"$secret_env_file"

cleanup_failed=0
cleanup() {
  local status=$?
  trap - EXIT INT TERM

  if podman container exists "$container_name"; then
    local owner
    owner="$(podman container inspect --format '{{ index .Config.Labels "io.continuumwiki.m5-003b-run-id" }}' "$container_name" 2>/dev/null || true)"
    if [[ "$owner" == "$run_id" ]]; then
      local raw_log="${log_file}.raw"
      podman logs "$container_name" >"$raw_log" 2>&1 || true
      REDACT_ROOT="$root_password" REDACT_APP="$app_password" python3 - "$raw_log" "$log_file" <<'PY' || true
import os
import pathlib
import sys

source, destination = sys.argv[1:]
text = pathlib.Path(source).read_text(encoding="utf-8", errors="replace")
for name in ("REDACT_ROOT", "REDACT_APP"):
    secret = os.environ.get(name)
    if secret:
        text = text.replace(secret, "[REDACTED]")
pathlib.Path(destination).write_text(text, encoding="utf-8")
PY
      rm -f "$raw_log"
      if ! podman rm --force "$container_name" >/dev/null 2>&1; then
        cleanup_failed=1
        echo "cleanup failed; retained diagnostics: $log_file" >&2
      fi
    else
      cleanup_failed=1
      echo "cleanup refused: container identity did not match this run" >&2
    fi
  fi

  rm -f "$secret_env_file"
  unset root_password app_password CONTINUUMWIKI_MYSQL_TEST_CONNECTION
  unset CONTINUUMWIKI_RUN_MYSQL_TESTS CONTINUUMWIKI_M5_003_HOST_AUTHORIZED
  unset CONTINUUMWIKI_M5_003_TARGET_DATABASE CONTINUUMWIKI_M5_003_TARGET_IDENTITY
  unset CONTINUUMWIKI_M5_003_APPROVAL_PATH
  if ((cleanup_failed)) && ((status == 0)); then status=75; fi
  exit "$status"
}
trap cleanup EXIT INT TERM

podman pull "$image" >/dev/null
podman run --detach --name "$container_name" \
  --label "io.continuumwiki.m5-003b-run-id=$run_id" \
  --label "io.continuumwiki.m5-003b-database=$database_name" \
  --publish "127.0.0.1:${port}:3306" \
  --env-file "$secret_env_file" \
  "$image" >/dev/null

until podman exec --env-file "$secret_env_file" "$container_name" \
  mysqladmin --protocol=socket -uroot ping --silent >/dev/null 2>&1; do
  sleep 1
done

published_port="$(podman port "$container_name" 3306/tcp)"
grep -Fq "127.0.0.1:${port}" <<<"$published_port" || {
  echo "refusing non-loopback or unexpected published binding" >&2
  exit 74
}

runtime="$(podman exec --env-file "$secret_env_file" "$container_name" \
  mysql --protocol=socket -uroot -D "$database_name" -N -B \
  -e 'SELECT DATABASE(), VERSION(), @@version_comment;')"
read -r runtime_database runtime_version runtime_comment <<<"$runtime"
[[ "$runtime_database" == "$database_name" ]] || { echo "container database identity mismatch" >&2; exit 74; }
[[ "$runtime_version" == 8.4.* && "$runtime_version" != *MariaDB* && "$runtime_comment" == *MySQL* ]] || {
  echo "runtime is not the approved Oracle MySQL 8.4 target" >&2
  exit 74
}

root_hosts="$(podman exec --env-file "$secret_env_file" "$container_name" \
  mysql --protocol=socket -uroot -N -B \
  -e "SELECT GROUP_CONCAT(Host ORDER BY Host SEPARATOR ',') FROM mysql.user WHERE User='root';")"
[[ "$root_hosts" == "localhost" ]] || { echo "root has a remotely reachable or unexpected host entry" >&2; exit 74; }

app_password_sql="${app_password//\\/\\\\}"
app_password_sql="${app_password_sql//\'/\'\'}"
podman exec --env-file "$secret_env_file" "$container_name" \
  mysql --protocol=socket -uroot \
  -e "CREATE USER IF NOT EXISTS '${app_user}'@'%' IDENTIFIED BY '${app_password_sql}'; ALTER USER '${app_user}'@'%' IDENTIFIED BY '${app_password_sql}'; REVOKE ALL PRIVILEGES, GRANT OPTION FROM '${app_user}'@'%'; GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES ON \`${database_name}\`.* TO '${app_user}'@'%'; FLUSH PRIVILEGES;"

app_grants="$(podman exec --env-file "$secret_env_file" "$container_name" \
  mysql --protocol=socket -uroot -N -B \
  -e "SHOW GRANTS FOR '${app_user}'@'%';")"
grep -Fq "\`${database_name}\`.*" <<<"$app_grants" || { echo "application grant is not exact-schema scoped" >&2; exit 74; }
! grep -Fq 'WITH GRANT OPTION' <<<"$app_grants" || { echo "application grant is grantable" >&2; exit 74; }
global_privileges="$(podman exec --env-file "$secret_env_file" "$container_name" \
  mysql --protocol=socket -uroot -N -B \
  -e "SELECT GROUP_CONCAT(PRIVILEGE_TYPE ORDER BY PRIVILEGE_TYPE SEPARATOR ',') FROM information_schema.USER_PRIVILEGES WHERE GRANTEE LIKE '%${app_user}%' AND PRIVILEGE_TYPE <> 'USAGE';")"
[[ -z "$global_privileges" || "$global_privileges" == "NULL" ]] || { echo "application identity has global privileges" >&2; exit 74; }

export CONTINUUMWIKI_RUN_MYSQL_TESTS=1
export CONTINUUMWIKI_M5_003_HOST_AUTHORIZED=1
export CONTINUUMWIKI_M5_003_TARGET_DATABASE="$database_name"
export CONTINUUMWIKI_M5_003_TARGET_IDENTITY="$target_identity"
export CONTINUUMWIKI_M5_003_APPROVAL_PATH="$approval_path"
export CONTINUUMWIKI_MYSQL_TEST_CONNECTION="Server=127.0.0.1;Port=${port};Database=${database_name};User ID=${app_user};Password=${app_password};Allow User Variables=True;"

dotnet test TightWiki.Tests/TightWiki.Tests.csproj \
  -c Debug --no-restore -m:1 \
  --filter 'FullyQualifiedName~MediaWikiTemplateStylesHostAuthorizationTests|FullyQualifiedName~MediaWikiTemplateStylesMySqlHostTests'
