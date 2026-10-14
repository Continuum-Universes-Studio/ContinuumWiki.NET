#!/usr/bin/env python3
"""Nonconnecting authorization and credential-metadata preflight for the fixed two-query diagnostic."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import socket
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
CONTRACT = "cw-m5-026b-restricted-query-diagnostic-v1"
SCHEMA_PATH = ROOT / "docs/operations/cw-m5-026b-restricted-query-diagnostic-authorization.schema.json"
VALIDATION_ROOT = Path("/home/cdjensen94/continuumwiki-validation")
INSPECTION_PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-inspection.py"
INSPECTOR_PATH = ROOT / "scripts/cw-m5-026b/inspect_readiness.py"

def _load_module(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    if not spec or not spec.loader:
        raise ValueError("IMPLEMENTATION_UNAVAILABLE")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module

BASE = _load_module("cw_m5_026b_inspection_preflight_for_query_diag", INSPECTION_PREFLIGHT_PATH)
INSPECTOR = _load_module("cw_m5_026b_inspector_for_query_diag", INSPECTOR_PATH)
QUERY_BINDINGS = [{"QueryId": "SOURCE_DATABASE_IDENTITY", "TargetRole": "Source"},
                  {"QueryId": "DESTINATION_MIGRATION_LEDGER", "TargetRole": "Destination"}]

class DiagnosticPreflightError(ValueError):
    pass

def _schema_validate(document: Any) -> None:
    try:
        from jsonschema import Draft202012Validator
    except ImportError:
        raise DiagnosticPreflightError("JSON_SCHEMA_VALIDATOR_UNAVAILABLE") from None
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    errors = list(Draft202012Validator(schema).iter_errors(document))
    if errors:
        raise DiagnosticPreflightError("AUTHORIZATION_SCHEMA_INVALID")

def _targets() -> dict[str, dict[str, Any]]:
    targets = BASE.expected_targets()
    result = {}
    for role, expected in targets.items():
        target = {key: expected[key] for key in ("ContainerName", "ContainerId", "VolumeName", "DatabaseName", "Endpoint", "ImageReference", "ProvisioningRunId", "DatabaseIdentity")}
        target["CredentialOptionFile"] = expected["CredentialDirectory"] + "/inspection-client.cnf"
        result[role] = target
    return result

def _time(value: Any, code: str) -> datetime:
    return BASE._utc(value, code)

def validate_manifest(document: Any, manifest_path: Path, *, now: datetime | None = None) -> dict[str, Any]:
    _schema_validate(document)
    instant = now or datetime.now(timezone.utc)
    approved, expires = _time(document["ApprovedAtUtc"], "APPROVAL_TIME_INVALID"), _time(document["ExpiresAtUtc"], "EXPIRY_TIME_INVALID")
    if approved > instant or expires <= instant or expires <= approved or expires - approved > timedelta(hours=24):
        raise DiagnosticPreflightError("AUTHORIZATION_NOT_CURRENT_OR_WINDOW_INVALID")
    if (document["ExecutionHost"] != {"HostName": socket.gethostname(), "UnixUser": "cdjensen94", "Uid": os.getuid(), "RootlessPodman": True}
            or socket.gethostname() != "christian-laptop" or os.getuid() != 1000):
        raise DiagnosticPreflightError("EXECUTION_HOST_IDENTITY_MISMATCH")
    if document["ApprovalId"] == "CW-M5-026B-QUERY-DIAG-20261014-001":
        raise DiagnosticPreflightError("KNOWN_UNUSED_DIAGNOSTIC_APPROVAL_NOT_EXECUTION_AUTHORITY")
    expected_targets = _targets()
    if document["Targets"] != expected_targets:
        raise DiagnosticPreflightError("EXACT_TARGET_BINDING_MISMATCH")
    if document["AuthorizedQueries"] != QUERY_BINDINGS:
        raise DiagnosticPreflightError("QUERY_ALLOWLIST_BINDING_MISMATCH")
    evidence = document["Evidence"]
    if Path(evidence["ManifestPath"]) != manifest_path or Path(evidence["ManifestPath"]).parent != Path(evidence["PreflightReportPath"]).parent or Path(evidence["ManifestPath"]).parent != Path(evidence["ResultReportPath"]).parent:
        raise DiagnosticPreflightError("EVIDENCE_PATH_BINDING_MISMATCH")
    directory = Path(evidence["ManifestPath"]).parent
    if directory.name != "cw-m5-026b-query-diagnostic-" + document["ApprovalId"]:
        raise DiagnosticPreflightError("EVIDENCE_DIRECTORY_APPROVAL_MISMATCH")
    if document["Limits"] != {"PerQueryTimeoutSeconds": 60, "OverallTimeoutSeconds": 600}:
        raise DiagnosticPreflightError("EXECUTION_LIMITS_INVALID")
    digest = hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return {"ApprovalId": document["ApprovalId"], "ManifestSha256": digest, "ExpiresAtUtc": document["ExpiresAtUtc"]}

def check_credentials(document: dict[str, Any]) -> None:
    expected = _targets()
    for role in ("Source", "Destination"):
        target = dict(expected[role])
        target["CredentialDirectory"] = str(Path(expected[role]["CredentialOptionFile"]).parent)
        target["CredentialOptionFile"] = expected[role]["CredentialOptionFile"]
        INSPECTOR._check_credential_option_file(target, 1000)

def check_unique_and_create_only(document: dict[str, Any]) -> None:
    evidence = document["Evidence"]
    directory = Path(evidence["ManifestPath"]).parent
    if not directory.is_absolute() or directory.resolve(strict=True) != directory:
        raise DiagnosticPreflightError("EVIDENCE_DIRECTORY_NOT_CANONICAL")
    info = directory.lstat()
    if info.st_uid != os.getuid() or (info.st_mode & 0o777) != 0o700 or not directory.is_dir():
        raise DiagnosticPreflightError("EVIDENCE_DIRECTORY_PERMISSIONS_INVALID")
    for key in ("PreflightReportPath", "ResultReportPath"):
        if os.path.lexists(evidence[key]):
            raise DiagnosticPreflightError("EVIDENCE_PATH_ALREADY_EXISTS")
    current_manifest = Path(evidence["ManifestPath"])
    for candidate in VALIDATION_ROOT.glob("cw-m5-026b-query-diagnostic-*/authorization.json"):
        if candidate == current_manifest:
            continue
        try:
            prior = json.loads(candidate.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError):
            continue
        if isinstance(prior, dict) and prior.get("ApprovalId") == document["ApprovalId"]:
            raise DiagnosticPreflightError("APPROVAL_ID_ALREADY_RECORDED")
    if (directory / ".started").exists():
        raise DiagnosticPreflightError("AUTHORIZATION_ALREADY_CONSUMED")

def preflight(manifest_path: Path, report_path: Path, *, now: datetime | None = None) -> dict[str, Any]:
    document = BASE._read_private_json(manifest_path, "MANIFEST_JSON_INVALID")
    validated = validate_manifest(document, manifest_path, now=now)
    check_credentials(document)
    check_unique_and_create_only(document)
    if report_path != Path(document["Evidence"]["PreflightReportPath"]):
        raise DiagnosticPreflightError("PREFLIGHT_OUTPUT_PATH_MISMATCH")
    report = {"ReportVersion": CONTRACT + "-preflight", "GeneratedAtUtc": (now or datetime.now(timezone.utc)).astimezone(timezone.utc).isoformat().replace("+00:00", "Z"), "Classification": "RESTRICTED_QUERY_PREFLIGHT_VALID_NONCONNECTING", **validated, "Queries": QUERY_BINDINGS, "CredentialContentsRead": False, "PodmanCommands": 0, "DatabaseConnections": 0, "DatabaseQueries": 0}
    BASE._write_once(report_path, report)
    return report

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True); parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    try:
        report = preflight(Path(args.manifest), Path(args.report))
    except (OSError, KeyError, TypeError, DiagnosticPreflightError, BASE.InspectionPreflightError, INSPECTOR.InspectionError) as error:
        code = str(error) if isinstance(error, (DiagnosticPreflightError, BASE.InspectionPreflightError, INSPECTOR.InspectionError)) else "PREFLIGHT_STOPPED"
        print("Restricted query preflight stopped: " + code, file=sys.stderr); return 2
    print("Restricted query preflight: " + report["Classification"]); return 0

if __name__ == "__main__":
    raise SystemExit(main())
