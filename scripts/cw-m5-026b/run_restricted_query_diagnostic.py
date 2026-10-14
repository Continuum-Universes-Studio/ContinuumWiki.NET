#!/usr/bin/env python3
"""Execute only the two explicitly authorized CW-M5-026B diagnostic queries."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-restricted-query-diagnostic.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_restricted_query_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = PREFLIGHT
SPEC.loader.exec_module(PREFLIGHT)
INSPECTOR = PREFLIGHT.INSPECTOR
QUERY_SQL = {"SOURCE_DATABASE_IDENTITY": INSPECTOR.READINESS.IDENTITY_SQL,
             "DESTINATION_MIGRATION_LEDGER": INSPECTOR.READINESS.LEDGER_SQL}
QUERY_TARGET = {"SOURCE_DATABASE_IDENTITY": "Source", "DESTINATION_MIGRATION_LEDGER": "Destination"}
ERR = re.compile(r"(?:ERROR\s+)?([0-9]{4})\s+\(([0-9A-Z]{5})\):")
SERVER_REACHED = {1044, 1045, 1064, 1142, 1143, 1146, 1227, 1698}
CONNECT_FAILURE = {2002, 2003, 2005}

class DiagnosticRunError(ValueError):
    pass

def _now() -> datetime:
    return datetime.now(timezone.utc)

def _stamp(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")

def _error_details(stderr: str, exit_code: int) -> dict[str, Any]:
    match = ERR.search(stderr[:4096])
    errno = int(match.group(1)) if match else None
    state = match.group(2) if match else None
    if errno in CONNECT_FAILURE:
        category, reached = "CONNECTION_FAILURE", "NO"
    elif errno in SERVER_REACHED:
        category, reached = "SERVER_REJECTED_QUERY", "YES"
    elif errno in {2006, 2013}:
        category, reached = "CONNECTION_INTERRUPTED", "UNKNOWN"
    else:
        category, reached = "CLIENT_FAILURE_UNCLASSIFIED", "UNKNOWN"
    return {"Outcome": category, "ReachedMariaDB": reached, "ClientExitCode": exit_code,
            "MariaDbErrorNumber": errno, "SqlState": state}

def _claim_once(manifest: dict[str, Any], digest: str) -> None:
    directory = Path(manifest["Evidence"]["ManifestPath"]).parent
    try:
        info = directory.lstat()
        if (directory.resolve(strict=True) != directory or not directory.is_dir() or info.st_uid != os.getuid()
                or (info.st_mode & 0o777) != 0o700):
            raise DiagnosticRunError("EVIDENCE_DIRECTORY_CHANGED")
    except OSError:
        raise DiagnosticRunError("EVIDENCE_DIRECTORY_CHANGED") from None
    marker = directory / ".started"
    payload = json.dumps({"ApprovalId": manifest["ApprovalId"], "ManifestSha256": digest, "StartedAtUtc": _stamp(_now())}) + "\n"
    fd = os.open(marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as stream:
        stream.write(payload); stream.flush(); os.fsync(stream.fileno())

def _validate_fresh_preflight(report: dict[str, Any], manifest: dict[str, Any], digest: str, now: datetime) -> dict[str, Any]:
    try:
        created = PREFLIGHT._time(report["GeneratedAtUtc"], "PREFLIGHT_TIMESTAMP_INVALID")
    except (KeyError, PREFLIGHT.BASE.InspectionPreflightError):
        raise DiagnosticRunError("PREFLIGHT_TIMESTAMP_INVALID") from None
    if (report.get("Classification") != "RESTRICTED_QUERY_PREFLIGHT_VALID_NONCONNECTING"
            or report.get("ApprovalId") != manifest["ApprovalId"] or report.get("ManifestSha256") != digest
            or report.get("Queries") != PREFLIGHT.QUERY_BINDINGS
            or report.get("CredentialContentsRead") is not False
            or report.get("PodmanCommands") != 0 or report.get("DatabaseConnections") != 0
            or report.get("DatabaseQueries") != 0
            or (now - created).total_seconds() < 0 or (now - created).total_seconds() > 600):
        raise DiagnosticRunError("PREFLIGHT_NOT_FRESH_OR_NOT_MATCHED")
    return report

def _read_fresh_preflight(path: Path, manifest: dict[str, Any], digest: str, now: datetime) -> dict[str, Any]:
    return _validate_fresh_preflight(PREFLIGHT.BASE._read_private_json(path, "PREFLIGHT_REPORT_INVALID"), manifest, digest, now)

def _execute_query(query_id: str, target: dict[str, Any], option_file: Path, timeout: int) -> dict[str, Any]:
    sql = QUERY_SQL[query_id]
    command = INSPECTOR._db_command(target, option_file, sql)
    try:
        result = subprocess.run(command, shell=False, check=False, capture_output=True, text=True,
                               timeout=timeout, env={"PATH": "/usr/bin:/bin", "HOME": str(Path.home()),
                               "XDG_RUNTIME_DIR": f"/run/user/{os.getuid()}", "LC_ALL": "C"})
    except subprocess.TimeoutExpired:
        return {"QueryId": query_id, "Outcome": "TIMEOUT", "ReachedMariaDB": "UNKNOWN", "ClientExitCode": None, "MariaDbErrorNumber": None, "SqlState": None}
    except OSError:
        return {"QueryId": query_id, "Outcome": "CLIENT_UNAVAILABLE", "ReachedMariaDB": "UNKNOWN", "ClientExitCode": None, "MariaDbErrorNumber": None, "SqlState": None}
    if result.returncode != 0:
        return {"QueryId": query_id, **_error_details(result.stderr if isinstance(result.stderr, str) else "", result.returncode)}
    if not isinstance(result.stdout, str) or len(result.stdout) > 1_000_000:
        return {"QueryId": query_id, "Outcome": "RESPONSE_INVALID_OR_TOO_LARGE", "ReachedMariaDB": "YES", "ClientExitCode": result.returncode, "MariaDbErrorNumber": None, "SqlState": None}
    if query_id == "SOURCE_DATABASE_IDENTITY":
        status = INSPECTOR._identity_status(result.stdout, target)
        return {"QueryId": query_id, "Outcome": status, "ReachedMariaDB": "YES", "ClientExitCode": 0,
                "MariaDbErrorNumber": None, "SqlState": None}
    try:
        rows = INSPECTOR._rows(result.stdout, 2, "LEDGER_RESPONSE_MALFORMED", 32)
        expected = INSPECTOR._expected_schema()["Ledgers"]
        actual: dict[str, list[int]] = {}
        for domain, version in rows:
            if not version.isdigit():
                raise ValueError
            actual.setdefault(domain, []).append(int(version))
        missing = sorted(set(expected) - set(actual))
        unexpected = sorted(set(actual) - set(expected))
        mismatched = sorted(domain for domain in set(expected) & set(actual)
                            if actual[domain] != [expected[domain]])
        outcome = "LEDGER_MATCH" if not missing and not unexpected and not mismatched else "LEDGER_MISMATCH"
        return {"QueryId": query_id, "Outcome": outcome, "ReachedMariaDB": "YES", "ClientExitCode": 0,
                "MariaDbErrorNumber": None, "SqlState": None, "ExpectedDomainCount": len(expected),
                "ObservedRowCount": len(rows), "MissingDomains": missing, "UnexpectedDomains": unexpected,
                "MismatchedDomains": mismatched}
    except (ValueError, INSPECTOR.InspectionError):
        return {"QueryId": query_id, "Outcome": "LEDGER_RESPONSE_MALFORMED", "ReachedMariaDB": "YES", "ClientExitCode": 0,
                "MariaDbErrorNumber": None, "SqlState": None}

def run_diagnostic(manifest: dict[str, Any], preflight_report: dict[str, Any], *, now: datetime | None = None) -> dict[str, Any]:
    instant = now or _now()
    validated = PREFLIGHT.validate_manifest(manifest, Path(manifest["Evidence"]["ManifestPath"]), now=instant)
    digest = validated["ManifestSha256"]
    _validate_fresh_preflight(preflight_report, manifest, digest, instant)
    _claim_once(manifest, digest)
    report: dict[str, Any] = {"ReportVersion": PREFLIGHT.CONTRACT + "-result", "GeneratedAtUtc": _stamp(instant),
        "Classification": "RESTRICTED_QUERY_DIAGNOSTIC_INCOMPLETE", "ApprovalId": manifest["ApprovalId"],
        "ManifestSha256": digest, "Queries": [], "ContainerIdentity": "NOT_VERIFIED",
        "Sanitization": {"RawSqlOutputPersisted": False, "RawClientStderrPersisted": False, "CredentialContentsRead": False},
        "DatabaseMutations": 0, "Retries": 0, "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"}
    started = time.monotonic()
    deadline = started + 600
    def bounded_host_run(command: list[str]) -> subprocess.CompletedProcess[str]:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise INSPECTOR.InspectionError("OVERALL_TIMEOUT")
        try:
            return subprocess.run(command, shell=False, check=False, capture_output=True, text=True,
                                  timeout=min(INSPECTOR.MAX_SECONDS, max(1, int(remaining))),
                                  env={"PATH": "/usr/bin:/bin", "HOME": str(Path.home()),
                                       "XDG_RUNTIME_DIR": f"/run/user/{os.getuid()}", "LC_ALL": "C"})
        except subprocess.TimeoutExpired:
            raise INSPECTOR.InspectionError("HOST_IDENTITY_CHECK_TIMEOUT") from None
        except OSError:
            raise INSPECTOR.InspectionError("HOST_IDENTITY_CHECK_FAILED") from None
    try:
        host = INSPECTOR._stdout(bounded_host_run([INSPECTOR.PODMAN, "info", "--format", "{{.Host.Security.Rootless}}"]), "PODMAN_ROOTLESS_CHECK_FAILED")
        if host.casefold() != "true":
            raise INSPECTOR.InspectionError("PODMAN_NOT_ROOTLESS")
        for role in ("Source", "Destination"):
            bound = dict(PREFLIGHT._targets()[role])
            bound["CredentialDirectory"] = str(Path(bound["CredentialOptionFile"]).parent)
            INSPECTOR._inspect_target_host(bound, bounded_host_run)
        report["ContainerIdentity"] = "BOTH_EXACT_TARGETS_RUNNING_IDENTITY_MATCH"
    except (INSPECTOR.InspectionError, OSError) as error:
        report["Blocker"] = str(error) if isinstance(error, INSPECTOR.InspectionError) else "HOST_IDENTITY_CHECK_FAILED"
        return report
    results = []
    for binding in PREFLIGHT.QUERY_BINDINGS:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            results.append({"QueryId": binding["QueryId"], "Outcome": "OVERALL_TIMEOUT", "ReachedMariaDB": "UNKNOWN", "ClientExitCode": None, "MariaDbErrorNumber": None, "SqlState": None})
            break
        role = QUERY_TARGET[binding["QueryId"]]
        target = dict(PREFLIGHT._targets()[role])
        target["CredentialDirectory"] = str(Path(target["CredentialOptionFile"]).parent)
        credential = INSPECTOR._check_credential_option_file(target, 1000)
        timeout = min(60, max(1, int(remaining)))
        results.append(_execute_query(binding["QueryId"], target, credential, timeout))
    report["Queries"] = results
    report["Classification"] = "RESTRICTED_QUERY_DIAGNOSTIC_COMPLETED" if len(results) == 2 and all(item["Outcome"] in {"MATCH", "LEDGER_MATCH"} for item in results) else "RESTRICTED_QUERY_DIAGNOSTIC_QUERY_FAILURE"
    report["ElapsedSeconds"] = round(time.monotonic() - started, 3)
    return report

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True); parser.add_argument("--preflight-report", required=True)
    parser.add_argument("--execute-authorized-diagnostic", action="store_true")
    args = parser.parse_args(argv)
    if not args.execute_authorized_diagnostic:
        print("Diagnostic not executed; explicit execution flag required after fresh preflight.", file=sys.stderr); return 2
    try:
        manifest_path, preflight_path = Path(args.manifest), Path(args.preflight_report)
        manifest = PREFLIGHT.BASE._read_private_json(manifest_path, "MANIFEST_JSON_INVALID")
        fresh = _read_fresh_preflight(preflight_path, manifest, PREFLIGHT.validate_manifest(manifest, manifest_path)["ManifestSha256"], _now())
        if preflight_path != Path(manifest["Evidence"]["PreflightReportPath"]):
            raise DiagnosticRunError("PREFLIGHT_PATH_MISMATCH")
        result = run_diagnostic(manifest, fresh)
        PREFLIGHT.BASE._write_once(Path(manifest["Evidence"]["ResultReportPath"]), result)
    except (OSError, KeyError, TypeError, DiagnosticRunError, PREFLIGHT.DiagnosticPreflightError, PREFLIGHT.BASE.InspectionPreflightError) as error:
        code = str(error) if isinstance(error, (DiagnosticRunError, PREFLIGHT.DiagnosticPreflightError, PREFLIGHT.BASE.InspectionPreflightError)) else "DIAGNOSTIC_STOPPED"
        print("Restricted query diagnostic stopped: " + code, file=sys.stderr); return 3
    print("Restricted query diagnostic: " + result["Classification"]); return 0 if result["Classification"] == "RESTRICTED_QUERY_DIAGNOSTIC_COMPLETED" else 4

if __name__ == "__main__":
    raise SystemExit(main())
