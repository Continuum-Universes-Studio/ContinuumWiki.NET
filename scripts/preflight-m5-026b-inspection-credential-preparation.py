#!/usr/bin/env python3
"""Nonconnecting authorization preflight for inspection credential preparation."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import secrets
import stat
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "cw-m5-026b-inspection-credential-preparation-authorization-v1"
SCHEMA_PATH = ROOT / "docs/operations/cw-m5-026b-inspection-credential-preparation-authorization.schema.json"
INSPECTION_PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-inspection.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_inspection_preflight_for_credential_prep", INSPECTION_PREFLIGHT_PATH)
assert SPEC and SPEC.loader
INSPECTION_PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(INSPECTION_PREFLIGHT)

APPROVAL_ID = re.compile(r"^CW-M5-026B-CREDPREP-[A-Za-z0-9._:-]{1,96}$")
MAX_LIFETIME = timedelta(hours=24)
RENEWAL_NAME = re.compile(r"\.renewal-\d{8}T\d{6}Z-[0-9a-f]{32}\.json$")
REQUIRED_PERMISSIONS = {
    "InspectCredentialFileMetadata": True, "ReadApplicationCredentialValues": True,
    "CreateInspectionClientOptionFile": True, "ReadRootEnvironmentFile": False,
    "ModifyProvisioningCredentialFiles": False, "DatabaseConnections": False,
    "PodmanCommands": False, "Migrations": False, "GrantChanges": False,
    "FixtureWrites": False, "Backup": False, "Restore": False, "Cleanup": False,
    "DeleteExistingCredentialFiles": False, "ProductionAccess": False,
    "CredentialValueLogging": False,
}


class CredentialPreflightError(ValueError):
    pass


def _read_json(path: Path, code: str) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise CredentialPreflightError(code) from None
    if not isinstance(value, dict):
        raise CredentialPreflightError(code)
    return value


def _read_private(path: Path, code: str) -> dict[str, Any]:
    try:
        if not path.is_absolute() or path.resolve(strict=True) != path or ROOT in path.parents:
            raise CredentialPreflightError(code)
        file_info = path.lstat()
        parent_info = path.parent.lstat()
    except OSError:
        raise CredentialPreflightError(code) from None
    if (not stat.S_ISREG(file_info.st_mode) or stat.S_ISLNK(file_info.st_mode)
            or file_info.st_uid != os.getuid() or stat.S_IMODE(file_info.st_mode) != 0o600
            or not stat.S_ISDIR(parent_info.st_mode) or stat.S_ISLNK(parent_info.st_mode)
            or parent_info.st_uid != os.getuid() or stat.S_IMODE(parent_info.st_mode) != 0o700):
        raise CredentialPreflightError(code)
    return _read_json(path, code)


def _utc(value: Any, code: str) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise CredentialPreflightError(code)
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        raise CredentialPreflightError(code) from None
    return parsed.astimezone(timezone.utc)


def expected_target(role: str) -> dict[str, str]:
    if role not in {"Source", "Destination"}:
        raise CredentialPreflightError("TARGET_ROLE_INVALID")
    try:
        base = INSPECTION_PREFLIGHT.expected_targets()[role]
    except (KeyError, INSPECTION_PREFLIGHT.InspectionPreflightError):
        raise CredentialPreflightError("RETAINED_TARGET_EVIDENCE_INVALID") from None
    directory = base["CredentialDirectory"]
    return {
        key: base[key] for key in (
            "ContainerName", "ContainerId", "VolumeName", "DatabaseName", "Endpoint",
            "ImageReference", "ProvisioningRunId", "DatabaseIdentity", "InstanceIdentity",
            "CredentialDirectory",
        )
    } | {
        "ApplicationEnvironmentFile": f"{directory}/application.env",
        "CredentialOptionFile": f"{directory}/inspection-client.cnf",
    }


def validate_manifest(document: Any, *, now: datetime | None = None) -> dict[str, Any]:
    if not isinstance(document, dict):
        raise CredentialPreflightError("MANIFEST_ROOT_INVALID")
    fields = {
        "ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver",
        "Operator", "TargetOwner", "ExecutionHost", "TargetRole", "Target",
        "PermittedOperations", "PreflightReportPath",
    }
    if set(document) != fields or document.get("ManifestVersion") != CONTRACT:
        raise CredentialPreflightError("MANIFEST_SHAPE_OR_VERSION_INVALID")
    if not isinstance(document["ApprovalId"], str) or not APPROVAL_ID.fullmatch(document["ApprovalId"]):
        raise CredentialPreflightError("APPROVAL_ID_INVALID")
    approved = _utc(document["ApprovedAtUtc"], "APPROVAL_TIME_INVALID")
    expires = _utc(document["ExpiresAtUtc"], "EXPIRY_TIME_INVALID")
    instant = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if approved > instant or expires <= instant:
        raise CredentialPreflightError("AUTHORIZATION_NOT_CURRENT")
    if expires <= approved or expires - approved > MAX_LIFETIME:
        raise CredentialPreflightError("AUTHORIZATION_WINDOW_INVALID")
    for name in ("Approver", "Operator"):
        person = document.get(name)
        if (not isinstance(person, dict) or set(person) != {"Name", "Role"}
                or any(not isinstance(person.get(k), str) or not person[k].strip() for k in ("Name", "Role"))):
            raise CredentialPreflightError("APPROVAL_IDENTITY_INCOMPLETE")
    if not isinstance(document.get("TargetOwner"), str) or not document["TargetOwner"].strip():
        raise CredentialPreflightError("TARGET_OWNER_INCOMPLETE")
    if document.get("ExecutionHost") != {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True}:
        raise CredentialPreflightError("AUTHORIZED_HOST_IDENTITY_MISMATCH")
    role = document.get("TargetRole")
    target = document.get("Target")
    expected = expected_target(role) if isinstance(role, str) else {}
    if not isinstance(target, dict) or set(target) != set(expected) or target != expected:
        raise CredentialPreflightError("TARGET_BINDING_MISMATCH")
    if document.get("PermittedOperations") != REQUIRED_PERMISSIONS:
        raise CredentialPreflightError("OPERATION_SCOPE_NOT_EXACT")
    report_path = document.get("PreflightReportPath")
    if (not isinstance(report_path, str)
            or not report_path.startswith("/home/cdjensen94/continuumwiki-validation/")
            or str(Path(report_path)) != report_path
            or any(part in {"", ".", ".."} for part in Path(report_path).parts[1:])):
        raise CredentialPreflightError("REPORT_PATH_INVALID")
    digest = hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return {"ManifestSha256": digest, "ApprovalId": document["ApprovalId"], "TargetRole": role}


def report_path_is_bound(bound_path: Path, report_path: Path) -> bool:
    """Accept only the manifest's exact path or a generated renewal sibling."""
    if report_path == bound_path:
        return True
    if report_path.parent != bound_path.parent or report_path.suffix != ".json":
        return False
    prefix = f"{bound_path.stem}.renewal-"
    return report_path.name.startswith(prefix) and bool(RENEWAL_NAME.search(report_path.name[len(bound_path.stem):]))


def _renewal_report_path(bound_path: Path, *, now: datetime | None = None) -> Path:
    instant = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    stamp = instant.strftime("%Y%m%dT%H%M%SZ")
    return bound_path.with_name(f"{bound_path.stem}.renewal-{stamp}-{secrets.token_hex(16)}.json")


def _write_once(path: Path, report: dict[str, Any]) -> None:
    if not path.is_absolute() or path == ROOT or ROOT in path.parents or any(part in {".", ".."} for part in path.parts):
        raise CredentialPreflightError("PREFLIGHT_REPORT_PATH_UNSAFE")
    try:
        parent = path.parent.lstat()
        if (path.parent.resolve(strict=True) != path.parent or not stat.S_ISDIR(parent.st_mode)
                or stat.S_ISLNK(parent.st_mode) or parent.st_uid != os.getuid()
                or stat.S_IMODE(parent.st_mode) != 0o700):
            raise CredentialPreflightError("PREFLIGHT_REPORT_DIRECTORY_UNSAFE")
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    except FileExistsError:
        raise CredentialPreflightError("PREFLIGHT_REPORT_ALREADY_EXISTS") from None
    except OSError:
        raise CredentialPreflightError("PREFLIGHT_REPORT_IO_FAILED") from None
    try:
        os.fchmod(fd, 0o600)
        info = os.fstat(fd)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
                or stat.S_IMODE(info.st_mode) != 0o600 or info.st_nlink != 1):
            raise CredentialPreflightError("PREFLIGHT_REPORT_PERMISSIONS_INVALID")
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            fd = -1
            json.dump(report, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    finally:
        if fd >= 0:
            os.close(fd)


def preflight(manifest_path: Path, *, renew: bool = False,
              now: datetime | None = None) -> tuple[bool, dict[str, Any]]:
    errors: list[str] = []
    try:
        manifest = _read_private(manifest_path, "MANIFEST_JSON_OR_PERMISSIONS_INVALID")
        validated = validate_manifest(manifest, now=now)
        bound_path = Path(manifest["PreflightReportPath"])
        report_path = _renewal_report_path(bound_path, now=now) if renew else bound_path
        if not report_path_is_bound(bound_path, report_path):
            raise CredentialPreflightError("REPORT_PATH_NOT_BOUND_TO_MANIFEST")
    except CredentialPreflightError as error:
        errors.append(str(error))
        manifest = {}
        validated = {}
    return not errors, {
        "ReportVersion": "cw-m5-026b-inspection-credential-preparation-preflight-v1",
        "GeneratedAtUtc": (now or datetime.now(timezone.utc)).astimezone(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Classification": "CREDENTIAL_PREPARATION_AUTHORIZATION_VALID_NONCONNECTING" if not errors else "CREDENTIAL_PREPARATION_AUTHORIZATION_INVALID",
        "Errors": errors,
        "ManifestSha256": validated.get("ManifestSha256"),
        "ApprovalId": validated.get("ApprovalId"),
        "TargetRole": validated.get("TargetRole"),
        "ReportPath": str(report_path) if validated else None,
        "OperationCounts": {"CredentialValueReads": 0, "CredentialWrites": 0, "DatabaseConnections": 0, "PodmanCommands": 0},
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--renew", action="store_true",
                        help="create a uniquely named sibling preflight report without changing the manifest")
    args = parser.parse_args(argv)
    try:
        valid, report = preflight(Path(args.manifest), renew=args.renew)
        if not valid:
            print("CW-M5-026B credential preparation preflight: classification=CREDENTIAL_PREPARATION_AUTHORIZATION_INVALID; credential_reads=0; credential_writes=0; database_connections=0; podman_commands=0")
            return 3
        _write_once(Path(report["ReportPath"]), report)
    except (OSError, KeyError, CredentialPreflightError) as error:
        code = str(error) if isinstance(error, CredentialPreflightError) else "PREFLIGHT_STOPPED"
        print(f"CW-M5-026B credential preparation preflight stopped: {code}", file=sys.stderr)
        return 2
    print(f"CW-M5-026B credential preparation preflight: classification=CREDENTIAL_PREPARATION_AUTHORIZATION_VALID_NONCONNECTING; credential_reads=0; credential_writes=0; database_connections=0; podman_commands=0; report={report['ReportPath']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
