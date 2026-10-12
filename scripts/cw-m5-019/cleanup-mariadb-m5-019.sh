#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

script_dir="$(cd "$(dirname "$BASH_SOURCE")" && pwd -P)"
source "$script_dir/common.sh"

execute=0
metadata=""
manifest=""
attestation=""
while (($# > 0)); do
  case "$1" in
    --execute) execute=1 ;;
    --metadata) (($# >= 2)) || m5_die "--metadata requires a path"; metadata="$2"; shift ;;
    --manifest) (($# >= 2)) || m5_die "--manifest requires a path"; manifest="$2"; shift ;;
    --attestation) (($# >= 2)) || m5_die "--attestation requires a path"; attestation="$2"; shift ;;
    -h|--help) echo "Usage: $0 [--execute] --metadata PATH --manifest PATH --attestation PATH"; exit 0 ;;
    *) m5_die "unknown option: $1" ;;
  esac
  shift
done
[[ -n "$metadata" && -n "$manifest" && -n "$attestation" ]] || m5_die "--metadata, --manifest, and --attestation are required"
[[ -r "$metadata" ]] || m5_die "metadata is unreadable"
[[ -r "$manifest" ]] || m5_die "authorization manifest is unreadable"
m5_require_tool python3

IFS=$'\t' read -r container_name volume_name database_name port instance_identity image_digest run_id environment_file < <(m5_read_metadata "$metadata")
m5_validate_names "$database_name" "$container_name" "$volume_name"
m5_validate_port "$port"
repo_root="$(m5_repo_root)"
manifest_identity="$(python3 - "$repo_root/scripts/preflight-m5-019.py" "$manifest" "$instance_identity" "$container_name" "$volume_name" "$database_name" "127.0.0.1:$port" <<'PY'
import importlib.util
import json
import pathlib
import sys

module_path, manifest_path, expected_identity, expected_container, expected_volume, expected_database, expected_endpoint = sys.argv[1:]
spec = importlib.util.spec_from_file_location("cw_m5_019_preflight", module_path)
module = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(module)
document = json.loads(pathlib.Path(manifest_path).read_text(encoding="utf-8"))
errors = module.validate_manifest(document)
if errors:
    raise SystemExit("authorization manifest is invalid")
target = document["Target"]
if (target.get("ContainerName") != expected_container or
        target.get("VolumeName") != expected_volume or
        target.get("InstanceIdentity") != expected_identity or
        target.get("DatabaseName") != expected_database or
        target.get("Endpoint") != expected_endpoint):
    raise SystemExit("authorization target does not match provisioned metadata")
if document["PermittedOperations"].get("ContainerVolumeDeletion") is not True:
    raise SystemExit("authorization does not explicitly permit container/volume deletion")
print(target["InstanceIdentity"])
PY
)"

if (( ! execute )); then
  cat <<EOF
CW-M5-019A cleanup plan-only: no container, volume, database, or credentials will be removed.
Exact container: $container_name
Exact volume: $volume_name
Exact database: $database_name
Authorization target: $manifest_identity
Run with --execute only after reviewing the exact target and cleanup scope.
EOF
  exit 0
fi

m5_require_tool podman
container_removed=false
volume_removed=false
if podman container exists "$container_name"; then
  actual_database="$(podman container inspect --format '{{ index .Config.Labels "io.continuumwiki.m5-019-database" }}' "$container_name")"
  actual_volume="$(podman container inspect --format '{{ index .Config.Labels "io.continuumwiki.m5-019-volume" }}' "$container_name")"
  actual_endpoint="$(podman container inspect --format '{{ index .Config.Labels "io.continuumwiki.m5-019-endpoint" }}' "$container_name")"
  actual_run_id="$(podman container inspect --format '{{ index .Config.Labels "io.continuumwiki.m5-019-run-id" }}' "$container_name")"
  [[ "$actual_database" == "$database_name" && "$actual_volume" == "$volume_name" && "$actual_endpoint" == "127.0.0.1:$port" && "$actual_run_id" == "$run_id" ]] || m5_die "container identity mismatch; refusing cleanup"
  podman rm --force "$container_name" >/dev/null
  container_removed=true
fi
if podman volume exists "$volume_name"; then
  actual_volume_database="$(podman volume inspect --format '{{ index .Labels "io.continuumwiki.m5-019-database" }}' "$volume_name")"
  actual_volume_run_id="$(podman volume inspect --format '{{ index .Labels "io.continuumwiki.m5-019-run-id" }}' "$volume_name")"
  [[ "$actual_volume_database" == "$database_name" && "$actual_volume_run_id" == "$run_id" ]] || m5_die "volume identity mismatch; refusing cleanup"
  podman volume rm "$volume_name" >/dev/null
  volume_removed=true
fi

payload="$(python3 - "$metadata" "$manifest" "$container_name" "$volume_name" "$database_name" "$container_removed" "$volume_removed" <<'PY'
import hashlib
import json
import pathlib
import sys
from datetime import datetime, timezone

metadata, manifest, container, volume, database, container_removed, volume_removed = sys.argv[1:]
document = json.loads(pathlib.Path(manifest).read_text(encoding="utf-8"))
identity = document["Target"]["InstanceIdentity"]
print(json.dumps({
    "ReportVersion": "cw-m5-019a-disposable-mariadb-cleanup-v1",
    "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "Classification": "DISPOSABLE_TARGET_CLEANED",
    "Authorization": {"ManifestValidated": True, "ApprovalIdSha256": hashlib.sha256(document["ApprovalId"].encode()).hexdigest(), "TargetIdentitySha256": hashlib.sha256(identity.encode()).hexdigest()},
    "Target": {"ContainerName": container, "VolumeName": volume, "DatabaseName": database, "InstanceIdentity": identity},
    "Cleanup": {"ContainerRemoved": container_removed == "true", "VolumeRemoved": volume_removed == "true", "DatabaseConnectionsAfterCleanup": 0, "DatabaseMutationsAfterCleanup": 0},
    "MetadataPath": metadata,
    "SecretsIncluded": False,
}, indent=2))
PY
)"
m5_write_json "$attestation" "$payload"
echo "CW-M5-019A cleanup completed for the exact authorized target."
echo "Attestation: $attestation"
