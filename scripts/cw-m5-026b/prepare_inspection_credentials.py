#!/usr/bin/env python3
"""Create one MariaDB client option file after separate credential-preparation approval.

This utility never connects to MariaDB or invokes Podman. It reads only the
target's generated application.env file and writes a new, owner-only client
option file with O_EXCL. It never prints credential content.
"""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pwd
import re
import socket
import stat
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-inspection-credential-preparation.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_credential_preparation_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)
MAX_ENV_BYTES = 1024
PASSWORD_PATTERN = re.compile(rb"^[0-9a-f]{64}$")
OPTION_FILE_PATTERN = re.compile(rb"^\[client\]\nuser=([A-Za-z0-9_]{1,128})\npassword=([0-9a-f]{64})\n$")
USER_LINE = re.compile(rb"^CW_M5_APP_USER=([A-Za-z0-9_]{1,128})$")
PASSWORD_LINE = re.compile(rb"^CW_M5_APP_PASSWORD=([0-9a-f]{64})$")
O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)
O_DIRECTORY = getattr(os, "O_DIRECTORY", 0)


class CredentialPreparationError(ValueError):
    """Stable non-sensitive diagnostic; never carries file data or OS detail."""


def _read_preflight(path: Path) -> dict[str, Any]:
    try:
        if not path.is_absolute() or path.resolve(strict=True) != path or ROOT in path.parents:
            raise CredentialPreparationError("PREFLIGHT_PATH_INVALID")
        fstat = path.lstat()
        parent = path.parent.lstat()
        if (not stat.S_ISREG(fstat.st_mode) or stat.S_ISLNK(fstat.st_mode)
                or fstat.st_uid != os.getuid() or stat.S_IMODE(fstat.st_mode) != 0o600
                or fstat.st_nlink != 1
                or not stat.S_ISDIR(parent.st_mode) or stat.S_ISLNK(parent.st_mode)
                or parent.st_uid != os.getuid() or stat.S_IMODE(parent.st_mode) != 0o700):
            raise CredentialPreparationError("PREFLIGHT_PERMISSIONS_INVALID")
        value = json.loads(path.read_text(encoding="utf-8"))
    except CredentialPreparationError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError):
        raise CredentialPreparationError("PREFLIGHT_EVIDENCE_INVALID") from None
    if not isinstance(value, dict):
        raise CredentialPreparationError("PREFLIGHT_EVIDENCE_INVALID")
    return value


def _validate_gate(manifest: dict[str, Any], preflight: dict[str, Any], approval_id: str,
                   preflight_report_path: Path,
                   *, now: datetime | None = None) -> tuple[dict[str, Any], str]:
    validated = PREFLIGHT.validate_manifest(manifest, now=now)
    instant = now or datetime.now(timezone.utc)
    try:
        generated = datetime.fromisoformat(preflight["GeneratedAtUtc"].replace("Z", "+00:00"))
        expiry = datetime.fromisoformat(manifest["ExpiresAtUtc"].replace("Z", "+00:00"))
    except (KeyError, TypeError, ValueError, AttributeError):
        raise CredentialPreparationError("PREFLIGHT_EVIDENCE_MALFORMED") from None
    if generated.tzinfo is None or generated.utcoffset() is None or expiry.tzinfo is None or expiry.utcoffset() is None:
        raise CredentialPreparationError("PREFLIGHT_EVIDENCE_MALFORMED")
    if (approval_id != manifest["ApprovalId"]
            or preflight.get("Classification") != "CREDENTIAL_PREPARATION_AUTHORIZATION_VALID_NONCONNECTING"
            or preflight.get("ManifestSha256") != validated["ManifestSha256"]
            or preflight.get("ApprovalId") != approval_id
            or preflight.get("TargetRole") != manifest["TargetRole"]
            or preflight.get("ReportPath") != str(preflight_report_path)
            or not PREFLIGHT.report_path_is_bound(Path(manifest["PreflightReportPath"]), preflight_report_path)
            or preflight.get("OperationCounts") != {"CredentialValueReads": 0, "CredentialWrites": 0,
                                                     "DatabaseConnections": 0, "PodmanCommands": 0}
            or (instant - generated).total_seconds() < 0
            or (instant - generated).total_seconds() > 600
            or instant >= expiry):
        raise CredentialPreparationError("FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH")
    host = manifest["ExecutionHost"]
    actual = (socket.gethostname(), pwd.getpwuid(os.getuid()).pw_name, os.getuid())
    if actual != (host["HostName"], host["UnixUser"], host["Uid"]):
        raise CredentialPreparationError("EXECUTION_HOST_IDENTITY_MISMATCH")
    return manifest["Target"], manifest["TargetRole"]


def _private_directory_fd(path: Path, uid: int) -> int:
    try:
        if path.resolve(strict=True) != path:
            raise CredentialPreparationError("CREDENTIAL_DIRECTORY_PATH_INVALID")
        path_info = path.lstat()
        fd = os.open(path, os.O_RDONLY | O_DIRECTORY | O_NOFOLLOW)
        opened = os.fstat(fd)
    except CredentialPreparationError:
        raise
    except OSError:
        raise CredentialPreparationError("CREDENTIAL_DIRECTORY_UNAVAILABLE") from None
    if (not stat.S_ISDIR(path_info.st_mode) or stat.S_ISLNK(path_info.st_mode)
            or path_info.st_uid != uid or stat.S_IMODE(path_info.st_mode) != 0o700
            or not stat.S_ISDIR(opened.st_mode) or opened.st_ino != path_info.st_ino
            or opened.st_dev != path_info.st_dev or opened.st_uid != uid
            or stat.S_IMODE(opened.st_mode) != 0o700):
        os.close(fd)
        raise CredentialPreparationError("CREDENTIAL_DIRECTORY_PERMISSIONS_INVALID")
    return fd


def _read_application_env(directory_fd: int, identity: str) -> bytearray:
    try:
        fd = os.open("application.env", os.O_RDONLY | O_NOFOLLOW, dir_fd=directory_fd)
    except OSError:
        raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_UNAVAILABLE") from None
    raw = bytearray()
    try:
        info = os.fstat(fd)
        if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
                or stat.S_IMODE(info.st_mode) != 0o600 or info.st_nlink != 1):
            raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_PERMISSIONS_INVALID")
        while len(raw) <= MAX_ENV_BYTES:
            chunk = os.read(fd, min(256, MAX_ENV_BYTES + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        if len(raw) > MAX_ENV_BYTES:
            raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
        if not raw.endswith(b"\n") or b"\r" in raw or b"\x00" in raw:
            raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
        rows = raw[:-1].split(b"\n")
        if len(rows) != 2:
            raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
        parsed: dict[bytes, bytes] = {}
        for row in rows:
            user_match = USER_LINE.fullmatch(row)
            password_match = PASSWORD_LINE.fullmatch(row)
            if user_match:
                if b"user" in parsed:
                    raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
                parsed[b"user"] = user_match.group(1)
            elif password_match:
                if b"password" in parsed:
                    raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
                parsed[b"password"] = password_match.group(1)
            else:
                raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
        if set(parsed) != {b"user", b"password"} or parsed[b"user"].decode("ascii") != identity:
            raise CredentialPreparationError("APPLICATION_CREDENTIAL_IDENTITY_MISMATCH")
        if not PASSWORD_PATTERN.fullmatch(parsed[b"password"]):
            raise CredentialPreparationError("APPLICATION_CREDENTIAL_FILE_MALFORMED")
        return bytearray(parsed[b"password"])
    finally:
        for index in range(len(raw)):
            raw[index] = 0
        os.close(fd)


def _write_option_file(directory_fd: int, identity: str, password: bytearray, uid: int) -> None:
    data = bytearray(b"[client]\nuser=" + identity.encode("ascii") + b"\npassword=" + password + b"\n")
    match = OPTION_FILE_PATTERN.fullmatch(data)
    if match is None or match.group(1).decode("ascii") != identity or not PASSWORD_PATTERN.fullmatch(match.group(2)):
        for index in range(len(data)):
            data[index] = 0
        for index in range(len(password)):
            password[index] = 0
        raise CredentialPreparationError("OPTION_FILE_SYNTAX_INVALID")
    fd = -1
    created_inode: tuple[int, int] | None = None
    try:
        try:
            fd = os.open("inspection-client.cnf", os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_NOFOLLOW,
                         0o600, dir_fd=directory_fd)
        except FileExistsError:
            raise CredentialPreparationError("OPTION_FILE_ALREADY_EXISTS") from None
        except OSError:
            raise CredentialPreparationError("OPTION_FILE_CREATE_FAILED") from None
        info = os.fstat(fd)
        created_inode = (info.st_dev, info.st_ino)
        os.fchmod(fd, 0o600)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != uid:
            raise CredentialPreparationError("OPTION_FILE_OWNERSHIP_INVALID")
        offset = 0
        while offset < len(data):
            written = os.write(fd, data[offset:])
            if written <= 0:
                raise CredentialPreparationError("OPTION_FILE_WRITE_FAILED")
            offset += written
        os.fsync(fd)
        final = os.fstat(fd)
        if final.st_uid != uid or stat.S_IMODE(final.st_mode) != 0o600 or final.st_nlink != 1:
            raise CredentialPreparationError("OPTION_FILE_PERMISSIONS_INVALID")
    except BaseException as error:
        cleanup_complete = True
        if fd >= 0 and created_inode is not None:
            try:
                current = os.stat("inspection-client.cnf", dir_fd=directory_fd, follow_symlinks=False)
                if (current.st_dev, current.st_ino) == created_inode:
                    os.unlink("inspection-client.cnf", dir_fd=directory_fd)
            except FileNotFoundError:
                pass
            except OSError:
                cleanup_complete = False
        if not cleanup_complete:
            raise CredentialPreparationError("OPTION_FILE_PARTIAL_RETAINED_OWNER_ONLY") from None
        if isinstance(error, CredentialPreparationError):
            raise
        if isinstance(error, OSError):
            raise CredentialPreparationError("OPTION_FILE_WRITE_FAILED") from None
        raise
    finally:
        for index in range(len(data)):
            data[index] = 0
        for index in range(len(password)):
            password[index] = 0
        if fd >= 0:
            os.close(fd)


def prepare(manifest: dict[str, Any], preflight: dict[str, Any], approval_id: str,
            preflight_report_path: Path,
            *, now: datetime | None = None) -> dict[str, str]:
    target, role = _validate_gate(manifest, preflight, approval_id, preflight_report_path, now=now)
    directory = Path(target["CredentialDirectory"])
    directory_fd = _private_directory_fd(directory, manifest["ExecutionHost"]["Uid"])
    try:
        password = _read_application_env(directory_fd, target["DatabaseIdentity"])
        _write_option_file(directory_fd, target["DatabaseIdentity"], password,
                           manifest["ExecutionHost"]["Uid"])
    finally:
        os.close(directory_fd)
    return {"Classification": "INSPECTION_CLIENT_OPTION_FILE_CREATED", "TargetRole": role}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--preflight-report", required=True)
    parser.add_argument("--approval-id", required=True)
    parser.add_argument("--prepare", action="store_true")
    args = parser.parse_args(argv)
    if not args.prepare:
        print("Credential preparation refused: pass --prepare only after genuine target-specific approval and valid preflight.", file=sys.stderr)
        return 2
    try:
        manifest = PREFLIGHT._read_private(Path(args.manifest), "MANIFEST_JSON_OR_PERMISSIONS_INVALID")
        preflight = _read_preflight(Path(args.preflight_report))
        result = prepare(manifest, preflight, args.approval_id, Path(args.preflight_report))
    except (OSError, KeyError, CredentialPreparationError, PREFLIGHT.CredentialPreflightError) as error:
        code = str(error) if isinstance(error, (CredentialPreparationError, PREFLIGHT.CredentialPreflightError)) else "CREDENTIAL_PREPARATION_STOPPED"
        print(f"CW-M5-026B credential preparation stopped: {code}; credential_values_logged=0", file=sys.stderr)
        return 3
    print(f"CW-M5-026B credential preparation: classification={result['Classification']}; target={result['TargetRole']}; credential_values_logged=0; database_connections=0; podman_commands=0")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
