#!/usr/bin/env python3
"""Nonconnecting CW-M5-026B disposable-target provisioning preflight."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


MANIFEST_VERSION = "cw-m5-026b-provisioning-authorization-v1"
REPORT_VERSION = "cw-m5-026b-provisioning-preflight-v1"
SAFE_TOKEN = re.compile(r"^[A-Za-z0-9._:-]{1,128}$")
SECRET_KEY = re.compile(r"password|passwd|secret|token|connectionstring|privatekey|credentialmaterial", re.I)
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
    return parsed.astimezone(timezone.utc)


def _validate_target(target: dict[str, Any] | None, role: str, errors: list[str]) -> None:
    field = f"TargetPlan.{role.capitalize()}"
    if target is None:
        return
    for name in ("ContainerName", "VolumeName", "DatabaseName", "Endpoint", "DatabaseIdentity", "CredentialDirectory", "IdentityPrefix"):
        if name not in target:
            errors.append(f"{field}.{name}:missing")
    container = target.get("ContainerName")
    volume = target.get("VolumeName")
    database = target.get("DatabaseName")
    endpoint = target.get("Endpoint")
    identity_prefix = target.get("IdentityPrefix")
    if not isinstance(container, str) or not re.fullmatch(rf"continuumwiki-m5-026b-{role}-[a-z0-9-]{{1,40}}", container):
        errors.append(f"{field}.ContainerName:role-scoped-name-required")
    if not isinstance(volume, str) or not re.fullmatch(rf"continuumwiki-m5-026b-{role}-[a-z0-9-]{{1,40}}-data", volume):
        errors.append(f"{field}.VolumeName:role-scoped-name-required")
    if not isinstance(database, str) or not re.fullmatch(rf"cw_m5_026b_{role}_[a-z0-9_]{{1,40}}", database):
        errors.append(f"{field}.DatabaseName:role-scoped-name-required")
    if not isinstance(endpoint, str) or not re.fullmatch(r"127\.0\.0\.1:[1-9][0-9]{2,4}", endpoint) or int(endpoint.rsplit(":", 1)[1]) <= 1024:
        errors.append(f"{field}.Endpoint:loopback-unprivileged-port-required")
    if target.get("DatabaseIdentity") != f"continuumwiki_m5_026b_{role}_app":
        errors.append(f"{field}.DatabaseIdentity:dedicated-role-identity-required")
    credential = target.get("CredentialDirectory")
    if not isinstance(credential, str) or not credential.startswith("/") or any(token in credential.lower() for token in ("password", "secret", "token", "credentialmaterial")):
        errors.append(f"{field}.CredentialDirectory:protected-absolute-directory-required")
    expected_prefix = f"milestone=CW-M5-026B;role={role};container={container};volume={volume};database={database};endpoint={endpoint};"
    if not isinstance(identity_prefix, str) or identity_prefix != expected_prefix:
        errors.append(f"{field}.IdentityPrefix:exact-planned-identity-required")


def validate_manifest(document: Any, now: datetime | None = None) -> list[str]:
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root:required-object"]
    _scan_secrets(document, "root", errors)
    required = (
        "ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver",
        "Operator", "TargetOwner", "Host", "Image", "TargetPlan", "ExcludedResources",
        "PermittedOperations", "Scope",
    )
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
    host = _object(document, "Host", errors)
    if host:
        _string(host.get("HostName"), "Host.HostName", errors, 128)
        if host.get("AllowedEndpointHost") != "127.0.0.1":
            errors.append("Host.AllowedEndpointHost:loopback-only-required")
        if host.get("RootlessPodmanRequired") is not True:
            errors.append("Host.RootlessPodmanRequired:must-be-true")
        ports = _object(host, "PortRange", errors)
        if ports:
            minimum, maximum = ports.get("Minimum"), ports.get("Maximum")
            if not isinstance(minimum, int) or isinstance(minimum, bool) or minimum <= 1024 or minimum > 65535:
                errors.append("Host.PortRange.Minimum:unprivileged-port-required")
            if not isinstance(maximum, int) or isinstance(maximum, bool) or maximum <= 1024 or maximum > 65535:
                errors.append("Host.PortRange.Maximum:unprivileged-port-required")
            if isinstance(minimum, int) and isinstance(maximum, int) and minimum >= maximum:
                errors.append("Host.PortRange:ascending-range-required")
    image = _object(document, "Image", errors)
    if image:
        reference = image.get("Reference")
        if not isinstance(reference, str) or not re.fullmatch(r"docker\.io/library/mariadb@sha256:[a-f0-9]{64}", reference):
            errors.append("Image.Reference:immutable-mariadb-11-8-digest-required")
        if image.get("EngineVersion") != "MariaDB 11.8.x":
            errors.append("Image.EngineVersion:mariadb-11-8-required")
        if image.get("DigestRequired") is not True:
            errors.append("Image.DigestRequired:must-be-true")
    target_plan = _object(document, "TargetPlan", errors)
    source = target_plan.get("Source") if target_plan else None
    destination = target_plan.get("Destination") if target_plan else None
    source = source if isinstance(source, dict) else None
    destination = destination if isinstance(destination, dict) else None
    _validate_target(source, "source", errors)
    _validate_target(destination, "destination", errors)
    if source and destination:
        for field in ("ContainerName", "VolumeName", "DatabaseName", "Endpoint", "DatabaseIdentity", "CredentialDirectory", "IdentityPrefix"):
            if source.get(field) == destination.get(field):
                errors.append(f"TargetPlan.Source.Destination.{field}:must-be-distinct")
    excluded = _object(document, "ExcludedResources", errors)
    if excluded:
        required_prefixes = {
            "ContainerPrefixes": "continuumwiki-m5-025-",
            "VolumePrefixes": "continuumwiki-m5-025-",
            "DatabasePrefixes": "cw_m5_025_",
        }
        for field, required_prefix in required_prefixes.items():
            values = excluded.get(field)
            if not isinstance(values, list) or not values or required_prefix not in values:
                errors.append(f"ExcludedResources.{field}:must-exclude-prior-m5-025-scope")
        if not isinstance(excluded.get("ExactNames"), list) or not excluded["ExactNames"]:
            errors.append("ExcludedResources.ExactNames:nonempty-list-required")
    operations = _object(document, "PermittedOperations", errors)
    if operations:
        known = {
            "ProvisionSourceTarget", "ProvisionDestinationTarget", "VerifyTargetIsolation",
            "RetainSanitizedEvidence", "CleanupSource", "CleanupDestination",
            "SchemaDeletionSource", "SchemaDeletionDestination",
            "ContainerVolumeDeletionSource", "ContainerVolumeDeletionDestination",
        }
        errors.extend(f"PermittedOperations.{name}:unsupported-or-ambiguous-operation" for name in sorted(set(operations) - known))
        for name in known:
            if not isinstance(operations.get(name), bool):
                errors.append(f"PermittedOperations.{name}:explicit-boolean-required")
        for name in ("ProvisionSourceTarget", "ProvisionDestinationTarget", "VerifyTargetIsolation", "RetainSanitizedEvidence"):
            if operations.get(name) is not True:
                errors.append(f"PermittedOperations.{name}:provisioning-true-required")
        for name in ("CleanupSource", "CleanupDestination", "SchemaDeletionSource", "SchemaDeletionDestination", "ContainerVolumeDeletionSource", "ContainerVolumeDeletionDestination"):
            if operations.get(name) is not False:
                errors.append(f"PermittedOperations.{name}:must-remain-false")
    scope = _object(document, "Scope", errors)
    if scope:
        expected = {
            "FreshTargetsRequired": True, "ReuseExistingResources": False,
            "Production": False, "SharedWithProduction": False,
            "DedicatedCredentials": True, "RehearsalRightsGranted": False,
            "BackupAllowed": False, "RestoreAllowed": False,
            "SchemaMigrationAllowed": False, "CertificationAllowed": False,
            "CleanupAllowed": False,
        }
        for key, value in expected.items():
            if scope.get(key) != value:
                errors.append(f"Scope.{key}:required-value-mismatch")
    return sorted(set(errors))


def _digest(value: Any) -> str | None:
    if not isinstance(value, str) or not value:
        return None
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def build_report(document: Any, errors: list[str], manifest_path: str, generated_at: datetime | None = None) -> dict[str, Any]:
    generated_at = (generated_at or datetime.now(timezone.utc)).astimezone(timezone.utc)
    plan = document.get("TargetPlan") if isinstance(document, dict) and isinstance(document.get("TargetPlan"), dict) else {}
    targets = {}
    for role in ("Source", "Destination"):
        target = plan.get(role) if isinstance(plan, dict) else None
        if isinstance(target, dict) and not errors:
            targets[role] = {
                "ContainerName": target.get("ContainerName"),
                "VolumeName": target.get("VolumeName"),
                "DatabaseName": target.get("DatabaseName"),
                "Endpoint": target.get("Endpoint"),
                "DatabaseIdentity": target.get("DatabaseIdentity"),
                "CredentialDirectory": target.get("CredentialDirectory"),
                "IdentityPrefixSha256": _digest(target.get("IdentityPrefix")),
            }
    return {
        "ReportVersion": REPORT_VERSION,
        "GeneratedAtUtc": generated_at.isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_BACKUP_RESTORE_PROVISIONING_AUTHORIZATION_VALID_NONCONNECTING" if not errors else "CATEGORY_BACKUP_RESTORE_PROVISIONING_AUTHORIZATION_INVALID",
        "Manifest": {
            "Path": Path(manifest_path).name,
            "AuthorizationManifestVersion": MANIFEST_VERSION,
            "Validated": not errors,
            "ApprovalIdSha256": _digest(document.get("ApprovalId") if isinstance(document, dict) else None),
            "ContainsCredentials": False,
        },
        "Targets": targets,
        "Provisioning": {
            "Executed": False,
            "RootlessPodmanRequired": True,
            "LoopbackOnlyRequired": True,
            "FreshTargetsRequired": True,
            "RehearsalRightsGranted": False,
            "BackupRestoreAllowed": False,
            "CleanupAuthorized": False,
        },
        "Preflight": {
            "Mode": "NON_CONNECTING",
            "DatabaseConnections": 0,
            "DatabaseMutations": 0,
            "PodmanCommands": 0,
            "CredentialsRead": False,
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
        "CW-M5-026B provisioning preflight: "
        f"classification={report['Classification']}; database_connections=0; "
        "database_mutations=0; podman_commands=0"
    )
    return 0 if not errors else 3


if __name__ == "__main__":
    sys.exit(main())
