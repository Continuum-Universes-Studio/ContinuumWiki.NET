#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

script_dir="$(cd "$(dirname "$BASH_SOURCE")" && pwd -P)"
source "$script_dir/common.sh"

metadata=""
report=""
while (($# > 0)); do
  case "$1" in
    --metadata) (($# >= 2)) || m5_die "--metadata requires a path"; metadata="$2"; shift ;;
    --report) (($# >= 2)) || m5_die "--report requires a path"; report="$2"; shift ;;
    -h|--help) echo "Usage: $0 --metadata PATH --report PATH"; exit 0 ;;
    *) m5_die "unknown option: $1" ;;
  esac
  shift
done
[[ -n "$metadata" && -n "$report" ]] || m5_die "--metadata and --report are required"
m5_require_tool podman
m5_require_tool python3
[[ -r "$metadata" ]] || m5_die "metadata is unreadable"

IFS=$'\t' read -r container_name volume_name database_name port instance_identity image_digest run_id environment_file < <(m5_read_metadata "$metadata")
m5_validate_names "$database_name" "$container_name" "$volume_name"
m5_validate_port "$port"
m5_require_clean_secret_file "$environment_file"

error_count=0
errors=""
add_error() {
  errors="$errors$1"$'\n'
  ((error_count+=1))
}
label() { podman container inspect --format "{{ index .Config.Labels \"$1\" }}" "$container_name" 2>/dev/null; }
volume_label() { podman volume inspect --format "{{ index .Labels \"$1\" }}" "$volume_name" 2>/dev/null; }

if ! podman container exists "$container_name"; then
  add_error container-missing
elif ! podman volume exists "$volume_name"; then
  add_error volume-missing
else
  [[ "$(label io.continuumwiki.m5-019-database)" == "$database_name" ]] || add_error container-database-label-mismatch
  [[ "$(label io.continuumwiki.m5-019-volume)" == "$volume_name" ]] || add_error container-volume-label-mismatch
  [[ "$(label io.continuumwiki.m5-019-endpoint)" == "127.0.0.1:$port" ]] || add_error container-endpoint-label-mismatch
  [[ "$(label io.continuumwiki.m5-019-image-digest)" == "$image_digest" ]] || add_error container-image-digest-mismatch
  [[ "$(volume_label io.continuumwiki.m5-019-database)" == "$database_name" ]] || add_error volume-database-label-mismatch
  [[ "$(label io.continuumwiki.m5-019-run-id)" == "$run_id" ]] || add_error container-run-id-mismatch
  [[ "$(volume_label io.continuumwiki.m5-019-run-id)" == "$run_id" ]] || add_error volume-run-id-mismatch
  published_port="$(podman port "$container_name" 3306/tcp 2>/dev/null || true)"
  grep -Fq "127.0.0.1:$port" <<<"$published_port" || add_error published-binding-not-loopback
  if ((error_count == 0)); then
    runtime="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -D "$database_name" -N -B -e 'SELECT DATABASE(), VERSION(), @@version_comment;')"
    IFS=$'\t' read -r runtime_database runtime_version runtime_comment <<<"$runtime"
    [[ "$runtime_database" == "$database_name" ]] || add_error database-identity-mismatch
    [[ "$runtime_version" =~ ^11\.8\.[0-9]+-MariaDB ]] || add_error engine-version-not-mariadb-11-8
    grep -qi MariaDB <<<"$runtime_comment" || add_error engine-comment-not-mariadb
    app_count="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT COUNT(*) FROM mysql.user WHERE User='$CW_M5_019_APP_IDENTITY';")"
    [[ "$app_count" == "1" ]] || add_error dedicated-application-identity-missing
    table_count="$(podman exec --env-file "$environment_file" "$container_name" mariadb --protocol=socket -uroot -N -B -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$database_name';")"
    [[ "$table_count" == "0" ]] || add_error target-is-not-schema-empty
  fi
fi

classification=PROVISIONED_TARGET_UNAVAILABLE
if ((error_count == 0)); then classification=PROVISIONED_TARGET_READY_NOT_AUTHORIZED; fi
errors_json="$(printf '%s' "$errors" | python3 -c 'import json,sys; print(json.dumps([x for x in sys.stdin.read().splitlines() if x]))')"
payload="$(python3 - "$metadata" "$container_name" "$volume_name" "$database_name" "$port" "$instance_identity" "$image_digest" "$classification" "$errors_json" <<'PY'
import json
import sys
from datetime import datetime, timezone

metadata, container, volume, database, port, identity, digest, classification, errors = sys.argv[1:]
print(json.dumps({
    "ReportVersion": "cw-m5-019a-disposable-mariadb-inspection-v1",
    "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "Classification": classification,
    "ReadOnly": True,
    "Target": {"ContainerName": container, "VolumeName": volume, "DatabaseName": database, "Endpoint": "127.0.0.1:" + port, "ImageResolvedDigest": digest, "InstanceIdentity": identity},
    "ValidationErrors": json.loads(errors),
    "DatabaseContacts": {"ReadOnlyInspectionConnections": 0 if json.loads(errors) else 1, "DatabaseMutations": 0},
    "Production": {"Connections": 0, "Mutations": 0, "Migration": "NO_GO", "CategoryProviderActivation": "NO_GO"},
    "Authorization": {"CertificationExecuted": False, "AuthorizationManifestValidated": False},
    "MetadataPath": metadata,
}, indent=2))
PY
)"
m5_write_json "$report" "$payload"
echo "CW-M5-019A inspection: classification=$classification; report=$report"
((error_count == 0))
