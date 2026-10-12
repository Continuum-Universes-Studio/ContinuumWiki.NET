#!/usr/bin/env bash

# Shared fail-closed helpers for CW-M5-019A. This file is sourced by the
# operator-run provisioning, inspection, and cleanup commands.

readonly CW_M5_019_APP_IDENTITY="continuumwiki_m5_019_app"
readonly CW_M5_019_IMAGE_PATTERN='^docker\.io/library/mariadb:(11\.8|11\.8\.[0-9]+)$'
readonly CW_M5_019_DIGEST_PATTERN='^docker\.io/library/mariadb@sha256:[0-9a-f]{64}$'
readonly CW_M5_019_DATABASE_PATTERN='^cw_m5_019_[a-z0-9_]{1,48}$'
readonly CW_M5_019_CONTAINER_PATTERN='^continuumwiki-m5-019-[a-z0-9-]{1,48}$'
readonly CW_M5_019_VOLUME_PATTERN='^continuumwiki-m5-019-[a-z0-9-]{1,48}-data$'

m5_die() {
  echo "CW-M5-019A: $*" >&2
  exit 64
}

m5_require_tool() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "CW-M5-019A: required tool is unavailable: $1" >&2
    exit 127
  }
}

m5_validate_port() {
  [[ "$1" =~ ^[0-9]+$ && "$1" -gt 1024 && "$1" -lt 65536 ]] ||
    m5_die "port must be an unprivileged TCP port from 1025 through 65535"
}

m5_validate_image() {
  [[ "$1" =~ $CW_M5_019_IMAGE_PATTERN || "$1" =~ $CW_M5_019_DIGEST_PATTERN ]] ||
    m5_die "image must be docker.io/library/mariadb:11.8[.<patch>] or an immutable 11.8 digest reference"
}

m5_validate_names() {
  [[ "$1" =~ $CW_M5_019_DATABASE_PATTERN ]] || m5_die "database name is outside cw_m5_019_ scope"
  [[ "$2" =~ $CW_M5_019_CONTAINER_PATTERN ]] || m5_die "container name is outside CW-M5-019 scope"
  [[ "$3" =~ $CW_M5_019_VOLUME_PATTERN ]] || m5_die "volume name is outside CW-M5-019 scope"
}

m5_repo_root() {
  local helper_dir
  helper_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
  cd "$helper_dir/../.." && pwd -P
}

m5_resolved_path() {
  python3 - "$1" <<'PY'
import os
import sys

print(os.path.realpath(os.path.abspath(sys.argv[1])))
PY
}

m5_assert_secret_dir_outside_repo() {
  local candidate repo
  candidate="$(m5_resolved_path "$1")"
  repo="$(m5_repo_root)"
  [[ "$candidate" != "$repo" && "$candidate" != "$repo"/* ]] ||
    m5_die "secret directory must be outside the repository"
}

m5_port_available() {
  python3 - "$1" <<'PY'
import socket
import sys

port = int(sys.argv[1])
with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind(("127.0.0.1", port))
    except OSError:
        raise SystemExit(1)
PY
}

m5_require_clean_secret_file() {
  local path mode
  path="$1"
  [[ -f "$path" ]] || m5_die "secret file is missing: $path"
  mode="$(stat -c '%a' "$path" 2>/dev/null || stat -f '%Lp' "$path")"
  [[ "$mode" == "600" || "$mode" == "400" ]] ||
    m5_die "secret file must be owner-only (0600 or 0400): $path"
}

m5_read_metadata() {
  python3 - "$1" <<'PY'
import json
import pathlib
import re
import sys

path = pathlib.Path(sys.argv[1])
try:
    document = json.loads(path.read_text(encoding="utf-8"))
    target = document["Target"]
    credentials = document["Credentials"]
    provisioning = document["Provisioning"]
    values = (
        target["ContainerName"],
        target["VolumeName"],
        target["DatabaseName"],
        target["Port"],
        target["InstanceIdentity"],
        target["ImageResolvedDigest"],
        provisioning["RunId"],
        credentials["EnvironmentFile"],
    )
except (OSError, ValueError, KeyError, TypeError):
    raise SystemExit("metadata is missing, malformed, or incomplete")

if not re.fullmatch(r"continuumwiki-m5-019-[a-z0-9-]{1,48}", values[0]):
    raise SystemExit("metadata contains an invalid container name")
if not re.fullmatch(r"continuumwiki-m5-019-[a-z0-9-]{1,48}-data", values[1]):
    raise SystemExit("metadata contains an invalid volume name")
if not re.fullmatch(r"cw_m5_019_[a-z0-9_]{1,48}", values[2]):
    raise SystemExit("metadata contains an invalid database name")
if not isinstance(values[3], int) or not 1024 < values[3] < 65536:
    raise SystemExit("metadata contains an invalid port")
if not isinstance(values[4], str) or f"database={values[2]}" not in values[4]:
    raise SystemExit("metadata contains an invalid instance identity")
if not isinstance(values[5], str) or not re.fullmatch(r"docker\.io/library/mariadb@sha256:[0-9a-f]{64}", values[5]):
    raise SystemExit("metadata does not contain an immutable resolved image digest")
if not isinstance(values[6], str) or not re.fullmatch(r"[A-Za-z0-9T:-]{8,64}", values[6]):
    raise SystemExit("metadata does not contain a valid provisioning run id")
if not isinstance(values[7], str) or not values[7]:
    raise SystemExit("metadata does not contain a credential file path")

print("\t".join(str(value) for value in values))
PY
}

m5_write_json() {
  local destination="$1"
  local payload="$2"
  mkdir -p "$(dirname "$destination")"
  python3 - "$destination" "$payload" <<'PY'
import json
import os
import pathlib
import sys

destination = pathlib.Path(sys.argv[1])
payload = json.loads(sys.argv[2])
destination.parent.mkdir(parents=True, exist_ok=True)
temporary = destination.with_name(f".{destination.name}.tmp.{os.getpid()}")
temporary.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
os.chmod(temporary, 0o600)
os.replace(temporary, destination)
PY
}

m5_write_provision_metadata() {
  local destination="$1" outcome="$2" run_id="$3" container="$4" volume="$5"
  local database="$6" endpoint="$7" requested_image="$8" resolved_digest="$9"
  local version="${10}" version_comment="${11}" identity="${12}"
  local environment_file="${13}"
  local payload
  payload="$(python3 - "$outcome" "$run_id" "$container" "$volume" "$database" "$endpoint" "$requested_image" "$resolved_digest" "$version" "$version_comment" "$identity" "$environment_file" <<'PY'
import json
import sys
from datetime import datetime, timezone

(outcome, run_id, container, volume, database, endpoint, requested_image,
 resolved_digest, version, version_comment, identity, environment_file) = sys.argv[1:]
print(json.dumps({
    "ReportVersion": "cw-m5-019a-disposable-mariadb-provisioning-v1",
    "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "Milestone": "CW-M5-019A",
    "Classification": outcome,
    "Provisioning": {
        "Executed": True,
        "Outcome": outcome,
        "RunId": run_id,
        "AutomaticCleanup": False,
        "MigrationsExecuted": False,
        "CertificationExecuted": False,
        "AuthorizationCreated": False,
    },
    "Target": {
        "ContainerName": container,
        "VolumeName": volume,
        "DatabaseName": database,
        "Port": int(endpoint.rsplit(":", 1)[1]),
        "Endpoint": endpoint,
        "ImageRequested": requested_image,
        "ImageResolvedDigest": resolved_digest,
        "EngineVersion": version,
        "VersionComment": version_comment,
        "DedicatedApplicationIdentity": "continuumwiki_m5_019_app",
        "InstanceIdentity": identity,
        "LoopbackBinding": True,
        "EmptyBeforeCertification": outcome == "DISPOSABLE_TARGET_PROVISIONED_NOT_AUTHORIZED",
    },
    "Credentials": {
        "EnvironmentFile": environment_file,
        "ApplicationEnvironmentFile": str(__import__("pathlib").Path(environment_file).with_name("application.env")),
        "DirectoryPermissions": "0700",
        "FilePermissions": "0600",
        "ValuesIncluded": False,
    },
    "Authorization": {
        "Authorized": False,
        "ManifestGenerated": False,
        "CertificationAllowed": False,
    },
    "DatabaseContacts": {
        "Purpose": "Fresh-target health, identity, grant, and empty-schema validation only",
        "ApplicationSchemaMigrations": 0,
        "CertificationConnections": 0,
        "ProductionConnections": 0,
        "ProductionMutations": 0,
    },
    "Cleanup": {
        "Required": True,
        "Procedure": "Use the exact metadata and separately authorized CW-M5-019A cleanup command",
        "Attestation": None,
    },
}, indent=2))
PY
)"
  m5_write_json "$destination" "$payload"
}
