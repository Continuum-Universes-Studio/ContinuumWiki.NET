#!/usr/bin/env python3
"""Nonconnecting CW-M5-026B two-target authorization preflight.

This module validates only a human-supplied authorization document.  It does
not read connection environment variables, launch Podman or MariaDB clients,
open sockets, inspect targets, or mutate any database or filesystem target.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any


MANIFEST_VERSION = "cw-m5-026b-authorization-v2"
REPORT_VERSION = "cw-m5-026b-authorization-preflight-v2"
ENGINE_VERSION = "MariaDB 11.8.x"
ROLES = ("source", "destination")
CORE_OPERATIONS = (
    "ConnectSource", "VerifySourceIsolation", "BackupSource",
    "VerifyBackupArtifact", "ConnectDestination", "VerifyDestinationIsolation",
    "RestoreDestination", "VerifyRestore", "RetainSanitizedEvidence",
)
ALL_OPERATIONS = CORE_OPERATIONS + (
    "SeedDisposableSource",
    "FaultInjection",
    "CleanupSource", "CleanupDestination", "SchemaDeletionSource",
    "SchemaDeletionDestination", "ContainerVolumeDeletionSource",
    "ContainerVolumeDeletionDestination",
)
REQUIRED_CRASH_BOUNDARIES: tuple[str, ...] = ()
SAFE_TOKEN = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
IMAGE_DIGEST = re.compile(r"^docker\.io/library/mariadb@sha256:[0-9a-f]{64}$")
MAX_AUTHORIZATION_LIFETIME = timedelta(hours=24)
LOOPBACK_ENDPOINT = re.compile(r"^(127\.0\.0\.1|localhost|\[::1\]):([1-9][0-9]{2,4})$")
SECRET_KEY = re.compile(
    r"password|passwd|secret|token|connectionstring|connection_string|accesskey|privatekey|credentialmaterial",
    re.I,
)
SECRET_VALUE = re.compile(
    r"password\s*=|user\s*id\s*=|access[_-]?token|-----BEGIN .*PRIVATE KEY-----",
    re.I,
)


def _string(value: Any, field: str, errors: list[str], maximum: int = 512) -> str | None:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        errors.append(f"{field}:required-bounded-string")
        return None
    return value


def _object(parent: dict[str, Any], name: str, errors: list[str]) -> dict[str, Any] | None:
    value = parent.get(name)
    if not isinstance(value, dict):
        errors.append(f"{name}:required-object")
        return None
    return value


def _scan_secrets(value: Any, path: str, errors: list[str]) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if SECRET_KEY.search(str(key)):
                errors.append(f"{path}.{key}:secret-bearing-property-forbidden")
            _scan_secrets(child, f"{path}.{key}", errors)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _scan_secrets(child, f"{path}[{index}]", errors)
    elif isinstance(value, str) and SECRET_VALUE.search(value):
        errors.append(f"{path}:secret-bearing-value-forbidden")


def _utc(value: Any, field: str, errors: list[str]) -> datetime | None:
    if not isinstance(value, str) or not value.endswith("Z"):
        errors.append(f"{field}:utc-z-timestamp-required")
        return None
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        errors.append(f"{field}:invalid-timestamp")
        return None
    if parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        errors.append(f"{field}:utc-timestamp-required")
        return None
    return parsed.astimezone(timezone.utc)


def _validate_target(target: dict[str, Any] | None, role: str, errors: list[str]) -> None:
    field = role.capitalize()
    if target is None:
        return
    required = (
        "Disposable", "ContainerName", "VolumeName", "DatabaseName", "Endpoint",
        "InstanceIdentity", "EngineVersion", "DatabaseIdentity", "CredentialReference",
    )
    for name in required:
        if name not in target:
            errors.append(f"{field}.{name}:missing")
    if target.get("Disposable") is not True:
        errors.append(f"{field}.Disposable:must-be-true")
    container = target.get("ContainerName")
    volume = target.get("VolumeName")
    database = target.get("DatabaseName")
    endpoint = target.get("Endpoint")
    identity = _string(target.get("InstanceIdentity"), f"{field}.InstanceIdentity", errors, 1536)
    credential = _string(target.get("CredentialReference"), f"{field}.CredentialReference", errors)
    expected_container = re.compile(rf"^continuumwiki-m5-026b-{role}-[a-z0-9-]{{1,40}}$")
    expected_volume = re.compile(rf"^continuumwiki-m5-026b-{role}-[a-z0-9-]{{1,40}}-data$")
    expected_database = re.compile(rf"^cw_m5_026b_{role}_[a-z0-9_]{{1,40}}$")
    expected_identity = (
        f"milestone=CW-M5-026B;role={role};container={container};volume={volume};"
        f"database={database};endpoint={endpoint};"
    )
    if not isinstance(container, str) or not expected_container.fullmatch(container):
        errors.append(f"{field}.ContainerName:role-scoped-name-required")
    if not isinstance(volume, str) or not expected_volume.fullmatch(volume):
        errors.append(f"{field}.VolumeName:role-scoped-name-required")
    if not isinstance(database, str) or not expected_database.fullmatch(database):
        errors.append(f"{field}.DatabaseName:role-scoped-name-required")
    if not isinstance(endpoint, str) or not LOOPBACK_ENDPOINT.fullmatch(endpoint) or int(endpoint.rsplit(":", 1)[1]) <= 1024:
        errors.append(f"{field}.Endpoint:loopback-unprivileged-port-required")
    if identity and not identity.startswith(expected_identity):
        errors.append(f"{field}.InstanceIdentity:exact-role-bound-prefix-required")
    if identity:
        identity_fields: dict[str, str] = {}
        for item in identity.split(";"):
            if "=" in item:
                key, value = item.split("=", 1)
                if key in identity_fields:
                    errors.append(f"{field}.InstanceIdentity:duplicate-field")
                identity_fields[key] = value
        if identity_fields.get("containerId") is None or not HEX64.fullmatch(identity_fields.get("containerId", "")):
            errors.append(f"{field}.InstanceIdentity:container-id-pin-required")
        if not IMAGE_DIGEST.fullmatch(identity_fields.get("image", "")):
            errors.append(f"{field}.InstanceIdentity:immutable-mariadb-image-pin-required")
        if not SAFE_TOKEN.fullmatch(identity_fields.get("runId", "")):
            errors.append(f"{field}.InstanceIdentity:provisioning-run-id-pin-required")
        for item in expected_identity.removeprefix("milestone=CW-M5-026B;").split(";"):
            if not item:
                continue
            key, value = item.split("=", 1)
            if identity_fields.get(key) != value:
                errors.append(f"{field}.InstanceIdentity:{key}-binding-mismatch")
    if target.get("EngineVersion") != ENGINE_VERSION:
        errors.append(f"{field}.EngineVersion:mariadb-11-8-required")
    if target.get("DatabaseIdentity") != f"continuumwiki_m5_026b_{role}_app":
        errors.append(f"{field}.DatabaseIdentity:dedicated-role-identity-required")
    if credential and (not credential.startswith("/") or "continuumwiki" not in credential.lower()):
        errors.append(f"{field}.CredentialReference:protected-absolute-reference-required")


def validate_manifest(document: Any, now: datetime | None = None) -> list[str]:
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root:required-object"]
    _scan_secrets(document, "root", errors)
    required = (
        "ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver",
        "Operator", "TargetOwner", "ExecutionHost", "Source", "Destination", "PermittedOperations",
        "Isolation", "SnapshotPlan", "TestScope",
    )
    for field in required:
        if field not in document:
            errors.append(f"{field}:missing")
    if document.get("ManifestVersion") != MANIFEST_VERSION:
        errors.append("ManifestVersion:unsupported")
    if not isinstance(document.get("ApprovalId"), str) or not SAFE_TOKEN.fullmatch(document["ApprovalId"]):
        errors.append("ApprovalId:invalid-token")
    elif not document["ApprovalId"].startswith("CW-M5-026B-REHEARSAL-"):
        errors.append("ApprovalId:fresh-rehearsal-scope-required")
    approved = _utc(document.get("ApprovedAtUtc"), "ApprovedAtUtc", errors)
    expires = _utc(document.get("ExpiresAtUtc"), "ExpiresAtUtc", errors)
    current = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if approved and approved > current:
        errors.append("ApprovedAtUtc:not-yet-valid")
    if approved and expires and approved >= expires:
        errors.append("ExpiresAtUtc:not-after-approval")
    if approved and expires and expires - approved > MAX_AUTHORIZATION_LIFETIME:
        errors.append("ExpiresAtUtc:maximum-24-hours-exceeded")
    if expires and expires <= current:
        errors.append("ExpiresAtUtc:expired")
    for name in ("Approver", "Operator"):
        person = _object(document, name, errors)
        if person:
            _string(person.get("Name"), f"{name}.Name", errors, 160)
            _string(person.get("Role"), f"{name}.Role", errors, 160)
    _string(document.get("TargetOwner"), "TargetOwner", errors, 160)
    host = _object(document, "ExecutionHost", errors)
    if host:
        _string(host.get("HostName"), "ExecutionHost.HostName", errors, 255)
        _string(host.get("UnixUser"), "ExecutionHost.UnixUser", errors, 160)
        if not isinstance(host.get("Uid"), int) or isinstance(host.get("Uid"), bool) or host["Uid"] < 1:
            errors.append("ExecutionHost.Uid:positive-integer-required")
        if host.get("RootlessPodman") is not True:
            errors.append("ExecutionHost.RootlessPodman:must-be-true")
    source = _object(document, "Source", errors)
    destination = _object(document, "Destination", errors)
    _validate_target(source, "source", errors)
    _validate_target(destination, "destination", errors)
    if source and destination:
        for field in ("ContainerName", "VolumeName", "DatabaseName", "Endpoint", "InstanceIdentity", "DatabaseIdentity"):
            if source.get(field) == destination.get(field):
                errors.append(f"Source.Destination.{field}:must-be-distinct")
        if source.get("InstanceIdentity") and destination.get("InstanceIdentity"):
            source_fields = dict(item.split("=", 1) for item in source["InstanceIdentity"].split(";") if "=" in item)
            destination_fields = dict(item.split("=", 1) for item in destination["InstanceIdentity"].split(";") if "=" in item)
            if source_fields.get("image") != destination_fields.get("image"):
                errors.append("Source.Destination.Image:immutable-image-must-match")
    operations = _object(document, "PermittedOperations", errors)
    if operations:
        unknown = sorted(set(operations) - set(ALL_OPERATIONS))
        errors.extend(f"PermittedOperations.{name}:unsupported-or-ambiguous-operation" for name in unknown)
        for name in ALL_OPERATIONS:
            if not isinstance(operations.get(name), bool):
                errors.append(f"PermittedOperations.{name}:explicit-boolean-required")
        for name in CORE_OPERATIONS:
            if operations.get(name) is not True:
                errors.append(f"PermittedOperations.{name}:rehearsal-true-required")
    isolation = _object(document, "Isolation", errors)
    if isolation:
        if isolation.get("Production") is not False:
            errors.append("Isolation.Production:must-be-false")
        if isolation.get("SharedWithProduction") is not False:
            errors.append("Isolation.SharedWithProduction:must-be-false")
        if isolation.get("DedicatedCredentials") is not True:
            errors.append("Isolation.DedicatedCredentials:must-be-true")
        _string(isolation.get("Proof"), "Isolation.Proof", errors, 2048)
    plan = _object(document, "SnapshotPlan", errors)
    if plan:
        expected = {
            "Client": "mariadb-dump", "SingleTransaction": True, "IncludeRoutines": True,
            "IncludeEvents": True, "IncludeTriggers": True, "IncludeHexBlob": True,
            "CharacterSet": "utf8mb4", "PostSnapshotWritePolicy": "record-divergence-and-reconcile-explicitly",
        }
        for key, value in expected.items():
            if plan.get(key) != value:
                errors.append(f"SnapshotPlan.{key}:required-value-mismatch")
        artifact_directory = plan.get("ArtifactDirectory")
        if not isinstance(artifact_directory, str) or not artifact_directory.strip() or "password" in artifact_directory.lower() or "secret" in artifact_directory.lower() or "token" in artifact_directory.lower():
            errors.append("SnapshotPlan.ArtifactDirectory:private-nonsecret-directory-required")
        if plan.get("CredentialTransport") not in ("option-file", "secret-store"):
            errors.append("SnapshotPlan.CredentialTransport:option-file-or-secret-store-required")
        if plan.get("DdlFreezeConfirmed") is not True:
            errors.append("SnapshotPlan.DdlFreezeConfirmed:operator-attestation-required")
    scope = _object(document, "TestScope", errors)
    if scope:
        suite = scope.get("Suite")
        if not isinstance(suite, list) or not suite or any(not isinstance(item, str) or not item or len(item) > 128 for item in suite):
            errors.append("TestScope.Suite:nonempty-bounded-list-required")
        workers = scope.get("MaxConcurrentWorkers")
        if not isinstance(workers, int) or isinstance(workers, bool) or workers != 1:
            errors.append("TestScope.MaxConcurrentWorkers:serial-execution-required")
        boundaries = scope.get("CrashBoundaries")
        if boundaries != list(REQUIRED_CRASH_BOUNDARIES):
            errors.append("TestScope.CrashBoundaries:must-be-empty-for-non-fault-rehearsal")
        if scope.get("PostSnapshotWriteCases") is not False:
            errors.append("TestScope.PostSnapshotWriteCases:must-be-false-for-readback-only-rehearsal")
        suite = scope.get("Suite")
        if isinstance(suite, list) and any(item not in {"snapshot", "restore", "table-content-readback"} for item in suite):
            errors.append("TestScope.Suite:unsupported-operation")
    return sorted(set(errors))


def _digest(value: Any) -> str | None:
    if not isinstance(value, str) or not value:
        return None
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def build_report(document: Any, errors: list[str], manifest_path: str, generated_at: datetime | None = None) -> dict[str, Any]:
    generated_at = (generated_at or datetime.now(timezone.utc)).astimezone(timezone.utc)
    targets: dict[str, Any] = {}
    for role in ROLES:
        target = document.get(role.capitalize()) if isinstance(document, dict) else None
        if isinstance(target, dict) and not errors:
            targets[role] = {
                "ContainerName": target.get("ContainerName"),
                "VolumeName": target.get("VolumeName"),
                "DatabaseName": target.get("DatabaseName"),
                "Endpoint": target.get("Endpoint"),
                "DatabaseIdentity": target.get("DatabaseIdentity"),
                "InstanceIdentitySha256": _digest(target.get("InstanceIdentity")),
            }
    operations = document.get("PermittedOperations", {}) if isinstance(document, dict) else {}
    return {
        "ReportVersion": REPORT_VERSION,
        "GeneratedAtUtc": generated_at.isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_BACKUP_RESTORE_AUTHORIZATION_MANIFEST_VALID_NONCONNECTING" if not errors else "CATEGORY_BACKUP_RESTORE_AUTHORIZATION_MANIFEST_INVALID",
        "Manifest": {
            "Path": Path(manifest_path).name,
            "AuthorizationManifestVersion": MANIFEST_VERSION,
            "Validated": not errors,
            "ApprovalIdSha256": _digest(document.get("ApprovalId") if isinstance(document, dict) else None),
            "ContainsCredentials": False,
        },
        "Targets": targets,
        "Permissions": {
            "FaultInjectionAuthorized": operations.get("FaultInjection") is True,
            "CleanupAuthorized": operations.get("CleanupSource") is True or operations.get("CleanupDestination") is True,
            "SchemaDeletionAuthorized": operations.get("SchemaDeletionSource") is True or operations.get("SchemaDeletionDestination") is True,
            "ContainerVolumeDeletionAuthorized": operations.get("ContainerVolumeDeletionSource") is True or operations.get("ContainerVolumeDeletionDestination") is True,
        },
        "Preflight": {
            "Mode": "NON_CONNECTING",
            "DatabaseConnections": 0,
            "DatabaseMutations": 0,
            "PodmanCommands": 0,
            "ConnectionEnvironmentVariablesRead": False,
            "DatabaseContacted": False,
            "InfrastructureModified": False,
        },
        "ValidationErrors": errors,
        "Production": {"Migration": "NO_GO", "RepositoryActivation": "NO_GO"},
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    now = datetime.now(timezone.utc)
    try:
        document = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        document = {}
    errors = validate_manifest(document, now)
    report = build_report(document, errors, args.manifest, now)
    report_path = Path(args.report)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(
        "CW-M5-026B nonconnecting preflight: "
        f"classification={report['Classification']}; database_connections=0; "
        "database_mutations=0; podman_commands=0"
    )
    return 0 if not errors else 3


if __name__ == "__main__":
    sys.exit(main())
