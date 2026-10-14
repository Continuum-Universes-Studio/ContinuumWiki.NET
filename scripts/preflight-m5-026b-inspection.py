#!/usr/bin/env python3
"""Nonconnecting preflight for the CW-M5-026B read-only inspection contract."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "cw-m5-026b-readonly-inspection-authorization-v1"
SCHEMA_PATH = ROOT / "docs/operations/cw-m5-026b-readonly-inspection-authorization.schema.json"
PROVISIONING_PATH = ROOT / "secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json"
SOURCE_AUTH_PATH = ROOT / "secure/evidence/cw-m5-026b-source-verification-authorization-20261012-002.json"
DEST_AUTH_PATH = ROOT / "secure/evidence/cw-m5-026b-destination-provisioning-authorization-20261012-002.json"
CONTINUATION_PATH = ROOT / "secure/evidence/cw-m5-026b-continuation-result-20261012-003.json"
HEX64 = re.compile(r"^[0-9a-f]{64}$")
RUN_ID = re.compile(r"^\d{8}T\d{6}Z-[A-Za-z0-9._-]+$")
APPROVAL_ID = re.compile(r"^CW-M5-026B-INSPECT-[A-Za-z0-9._:-]{1,96}$")
IMAGE = "docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15"
MAX_LIFETIME = timedelta(hours=24)
FORBIDDEN_KEYS = re.compile(r"password|passwd|secret|token|connectionstring|privatekey|credentialvalue|credentialmaterial", re.I)
FORBIDDEN_TEXT = re.compile(r"password\s*=|user\s*id\s*=|-----BEGIN .*PRIVATE KEY-----", re.I)

REQUIRED_PERMISSIONS = {
    "InspectHostIsolation": True, "ConnectSourceReadOnly": True,
    "InspectSourceMetadata": True, "InspectSourceGrantScope": True,
    "InspectSourceFixtureAggregates": True, "ConnectDestinationReadOnly": True,
    "InspectDestinationMetadata": True, "InspectDestinationEmptiness": True,
    "RetainSanitizedEvidence": True, "DatabaseMutations": False,
    "GrantChanges": False, "Migrations": False, "FixtureWrites": False,
    "Backup": False, "Restore": False, "FaultInjection": False,
    "Certification": False, "Cleanup": False, "SchemaDeletion": False,
    "ContainerVolumeDeletion": False, "ProductionAccess": False,
}


class InspectionPreflightError(ValueError):
    pass


def _read_json(path: Path, code: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise InspectionPreflightError(code) from None
    if not isinstance(value, dict):
        raise InspectionPreflightError(code)
    return value


def _read_private_json(path: Path, code: str) -> dict[str, Any]:
    try:
        if not path.is_absolute() or path.resolve(strict=True) != path or path == ROOT or ROOT in path.parents:
            raise InspectionPreflightError(code)
        file_info = path.lstat()
        parent_info = path.parent.lstat()
    except OSError:
        raise InspectionPreflightError(code) from None
    if (not stat.S_ISREG(file_info.st_mode) or stat.S_ISLNK(file_info.st_mode)
            or file_info.st_uid != os.getuid() or stat.S_IMODE(file_info.st_mode) != 0o600
            or not stat.S_ISDIR(parent_info.st_mode) or stat.S_ISLNK(parent_info.st_mode)
            or parent_info.st_uid != os.getuid() or stat.S_IMODE(parent_info.st_mode) != 0o700):
        raise InspectionPreflightError(code)
    return _read_json(path, code)


def _utc(value: Any, code: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise InspectionPreflightError(code)
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        raise InspectionPreflightError(code) from None
    if parsed.utcoffset() != timezone.utc.utcoffset(parsed):
        raise InspectionPreflightError(code)
    return parsed.astimezone(timezone.utc)


def _secret_scan(value: Any) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if FORBIDDEN_KEYS.search(str(key)):
                raise InspectionPreflightError("SECRET_BEARING_PROPERTY_REJECTED")
            _secret_scan(child)
    elif isinstance(value, list):
        for child in value:
            _secret_scan(child)
    elif isinstance(value, str) and FORBIDDEN_TEXT.search(value):
        raise InspectionPreflightError("SECRET_BEARING_VALUE_REJECTED")


def expected_targets() -> dict[str, dict[str, str]]:
    provisioning = _read_json(PROVISIONING_PATH, "RETAINED_PROVISIONING_EVIDENCE_INVALID")
    source_authorization = _read_json(SOURCE_AUTH_PATH, "RETAINED_SOURCE_BINDING_INVALID")
    destination_authorization = _read_json(DEST_AUTH_PATH, "RETAINED_DESTINATION_BINDING_INVALID")
    continuation = _read_json(CONTINUATION_PATH, "RETAINED_CONTINUATION_EVIDENCE_INVALID")
    try:
        source_run_id = provisioning["Provisioning"]["RunId"]
        source = source_authorization["Target"]
        destination = continuation["Destination"]
        destination_auth = destination_authorization["Target"]
        # Continuation provisioning owns a distinct destination RunId. Keep a
        # legacy fallback only for immutable historical evidence that predates
        # this field; newly produced continuation evidence is always bound to
        # its destination-specific value.
        destination_run_id = destination.get("ProvisioningRunId", source_run_id)
        binding_path = os.environ.get("CW_M5_026B_DESTINATION_BINDING_PATH")
        if binding_path:
            binding = _read_json(Path(binding_path), "DESTINATION_BINDING_EVIDENCE_INVALID")
            observed_binding_run_id = binding.get("ContainerRunId")
            if (binding.get("ContainerName") != destination["ContainerName"]
                    or binding.get("ContainerId") != destination["ContainerId"]
                    or binding.get("VolumeName") != destination["VolumeName"]
                    or binding.get("VolumeRunId") != observed_binding_run_id
                    or binding.get("ContainerAndVolumeRunIdLabelsMatch") is not True
                    or not isinstance(observed_binding_run_id, str)):
                raise InspectionPreflightError("DESTINATION_BINDING_EVIDENCE_INVALID")
            destination_run_id = observed_binding_run_id
        if (not isinstance(source_run_id, str) or not RUN_ID.fullmatch(source_run_id)
                or not isinstance(destination_run_id, str) or not RUN_ID.fullmatch(destination_run_id)):
            raise InspectionPreflightError("RETAINED_RUN_ID_EVIDENCE_INVALID")
        source_id = continuation["SourceContainerId"]
        image = continuation["PinnedImageReference"]
        if (source["ContainerId"] != source_id or image != destination["ImageDigest"]
                or destination_auth["ContainerName"] != destination["ContainerName"]
                or destination_auth["DatabaseName"] != destination["DatabaseName"]
                or destination_auth["VolumeName"] != destination["VolumeName"]
                or destination_auth["Endpoint"] != destination["Endpoint"]):
            raise InspectionPreflightError("RETAINED_TARGET_EVIDENCE_CONFLICT")
        if image != IMAGE or not HEX64.fullmatch(source_id) or not HEX64.fullmatch(destination["ContainerId"]):
            raise InspectionPreflightError("RETAINED_TARGET_EVIDENCE_INVALID")
        result: dict[str, dict[str, str]] = {}
        for role, record, container_id in (("source", source, source_id), ("destination", destination, destination["ContainerId"])):
            cred_dir = record["CredentialDirectory"]
            identity = (record.get("IdentityPrefix") or record.get("InstanceIdentity") or destination_auth.get("IdentityPrefix"))
            # Retained records differ in whether they persist the full identity or only its stable prefix.
            if role == "source":
                identity = source["IdentityPrefix"]
            else:
                identity = destination_auth["IdentityPrefix"]
            run_id = source_run_id if role == "source" else destination_run_id
            full_identity = identity + f"image={image};containerId={container_id};runId={run_id};"
            result[role.capitalize()] = {
                "ContainerName": record["ContainerName"], "ContainerId": container_id,
                "VolumeName": record["VolumeName"], "DatabaseName": record["DatabaseName"],
                "Endpoint": record["Endpoint"], "ImageReference": image,
                "ProvisioningRunId": run_id, "DatabaseIdentity": record["DatabaseIdentity"],
                "EngineVersion": "MariaDB 11.8.x", "InstanceIdentity": full_identity,
                "CredentialDirectory": cred_dir,
            }
        return result
    except InspectionPreflightError:
        raise
    except (KeyError, TypeError):
        raise InspectionPreflightError("RETAINED_TARGET_EVIDENCE_INCOMPLETE") from None


def validate_manifest(document: Any, *, now: datetime | None = None) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise InspectionPreflightError("MANIFEST_ROOT_INVALID")
    _secret_scan(document)
    expected_root = {
        "ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver",
        "Operator", "TargetOwner", "ExecutionHost", "Source", "Destination", "Isolation",
        "PermittedOperations", "ReportPath",
    }
    if set(document) != expected_root or document.get("ManifestVersion") != CONTRACT:
        raise InspectionPreflightError("MANIFEST_SHAPE_OR_VERSION_INVALID")
    if not isinstance(document["ApprovalId"], str) or not APPROVAL_ID.fullmatch(document["ApprovalId"]):
        raise InspectionPreflightError("APPROVAL_ID_INVALID")
    approved = _utc(document["ApprovedAtUtc"], "APPROVAL_TIME_INVALID")
    expires = _utc(document["ExpiresAtUtc"], "EXPIRY_TIME_INVALID")
    instant = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if approved > instant or expires <= instant:
        raise InspectionPreflightError("AUTHORIZATION_NOT_CURRENT")
    if expires <= approved or expires - approved > MAX_LIFETIME:
        raise InspectionPreflightError("AUTHORIZATION_WINDOW_INVALID")
    for name in ("Approver", "Operator"):
        person = document.get(name)
        if not isinstance(person, dict) or set(person) != {"Name", "Role"} or any(not isinstance(person.get(k), str) or not person[k].strip() for k in ("Name", "Role")):
            raise InspectionPreflightError("APPROVAL_IDENTITY_INCOMPLETE")
    if not isinstance(document.get("TargetOwner"), str) or not document["TargetOwner"].strip():
        raise InspectionPreflightError("TARGET_OWNER_INCOMPLETE")
    host = document.get("ExecutionHost")
    if host != {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True}:
        raise InspectionPreflightError("AUTHORIZED_HOST_IDENTITY_MISMATCH")
    isolation = document.get("Isolation")
    if isolation != {"Disposable": True, "Production": False, "SharedWithProduction": False}:
        raise InspectionPreflightError("TARGET_ISOLATION_NOT_AUTHORIZED")
    if document.get("PermittedOperations") != REQUIRED_PERMISSIONS:
        raise InspectionPreflightError("OPERATION_SCOPE_NOT_READONLY_EXACT")
    report_path = document.get("ReportPath")
    if not isinstance(report_path, str) or not report_path.startswith("/home/cdjensen94/continuumwiki-validation/"):
        raise InspectionPreflightError("REPORT_PATH_OUTSIDE_PRIVATE_VALIDATION_ROOT")
    if any(part in {"", ".", ".."} for part in Path(report_path).parts[1:]):
        raise InspectionPreflightError("REPORT_PATH_NOT_CANONICAL")

    retained = expected_targets()
    for role in ("Source", "Destination"):
        target = document.get(role)
        if not isinstance(target, dict):
            raise InspectionPreflightError("TARGET_BINDING_MISSING")
        expected = retained[role]
        if set(target) != set(expected) | {"Disposable", "CredentialOptionFile"}:
            raise InspectionPreflightError("TARGET_BINDING_SHAPE_INVALID")
        if target.get("Disposable") is not True:
            raise InspectionPreflightError("TARGET_NOT_MARKED_DISPOSABLE")
        for key, value in expected.items():
            if target.get(key) != value:
                raise InspectionPreflightError("TARGET_BINDING_MISMATCH")
        option_file = target.get("CredentialOptionFile")
        directory = expected["CredentialDirectory"]
        option_path = Path(option_file) if isinstance(option_file, str) else Path(".")
        if (not isinstance(option_file, str) or option_path.parent != Path(directory)
                or option_path.name != "inspection-client.cnf"):
            raise InspectionPreflightError("CREDENTIAL_OPTION_FILE_REFERENCE_INVALID")
    if document["Source"]["ContainerId"] == document["Destination"]["ContainerId"]:
        raise InspectionPreflightError("TARGETS_NOT_DISTINCT")
    return {
        "ApprovalId": document["ApprovalId"],
        "ApprovedAtUtc": document["ApprovedAtUtc"],
        "ExpiresAtUtc": document["ExpiresAtUtc"],
        "ManifestSha256": hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest(),
        "SourceContainerIdSha256": hashlib.sha256(document["Source"]["ContainerId"].encode()).hexdigest(),
        "DestinationContainerIdSha256": hashlib.sha256(document["Destination"]["ContainerId"].encode()).hexdigest(),
    }


def _write_once(path: Path, data: dict[str, Any]) -> None:
    if (not path.is_absolute() or path == ROOT or ROOT in path.parents
            or any(part in {".", ".."} for part in path.parts)):
        raise InspectionPreflightError("PREFLIGHT_REPORT_PATH_UNSAFE")
    parent = path.parent
    info = parent.lstat()
    if (parent.resolve(strict=True) != parent or not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode)
            or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700):
        raise InspectionPreflightError("PREFLIGHT_REPORT_DIRECTORY_UNSAFE")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as output:
        json.dump(data, output, indent=2, sort_keys=True)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())


def preflight(manifest_path: Path, report_path: Path, *, now: datetime | None = None) -> tuple[bool, dict[str, Any]]:
    errors: list[str] = []
    try:
        document = _read_private_json(manifest_path, "MANIFEST_JSON_INVALID")
        validated = validate_manifest(document, now=now)
    except InspectionPreflightError as error:
        errors.append(str(error))
        validated = {}
    result = {
        "ReportVersion": "cw-m5-026b-readonly-inspection-preflight-v1",
        "GeneratedAtUtc": (now or datetime.now(timezone.utc)).astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING" if not errors else "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_INVALID",
        "Errors": errors,
        "ManifestSha256": validated.get("ManifestSha256"),
        "ApprovalId": validated.get("ApprovalId"),
        "TargetContainerIdSha256": {k: validated[v] for k, v in (("Source", "SourceContainerIdSha256"), ("Destination", "DestinationContainerIdSha256")) if v in validated},
        "ResourceOperationCounts": {"Podman": 0, "DatabaseConnections": 0, "DatabaseQueries": 0, "DatabaseMutations": 0, "CredentialValueReads": 0, "CredentialWrites": 0},
    }
    _write_once(report_path, result)
    return not errors, result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    try:
        ok, report = preflight(Path(args.manifest), Path(args.report))
    except (OSError, InspectionPreflightError) as error:
        code = str(error) if isinstance(error, InspectionPreflightError) else "PREFLIGHT_IO_FAILED"
        print(f"CW-M5-026B inspection preflight stopped: {code}", file=sys.stderr)
        return 2
    print(f"CW-M5-026B inspection preflight: classification={report['Classification']}; database_connections=0; database_mutations=0; podman_commands=0")
    return 0 if ok else 3


if __name__ == "__main__":
    raise SystemExit(main())
