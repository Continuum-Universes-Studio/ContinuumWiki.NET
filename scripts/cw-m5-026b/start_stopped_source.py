#!/usr/bin/env python3
"""Run one separately authorized start of the exact retained source."""
from __future__ import annotations

import argparse
import getpass
import hashlib
import importlib.util
import json
import os
import socket
import stat
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[2]
REPORT_ROOT = Path("/home/cdjensen94/continuumwiki-validation")
TIMEOUT = 20
HOST_USER = "cdjensen94"
HOST_UID = 1000
START_REPORT_VERSION = "cw-m5-026b-stopped-source-start-v1"


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PREFLIGHT = _load(
    "m5_026b_stopped_source_restart_preflight",
    ROOT / "scripts/preflight-m5-026b-stopped-source-restart.py",
)
PODMAN = PREFLIGHT.PODMAN


class StartError(ValueError):
    pass


def verify_execution_host() -> None:
    if (
        socket.gethostname().split(".", 1)[0] != "christian-laptop"
        or os.getuid() != HOST_UID
        or getpass.getuser() != HOST_USER
    ):
        raise StartError("AUTHORIZED_HOST_IDENTITY_MISMATCH")


def _read_private(path: Path, code: str) -> dict[str, Any]:
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
            raise StartError(code)
        value = json.loads(path.read_text(encoding="utf-8"))
    except StartError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise StartError(code) from None
    if not isinstance(value, dict):
        raise StartError(code)
    return value


def _canonical_hash(value: dict[str, Any]) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def _validate_result_destination(path: Path) -> None:
    try:
        if (
            not path.is_absolute()
            or path.resolve(strict=False) != path
            or REPORT_ROOT not in path.parents
            or ROOT in path.parents
        ):
            raise StartError("RESULT_PATH_UNSAFE")
        parent = path.parent
        info = parent.lstat()
        if (
            not stat.S_ISDIR(info.st_mode)
            or stat.S_ISLNK(info.st_mode)
            or info.st_uid != os.getuid()
            or stat.S_IMODE(info.st_mode) != 0o700
        ):
            raise StartError("RESULT_DIRECTORY_PERMISSIONS_INVALID")
        try:
            path.lstat()
        except FileNotFoundError:
            return
        raise StartError("RESULT_REPORT_ALREADY_EXISTS")
    except StartError:
        raise
    except OSError:
        raise StartError("RESULT_PATH_UNAVAILABLE") from None


def _validate_preflight(
    manifest_path: Path,
    diagnostic_report_path: Path,
    preflight_path: Path,
    result_path: Path,
    now: datetime | None,
) -> tuple[dict[str, Any], str, str]:
    manifest = _read_private(manifest_path, "AUTHORIZATION_MANIFEST_UNAVAILABLE")
    try:
        validated = PREFLIGHT.validate(manifest, diagnostic_report_path, now)
    except PREFLIGHT.PreflightError as error:
        raise StartError(str(error)) from None
    if Path(validated["ResultReportPath"]) != result_path:
        raise StartError("AUTHORIZED_RESULT_PATH_MISMATCH")
    _validate_result_destination(result_path)
    preflight = _read_private(preflight_path, "PREFLIGHT_REPORT_UNAVAILABLE")
    try:
        generated = datetime.fromisoformat(preflight["GeneratedAtUtc"].replace("Z", "+00:00"))
        age = (now or datetime.now(timezone.utc)) - generated
    except (KeyError, TypeError, ValueError):
        raise StartError("PREFLIGHT_REPORT_TIME_INVALID") from None
    if (
        preflight.get("Classification") != PREFLIGHT.PREFLIGHT_CLASSIFICATION
        or preflight.get("ApprovalId") != manifest["ApprovalId"]
        or preflight.get("ManifestSha256") != _canonical_hash(manifest)
        or preflight.get("DiagnosticReportSha256") != manifest["DiagnosticReportSha256"]
        or preflight.get("DiagnosticReportPath") != str(diagnostic_report_path)
        or preflight.get("ResultReportPath") != str(result_path)
        or age.total_seconds() < 0
        or age.total_seconds() > 600
        or preflight.get("ResourceOperationCounts") != {"Podman": 0, "DatabaseConnections": 0, "DatabaseMutations": 0, "CredentialReads": 0}
    ):
        raise StartError("PREFLIGHT_REPORT_INVALID_OR_STALE")
    verify_execution_host()
    instant = now or datetime.now(timezone.utc)
    if instant >= datetime.fromisoformat(manifest["ExpiresAtUtc"].replace("Z", "+00:00")):
        raise StartError("AUTHORIZATION_EXPIRED_BEFORE_EXECUTION")
    return manifest, _canonical_hash(manifest), _canonical_hash(preflight)


def start_command(manifest: dict[str, Any]) -> list[str]:
    command = [PODMAN, "start", PREFLIGHT.AUTHORIZED_TARGET["ContainerName"]]
    if manifest["Target"]["ContainerName"] != PREFLIGHT.AUTHORIZED_TARGET["ContainerName"]:
        raise StartError("TARGET_BINDING_MISMATCH")
    return command


def _run_allowed(command: list[str], run: Callable[..., subprocess.CompletedProcess[str]]) -> int:
    if command != [PODMAN, "start", PREFLIGHT.AUTHORIZED_TARGET["ContainerName"]]:
        raise StartError("PODMAN_COMMAND_NOT_ALLOWLISTED")
    try:
        result = run(
            command,
            timeout=TIMEOUT,
            shell=False,
            capture_output=True,
            text=True,
            env={"PATH": "/usr/bin:/bin", "HOME": "/home/cdjensen94", "XDG_RUNTIME_DIR": "/run/user/1000", "LC_ALL": "C"},
        )
    except (OSError, subprocess.TimeoutExpired):
        return 124
    return result.returncode if type(result.returncode) is int else 125


def _write_once(path: Path, data: dict[str, Any]) -> None:
    try:
        if (
            not path.is_absolute()
            or path.resolve(strict=False) != path
            or REPORT_ROOT not in path.parents
            or ROOT in path.parents
        ):
            raise StartError("RESULT_PATH_UNSAFE")
        parent = path.parent
        info = parent.lstat()
        if (
            not stat.S_ISDIR(info.st_mode)
            or stat.S_ISLNK(info.st_mode)
            or info.st_uid != os.getuid()
            or stat.S_IMODE(info.st_mode) != 0o700
        ):
            raise StartError("RESULT_DIRECTORY_PERMISSIONS_INVALID")
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream, sort_keys=True, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError:
        raise StartError("RESULT_REPORT_ALREADY_EXISTS") from None


def _claim_once(result_path: Path, manifest_hash: str, now: datetime | None) -> None:
    marker = result_path.with_name(result_path.name + ".started")
    payload = {
        "MarkerVersion": "cw-m5-026b-stopped-source-start-attempt-v1",
        "ManifestSha256": manifest_hash,
        "StartedAtUtc": (now or datetime.now(timezone.utc)).isoformat().replace("+00:00", "Z"),
    }
    try:
        fd = os.open(marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError:
        raise StartError("AUTHORIZATION_ATTEMPT_ALREADY_CLAIMED") from None


def execute(
    manifest_path: Path,
    diagnostic_report_path: Path,
    preflight_path: Path,
    result_path: Path,
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
    now: datetime | None = None,
) -> dict[str, Any]:
    manifest, manifest_hash, preflight_hash = _validate_preflight(
        manifest_path, diagnostic_report_path, preflight_path, result_path, now
    )
    _claim_once(result_path, manifest_hash, now)
    exit_code = _run_allowed(start_command(manifest), run)
    report = {
        "ReportVersion": START_REPORT_VERSION,
        "Classification": f"{PREFLIGHT.TARGET_ROLE.upper()}_CONTAINER_START_SUCCEEDED" if exit_code == 0 else f"{PREFLIGHT.TARGET_ROLE.upper()}_CONTAINER_START_FAILED",
        "TargetRole": PREFLIGHT.TARGET_ROLE,
        "ExpectedContainerId": PREFLIGHT.AUTHORIZED_TARGET["ContainerId"],
        "TargetContainerName": PREFLIGHT.AUTHORIZED_TARGET["ContainerName"],
        "StartExitCode": exit_code,
        "PodmanStartCommands": 1,
        "PodmanCommandsAreStartOnly": True,
        "PostStartIdentityVerification": "NOT_AUTHORIZED",
        "RawPodmanOutputPersisted": False,
        "StderrPersisted": False,
        "DatabaseConnections": 0,
        "DatabaseMutations": 0,
        "CredentialsRead": 0,
        "AuthorizationApprovalId": manifest["ApprovalId"],
        "ManifestSha256": manifest_hash,
        "DiagnosticReportSha256": manifest["DiagnosticReportSha256"],
        "PreflightReportSha256": preflight_hash,
        "GeneratedAtUtc": (now or datetime.now(timezone.utc)).isoformat().replace("+00:00", "Z"),
    }
    _write_once(result_path, report)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--diagnostic-report", required=True)
    parser.add_argument("--preflight-report", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--execute-authorized-start", action="store_true")
    args = parser.parse_args(argv)
    if not args.execute_authorized_start:
        parser.error("explicit --execute-authorized-start is required")
    try:
        report = execute(
            Path(args.manifest), Path(args.diagnostic_report), Path(args.preflight_report), Path(args.report)
        )
        print(f"{report['Classification']}; podman_start_commands=1; database_connections=0; database_mutations=0; credentials_read=0")
        return 0 if report["Classification"] == f"{PREFLIGHT.TARGET_ROLE.upper()}_CONTAINER_START_SUCCEEDED" else 2
    except StartError as error:
        print(f"START_NOT_EXECUTED: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
