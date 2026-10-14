#!/usr/bin/env python3
"""Verify the started source container with fixed read-only Podman checks."""
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


def _load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PLAN = _load("m5_026b_started_source_plan", ROOT / "scripts/cw-m5-026b/stopped_source_diagnostic.py")
PREFLIGHT = _load("m5_026b_started_source_start_preflight", ROOT / "scripts/preflight-m5-026b-stopped-source-restart.py")
START_REPORT_VERSION = "cw-m5-026b-stopped-source-start-v1"
VERIFICATION_REPORT_VERSION = "cw-m5-026b-started-source-verification-v1"


class VerificationError(ValueError):
    pass


def _canonical_hash(value: dict[str, Any]) -> str:
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


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
            raise VerificationError(code)
        value = json.loads(path.read_text(encoding="utf-8"))
    except VerificationError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise VerificationError(code) from None
    if not isinstance(value, dict):
        raise VerificationError(code)
    return value


def _verify_host() -> None:
    if socket.gethostname().split(".", 1)[0] != "christian-laptop" or os.getuid() != HOST_UID or getpass.getuser() != HOST_USER:
        raise VerificationError("AUTHORIZED_HOST_IDENTITY_MISMATCH")


def _validate_result_destination(path: Path) -> None:
    try:
        if (
            not path.is_absolute()
            or path.resolve(strict=False) != path
            or REPORT_ROOT not in path.parents
            or ROOT in path.parents
        ):
            raise VerificationError("RESULT_PATH_UNSAFE")
        info = path.parent.lstat()
        if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
            raise VerificationError("RESULT_DIRECTORY_PERMISSIONS_INVALID")
        try:
            path.lstat()
        except FileNotFoundError:
            return
        raise VerificationError("RESULT_REPORT_ALREADY_EXISTS")
    except VerificationError:
        raise
    except OSError:
        raise VerificationError("RESULT_PATH_UNAVAILABLE") from None


def _validate_start_evidence(manifest_path: Path, start_report_path: Path) -> tuple[dict[str, Any], dict[str, Any], str, str]:
    manifest = _read_private(manifest_path, "AUTHORIZATION_MANIFEST_UNAVAILABLE")
    start_report = _read_private(start_report_path, "START_REPORT_UNAVAILABLE")
    if manifest.get("ResultReportPath") != str(start_report_path):
        raise VerificationError("AUTHORIZED_START_REPORT_PATH_MISMATCH")
    start_hash = hashlib.sha256(start_report_path.read_bytes()).hexdigest()
    if (
        start_report.get("ReportVersion") != START_REPORT_VERSION
        or start_report.get("Classification") != f"{PREFLIGHT.TARGET_ROLE.upper()}_CONTAINER_START_SUCCEEDED"
        or start_report.get("AuthorizationApprovalId") != manifest.get("ApprovalId")
        or start_report.get("ManifestSha256") != _canonical_hash(manifest)
        or start_report.get("ExpectedContainerId") != PREFLIGHT.AUTHORIZED_TARGET["ContainerId"]
        or start_report.get("TargetContainerName") != PREFLIGHT.AUTHORIZED_TARGET["ContainerName"]
        or start_report.get("PodmanStartCommands") != 1
        or start_report.get("DatabaseConnections") != 0
        or start_report.get("DatabaseMutations") != 0
        or start_report.get("CredentialsRead") != 0
    ):
        raise VerificationError("START_REPORT_NOT_SUCCESSFULLY_BOUND")
    return manifest, start_report, start_hash, _canonical_hash(manifest)


def _run_allowed(command: list[str], run: Callable[..., subprocess.CompletedProcess[str]]) -> dict[str, Any]:
    if command not in PLAN.podman_diagnostic_commands():
        raise VerificationError("PODMAN_COMMAND_NOT_ALLOWLISTED")
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
        return {"ReturnCode": 124, "Stdout": ""}
    return {"ReturnCode": result.returncode if type(result.returncode) is int else 125, "Stdout": result.stdout if isinstance(result.stdout, str) else ""}


def _collect(run: Callable[..., subprocess.CompletedProcess[str]]) -> tuple[dict[str, Any], int]:
    commands = PLAN.podman_diagnostic_commands()
    names = ("Rootless", "Exists", "Container", "Port", "Image", "Volume")
    observations: dict[str, Any] = {}
    for name, command in zip(names, commands):
        observations[name] = _run_allowed(command, run)
    return observations, len(commands)


def _write_once(path: Path, data: dict[str, Any]) -> None:
    try:
        if not path.is_absolute() or path.resolve(strict=False) != path or REPORT_ROOT not in path.parents or ROOT in path.parents:
            raise VerificationError("RESULT_PATH_UNSAFE")
        info = path.parent.lstat()
        if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700:
            raise VerificationError("RESULT_DIRECTORY_PERMISSIONS_INVALID")
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream, sort_keys=True, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError:
        raise VerificationError("RESULT_REPORT_ALREADY_EXISTS") from None


def _claim_once(path: Path, manifest_hash: str) -> None:
    marker = path.with_name(path.name + ".started")
    try:
        fd = os.open(marker, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump({"MarkerVersion": VERIFICATION_REPORT_VERSION, "ManifestSha256": manifest_hash, "StartedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")}, stream, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
    except FileExistsError:
        raise VerificationError("VERIFICATION_ATTEMPT_ALREADY_CLAIMED") from None


def execute(
    manifest_path: Path,
    start_report_path: Path,
    result_path: Path,
    run: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run,
) -> dict[str, Any]:
    manifest, start_report, start_hash, manifest_hash = _validate_start_evidence(manifest_path, start_report_path)
    _validate_result_destination(result_path)
    _verify_host()
    _claim_once(result_path, manifest_hash)
    observations, count = _collect(run)
    report = PLAN.classify_observations(observations)
    report["ReportVersion"] = VERIFICATION_REPORT_VERSION
    report["AuthorizationApprovalId"] = manifest["ApprovalId"]
    report["StartReportSha256"] = start_hash
    report["ManifestSha256"] = manifest_hash
    report["PodmanReadOnlyCommands"] = count
    report["GeneratedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    _write_once(result_path, report)
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--start-report", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--verify-started-source", action="store_true")
    args = parser.parse_args(argv)
    if not args.verify_started_source:
        parser.error("explicit --verify-started-source is required")
    try:
        report = execute(Path(args.manifest), Path(args.start_report), Path(args.report))
        print(f"{report['Classification']}; podman_readonly_commands={report['PodmanReadOnlyCommands']}; database_connections=0; database_mutations=0; credentials_read=0")
        return 0 if report["Classification"] == f"{PREFLIGHT.TARGET_ROLE.upper()}_CONTAINER_RUNNING_IDENTITY_MATCH" else 2
    except VerificationError as error:
        print(f"START_VERIFICATION_NOT_EXECUTED: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
