#!/usr/bin/env python3
"""Nonconnecting preflight for the separate stopped-source diagnostic approval."""
from __future__ import annotations
import argparse, hashlib, json, os, re, stat, sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
SCHEMA = ROOT / "docs/operations/cw-m5-026b-stopped-source-diagnostic-authorization.schema.json"
CONTRACT = "cw-m5-026b-stopped-source-diagnostic-authorization-v1"
PREFLIGHT_REPORT_VERSION = "cw-m5-026b-stopped-source-diagnostic-preflight-v1"
PREFLIGHT_CLASSIFICATION = "CATEGORY_STOPPED_SOURCE_DIAGNOSTIC_AUTHORIZATION_VALID_NONCONNECTING"
VALIDATION_ROOT = Path("/home/cdjensen94/continuumwiki-validation")
from importlib.util import spec_from_file_location, module_from_spec
SPEC = spec_from_file_location("stopped_source_diagnostic_preflight", ROOT / "scripts/cw-m5-026b/stopped_source_diagnostic.py")
assert SPEC and SPEC.loader
DIAG = module_from_spec(SPEC); SPEC.loader.exec_module(DIAG)
MAX_WINDOW = timedelta(hours=24)
APPROVAL = re.compile(r"^CW-M5-026B-DIAGNOSTIC-[A-Za-z0-9._:-]{1,80}$")
OPS = {"InspectRootlessPodman": True, "InspectExactSourceContainer": True, "InspectPinnedImage": True,
       "InspectBoundVolume": True, "InspectPublishedEndpoint": True, "RetainSanitizedEvidence": True,
       "DatabaseConnections": False, "CredentialReads": False, "ContainerStart": False,
       "ContainerRestart": False, "ContainerCreation": False, "ContainerRecreation": False,
       "ContainerStop": False, "ImageOrVolumeChanges": False, "DatabaseMutations": False,
       "Migrations": False, "GrantChanges": False, "FixtureWrites": False, "Backup": False,
       "Restore": False, "Cleanup": False, "Deletion": False, "ProductionAccess": False}
AUTHORIZED_TARGET = {
    "ContainerName": "continuumwiki-m5-026b-source-ro1",
    "ContainerId": "6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914",
    "ImageReference": "docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15",
    "VolumeName": "continuumwiki-m5-026b-source-ro1-data", "DatabaseName": "cw_m5_026b_source_ro1",
    "Endpoint": "127.0.0.1:13326", "ProvisioningRunId": "20261012T093746Z-86380"}

class PreflightError(ValueError): pass

def _utc(value: Any) -> datetime:
    if not isinstance(value, str) or not value.endswith("Z"):
        raise PreflightError("AUTHORIZATION_TIME_INVALID")
    try: parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError: raise PreflightError("AUTHORIZATION_TIME_INVALID") from None
    return parsed.astimezone(timezone.utc)

def _private_json(path: Path) -> dict[str, Any]:
    try:
        info, parent = path.lstat(), path.parent.lstat()
        if (not path.is_absolute() or path.resolve(strict=True) != path or ROOT in path.parents
            or not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600
            or not stat.S_ISDIR(parent.st_mode) or stat.S_ISLNK(parent.st_mode) or parent.st_uid != os.getuid() or stat.S_IMODE(parent.st_mode) != 0o700):
            raise PreflightError("MANIFEST_FILE_PERMISSIONS_INVALID")
        value = json.loads(path.read_text(encoding="utf-8"))
    except PreflightError: raise
    except (OSError, UnicodeError, json.JSONDecodeError): raise PreflightError("MANIFEST_JSON_INVALID") from None
    if not isinstance(value, dict): raise PreflightError("MANIFEST_ROOT_INVALID")
    return value

def validate(document: Any, now: datetime | None = None) -> dict[str, str]:
    if not isinstance(document, dict): raise PreflightError("MANIFEST_ROOT_INVALID")
    fields = {"ManifestVersion", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver", "Operator", "TargetOwner", "ExecutionHost", "Target", "PermittedOperations", "ResultReportPath"}
    if set(document) != fields or document.get("ManifestVersion") != CONTRACT: raise PreflightError("MANIFEST_SHAPE_OR_VERSION_INVALID")
    if not isinstance(document["ApprovalId"], str) or not APPROVAL.fullmatch(document["ApprovalId"]): raise PreflightError("APPROVAL_ID_INVALID")
    approved, expires = _utc(document["ApprovedAtUtc"]), _utc(document["ExpiresAtUtc"])
    instant = now or datetime.now(timezone.utc)
    if approved > instant or expires <= instant: raise PreflightError("AUTHORIZATION_NOT_CURRENT")
    if expires <= approved or expires - approved > MAX_WINDOW: raise PreflightError("AUTHORIZATION_WINDOW_INVALID")
    for key in ("Approver", "Operator"):
        person = document[key]
        if not isinstance(person, dict) or set(person) != {"Name", "Role"} or not all(isinstance(person.get(k), str) and person[k].strip() for k in ("Name", "Role")): raise PreflightError("APPROVAL_IDENTITY_INCOMPLETE")
    if not isinstance(document["TargetOwner"], str) or not document["TargetOwner"].strip(): raise PreflightError("TARGET_OWNER_INCOMPLETE")
    if document["ExecutionHost"] != {"HostName":"christian-laptop", "UnixUser":"cdjensen94", "Uid":1000, "RootlessPodman":True}: raise PreflightError("AUTHORIZED_HOST_MISMATCH")
    target = DIAG.EXPECTED
    if ({k: target.get(k) for k in AUTHORIZED_TARGET} != AUTHORIZED_TARGET
        or document["Target"] != AUTHORIZED_TARGET): raise PreflightError("TARGET_BINDING_MISMATCH")
    if document["PermittedOperations"] != OPS: raise PreflightError("OPERATION_SCOPE_MISMATCH")
    report = document["ResultReportPath"]
    report_path = Path(report) if isinstance(report, str) else Path(".")
    if (not isinstance(report, str) or VALIDATION_ROOT not in report_path.parents
        or any(p in {"", ".", ".."} for p in report_path.parts[1:])): raise PreflightError("REPORT_PATH_UNSAFE")
    canonical = hashlib.sha256(json.dumps(document, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    return {"ApprovalId": document["ApprovalId"], "ManifestSha256": canonical, "ExpiresAtUtc": document["ExpiresAtUtc"], "ResultReportPath": report}

def _write_once(path: Path, data: dict[str, Any]) -> None:
    try:
        if not path.is_absolute() or path.resolve(strict=False) != path or ROOT in path.parents: raise PreflightError("PREFLIGHT_REPORT_PATH_UNSAFE")
        parent = path.parent; info = parent.lstat()
        if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o700: raise PreflightError("PREFLIGHT_REPORT_DIRECTORY_UNSAFE")
        fd = os.open(path, os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,"O_NOFOLLOW",0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as stream: json.dump(data, stream, sort_keys=True, indent=2); stream.write("\n"); stream.flush(); os.fsync(stream.fileno())
    except FileExistsError: raise PreflightError("PREFLIGHT_REPORT_ALREADY_EXISTS") from None

def run(manifest: Path, report: Path, now: datetime | None = None) -> tuple[bool, dict[str, Any]]:
    errors=[]; validated={}
    try: validated=validate(_private_json(manifest), now)
    except PreflightError as error: errors.append(str(error))
    data={"ReportVersion":PREFLIGHT_REPORT_VERSION, "GeneratedAtUtc":(now or datetime.now(timezone.utc)).isoformat().replace("+00:00","Z"),
          "Classification":PREFLIGHT_CLASSIFICATION if not errors else PREFLIGHT_CLASSIFICATION.replace("_VALID_NONCONNECTING", "_INVALID"),
          "Errors":errors, **validated, "ResourceOperationCounts":{"Podman":0,"DatabaseConnections":0,"DatabaseMutations":0,"CredentialReads":0}}
    _write_once(report,data); return not errors,data

def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument("--manifest",required=True); parser.add_argument("--report",required=True); args=parser.parse_args(argv)
    try: ok,data=run(Path(args.manifest),Path(args.report)); print(f"{data['Classification']}; errors={len(data['Errors'])}; podman=0; database_connections=0; database_mutations=0; credential_reads=0"); return 0 if ok else 2
    except PreflightError as e: print(f"CATEGORY_STOPPED_SOURCE_DIAGNOSTIC_PREFLIGHT_FAILED: {e}",file=sys.stderr); return 2
if __name__=="__main__": raise SystemExit(main())
