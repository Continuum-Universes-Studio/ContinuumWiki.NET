#!/usr/bin/env python3
"""Nonconnecting preflight for the fixed CW-M5-026B host-connectivity diagnostic."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pwd
import socket
import stat
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "cw-m5-026b-host-connectivity-v1"
SCHEMA_PATH = ROOT / "docs/operations/cw-m5-026b-host-connectivity-authorization.schema.json"
VALIDATION_ROOT = Path("/home/cdjensen94/continuumwiki-validation")
DESTINATION_BINDING = VALIDATION_ROOT / "cw-m5-026b-destination-identity-binding-20261013-001/binding.json"
BASE_PATH = ROOT / "scripts/preflight-m5-026b-inspection.py"
NON_EXECUTABLE_PRIOR_APPROVAL = "CW-M5-026B-CONNECTIVITY-20261014-001"


def _load_base():
    spec = importlib.util.spec_from_file_location("cw_m5_026b_base_for_host_connectivity", BASE_PATH)
    if not spec or not spec.loader:
        raise RuntimeError("EXISTING_EVIDENCE_HELPERS_UNAVAILABLE")
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


BASE = _load_base()


class ConnectivityPreflightError(ValueError):
    pass


def _schema_validate(document: Any) -> None:
    try:
        from jsonschema import Draft202012Validator
    except ImportError:
        raise ConnectivityPreflightError("JSON_SCHEMA_VALIDATOR_UNAVAILABLE") from None
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    if list(Draft202012Validator(schema).iter_errors(document)):
        raise ConnectivityPreflightError("AUTHORIZATION_SCHEMA_INVALID")


def _utc(value: Any, code: str) -> datetime:
    try:
        return BASE._utc(value, code)
    except BASE.InspectionPreflightError:
        raise ConnectivityPreflightError(code) from None


def _expected_targets() -> dict[str, dict[str, Any]]:
    # Reuse the repository's provisioning-derived target comparison, but pin
    # its destination-specific RunId evidence rather than accepting an env path.
    old = os.environ.get("CW_M5_026B_DESTINATION_BINDING_PATH")
    os.environ["CW_M5_026B_DESTINATION_BINDING_PATH"] = str(DESTINATION_BINDING)
    try:
        records = BASE.expected_targets()
    except (BASE.InspectionPreflightError, OSError):
        raise ConnectivityPreflightError("RETAINED_TARGET_EVIDENCE_INVALID") from None
    finally:
        if old is None:
            os.environ.pop("CW_M5_026B_DESTINATION_BINDING_PATH", None)
        else:
            os.environ["CW_M5_026B_DESTINATION_BINDING_PATH"] = old
    return {role: {key: records[role][key] for key in ("ContainerName", "ContainerId", "VolumeName", "DatabaseName", "Endpoint", "ImageReference", "ProvisioningRunId")} for role in ("Source", "Destination")}


def validate_manifest(document: Any, manifest_path: Path, *, now: datetime | None = None,
                      allow_existing_preflight: bool = False) -> dict[str, Any]:
    _schema_validate(document)
    instant = now or datetime.now(timezone.utc)
    approved = _utc(document["ApprovedAtUtc"], "APPROVAL_TIME_INVALID")
    expires = _utc(document["ExpiresAtUtc"], "EXPIRY_TIME_INVALID")
    if approved > instant or expires <= instant or expires <= approved or expires - approved > timedelta(hours=24):
        raise ConnectivityPreflightError("AUTHORIZATION_NOT_CURRENT_OR_WINDOW_INVALID")
    try:
        user_name = pwd.getpwuid(os.getuid()).pw_name
    except KeyError:
        user_name = ""
    if socket.gethostname() != "christian-laptop" or os.getuid() != 1000 or user_name != "cdjensen94":
        raise ConnectivityPreflightError("EXECUTION_HOST_IDENTITY_MISMATCH")
    if document["ApprovalId"] == NON_EXECUTABLE_PRIOR_APPROVAL:
        raise ConnectivityPreflightError("PRIOR_APPROVAL_NOT_EXECUTION_AUTHORITY")
    if document["ExecutionHost"] != {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True}:
        raise ConnectivityPreflightError("AUTHORIZED_HOST_IDENTITY_MISMATCH")
    expected = _expected_targets()
    if document["Targets"] != {role: {key: expected[role][key] for key in ("ContainerName", "ContainerId", "ProvisioningRunId", "Endpoint")} for role in ("Source", "Destination")}:
        raise ConnectivityPreflightError("EXACT_TARGET_BINDING_MISMATCH")
    if expected["Source"]["ProvisioningRunId"] == expected["Destination"]["ProvisioningRunId"]:
        raise ConnectivityPreflightError("TARGET_RUN_IDS_NOT_DISTINCT")
    evidence = document["Evidence"]
    root = VALIDATION_ROOT.resolve()
    paths = [Path(evidence[key]) for key in ("ManifestPath", "PreflightReportPath", "ResultReportPath")]
    if paths[0] != manifest_path or any(path.parent != paths[0].parent for path in paths) or paths[0].parent.name != "cw-m5-026b-connectivity-" + document["ApprovalId"]:
        raise ConnectivityPreflightError("EVIDENCE_PATH_BINDING_MISMATCH")
    directory = paths[0].parent
    if not directory.is_absolute() or not directory.exists() or directory.resolve(strict=True) != directory or root not in directory.parents:
        raise ConnectivityPreflightError("EVIDENCE_DIRECTORY_NOT_CANONICAL")
    info = directory.lstat()
    if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != 1000 or stat.S_IMODE(info.st_mode) != 0o700:
        raise ConnectivityPreflightError("EVIDENCE_DIRECTORY_PERMISSIONS_INVALID")
    manifest_info = paths[0].lstat()
    if not stat.S_ISREG(manifest_info.st_mode) or stat.S_ISLNK(manifest_info.st_mode) or manifest_info.st_uid != 1000 or stat.S_IMODE(manifest_info.st_mode) != 0o600:
        raise ConnectivityPreflightError("MANIFEST_PERMISSIONS_INVALID")
    if os.path.lexists(paths[2]) or os.path.lexists(directory / ".started"):
        raise ConnectivityPreflightError("EVIDENCE_PATH_ALREADY_USED")
    if os.path.lexists(paths[1]):
        if not allow_existing_preflight:
            raise ConnectivityPreflightError("EVIDENCE_PATH_ALREADY_USED")
        preflight_info = paths[1].lstat()
        if (not stat.S_ISREG(preflight_info.st_mode) or stat.S_ISLNK(preflight_info.st_mode)
                or preflight_info.st_uid != 1000 or stat.S_IMODE(preflight_info.st_mode) != 0o600):
            raise ConnectivityPreflightError("PREFLIGHT_REPORT_PERMISSIONS_INVALID")
    for prior_path in VALIDATION_ROOT.glob("cw-m5-026b-connectivity-*/authorization.json"):
        if prior_path == paths[0]:
            continue
        try:
            prior = json.loads(prior_path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError):
            continue
        if isinstance(prior, dict) and prior.get("ApprovalId") == document["ApprovalId"]:
            raise ConnectivityPreflightError("APPROVAL_ID_ALREADY_RECORDED")
    digest = hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return {"ApprovalId": document["ApprovalId"], "ManifestSha256": digest, "ExpiresAtUtc": document["ExpiresAtUtc"]}


def preflight(manifest_path: Path, report_path: Path, *, now: datetime | None = None) -> dict[str, Any]:
    try:
        document = BASE._read_private_json(manifest_path, "MANIFEST_JSON_INVALID")
    except BASE.InspectionPreflightError:
        raise ConnectivityPreflightError("MANIFEST_JSON_INVALID") from None
    validated = validate_manifest(document, manifest_path, now=now)
    if report_path != Path(document["Evidence"]["PreflightReportPath"]):
        raise ConnectivityPreflightError("PREFLIGHT_OUTPUT_PATH_MISMATCH")
    report = {"ReportVersion": CONTRACT + "-preflight", "GeneratedAtUtc": (now or datetime.now(timezone.utc)).astimezone(timezone.utc).isoformat().replace("+00:00", "Z"), "Classification": "HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING", **validated, "Targets": document["Targets"], "PodmanCommands": 0, "TcpConnections": 0, "DatabaseConnections": 0, "CredentialsRead": False}
    try:
        BASE._write_once(report_path, report)
    except (OSError, BASE.InspectionPreflightError):
        raise ConnectivityPreflightError("PREFLIGHT_EVIDENCE_CREATE_FAILED") from None
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    try:
        report = preflight(Path(args.manifest), Path(args.report))
    except (OSError, KeyError, TypeError, ConnectivityPreflightError) as error:
        code = str(error) if isinstance(error, ConnectivityPreflightError) else "PREFLIGHT_STOPPED"
        print("Host-connectivity preflight stopped: " + code, file=sys.stderr)
        return 2
    print("Host-connectivity preflight: " + report["Classification"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
