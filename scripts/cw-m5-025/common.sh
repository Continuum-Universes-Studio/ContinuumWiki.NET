#!/usr/bin/env bash

# Shared fail-closed helpers for CW-M5-025C disposable target provisioning.
# This workflow is separate from CW-M5-019A and never authorizes migrations,
# certification, or cleanup.

readonly CW_M5_025_APP_IDENTITY="continuumwiki_m5_025_app"
readonly CW_M5_025_IMAGE_PATTERN='^docker\.io/library/mariadb:(11\.8|11\.8\.[0-9]+)$'
readonly CW_M5_025_DIGEST_PATTERN='^docker\.io/library/mariadb@sha256:[0-9a-f]{64}$'
readonly CW_M5_025_DATABASE_PATTERN='^cw_m5_025_[a-z0-9_]{1,48}$'
readonly CW_M5_025_CONTAINER_PATTERN='^continuumwiki-m5-025-[a-z0-9-]{1,48}$'
readonly CW_M5_025_VOLUME_PATTERN='^continuumwiki-m5-025-[a-z0-9-]{1,48}-data$'

m5_die() {
  echo "CW-M5-025C: $*" >&2
  exit 64
}

m5_require_tool() {
  command -v "$1" >/dev/null 2>&1 || {
    echo "CW-M5-025C: required tool is unavailable: $1" >&2
    exit 127
  }
}

m5_validate_port() {
  [[ "$1" =~ ^[0-9]+$ && "$1" -gt 1024 && "$1" -lt 65536 ]] ||
    m5_die "port must be an unprivileged TCP port from 1025 through 65535"
}

m5_validate_image() {
  [[ "$1" =~ $CW_M5_025_IMAGE_PATTERN || "$1" =~ $CW_M5_025_DIGEST_PATTERN ]] ||
    m5_die "image must be docker.io/library/mariadb:11.8[.<patch>] or an immutable 11.8 digest reference"
}

m5_validate_names() {
  [[ "$1" =~ $CW_M5_025_DATABASE_PATTERN ]] || m5_die "database name is outside cw_m5_025_ scope"
  [[ "$2" =~ $CW_M5_025_CONTAINER_PATTERN ]] || m5_die "container name is outside CW-M5-025 scope"
  [[ "$3" =~ $CW_M5_025_VOLUME_PATTERN ]] || m5_die "volume name is outside CW-M5-025 scope"
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

m5_find_available_port() {
  python3 - "$1" "$2" <<'PY'
import socket
import sys

lower, upper = map(int, sys.argv[1:])
for port in range(lower, upper + 1):
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            sock.bind(("127.0.0.1", port))
        except OSError:
            continue
        print(port)
        raise SystemExit(0)
raise SystemExit("no available loopback port in requested range")
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

m5_require_rootless_podman() {
  local rootless
  rootless="$(podman info --format '{{.Host.Security.Rootless}}' 2>/dev/null || true)"
  [[ "$rootless" == "true" ]] || m5_die "Podman must be running rootless for CW-M5-025C"
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
  local destination="$1" outcome="$2" run_id="$3" rootless="$4"
  local container="$5" volume="$6" database="$7" endpoint="$8"
  local requested_image="$9" resolved_digest="${10}" version="${11}"
  local version_comment="${12}" identity="${13}" environment_file="${14}"
  local application_file="${15}" app_schema_scoped="${16}"
  local payload
  payload="$(python3 - "$outcome" "$run_id" "$rootless" "$container" "$volume" "$database" "$endpoint" "$requested_image" "$resolved_digest" "$version" "$version_comment" "$identity" "$environment_file" "$application_file" "$app_schema_scoped" <<'PY'
import json
import sys
from datetime import datetime, timezone

(outcome, run_id, rootless, container, volume, database, endpoint,
 requested_image, resolved_digest, version, version_comment, identity,
 environment_file, application_file, app_schema_scoped) = sys.argv[1:]
print(json.dumps({
    "ReportVersion": "cw-m5-025c-disposable-mariadb-provisioning-v1",
    "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
    "Milestone": "CW-M5-025C",
    "Classification": outcome,
    "Provisioning": {
        "Executed": outcome != "PLAN_ONLY",
        "Outcome": outcome,
        "RunId": run_id,
        "RootlessPodman": rootless == "true",
        "AutomaticCleanup": False,
        "MigrationsExecuted": False,
        "CertificationExecuted": False,
        "AuthorizationCreated": False,
    },
    "Target": {
        "Disposable": True,
        "ContainerName": container,
        "VolumeName": volume,
        "DatabaseName": database,
        "Endpoint": endpoint,
        "ImageRequested": requested_image,
        "ImageResolvedDigest": resolved_digest,
        "EngineVersion": "MariaDB 11.8.x",
        "EngineVersionObserved": version,
        "VersionComment": version_comment,
        "DatabaseIdentity": "continuumwiki_m5_025_app",
        "InstanceIdentity": identity,
        "LoopbackBinding": True,
        "EmptyBeforeCertification": outcome == "DISPOSABLE_TARGET_PROVISIONED_NOT_AUTHORIZED",
    },
    "Credentials": {
        "EnvironmentFile": environment_file,
        "ApplicationEnvironmentFile": application_file,
        "DirectoryPermissions": "0700",
        "FilePermissions": "0600",
        "ValuesIncluded": False,
    },
    "IsolationEvidence": {
        "RootlessPodman": rootless == "true",
        "LoopbackOnlyPublishedBinding": True,
        "ApplicationIdentitySchemaScoped": app_schema_scoped == "true",
        "ProductionContacted": False,
        "SharedTargetReused": False,
    },
    "Authorization": {
        "Authorized": False,
        "ManifestGenerated": False,
        "CertificationAllowed": False,
    },
    "DatabaseContacts": {
        "Purpose": "Provisioning-only health, identity, grant, and empty-schema validation",
        "ProvisioningHealthChecksPerformed": outcome == "DISPOSABLE_TARGET_PROVISIONED_NOT_AUTHORIZED",
        "MigrationConnections": 0,
        "CertificationConnections": 0,
        "ProductionConnections": 0,
        "ProductionMutations": 0,
    },
    "Cleanup": {
        "Required": True,
        "Performed": False,
        "AutomaticCleanup": False,
        "SeparateAuthorizationRequired": True,
    },
}, indent=2))
PY
  )"
  m5_write_json "$destination" "$payload"
}
