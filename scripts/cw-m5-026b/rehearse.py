#!/usr/bin/env python3
"""Explicitly gated CW-M5-026B disposable backup/restore rehearsal.

This runner is separate from the four plan-only stage entry points. It requires
the exact rehearsal manifest, an affirmative execution flag and typed approval
ID; it never applies migrations, seeds rows, injects faults, or cleans up.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pwd
import re
import socket
import stat
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_rehearsal_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)

TABLES = (
    "Page", "PageRevision", "PageTag", "PageReference", "PageCategoryIndexState",
    "PageCategoryMembership", "TwCategoryRecoveryOperation", "TwCategoryRecoveryAuditEvent",
    "TwCategoryAdministrativeGrant", "TwCategoryAdministrativeGrantAudit",
)
HEX64 = re.compile(r"^[0-9a-f]{64}$")
EXPECTED_IMAGE_PREFIX = "docker.io/library/mariadb@sha256:"
HOST_COMMAND_TIMEOUT_SECONDS = 300


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _identity_tokens(target: dict[str, Any]) -> dict[str, str]:
    tokens: dict[str, str] = {}
    for item in target["InstanceIdentity"].split(";"):
        if "=" in item:
            key, value = item.split("=", 1)
            if key in tokens:
                raise ValueError("target identity contains duplicate fields")
            tokens[key] = value
    return tokens


def _check_strong_target_binding(target: dict[str, Any], role: str) -> dict[str, str]:
    tokens = _identity_tokens(target)
    expected = {
        "milestone": "CW-M5-026B", "role": role,
        "container": target["ContainerName"], "volume": target["VolumeName"],
        "database": target["DatabaseName"], "endpoint": target["Endpoint"],
    }
    if any(tokens.get(key) != value for key, value in expected.items()):
        raise ValueError(f"{role} authorization target identity is not exact")
    if not HEX64.fullmatch(tokens.get("containerId", "")):
        raise ValueError(f"{role} authorization must pin a 64-character container ID")
    image = tokens.get("image", "")
    if not image.startswith(EXPECTED_IMAGE_PREFIX) or not HEX64.fullmatch(image.removeprefix(EXPECTED_IMAGE_PREFIX)):
        raise ValueError(f"{role} authorization must pin the immutable MariaDB 11.8 image digest")
    if not tokens.get("runId") or len(tokens["runId"]) > 128:
        raise ValueError(f"{role} authorization must pin the disposable provisioning run ID")
    return tokens


def _private_file(path_value: str, uid: int) -> Path:
    path = Path(path_value).expanduser()
    if not path.is_absolute() or path.absolute() != path.resolve(strict=True):
        raise ValueError("credential reference must be an existing canonical absolute file")
    if path == ROOT or ROOT in path.parents:
        raise ValueError("credential file must remain outside the repository")
    try:
        info = path.lstat()
    except OSError:
        raise ValueError("credential metadata unavailable") from None
    if not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != uid or stat.S_IMODE(info.st_mode) != 0o600:
        raise ValueError("credential file must be an owner-owned regular 0600 file")
    return path


def _private_directory(path: Path, uid: int) -> Path:
    if not path.is_absolute() or path != path.resolve(strict=True):
        raise ValueError("artifact/report directory must be canonical and absolute")
    if path == ROOT or ROOT in path.parents:
        raise ValueError("artifact/report directory must remain outside the repository")
    if any(part.casefold() in {"password", "secret", "token", "credential", "credentialmaterial"} for part in path.parts):
        raise ValueError("artifact/report path contains a secret-bearing path component")
    info = path.lstat()
    if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != uid or stat.S_IMODE(info.st_mode) != 0o700:
        raise ValueError("artifact/report directory must be owner-owned mode 0700")
    return path


def _open_private_exclusive(path: Path, mode: str = "wb") -> Any:
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    return os.fdopen(descriptor, mode)


def validate_execution_manifest(document: Any, *, now: datetime | None = None) -> dict[str, Any]:
    errors = PREFLIGHT.validate_manifest(document, now)
    if errors:
        raise ValueError("authorization manifest rejected: " + ", ".join(errors))
    operations = document["PermittedOperations"]
    if operations.get("SeedDisposableSource") is True:
        raise ValueError("rehearsal runner refuses source seeding; no migration or fixture mutation is implemented")
    if any(operations.get(name) is True for name in (
        "FaultInjection", "CleanupSource", "CleanupDestination", "SchemaDeletionSource",
        "SchemaDeletionDestination", "ContainerVolumeDeletionSource", "ContainerVolumeDeletionDestination",
    )):
        raise ValueError("rehearsal runner refuses fault injection, cleanup, and deletion permissions")
    source = _check_strong_target_binding(document["Source"], "source")
    destination = _check_strong_target_binding(document["Destination"], "destination")
    if source["containerId"] == destination["containerId"] or source["runId"] == destination["runId"]:
        raise ValueError("source and destination runtime identities must be distinct")
    host = document["ExecutionHost"]
    if (host["HostName"] != socket.gethostname() or host["UnixUser"] != pwd.getpwuid(os.getuid()).pw_name
            or host["Uid"] != os.getuid() or host["RootlessPodman"] is not True):
        raise ValueError("execution host or rootless operator identity differs from authorization")
    if source["image"] != destination["image"]:
        raise ValueError("source and destination must pin the same immutable MariaDB image")
    if document["SnapshotPlan"].get("CredentialTransport") != "option-file":
        raise ValueError("only MariaDB client option-file credential transport is executable")
    return {"Source": source, "Destination": destination}


def _run(command: list[str], *, input_stream: Any = None, output_stream: Any = subprocess.PIPE) -> subprocess.CompletedProcess[str]:
    """Run an external client while withholding stdout/stderr from diagnostics."""
    try:
        return subprocess.run(command, stdin=input_stream, stdout=output_stream, stderr=subprocess.PIPE,
                              text=output_stream == subprocess.PIPE, check=False,
                              timeout=HOST_COMMAND_TIMEOUT_SECONDS)
    except OSError:
        raise RuntimeError("authorized host utility could not be started") from None


def _success(command: list[str]) -> str:
    result = _run(command)
    if result.returncode != 0:
        raise RuntimeError("authorized host verification command failed")
    return result.stdout if isinstance(result.stdout, str) else ""


def inspect_target(target: dict[str, Any], role: str, identity: dict[str, str], runner: Callable[..., subprocess.CompletedProcess[str]]) -> None:
    info_result = runner(["podman", "container", "inspect", "--format", "{{json .}}", target["ContainerName"]])
    if info_result.returncode != 0:
        raise RuntimeError(f"{role} container inspection failed")
    try:
        raw = json.loads(info_result.stdout)
        info = raw[0] if isinstance(raw, list) and len(raw) == 1 else raw
    except (json.JSONDecodeError, TypeError):
        raise RuntimeError(f"{role} container inspection was malformed") from None
    if not isinstance(info, dict):
        raise RuntimeError(f"{role} container inspection was malformed")
    config = info.get("Config") or {}
    state = info.get("State") or {}
    labels = config.get("Labels") or {} if isinstance(config, dict) else {}
    mounts = info.get("Mounts") or []
    if (info.get("Id") != identity["containerId"] or state.get("Running") is not True
            or config.get("Image") != identity["image"]
            or labels.get("io.continuumwiki.m5-026b-run-id") != identity["runId"]
            or labels.get("io.continuumwiki.m5-026b-database") != target["DatabaseName"]
            or not any(isinstance(m, dict) and m.get("Name") == target["VolumeName"] and m.get("Destination") == "/var/lib/mysql" for m in mounts)):
        raise RuntimeError(f"{role} container, image, run-label, state, or volume mismatch")
    published = runner(["podman", "port", target["ContainerName"], "3306/tcp"])
    expected_port = target["Endpoint"].rsplit(":", 1)[1]
    if published.returncode != 0 or published.stdout.strip() != f"127.0.0.1:{expected_port}":
        raise RuntimeError(f"{role} endpoint is not bound exactly to loopback")
    image_result = runner(["podman", "image", "inspect", "--format", "{{json .}}", identity["image"]])
    if image_result.returncode != 0:
        raise RuntimeError(f"{role} pinned image inspection failed")
    try:
        image = json.loads(image_result.stdout)
        image = image[0] if isinstance(image, list) and len(image) == 1 else image
    except (json.JSONDecodeError, TypeError):
        raise RuntimeError(f"{role} pinned image inspection was malformed") from None
    if (not isinstance(image, dict) or identity["image"] not in image.get("RepoDigests", [])
            or info.get("Image") != image.get("Id")):
        raise RuntimeError(f"{role} immutable image digest mismatch")
    volume_result = runner(["podman", "volume", "inspect", "--format", "{{json .}}", target["VolumeName"]])
    if volume_result.returncode != 0:
        raise RuntimeError(f"{role} volume inspection failed")
    try:
        volume = json.loads(volume_result.stdout)
        volume = volume[0] if isinstance(volume, list) and len(volume) == 1 else volume
    except (json.JSONDecodeError, TypeError):
        raise RuntimeError(f"{role} volume inspection was malformed") from None
    if (not isinstance(volume, dict) or volume.get("Name") != target["VolumeName"]
            or volume.get("Labels", {}).get("io.continuumwiki.m5-026b-run-id") != identity["runId"]):
        raise RuntimeError(f"{role} disposable volume identity mismatch")


def client_command(client: str, target: dict[str, Any], credential: Path, *, dump_options: bool = False) -> list[str]:
    command = [client, f"--defaults-extra-file={credential}", "--protocol=tcp", "--host", "127.0.0.1",
               "--port", target["Endpoint"].rsplit(":", 1)[1], "--user", target["DatabaseIdentity"],
               "--database", target["DatabaseName"], "--batch", "--skip-column-names"]
    if dump_options:
        command.extend(["--single-transaction", "--routines", "--events", "--triggers", "--hex-blob",
                        "--default-character-set=utf8mb4", "--skip-comments"])
    return command


def parse_identity(stdout: str, target: dict[str, Any]) -> None:
    fields = stdout.rstrip("\n").split("\t")
    if len(fields) != 4:
        raise RuntimeError("database identity response shape mismatch")
    database, version, comment, user = fields
    if (database != target["DatabaseName"] or not version.startswith("11.8.")
            or "mariadb" not in comment.casefold()
            or not user.startswith(target["DatabaseIdentity"] + "@")):
        raise RuntimeError("database name, MariaDB 11.8 version, server comment, or application identity mismatch")


def verify_server(target: dict[str, Any], credential: Path, runner: Callable[..., subprocess.CompletedProcess[str]]) -> None:
    cmd = client_command("mariadb", target, credential)
    cmd.extend(["--execute", "SELECT DATABASE(), VERSION(), @@version_comment, CURRENT_USER();"])
    result = runner(cmd)
    if result.returncode != 0:
        raise RuntimeError("database identity verification failed")
    parse_identity(result.stdout, target)
    grant_command = client_command("mariadb", target, credential)
    grant_command.extend(["--execute", "SHOW GRANTS;"])
    grants = runner(grant_command)
    if grants.returncode != 0:
        raise RuntimeError("database grant-scope verification failed")
    for line in grants.stdout.splitlines():
        normalized = line.strip().upper()
        padded = f" {normalized} "
        if "GRANT OPTION" in normalized or (" ON *.* " in padded and "GRANT USAGE ON *.*" not in normalized):
            raise RuntimeError("database identity has global privileges or grant option")
        if " ON " not in padded and "GRANT USAGE" not in normalized:
            raise RuntimeError("database identity has an unscoped role or grant")
        if " ON " in padded and " ON *.* " not in padded:
            target_name = re.escape(target["DatabaseName"].upper())
            if not re.search(rf"\sON\s+`?{target_name}`?\.(?:\*|`[A-Z0-9_]+`)\s", padded):
                raise RuntimeError("database identity grant is not confined to its authorized schema")


def list_tables_and_engines(target: dict[str, Any], credential: Path, runner: Callable[..., subprocess.CompletedProcess[str]]) -> list[tuple[str, str]]:
    cmd = client_command("mariadb", target, credential)
    cmd.extend(["--execute", "SELECT TABLE_NAME, COALESCE(ENGINE,'') FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME;"])
    result = runner(cmd)
    if result.returncode != 0:
        raise RuntimeError("table inventory query failed")
    rows: list[tuple[str, str]] = []
    for line in result.stdout.splitlines():
        fields = line.split("\t")
        if len(fields) != 2 or not fields[0]:
            raise RuntimeError("table inventory response malformed")
        rows.append((fields[0], fields[1]))
    if any(engine.casefold() != "innodb" for _, engine in rows):
        raise RuntimeError("non-InnoDB table prevents transaction-consistent rehearsal")
    return rows


def table_row_hashes(target: dict[str, Any], credential: Path, tables: list[tuple[str, str]], runner: Callable[..., subprocess.CompletedProcess[str]]) -> dict[str, str]:
    hashes: dict[str, str] = {}
    for table, _ in tables:
        if not re.fullmatch(r"[A-Za-z0-9_]+", table):
            raise RuntimeError("unsafe table identifier in readback")
        command = ["mariadb-dump", f"--defaults-extra-file={credential}", "--protocol=tcp", "--host", "127.0.0.1",
                   "--port", target["Endpoint"].rsplit(":", 1)[1], "--user", target["DatabaseIdentity"],
                   "--no-create-info", "--skip-triggers", "--skip-comments", "--compact", "--skip-extended-insert",
                   "--order-by-primary", "--default-character-set=utf8mb4", target["DatabaseName"], table]
        with tempfile.TemporaryFile(mode="w+b") as output:
            result = runner(command, output_stream=output)
            if result.returncode != 0:
                raise RuntimeError("table readback digest query failed")
            digest = hashlib.sha256()
            output.seek(0)
            for chunk in iter(lambda: output.read(1024 * 1024), b""):
                digest.update(chunk)
            hashes[table] = digest.hexdigest()
    return hashes


def _write_report_once(path: Path, payload: dict[str, Any], uid: int) -> Any:
    _private_directory(path.parent, uid)
    stream = path.open("x+", encoding="utf-8")
    os.chmod(path, 0o600)
    _write_report(stream, payload)
    return stream


def _write_report(stream: Any, payload: dict[str, Any]) -> None:
    stream.seek(0)
    stream.truncate()
    json.dump(payload, stream, indent=2)
    stream.write("\n")
    stream.flush()
    os.fsync(stream.fileno())


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _verify_checksum_sidecar(artifact: Path, sidecar: Path, expected_digest: str) -> None:
    try:
        info = sidecar.lstat()
        content = sidecar.read_text(encoding="ascii")
    except OSError:
        raise RuntimeError("snapshot checksum sidecar is unavailable") from None
    expected = f"{expected_digest}  {artifact.name}\n"
    if (not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_mode & 0o077
            or content != expected or _sha256_file(artifact) != expected_digest):
        raise RuntimeError("snapshot checksum verification failed")


def _reject_database_routing_statements(artifact: Path) -> None:
    """Prevent an input dump from redirecting restore to another schema."""
    pattern = re.compile(rb"^\s*(?:CREATE\s+DATABASE|USE)\s+", re.IGNORECASE)
    with artifact.open("rb") as stream:
        for line in stream:
            if pattern.match(line):
                raise RuntimeError("snapshot contains a database-routing statement; refusing restore")


def run_rehearsal(document: dict[str, Any], artifact: Path, report_path: Path, *, runner: Callable[..., subprocess.CompletedProcess[str]] = _run,
                  uid: int | None = None, now: Callable[[], datetime] = _now) -> dict[str, Any]:
    uid = os.getuid() if uid is None else uid
    identities = validate_execution_manifest(document, now=now())
    operations = document["PermittedOperations"]
    required = ("ConnectSource", "VerifySourceIsolation", "BackupSource", "VerifyBackupArtifact", "ConnectDestination",
                "VerifyDestinationIsolation", "RestoreDestination", "VerifyRestore", "RetainSanitizedEvidence")
    if any(operations.get(name) is not True for name in required):
        raise ValueError("mandatory rehearsal permission absent")
    plan = document["SnapshotPlan"]
    artifact = artifact.expanduser()
    if artifact.parent != Path(plan["ArtifactDirectory"]).expanduser().resolve(strict=True):
        raise ValueError("artifact must be inside the exact authorized artifact directory")
    _private_directory(artifact.parent, uid)
    source, destination = document["Source"], document["Destination"]
    source_credential = _private_file(source["CredentialReference"], uid)
    destination_credential = _private_file(destination["CredentialReference"], uid)
    if source_credential == destination_credential:
        raise ValueError("source and destination credentials must be distinct")
    report_stream = _write_report_once(report_path, {
        "ReportVersion": "cw-m5-026b-rehearsal-result-v1", "GeneratedAtUtc": now().isoformat().replace("+00:00", "Z"),
        "Classification": "REHEARSAL_IN_PROGRESS", "Phase": "TARGET_INSPECTION", "AutomaticCleanup": False,
        "DatabaseConnections": 0, "DatabaseMutations": 0, "ArtifactPath": str(artifact),
        "ApprovalId": document["ApprovalId"], "AuthorizationSha256": hashlib.sha256(json.dumps(document, sort_keys=True).encode()).hexdigest(),
        "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO",
    }, uid)
    podman_calls = 0
    connections = 0
    mutations = 0
    phase = "TARGET_INSPECTION"
    try:
        podman = runner(["podman", "info", "--format", "{{.Host.Security.Rootless}}"])
        podman_calls += 1
        if podman.returncode != 0 or podman.stdout.strip() != "true":
            raise RuntimeError("rootless Podman verification failed")
        for target, role in ((source, "source"), (destination, "destination")):
            inspect_target(target, role, identities[role.capitalize()], runner)
            podman_calls += 4
            phase = "DATABASE_IDENTITY_AND_GRANT_VERIFICATION"
            verify_server(target, source_credential if role == "source" else destination_credential, runner)
            connections += 2
        phase = "SOURCE_AND_DESTINATION_INVENTORY"
        _write_report(report_stream, {"ReportVersion": "cw-m5-026b-rehearsal-result-v1", "GeneratedAtUtc": now().isoformat().replace("+00:00", "Z"),
                                      "Classification": "REHEARSAL_IN_PROGRESS", "Phase": "SOURCE_INVENTORY", "AutomaticCleanup": False,
                                      "DatabaseConnections": connections, "DatabaseMutations": mutations, "PodmanCommands": podman_calls,
                                      "ArtifactPath": str(artifact), "ApprovalId": document["ApprovalId"],
                                      "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"})
        source_tables = list_tables_and_engines(source, source_credential, runner)
        connections += 1
        missing_tables = sorted(set(TABLES) - {name for name, _ in source_tables})
        if missing_tables:
            raise RuntimeError("source is missing required rehearsal tables: " + ", ".join(missing_tables))
        destination_tables = list_tables_and_engines(destination, destination_credential, runner)
        connections += 1
        if destination_tables:
            raise RuntimeError("destination is not empty; refusing restore")
        if artifact.exists() or Path(str(artifact) + ".sha256").exists():
            raise RuntimeError("artifact or checksum already exists; refusing overwrite")
        validate_execution_manifest(document, now=now())
        phase = "SNAPSHOT"
        sidecar = Path(str(artifact) + ".sha256")
        with _open_private_exclusive(artifact) as output:
            command = ["mariadb-dump", f"--defaults-extra-file={source_credential}", "--protocol=tcp", "--host", "127.0.0.1",
                       "--port", source["Endpoint"].rsplit(":", 1)[1], "--user", source["DatabaseIdentity"],
                       "--single-transaction", "--routines", "--events", "--triggers", "--hex-blob", "--default-character-set=utf8mb4",
                       "--skip-comments", "--skip-add-drop-table", source["DatabaseName"]]
            dumped = runner(command, output_stream=output)
            connections += 1
            if dumped.returncode != 0 or artifact.stat().st_size == 0:
                raise RuntimeError("snapshot command failed or produced an empty artifact")
            output.flush()
            os.fsync(output.fileno())
        digest = _sha256_file(artifact)
        with _open_private_exclusive(sidecar, "w") as checksum:
            checksum.write(f"{digest}  {artifact.name}\n")
            checksum.flush()
            os.fsync(checksum.fileno())
        _write_report(report_stream, {"ReportVersion": "cw-m5-026b-rehearsal-result-v1", "GeneratedAtUtc": now().isoformat().replace("+00:00", "Z"),
                                      "Classification": "REHEARSAL_IN_PROGRESS", "Phase": "SNAPSHOT_VERIFIED", "AutomaticCleanup": False,
                                      "DatabaseConnections": connections, "DatabaseMutations": mutations, "PodmanCommands": podman_calls,
                                      "Artifact": {"Path": str(artifact), "SizeBytes": artifact.stat().st_size, "Sha256": digest},
                                      "ApprovalId": document["ApprovalId"], "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"})
        _verify_checksum_sidecar(artifact, sidecar, digest)
        _reject_database_routing_statements(artifact)
        # Recheck authorization expiry immediately before the destructive data write.
        validate_execution_manifest(document, now=now())
        phase = "RESTORE"
        current_destination = list_tables_and_engines(destination, destination_credential, runner)
        connections += 1
        if current_destination:
            raise RuntimeError("destination became nonempty; refusing restore")
        restore_command = ["mariadb", f"--defaults-extra-file={destination_credential}", "--protocol=tcp", "--host", "127.0.0.1",
                           "--port", destination["Endpoint"].rsplit(":", 1)[1], "--user", destination["DatabaseIdentity"],
                           "--database", destination["DatabaseName"], "--binary-mode"]
        with artifact.open("rb") as source_stream:
            restored = runner(restore_command, input_stream=source_stream, output_stream=subprocess.DEVNULL)
        connections += 1
        mutations += 1
        if restored.returncode != 0:
            raise RuntimeError("restore command failed; destination may be partially restored and is retained")
        phase = "TABLE_CONTENT_READBACK"
        restored_tables = list_tables_and_engines(destination, destination_credential, runner)
        connections += 1
        if sorted(source_tables) != sorted(restored_tables):
            raise RuntimeError("restored table/engine inventory differs from source")
        source_hashes = table_row_hashes(source, source_credential, source_tables, runner)
        connections += len(source_tables)
        destination_hashes = table_row_hashes(destination, destination_credential, restored_tables, runner)
        connections += len(restored_tables)
        matches = source_hashes == destination_hashes
        result = {"ReportVersion": "cw-m5-026b-rehearsal-result-v1", "GeneratedAtUtc": now().isoformat().replace("+00:00", "Z"),
                  "Classification": "RESTORE_CONTENT_READBACK_MATCHED_NOT_FULL_CERTIFIED" if matches else "RESTORE_CONTENT_MISMATCH",
                  "Phase": "READBACK_COMPLETE", "AutomaticCleanup": False, "DatabaseConnections": connections,
                  "DatabaseMutations": mutations, "PodmanCommands": podman_calls, "CredentialsReadByRunner": True,
                  "Source": {"DatabaseName": source["DatabaseName"], "ContainerIdSha256": hashlib.sha256(identities["Source"]["containerId"].encode()).hexdigest()},
                  "Destination": {"DatabaseName": destination["DatabaseName"], "ContainerIdSha256": hashlib.sha256(identities["Destination"]["containerId"].encode()).hexdigest()},
                  "Artifact": {"Path": str(artifact), "SizeBytes": artifact.stat().st_size, "Sha256": digest},
                  "Readback": {"SourceBaseTableCount": len(source_tables), "DestinationBaseTableCount": len(restored_tables),
                               "TableContentHashesMatch": matches, "TableHashes": {name: hashlib.sha256(value.encode()).hexdigest() for name, value in source_hashes.items()},
                               "FullSchemaLedgerForeignKeyAndRecoveryCertification": "NOT_PERFORMED"},
                  "UnsupportedClaims": ["post-snapshot write recovery", "crash-boundary certification", "full schema/ledger/FK/audit verification", "lossless rollback"],
                  "ApprovalId": document["ApprovalId"], "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"}
        _write_report(report_stream, result)
        return result
    except Exception as error:
        safe_error = str(error) if isinstance(error, (RuntimeError, ValueError)) else "unexpected rehearsal failure"
        _write_report(report_stream, {"ReportVersion": "cw-m5-026b-rehearsal-result-v1", "GeneratedAtUtc": now().isoformat().replace("+00:00", "Z"),
                                      "Classification": "REHEARSAL_FAILED_RESOURCES_RETAINED", "Phase": phase,
                                      "Diagnostic": safe_error, "AutomaticCleanup": False, "DatabaseConnections": connections,
                                      "DatabaseMutations": mutations, "PodmanCommands": podman_calls, "ArtifactPath": str(artifact),
                                      "ArtifactRetainedIfCreated": artifact.exists(), "ApprovalId": document["ApprovalId"],
                                      "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"})
        raise
    finally:
        report_stream.close()


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--artifact", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--confirm-approval-id")
    args = parser.parse_args(argv)
    if not args.execute:
        print("CW-M5-026B rehearsal plan-only: no Podman or database operations will run.")
        return 0
    try:
        document = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
        if args.confirm_approval_id != document.get("ApprovalId"):
            raise ValueError("--execute requires --confirm-approval-id equal to the manifest approval ID")
        result = run_rehearsal(document, Path(args.artifact), Path(args.report))
        print(f"CW-M5-026B rehearsal: classification={result['Classification']}; report={args.report}")
        return 0 if result["Classification"] == "RESTORE_CONTENT_READBACK_MATCHED_NOT_FULL_CERTIFIED" else 4
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError, RuntimeError) as error:
        print(f"CW-M5-026B rehearsal refused/failed: {error}", file=sys.stderr)
        return 64


if __name__ == "__main__":
    sys.exit(main())
