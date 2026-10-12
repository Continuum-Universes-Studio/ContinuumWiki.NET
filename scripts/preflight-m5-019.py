#!/usr/bin/env python3
"""Nonconnecting CW-M5-019 authorization-manifest preflight.

This module deliberately has no database, socket, container, subprocess, or
environment-connection handling. It validates only the approval document and
produces a redacted proposed plan.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


MANIFEST_VERSION = "cw-m5-019-authorization-v2"
REPORT_VERSION = "cw-m5-019-certification-preflight-v1"
TARGET_PREFIX = "cw_m5_019_"
APP_IDENTITY = "continuumwiki_m5_019_app"
ENGINE_VERSION = "MariaDB 11.8.x"
REQUIRED_OPERATIONS = (
    "Connect",
    "CreateDisposableSchema",
    "ApplyV1Migration",
    "ApplyV2Migration",
    "CategoryMutations",
    "ConcurrencyAndCrashTests",
    "RetainSanitizedEvidence",
    "DeleteDisposableSchema",
    "DatabaseCleanup",
    "ContainerVolumeDeletion",
)
CERTIFICATION_OPERATIONS = (
    "Connect",
    "ApplyV1Migration",
    "ApplyV2Migration",
    "CategoryMutations",
    "ConcurrencyAndCrashTests",
    "RetainSanitizedEvidence",
)
REQUIRED_CRASH_BOUNDARIES = (
    "before-mutation",
    "after-mutation-before-audit",
    "after-audit-before-checkpoint",
    "during-checkpoint-cas",
    "after-checkpoint",
)
SAFE_TOKEN = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")
LOOPBACK_ENDPOINT = re.compile(r"^(127\.0\.0\.1|localhost|\[::1\]):([1-9][0-9]{2,4})$")
DATABASE_NAME = re.compile(r"^cw_m5_019_[a-z0-9_]{1,48}$")
CONTAINER_NAME = re.compile(r"^continuumwiki-m5-019-[a-z0-9-]{1,48}$")
VOLUME_NAME = re.compile(r"^continuumwiki-m5-019-[a-z0-9-]{1,48}-data$")
SECRET_KEY = re.compile(r"password|passwd|secret|token|connectionstring|connection_string|accesskey|privatekey|credentialmaterial", re.I)
SECRET_VALUE = re.compile(r"password\s*=|user\s*id\s*=|access[_-]?token|-----BEGIN .*PRIVATE KEY-----", re.I)


def _string(value: Any, field: str, errors: list[str], maximum: int = 512) -> str | None:
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        errors.append(f"{field}:required-bounded-string")
        return None
    return value


def _required_object(parent: dict[str, Any], name: str, errors: list[str]) -> dict[str, Any] | None:
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


def _parse_utc(value: Any, field: str, errors: list[str]) -> datetime | None:
    if not isinstance(value, str) or not value.endswith("Z"):
        errors.append(f"{field}:utc-z-timestamp-required")
        return None
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        errors.append(f"{field}:invalid-timestamp")
        return None
    if parsed.tzinfo is None or parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        errors.append(f"{field}:utc-timestamp-required")
        return None
    return parsed.astimezone(timezone.utc)


def validate_manifest(document: Any, now: datetime | None = None) -> list[str]:
    """Return stable, non-sensitive denial codes. An empty list is valid."""

    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root:required-object"]
    _scan_secrets(document, "root", errors)

    required = (
        "ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc",
        "Approver", "Operator", "TargetOwner", "Target", "PermittedOperations",
        "Isolation", "Provisioning", "TestScope",
    )
    for field in required:
        if field not in document:
            errors.append(f"{field}:missing")

    if document.get("ManifestVersion") != MANIFEST_VERSION:
        errors.append("ManifestVersion:unsupported")
    approval_id = document.get("ApprovalId")
    if not isinstance(approval_id, str) or not SAFE_TOKEN.fullmatch(approval_id):
        errors.append("ApprovalId:invalid-token")
    approved = _parse_utc(document.get("ApprovedAtUtc"), "ApprovedAtUtc", errors)
    expires = _parse_utc(document.get("ExpiresAtUtc"), "ExpiresAtUtc", errors)
    now = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if approved and expires and approved >= expires:
        errors.append("ExpiresAtUtc:not-after-approval")
    if expires and expires <= now:
        errors.append("ExpiresAtUtc:expired")
    for name in ("Approver", "Operator"):
        person = _required_object(document, name, errors)
        if person:
            _string(person.get("Name"), f"{name}.Name", errors, 160)
            _string(person.get("Role"), f"{name}.Role", errors, 160)
    _string(document.get("TargetOwner"), "TargetOwner", errors, 160)

    target = _required_object(document, "Target", errors)
    if target:
        if target.get("Disposable") is not True:
            errors.append("Target.Disposable:must-be-true")
        container = target.get("ContainerName")
        if not isinstance(container, str) or not CONTAINER_NAME.fullmatch(container):
            errors.append("Target.ContainerName:approved-prefix-required")
        volume = target.get("VolumeName")
        if not isinstance(volume, str) or not VOLUME_NAME.fullmatch(volume):
            errors.append("Target.VolumeName:approved-prefix-required")
        database = target.get("DatabaseName")
        if not isinstance(database, str) or not DATABASE_NAME.fullmatch(database):
            errors.append("Target.DatabaseName:approved-prefix-required")
        endpoint = target.get("Endpoint")
        endpoint_match = LOOPBACK_ENDPOINT.fullmatch(endpoint) if isinstance(endpoint, str) else None
        if not endpoint_match or int(endpoint_match.group(2)) <= 1024:
            errors.append("Target.Endpoint:loopback-unprivileged-port-required")
        identity = _string(target.get("InstanceIdentity"), "Target.InstanceIdentity", errors)
        identity_prefix = (
            f"container={container};database={database};"
            f"endpoint={endpoint};volume={volume};"
        )
        if identity and (
            "production" in identity.lower()
            or (isinstance(database, str) and f"database={database}" not in identity)
            or (isinstance(container, str) and f"container={container}" not in identity)
            or (isinstance(volume, str) and f"volume={volume}" not in identity)
            or (isinstance(endpoint, str) and f"endpoint={endpoint}" not in identity)
            or not identity.startswith(identity_prefix)
        ):
            errors.append("Target.InstanceIdentity:isolated-database-identity-required")
        if target.get("EngineVersion") != ENGINE_VERSION:
            errors.append("Target.EngineVersion:mariadb-11-8-required")
        if target.get("DedicatedApplicationIdentity") != APP_IDENTITY:
            errors.append("Target.DedicatedApplicationIdentity:dedicated-identity-required")

    operations = _required_object(document, "PermittedOperations", errors)
    if operations:
        unknown = sorted(set(operations) - set(REQUIRED_OPERATIONS))
        for operation in unknown:
            errors.append(f"PermittedOperations.{operation}:unsupported-or-legacy-operation")
        for operation in REQUIRED_OPERATIONS:
            value = operations.get(operation)
            if not isinstance(value, bool):
                errors.append(f"PermittedOperations.{operation}:explicit-boolean-required")
            elif operation in CERTIFICATION_OPERATIONS and value is not True:
                errors.append(f"PermittedOperations.{operation}:certification-true-required")

    isolation = _required_object(document, "Isolation", errors)
    if isolation:
        if isolation.get("Production") is not False:
            errors.append("Isolation.Production:must-be-false")
        if isolation.get("SharedWithProduction") is not False:
            errors.append("Isolation.SharedWithProduction:must-be-false")
        if isolation.get("DedicatedCredentials") is not True:
            errors.append("Isolation.DedicatedCredentials:must-be-true")
        _string(isolation.get("Proof"), "Isolation.Proof", errors, 1024)

    provisioning = _required_object(document, "Provisioning", errors)
    if provisioning:
        for field, maximum in (("Procedure", 2048), ("CleanupProcedure", 2048), ("CleanupOwner", 160), ("CleanupAttestationPath", 512)):
            _string(provisioning.get(field), f"Provisioning.{field}", errors, maximum)

    scope = _required_object(document, "TestScope", errors)
    if scope:
        suite = scope.get("Suite")
        if not isinstance(suite, list) or not suite or any(not isinstance(item, str) or not item or len(item) > 128 for item in suite):
            errors.append("TestScope.Suite:nonempty-bounded-list-required")
        workers = scope.get("MaxConcurrentWorkers")
        if not isinstance(workers, int) or isinstance(workers, bool) or not 1 <= workers <= 4:
            errors.append("TestScope.MaxConcurrentWorkers:bounded-1-to-4-required")
        if scope.get("MaxPages") != 100:
            errors.append("TestScope.MaxPages:must-preserve-100-page-bound")
        boundaries = scope.get("CrashBoundaries")
        if not isinstance(boundaries, list) or set(boundaries) != set(REQUIRED_CRASH_BOUNDARIES):
            errors.append("TestScope.CrashBoundaries:complete-required-set")

    return sorted(set(errors))


def _digest(value: Any) -> str | None:
    if not isinstance(value, str) or not value:
        return None
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def build_report(document: Any, errors: list[str], manifest_path: str, generated_at: datetime | None = None) -> dict[str, Any]:
    generated_at = (generated_at or datetime.now(timezone.utc)).astimezone(timezone.utc)
    target = document.get("Target") if isinstance(document, dict) and isinstance(document.get("Target"), dict) else {}
    approval_id = document.get("ApprovalId") if isinstance(document, dict) else None
    identity = target.get("InstanceIdentity") if isinstance(target, dict) else None
    return {
        "ReportVersion": REPORT_VERSION,
        "GeneratedAtUtc": generated_at.isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_CERTIFICATION_READINESS_SOURCE_VERIFIED" if not errors else "CATEGORY_CERTIFICATION_READINESS_SOURCE_PARTIAL",
        "Manifest": {
            "Path": Path(manifest_path).name,
            "AuthorizationManifestVersion": MANIFEST_VERSION,
            "PermissionModel": "separate-certification-and-destructive-scopes-v2",
            "Validated": not errors,
            "ApprovalIdSha256": _digest(approval_id),
            "TargetIdentitySha256": _digest(identity),
            "ContainsCredentials": False,
        },
        "Preflight": {
            "Mode": "NON_CONNECTING",
            "DatabaseConnections": 0,
            "DatabaseMutations": 0,
            "LiveCertification": "NOT_EXECUTED",
            "ConnectionEnvironmentVariablesRead": False,
            "ProposedSteps": [
                "verify-authorized-target-identity-and-MariaDB-11.8",
                "apply-V1-category-schema-then-V2-recovery-schema",
                "run-authorized-category-and-recovery-certification",
                "capture-sanitized-evidence-and-cleanup-attestation",
            ],
        },
        "ValidationErrors": errors,
        "Production": {
            "Migration": "NO_GO",
            "CategoryProviderActivation": "NO_GO",
            "LuaChanged": False,
            "WorkerIsolationChanged": False,
        },
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Nonconnecting CW-M5-019 manifest preflight")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    errors: list[str]
    try:
        document = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
        errors = validate_manifest(document)
    except (OSError, json.JSONDecodeError):
        document = {}
        errors = ["Manifest:unreadable-or-invalid-json"]
    report = build_report(document, errors, args.manifest)
    output = Path(args.report)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"CW-M5-019 nonconnecting preflight: classification={report['Classification']}; database_connections=0; database_mutations=0")
    return 0 if not errors else 3


if __name__ == "__main__":
    raise SystemExit(main())
