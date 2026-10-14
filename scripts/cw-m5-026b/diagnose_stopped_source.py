#!/usr/bin/env python3
"""Run one separately authorized, read-only diagnostic on the retained source."""
from __future__ import annotations
import argparse, getpass, hashlib, importlib.util, json, os, socket, stat, subprocess, sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

ROOT=Path(__file__).resolve().parents[2]
REPORT_ROOT=Path("/home/cdjensen94/continuumwiki-validation")
def _load(name,path):
    spec=importlib.util.spec_from_file_location(name,path); assert spec and spec.loader
    module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module); return module
PREFLIGHT=_load("m5_026b_stopped_source_preflight", ROOT/"scripts/preflight-m5-026b-stopped-source-diagnostic.py")
PLAN=_load("m5_026b_stopped_source_plan", ROOT/"scripts/cw-m5-026b/stopped_source_diagnostic.py")
TIMEOUT=20
HOST_USER="cdjensen94"
HOST_UID=1000

class DiagnosticError(ValueError): pass

def verify_execution_host()->None:
    if (socket.gethostname().split(".",1)[0]!="christian-laptop" or os.getuid()!=HOST_UID
        or getpass.getuser()!=HOST_USER):
        raise DiagnosticError("AUTHORIZED_HOST_IDENTITY_MISMATCH")

def _read_private(path: Path, code: str) -> dict[str,Any]:
    try:
        info=path.lstat(); parent=path.parent.lstat()
        if (not path.is_absolute() or path.resolve(strict=True)!=path or ROOT in path.parents
            or not stat.S_ISREG(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o600
            or not stat.S_ISDIR(parent.st_mode) or stat.S_ISLNK(parent.st_mode) or parent.st_uid!=os.getuid() or stat.S_IMODE(parent.st_mode)!=0o700): raise DiagnosticError(code)
        value=json.loads(path.read_text(encoding="utf-8"))
    except DiagnosticError: raise
    except (OSError,UnicodeError,json.JSONDecodeError): raise DiagnosticError(code) from None
    if not isinstance(value,dict): raise DiagnosticError(code)
    return value

def _canonical_hash(value: dict[str,Any])->str:
    return hashlib.sha256(json.dumps(value,sort_keys=True,separators=(",",":")).encode()).hexdigest()

def validate_execution(manifest_path:Path,preflight_path:Path,result_path:Path,now:datetime|None=None)->tuple[dict[str,Any],dict[str,Any],str,str]:
    manifest=_read_private(manifest_path,"AUTHORIZATION_MANIFEST_UNAVAILABLE")
    try: validated=PREFLIGHT.validate(manifest,now)
    except PREFLIGHT.PreflightError as error: raise DiagnosticError(str(error)) from None
    if Path(validated["ResultReportPath"])!=result_path: raise DiagnosticError("AUTHORIZED_RESULT_PATH_MISMATCH")
    _validate_result_destination(result_path)
    preflight=_read_private(preflight_path,"PREFLIGHT_REPORT_UNAVAILABLE")
    try:
        age=(now or datetime.now(timezone.utc))-datetime.fromisoformat(preflight["GeneratedAtUtc"].replace("Z","+00:00"))
    except (KeyError,TypeError,ValueError): raise DiagnosticError("PREFLIGHT_REPORT_TIME_INVALID") from None
    if (preflight.get("Classification")!=PREFLIGHT.PREFLIGHT_CLASSIFICATION
        or preflight.get("ApprovalId")!=manifest["ApprovalId"] or preflight.get("ManifestSha256")!=_canonical_hash(manifest)
        or preflight.get("ResultReportPath")!=str(result_path) or age.total_seconds()<0 or age.total_seconds()>600
        or preflight.get("ResourceOperationCounts")!={"Podman":0,"DatabaseConnections":0,"DatabaseMutations":0,"CredentialReads":0}):
        raise DiagnosticError("PREFLIGHT_REPORT_INVALID_OR_STALE")
    verify_execution_host()
    instant=now or datetime.now(timezone.utc)
    if instant>=datetime.fromisoformat(manifest["ExpiresAtUtc"].replace("Z","+00:00")):
        raise DiagnosticError("AUTHORIZATION_EXPIRED_BEFORE_EXECUTION")
    return manifest,preflight,validated["ManifestSha256"],_canonical_hash(preflight)

def _validate_result_destination(path:Path)->None:
    try:
        if (not path.is_absolute() or path.resolve(strict=False)!=path or REPORT_ROOT not in path.parents
            or ROOT in path.parents): raise DiagnosticError("RESULT_PATH_UNSAFE")
        parent=path.parent; info=parent.lstat()
        if (not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid!=os.getuid()
            or stat.S_IMODE(info.st_mode)!=0o700): raise DiagnosticError("RESULT_DIRECTORY_PERMISSIONS_INVALID")
        try: path.lstat()
        except FileNotFoundError: pass
        else: raise DiagnosticError("RESULT_REPORT_ALREADY_EXISTS")
    except DiagnosticError: raise
    except OSError: raise DiagnosticError("RESULT_PATH_UNAVAILABLE") from None

def _run_allowed(command:list[str],run:Callable[...,subprocess.CompletedProcess[str]])->dict[str,Any]:
    # This assertion prevents callers from smuggling arbitrary commands into the runner.
    if command not in PLAN.podman_diagnostic_commands(): raise DiagnosticError("PODMAN_COMMAND_NOT_ALLOWLISTED")
    try: result=run(command,timeout=TIMEOUT,shell=False,capture_output=True,text=True,
                     env={"PATH":"/usr/bin:/bin","HOME":"/home/cdjensen94",
                          "XDG_RUNTIME_DIR":"/run/user/1000","LC_ALL":"C"})
    except (OSError,subprocess.TimeoutExpired): return {"ReturnCode":124,"Stdout":""}
    return {"ReturnCode":result.returncode if type(result.returncode)is int else 125,
            "Stdout":result.stdout if isinstance(result.stdout,str) else ""}

def collect_observations(run:Callable[...,subprocess.CompletedProcess[str]])->tuple[dict[str,Any],int]:
    commands=PLAN.podman_diagnostic_commands(); names=("Rootless","Exists","Container","Port","Image","Volume")
    observations={}; count=0
    rootless=_run_allowed(commands[0],run); count+=1; observations[names[0]]=rootless
    if rootless["ReturnCode"]!=0 or rootless["Stdout"].strip().casefold()!="true": return observations,count
    exists=_run_allowed(commands[1],run); count+=1; observations[names[1]]=exists
    # `exists` has explicit 0=present, 1=absent, 125=storage inaccessible semantics.
    if exists["ReturnCode"]!=0: return observations,count
    for index,name in ((2,"Container"),(3,"Port"),(4,"Image"),(5,"Volume")):
        observations[name]=_run_allowed(commands[index],run); count+=1
    return observations,count

def _write_once(path:Path,data:dict[str,Any])->None:
    try:
        if not path.is_absolute() or path.resolve(strict=False)!=path or REPORT_ROOT not in path.parents or ROOT in path.parents: raise DiagnosticError("RESULT_PATH_UNSAFE")
        parent=path.parent; info=parent.lstat()
        if not stat.S_ISDIR(info.st_mode) or stat.S_ISLNK(info.st_mode) or info.st_uid!=os.getuid() or stat.S_IMODE(info.st_mode)!=0o700: raise DiagnosticError("RESULT_DIRECTORY_PERMISSIONS_INVALID")
        fd=os.open(path,os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,"O_NOFOLLOW",0),0o600)
        with os.fdopen(fd,"w",encoding="utf-8") as f: json.dump(data,f,sort_keys=True,indent=2); f.write("\n"); f.flush(); os.fsync(f.fileno())
    except FileExistsError: raise DiagnosticError("RESULT_REPORT_ALREADY_EXISTS") from None

def _claim_once(result_path:Path,manifest_hash:str,now:datetime|None)->None:
    marker=result_path.with_name(result_path.name+".started")
    payload={"MarkerVersion":"cw-m5-026b-stopped-source-diagnostic-attempt-v1",
             "ManifestSha256":manifest_hash,
             "StartedAtUtc":(now or datetime.now(timezone.utc)).isoformat().replace("+00:00","Z")}
    try:
        fd=os.open(marker,os.O_WRONLY|os.O_CREAT|os.O_EXCL|getattr(os,"O_NOFOLLOW",0),0o600)
        with os.fdopen(fd,"w",encoding="utf-8") as f: json.dump(payload,f,sort_keys=True); f.write("\n"); f.flush(); os.fsync(f.fileno())
    except FileExistsError: raise DiagnosticError("AUTHORIZATION_ATTEMPT_ALREADY_CLAIMED") from None

def execute(manifest_path:Path,preflight_path:Path,result_path:Path,run:Callable[...,subprocess.CompletedProcess[str]]=subprocess.run,now:datetime|None=None)->dict[str,Any]:
    manifest,_,manifest_hash,preflight_hash=validate_execution(manifest_path,preflight_path,result_path,now)
    _claim_once(result_path,manifest_hash,now)
    observations,command_count=collect_observations(run)
    report=PLAN.classify_observations(observations)
    report["PodmanReadOnlyCommands"]=command_count
    report["AuthorizationApprovalId"]=manifest["ApprovalId"]
    report["ManifestSha256"]=manifest_hash
    report["PreflightReportSha256"]=preflight_hash
    report["GeneratedAtUtc"]=(now or datetime.now(timezone.utc)).isoformat().replace("+00:00","Z")
    _write_once(result_path,report)
    return report

def main(argv=None):
    parser=argparse.ArgumentParser(description=__doc__); parser.add_argument("--manifest",required=True); parser.add_argument("--preflight-report",required=True); parser.add_argument("--report",required=True); parser.add_argument("--execute-readonly-diagnostic",action="store_true")
    args=parser.parse_args(argv)
    if not args.execute_readonly_diagnostic: parser.error("explicit --execute-readonly-diagnostic is required")
    try:
        report=execute(Path(args.manifest),Path(args.preflight_report),Path(args.report))
        print(f"{report['Classification']}; podman_readonly_commands={report['PodmanReadOnlyCommands']}; database_connections=0; database_mutations=0; credentials_read=0")
        return 0 if report["Classification"] in {
            PLAN._classification("STOPPED_IDENTITY_MATCH"), PLAN._classification("RUNNING_IDENTITY_MATCH"),
            PLAN._classification("MISSING"), PLAN._classification("STORAGE_INACCESSIBLE"),
            PLAN._classification("RECREATED"), PLAN._classification("IDENTITY_MISMATCH"),
            PLAN._classification("STATE_UNEXPECTED"),
        } else 2
    except DiagnosticError as error: print(f"DIAGNOSTIC_NOT_EXECUTED: {error}",file=sys.stderr); return 2
if __name__=="__main__": raise SystemExit(main())
