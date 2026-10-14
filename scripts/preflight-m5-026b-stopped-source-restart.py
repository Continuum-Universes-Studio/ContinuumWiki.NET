#!/usr/bin/env python3
"""Nonconnecting preflight for one authorized start of the retained source."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import stat
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
VALIDATION_ROOT = Path("/home/cdjensen94/continuumwiki-validation")
CONTRACT = "cw-m5-026b-stopped-source-restart-authorization-v1"
PREFLIGHT_CONTRACT = "cw-m5-026b-stopped-source-restart-preflight-v1"
PREFLIGHT_CLASSIFICATION = "CATEGORY_STOPPED_SOURCE_RESTART_AUTHORIZATION_VALID_NONCONNECTING"
TARGET_ROLE = "Source"
MAX_WINDOW = timedelta(hours=24)
APPROVAL = re.compile(r"^CW-M5-026B-RECOVERY-START-[A-Za-z0-9._:-]{1,80}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")
PODMAN = "/usr/bin/podman"

SPEC = importlib.util.spec_from_file_location(
    "m5_026b_stopped_source_diagnostic_preflight",
    ROOT / "scripts/preflight-m5-026b-stopped-source-diagnostic.py",
)
assert SPEC and SPEC.loader
DIAGNOSTIC_PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DIAGNOSTIC_PREFLIGHT)

AUTHORIZED_TARGET = dict(DIAGNOSTIC_PREFLIGHT.AUTHORIZED_TARGET)
EXPECTED_DIAGNOSTIC_CLASSIFICATION = "SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH"
OPS = {
    "StartStoppedSourceContainerOnce": True,
    "ContainerCreation": False,
    "ContainerRecreation": False,
    "ContainerRestart": False,
    "ImageOrVolumeChanges": False,
    "DatabaseConnections": False,
    "DatabaseMutations": False,
    "Migrations": False,
    "GrantChanges": False,
    "FixtureWrites": False,
    "Backup": False,
    "Restore": False,
    "Cleanup": False,
    "ContainerVolumeDeletion": False,
    "ProductionAccess": False,
}


class PreflightError(ValueError):
    pass


def _utc(value: Any) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise PreflightError("AUTHORIZATION_TIME_INVALID")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError:
        raise PreflightError("AUTHORIZATION_TIME_INVALID") from None
    return parsed.astimezone(timezone.utc)


def _canonical_hash(value: dict[str, Any]) -> str:
    return hashlib.sha256(
        json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    ).hexdigest()


def _private_json(path: Path, unavailable: str) -> dict[str, Any]:
    try:
        info, parent = path.lstat(), path.parent.lstat()
        if (
            not path.is_absolute()
            or path.resolve(strict=True) != path
            or ROOT in path.parents
            or not stat.S_ISREG(info.st_mode)
            or stat.S_ISLNK(info.st_mode)
            or info.st_uid != os.getuid()
            or stat.S_IMODE(info.st_mode) != 0o600
            or not stat.S_ISDIR(parent.st_mode)
            or stat.S_ISLNK(parent.st_mode)
            or parent.st_uid != os.getuid()
            or stat.S_IMODE(parent.st_mode) != 0o700
        ):
            raise PreflightError(unavailable)
        value = json.loads(path.read_text(encoding="utf-8"))
    except PreflightError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise PreflightError(unavailable) from None
    if not isinstance(value, dict):
        raise PreflightError(unavailable)
    return value


def _safe_validation_path(path: Path, code: str) -> None:
    if (
        not path.is_absolute()
        or path.resolve(strict=True) != path
        or VALIDATION_ROOT not in path.parents
        or ROOT in path.parents
    ):
        raise PreflightError(code)


def _diagnostic_is_eligible(report: dict[str, Any]) -> None:
    required = {
        "ReportVersion", "Classification", "TargetRole", "ExpectedContainerId",
        "ObservedContainerIdSha256", "ObservedState", "ObservedExitCode",
        "PinnedImageIdentityMatched", "ContainerIdMatched", "RunIdLabelMatched",
        "DatabaseLabelMatched", "VolumeMountMatched", "EndpointMatched",
        "VolumeIdentityMatched", "Findings", "RawPodmanOutputPersisted",
        "StderrPersisted", "DatabaseConnections", "DatabaseMutations",
        "CredentialsRead", "PodmanCommandsAreReadOnly", "PodmanReadOnlyCommands",
        "AuthorizationApprovalId", "ManifestSha256", "PreflightReportSha256",
        "GeneratedAtUtc",
    }
    if (
        not required.issubset(report)
        or report.get("ReportVersion") != DIAGNOSTIC_PREFLIGHT.DIAG.REPORT_VERSION
        or report.get("Classification") != EXPECTED_DIAGNOSTIC_CLASSIFICATION
        or report.get("TargetRole") != DIAGNOSTIC_PREFLIGHT.DIAG.TARGET_ROLE
        or report.get("ExpectedContainerId") != AUTHORIZED_TARGET["ContainerId"]
        or report.get("ObservedState") not in {"exited", "stopped"}
        or report.get("ObservedExitCode") is None
        or report.get("PinnedImageIdentityMatched") is not True
        or report.get("ContainerIdMatched") is not True
        or report.get("RunIdLabelMatched") is not True
        or report.get("DatabaseLabelMatched") is not True
        or report.get("VolumeMountMatched") is not True
        or report.get("EndpointMatched") is not True
        or report.get("VolumeIdentityMatched") is not True
        or report.get("RawPodmanOutputPersisted") is not False
        or report.get("StderrPersisted") is not False
        or report.get("DatabaseConnections") != 0
        or report.get("DatabaseMutations") != 0
        or report.get("CredentialsRead") != 0
        or report.get("PodmanCommandsAreReadOnly") is not True
        or not isinstance(report.get("PodmanReadOnlyCommands"), int)
        or report["PodmanReadOnlyCommands"] < 1
        or not isinstance(report.get("ManifestSha256"), str)
        or not HEX64.fullmatch(report["ManifestSha256"])
    ):
        raise PreflightError("DIAGNOSTIC_REPORT_NOT_START_ELIGIBLE")


def _read_eligible_diagnostic(path: Path) -> tuple[dict[str, Any], str]:
    report = _private_json(path, "DIAGNOSTIC_REPORT_UNAVAILABLE")
    _diagnostic_is_eligible(report)
    try:
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
    except OSError:
        raise PreflightError("DIAGNOSTIC_REPORT_UNAVAILABLE") from None
    return report, digest


def validate(
    document: Any,
    diagnostic_report_path: Path,
    now: datetime | None = None,
) -> dict[str, str]:
    if not isinstance(document, dict):
        raise PreflightError("MANIFEST_ROOT_INVALID")
    fields = {
        "ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc",
        "Approver", "Operator", "TargetOwner", "ExecutionHost", "Target",
        "DiagnosticClassification", "DiagnosticReportSha256", "PermittedOperations",
        "ResultReportPath",
    }
    if set(document) != fields or document.get("ManifestVersion") != CONTRACT:
        raise PreflightError("MANIFEST_SHAPE_OR_VERSION_INVALID")
    if not isinstance(document["ApprovalId"], str) or not APPROVAL.fullmatch(document["ApprovalId"]):
        raise PreflightError("APPROVAL_ID_INVALID")
    approved, expires = _utc(document["ApprovedAtUtc"]), _utc(document["ExpiresAtUtc"])
    instant = now or datetime.now(timezone.utc)
    if approved > instant or expires <= instant:
        raise PreflightError("AUTHORIZATION_NOT_CURRENT")
    if expires <= approved or expires - approved > MAX_WINDOW:
        raise PreflightError("AUTHORIZATION_WINDOW_INVALID")
    for key in ("Approver", "Operator"):
        person = document[key]
        if (
            not isinstance(person, dict)
            or set(person) != {"Name", "Role"}
            or not all(isinstance(person.get(k), str) and person[k].strip() for k in ("Name", "Role"))
        ):
            raise PreflightError("APPROVAL_IDENTITY_INCOMPLETE")
    if not isinstance(document["TargetOwner"], str) or not document["TargetOwner"].strip():
        raise PreflightError("TARGET_OWNER_INCOMPLETE")
    if document["ExecutionHost"] != {
        "HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000,
        "RootlessPodman": True,
    }:
        raise PreflightError("AUTHORIZED_HOST_MISMATCH")
    if document["Target"] != AUTHORIZED_TARGET:
        raise PreflightError("TARGET_BINDING_MISMATCH")
    if document["DiagnosticClassification"] != EXPECTED_DIAGNOSTIC_CLASSIFICATION:
        raise PreflightError("DIAGNOSTIC_CLASSIFICATION_MISMATCH")
    if not isinstance(document["DiagnosticReportSha256"], str) or not HEX64.fullmatch(document["DiagnosticReportSha256"]):
        raise PreflightError("DIAGNOSTIC_REPORT_HASH_INVALID")
    if document["PermittedOperations"] != OPS:
        raise PreflightError("OPERATION_SCOPE_MISMATCH")
    result = document["ResultReportPath"]
    result_path = Path(result) if isinstance(result, str) else Path(".")
    if (
        not isinstance(result, str)
        or VALIDATION_ROOT not in result_path.parents
        or any(part in {"", ".", ".."} for part in result_path.parts[1:])
    ):
        raise PreflightError("REPORT_PATH_UNSAFE")
    _safe_validation_path(diagnostic_report_path, "DIAGNOSTIC_REPORT_PATH_UNSAFE")
    diagnostic, digest = _read_eligible_diagnostic(diagnostic_report_path)
    if digest != document["DiagnosticReportSha256"]:
        raise PreflightError("DIAGNOSTIC_REPORT_HASH_MISMATCH")
    if diagnostic["ExpectedContainerId"] != document["Target"]["ContainerId"]:
        raise PreflightError("DIAGNOSTIC_TARGET_MISMATCH")
    return {
        "ApprovalId": document["ApprovalId"],
        "ManifestSha256": _canonical_hash(document),
        "DiagnosticReportSha256": digest,
        "ExpiresAtUtc": document["ExpiresAtUtc"],
        "DiagnosticReportPath": str(diagnostic_report_path),
        "ResultReportPath": result,
    }


def _write_once(path: Path, data: dict[str, Any]) -> None:
    try:
        if (
            not path.is_absolute()
            or path.resolve(strict=False) != path
            or VALIDATION_ROOT not in path.parents
            or ROOT in path.parents
        ):
            raise PreflightError("PREFLIGHT_REPORT_PATH_UNSAFE")
        parent = path.parent
        info = parent.lstat()
        if (
            not stat.S_ISDIR(info.st_mode)
            or stat.S_ISLNK(info.st_mode)
            or info.st_uid != os.getuid()
            or stat.S_IMODE(info.st_mode) != 0o700
        ):
            raise PreflightError("PREFLIGHT_REPORT_DIRECTORY_UNSAFE")
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream, sort_keys=True, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError:
        raise PreflightError("PREFLIGHT_REPORT_ALREADY_EXISTS") from None


def run(manifest: Path, diagnostic_report: Path, report: Path, now: datetime | None = None) -> tuple[bool, dict[str, Any]]:
    errors: list[str] = []
    validated: dict[str, str] = {}
    try:
        validated = validate(_private_json(manifest, "MANIFEST_JSON_INVALID"), diagnostic_report, now)
    except PreflightError as error:
        errors.append(str(error))
    data: dict[str, Any] = {
        "ReportVersion": PREFLIGHT_CONTRACT,
        "GeneratedAtUtc": (now or datetime.now(timezone.utc)).isoformat().replace("+00:00", "Z"),
        "Classification": PREFLIGHT_CLASSIFICATION if not errors else PREFLIGHT_CLASSIFICATION.replace("_VALID_NONCONNECTING", "_INVALID"),
        "Errors": errors,
        **validated,
        "ResourceOperationCounts": {"Podman": 0, "DatabaseConnections": 0, "DatabaseMutations": 0, "CredentialReads": 0},
    }
    _write_once(report, data)
    return not errors, data


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--diagnostic-report", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    try:
        ok, data = run(Path(args.manifest), Path(args.diagnostic_report), Path(args.report))
        print(f"{data['Classification']}; errors={len(data['Errors'])}; podman=0; database_connections=0; database_mutations=0; credential_reads=0")
        return 0 if ok else 2
    except PreflightError as error:
        print(f"CATEGORY_STOPPED_SOURCE_RESTART_PREFLIGHT_FAILED: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
