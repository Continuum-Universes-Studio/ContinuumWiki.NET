#!/usr/bin/env python3
"""Nonconnecting CW-M5-025 authorization-manifest preflight."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


MANIFEST_VERSION = "cw-m5-025-authorization-v1"
REPORT_VERSION = "cw-m5-025-authorization-preflight-v1"
REQUIRED_OPERATIONS = (
    "Connect", "VerifyTargetIsolation", "ApplyV1Prerequisites", "ApplyV2Prerequisites",
    "ApplyV3Migration", "GrantIssueMutations", "GrantConsumeMutations",
    "GrantRevocationMutations", "GrantAuditMutations", "ConcurrentConnectionTests",
    "RestartRecoveryTests", "AcknowledgmentLossTests", "RetainSanitizedEvidence",
    "DatabaseCleanup", "SchemaDeletion", "ContainerVolumeDeletion",
)
REQUIRED_CRASH_BOUNDARIES = (
    "after-grant-commit-before-response",
    "forced-failure-before-grant-audit-commit",
    "recreated-service-instance",
)
SAFE_TOKEN = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")
LOOPBACK_ENDPOINT = re.compile(r"^(127\.0\.0\.1|localhost|\[::1\]):([1-9][0-9]{2,4})$")
DATABASE_NAME = re.compile(r"^cw_m5_025_[a-z0-9_]{1,48}$")
CONTAINER_NAME = re.compile(r"^continuumwiki-m5-025-[a-z0-9-]{1,48}$")
VOLUME_NAME = re.compile(r"^continuumwiki-m5-025-[a-z0-9-]{1,48}-data$")
SECRET_KEY = re.compile(r"password|passwd|secret|token|connectionstring|connection_string|accesskey|privatekey|credentialmaterial", re.I)
SECRET_VALUE = re.compile(r"password\s*=|user\s*id\s*=|access[_-]?token|-----BEGIN .*PRIVATE KEY-----", re.I)


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


def validate_manifest(document: Any, now: datetime | None = None) -> list[str]:
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root:required-object"]
    _scan_secrets(document, "root", errors)
    required = ("ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver", "Operator", "TargetOwner", "Target", "PermittedOperations", "Isolation", "TestScope")
    for field in required:
        if field not in document:
            errors.append(f"{field}:missing")
    if document.get("ManifestVersion") != MANIFEST_VERSION:
        errors.append("ManifestVersion:unsupported")
    if not isinstance(document.get("ApprovalId"), str) or not SAFE_TOKEN.fullmatch(document["ApprovalId"]):
        errors.append("ApprovalId:invalid-token")
    approved = _utc(document.get("ApprovedAtUtc"), "ApprovedAtUtc", errors)
    expires = _utc(document.get("ExpiresAtUtc"), "ExpiresAtUtc", errors)
    current = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if approved and approved > current:
        errors.append("ApprovedAtUtc:not-yet-valid")
    if approved and expires and approved >= expires:
        errors.append("ExpiresAtUtc:not-after-approval")
    if expires and expires <= current:
        errors.append("ExpiresAtUtc:expired")
    for name in ("Approver", "Operator"):
        person = _object(document, name, errors)
        if person:
            _string(person.get("Name"), f"{name}.Name", errors, 160)
            _string(person.get("Role"), f"{name}.Role", errors, 160)
    _string(document.get("TargetOwner"), "TargetOwner", errors, 160)
    target = _object(document, "Target", errors)
    if target:
        if target.get("Disposable") is not True:
            errors.append("Target.Disposable:must-be-true")
        container = target.get("ContainerName")
        volume = target.get("VolumeName")
        database = target.get("DatabaseName")
        endpoint = target.get("Endpoint")
        identity = _string(target.get("InstanceIdentity"), "Target.InstanceIdentity", errors)
        if not isinstance(container, str) or not CONTAINER_NAME.fullmatch(container):
            errors.append("Target.ContainerName:m5-025-prefix-required")
        if not isinstance(volume, str) or not VOLUME_NAME.fullmatch(volume):
            errors.append("Target.VolumeName:m5-025-prefix-required")
        if not isinstance(database, str) or not DATABASE_NAME.fullmatch(database):
            errors.append("Target.DatabaseName:m5-025-prefix-required")
        if not isinstance(endpoint, str) or not LOOPBACK_ENDPOINT.fullmatch(endpoint) or int(endpoint.rsplit(":", 1)[1]) <= 1024:
            errors.append("Target.Endpoint:loopback-unprivileged-port-required")
        prefix = f"container={container};database={database};endpoint={endpoint};volume={volume};"
        if identity and ("production" in identity.lower() or not identity.startswith(prefix)):
            errors.append("Target.InstanceIdentity:exact-isolated-prefix-required")
        if target.get("EngineVersion") != "MariaDB 11.8.x":
            errors.append("Target.EngineVersion:mariadb-11-8-required")
        if target.get("DatabaseIdentity") != "continuumwiki_m5_025_app":
            errors.append("Target.DatabaseIdentity:dedicated-m5-025-identity-required")
    operations = _object(document, "PermittedOperations", errors)
    if operations:
        unknown = sorted(set(operations) - set(REQUIRED_OPERATIONS))
        errors.extend(f"PermittedOperations.{name}:unsupported-operation" for name in unknown)
        for name in REQUIRED_OPERATIONS:
            if not isinstance(operations.get(name), bool):
                errors.append(f"PermittedOperations.{name}:explicit-boolean-required")
            elif name not in ("DatabaseCleanup", "SchemaDeletion", "ContainerVolumeDeletion") and operations[name] is not True:
                errors.append(f"PermittedOperations.{name}:certification-true-required")
            elif name in ("DatabaseCleanup", "SchemaDeletion", "ContainerVolumeDeletion") and operations[name] is not False:
                errors.append(f"PermittedOperations.{name}:must-remain-false")
    isolation = _object(document, "Isolation", errors)
    if isolation:
        if isolation.get("Production") is not False:
            errors.append("Isolation.Production:must-be-false")
        if isolation.get("SharedWithProduction") is not False:
            errors.append("Isolation.SharedWithProduction:must-be-false")
        if isolation.get("DedicatedCredentials") is not True:
            errors.append("Isolation.DedicatedCredentials:must-be-true")
        _string(isolation.get("Proof"), "Isolation.Proof", errors, 2048)
    scope = _object(document, "TestScope", errors)
    if scope:
        suite = scope.get("Suite")
        if not isinstance(suite, list) or not suite or any(not isinstance(item, str) or not item or len(item) > 128 for item in suite):
            errors.append("TestScope.Suite:nonempty-bounded-list-required")
        workers = scope.get("MaxConcurrentWorkers")
        if not isinstance(workers, int) or isinstance(workers, bool) or not 1 <= workers <= 4:
            errors.append("TestScope.MaxConcurrentWorkers:bounded-1-to-4-required")
        boundaries = scope.get("CrashBoundaries")
        if not isinstance(boundaries, list) or set(boundaries) != set(REQUIRED_CRASH_BOUNDARIES):
            errors.append("TestScope.CrashBoundaries:complete-required-set")
    return sorted(set(errors))


def build_report(document: Any, errors: list[str], manifest_path: str, now: datetime) -> dict[str, Any]:
    target = document.get("Target") if isinstance(document, dict) and isinstance(document.get("Target"), dict) else {}
    target_identity = target.get("InstanceIdentity") if isinstance(target, dict) else None
    fingerprint = hashlib.sha256(target_identity.encode("utf-8")).hexdigest() if isinstance(target_identity, str) else None
    return {
        "ReportVersion": REPORT_VERSION,
        "GeneratedAtUtc": now.astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_ADMIN_GRANT_MARIADB_AUTHORIZATION_MANIFEST_INVALID" if errors else "CATEGORY_ADMIN_GRANT_MARIADB_AUTHORIZATION_MANIFEST_VALID_NONCONNECTING",
        "Manifest": {"PathRecorded": str(manifest_path), "Validated": not errors, "Errors": errors},
        "Target": {"NonSecretFingerprint": fingerprint, "DatabaseNameRecorded": target.get("DatabaseName") if errors == [] else None, "EndpointRecorded": target.get("Endpoint") if errors == [] else None},
        "Preflight": {"DatabaseConnections": 0, "DatabaseMutations": 0, "ConnectionEnvironmentVariablesRead": False, "DatabaseContacted": False, "AuthorizationOnly": True},
        "Production": {"Migration": "NO_GO", "RepositoryActivation": "NO_GO", "Cleanup": "NO_GO"},
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
    print(f"CW-M5-025 nonconnecting preflight: classification={report['Classification']}; database_connections=0; database_mutations=0")
    return 0 if not errors else 3


if __name__ == "__main__":
    sys.exit(main())
