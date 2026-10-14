#!/usr/bin/env python3
"""Fail-closed continuation: verify retained source, then create destination only."""

from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
import pwd
import secrets
import socket
import stat
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[2]
PROVISIONER_PATH = ROOT / "scripts/cw-m5-026b/provision_targets.py"
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-continuation.py"


def _load(name: str, path: Path) -> Any:
    spec = importlib.util.spec_from_file_location(name, path)
    if not spec or not spec.loader:
        raise RuntimeError("continuation component unavailable")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PROVISIONER = _load("cw_m5_026b_provisioner", PROVISIONER_PATH)
PREFLIGHT = _load("cw_m5_026b_continuation_preflight", PREFLIGHT_PATH)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def target_context(evidence: dict[str, Any], original: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    targets = evidence["Targets"]
    plan = original["TargetPlan"]
    image = original["Image"]["Reference"]
    if image != PREFLIGHT.PINNED_IMAGE_REFERENCE or original.get("ApprovalId") != PREFLIGHT.PRIOR_APPROVAL_ID:
        raise ValueError("original immutable image or provisioning approval identity changed")
    src, dst = targets["Source"], targets["Destination"]
    source = {"ContainerName": src["ContainerName"], "ContainerId": src["ContainerId"], "VolumeName": src["VolumeName"],
              "DatabaseName": src["DatabaseName"], "Endpoint": src["Endpoint"], "DatabaseIdentity": src["DatabaseIdentity"], "ImageReference": image,
              "CredentialDirectory": src["CredentialDirectory"], "IdentityPrefix": src["InstanceIdentity"]}
    destination = {"ContainerName": dst["ContainerName"], "VolumeName": dst["VolumeName"], "DatabaseName": dst["DatabaseName"],
                   "Endpoint": dst["Endpoint"], "DatabaseIdentity": dst["DatabaseIdentity"],
                   "CredentialDirectory": dst["CredentialDirectory"], "IdentityPrefix": dst["InstanceIdentity"], "ImageReference": image}
    if (source["CredentialDirectory"] != PREFLIGHT.PINNED_SOURCE_CREDENTIAL_DIRECTORY
            or destination["CredentialDirectory"] != PREFLIGHT.PINNED_DESTINATION_CREDENTIAL_DIRECTORY):
        raise ValueError("retained credential directory identity changed")
    for role, observed in (("Source", src), ("Destination", dst)):
        expected = plan[role]
        for field in ("ContainerName", "VolumeName", "DatabaseName", "Endpoint", "DatabaseIdentity", "CredentialDirectory", "IdentityPrefix"):
            evidence_field = "InstanceIdentity" if field == "IdentityPrefix" else field
            if observed.get(evidence_field) != expected.get(field):
                raise ValueError("sanitized provisioning evidence conflicts with the original target plan")
    digest = image.rsplit("@sha256:", 1)[-1]
    if (evidence.get("Classification") != "PROVISIONING_FAILED_RESOURCES_RETAINED"
            or evidence.get("Provisioning", {}).get("RootlessPodman") is not True
            or evidence.get("Provisioning", {}).get("PinnedImageReference") != image
            or src.get("ObservedStatus") != "running" or dst.get("ObservedStatus") != "not-created"
            or src.get("ImageDigest") != f"sha256:{digest}"
            or src.get("CredentialDirectoryMode") != "700" or src.get("CredentialFileMode") != "600"
            or src.get("ObservedPortBinding") != f"{src.get('Endpoint')}->3306/tcp"
            or evidence.get("CrossTargetIsolation", {}).get("LoopbackOnly") is not True
            or evidence.get("CrossTargetIsolation", {}).get("ProductionContacted") is not False
            or evidence.get("CrossTargetIsolation", {}).get("PreviousTargetReused") is not False):
        raise ValueError("retained-source evidence is incomplete or inconsistent")
    return source, destination


def authorization_gate(source_doc: Any, destination_doc: Any, source_target: dict[str, Any], destination_target: dict[str, Any],
                       source_path: Path, destination_path: Path, report: dict[str, Any], prior_approval_id: str,
                       host_name: str, original: dict[str, Any], host_evidence: dict[str, Any], now: datetime | None = None) -> None:
    if (not isinstance(report, dict) or report.get("Valid") is not True
            or report.get("ReportVersion") != "cw-m5-026b-continuation-preflight-v1"
            or report.get("Classification") != "CATEGORY_CONTINUATION_AUTHORIZATIONS_VALID_NONCONNECTING"
            or report.get("PodmanCommands") != 0 or report.get("DatabaseQueries") != 0
            or report.get("DatabaseMutations") != 0 or report.get("Errors") != []):
        raise ValueError("nonconnecting operation-specific authorization preflight is not valid")
    files = report.get("AuthorizationFiles", {})
    if files.get("SourceSha256") != sha256(source_path) or files.get("DestinationSha256") != sha256(destination_path):
        raise ValueError("authorization files differ from the nonconnecting preflight evidence")
    source_errors = PREFLIGHT.validate_authorization(source_doc, PREFLIGHT.SOURCE_OPERATION, source_target, now)
    destination_errors = PREFLIGHT.validate_authorization(destination_doc, PREFLIGHT.DESTINATION_OPERATION, destination_target, now)
    if source_doc.get("ApprovalId") == prior_approval_id or destination_doc.get("ApprovalId") == prior_approval_id:
        raise ValueError("continuation approval must not reuse the initial provisioning approval")
    if source_doc.get("ApprovalId") == destination_doc.get("ApprovalId"):
        raise ValueError("source verification and destination provisioning require distinct approvals")
    if source_errors or destination_errors:
        raise ValueError("operation-specific authorization is invalid or expired")
    observed_host = host_evidence.get("ObservedHost", {})
    if (host_evidence.get("Classification") != "APPROVAL_RECORDED_MANIFEST_INCOMPLETE"
            or observed_host.get("Status") != "VERIFIED_READ_ONLY"
            or host_evidence.get("PodmanInspection", {}).get("Rootless") != "VERIFIED_OPERATOR_EVIDENCE"):
        raise ValueError("verified host and rootless-Podman evidence is unavailable")
    expected_host = {"HostName": observed_host.get("Hostname"), "UnixUser": observed_host.get("InvokingUser"), "Uid": observed_host.get("InvokingUid"), "RootlessPodmanRequired": True}
    for doc in (source_doc, destination_doc):
        if (doc.get("Host") != expected_host or doc.get("Operator") != original.get("Operator")
                or doc.get("Approver") != original.get("Approver") or doc.get("TargetOwner") != original.get("TargetOwner")):
            raise ValueError("authorization actor or host does not match recorded operator/owner evidence")
    if (source_doc["Host"]["HostName"] != host_name
            or source_doc["Host"]["UnixUser"] != pwd.getpwuid(os.getuid()).pw_name
            or source_doc["Host"]["Uid"] != os.getuid()):
        raise ValueError("authorization host or rootless Podman account does not match the current host")


def inspect_retained_source(source: dict[str, Any], run_id: str, runner: Callable[..., subprocess.CompletedProcess[str]], uid: int) -> dict[str, Any]:
    rootless = runner(["podman", "info", "--format", "{{.Host.Security.Rootless}}"])
    if rootless.returncode != 0 or not isinstance(rootless.stdout, str) or rootless.stdout.strip() != "true":
        raise RuntimeError("rootless Podman verification failed")
    result = runner(["podman", "container", "inspect", "--format", "{{json .}}", source["ContainerName"]])
    if result.returncode != 0:
        raise RuntimeError("retained source container inspection failed")
    try:
        raw = json.loads(result.stdout)
        info = raw[0] if isinstance(raw, list) and len(raw) == 1 else raw
    except (json.JSONDecodeError, TypeError):
        raise RuntimeError("retained source container inspection was malformed") from None
    if not isinstance(info, dict):
        raise RuntimeError("retained source container inspection was malformed")
    config = info.get("Config") or {}
    state = info.get("State") or {}
    labels = config.get("Labels") or {} if isinstance(config, dict) else {}
    image_ref = config.get("Image") if isinstance(config, dict) else None
    mounts = info.get("Mounts") or []
    if (not isinstance(state, dict) or not isinstance(labels, dict) or not isinstance(mounts, list)
            or any(not isinstance(mount, dict) for mount in mounts)
            or info.get("Id") != source["ContainerId"] or state.get("Running") is not True
            or labels.get("io.continuumwiki.m5-026b-run-id") != run_id
            or labels.get("io.continuumwiki.m5-026b-database") != source["DatabaseName"]
            or image_ref != source["ImageReference"]
            or not any(m.get("Name") == source["VolumeName"] and m.get("Destination") == "/var/lib/mysql" for m in mounts)):
        raise RuntimeError("retained source container identity, labels, image, state, or volume mismatch")
    image = runner(["podman", "image", "inspect", "--format", "{{json .}}", source["ImageReference"]])
    if image.returncode != 0:
        raise RuntimeError("pinned source image inspection failed")
    try:
        image_info = json.loads(image.stdout)
        image_info = image_info[0] if isinstance(image_info, list) and len(image_info) == 1 else image_info
    except (json.JSONDecodeError, TypeError):
        raise RuntimeError("pinned source image inspection was malformed") from None
    if not isinstance(image_info, dict):
        raise RuntimeError("pinned source image inspection was malformed")
    repo_digests = image_info.get("RepoDigests") or []
    if (not isinstance(repo_digests, list) or any(not isinstance(item, str) for item in repo_digests)
            or source["ImageReference"] not in repo_digests or info.get("Image") != image_info.get("Id")):
        raise RuntimeError("retained source immutable image identity mismatch")
    volume = runner(["podman", "volume", "inspect", "--format", "{{json .}}", source["VolumeName"]])
    if volume.returncode != 0:
        raise RuntimeError("retained source volume inspection failed")
    try:
        volume_info = json.loads(volume.stdout)
        volume_info = volume_info[0] if isinstance(volume_info, list) and len(volume_info) == 1 else volume_info
    except (json.JSONDecodeError, TypeError):
        raise RuntimeError("retained source volume inspection was malformed") from None
    if not isinstance(volume_info, dict):
        raise RuntimeError("retained source volume inspection was malformed")
    volume_labels = volume_info.get("Labels") or {}
    if (not isinstance(volume_labels, dict) or volume_info.get("Name") != source["VolumeName"]
            or volume_labels.get("io.continuumwiki.m5-026b-run-id") != run_id
            or volume_labels.get("io.continuumwiki.m5-026b-role") != "source"):
        raise RuntimeError("retained source volume identity mismatch")
    published = runner(["podman", "port", source["ContainerName"], "3306/tcp"])
    if published.returncode != 0 or not isinstance(published.stdout, str) or published.stdout.strip() != f"127.0.0.1:{source['Endpoint'].rsplit(':', 1)[1]}":
        raise RuntimeError("retained source endpoint is not bound exactly to loopback")
    credential_dir = Path(source["CredentialDirectory"])
    if credential_dir.expanduser().absolute() != credential_dir.expanduser().resolve(strict=False):
        raise RuntimeError("retained source credential path resolves through a symlink or alias")
    try:
        directory_stat = credential_dir.lstat()
        env_stat = (credential_dir / "environment.env").lstat()
        app_stat = (credential_dir / "application.env").lstat()
    except OSError:
        raise RuntimeError("retained source credential metadata unavailable") from None
    if (not stat.S_ISDIR(directory_stat.st_mode) or stat.S_ISLNK(directory_stat.st_mode)
            or stat.S_IMODE(directory_stat.st_mode) != 0o700 or directory_stat.st_uid != uid
            or any(not stat.S_ISREG(x.st_mode) or stat.S_ISLNK(x.st_mode) or stat.S_IMODE(x.st_mode) != 0o600 or x.st_uid != uid for x in (env_stat, app_stat))):
        raise RuntimeError("retained source credential ownership or permissions mismatch")
    return {"ContainerIdVerified": True, "ImageDigestVerified": True, "VolumeVerified": True,
            "LoopbackVerified": True, "CredentialOwnershipAndModesVerified": True}


def destination_is_absent(target: dict[str, Any], runner: Callable[..., subprocess.CompletedProcess[str]], provisioner: Any) -> None:
    for kind, args in (("container", ["podman", "container", "exists", target["ContainerName"]]),
                       ("volume", ["podman", "volume", "exists", target["VolumeName"]])):
        result = runner(args)
        if result.returncode == 0:
            raise RuntimeError(f"refusing existing destination {kind}")
        if result.returncode != 1:
            raise RuntimeError(f"destination {kind} absence could not be verified")
    if not provisioner.port_available(target["Endpoint"]):
        raise RuntimeError("destination loopback endpoint is unavailable")
    raw_credential = Path(target["CredentialDirectory"]).expanduser()
    if raw_credential.absolute() != raw_credential.resolve(strict=False):
        raise RuntimeError("destination credential path resolves through a symlink or alias")
    provisioner.safe_directory(target["CredentialDirectory"], must_be_new=True)


def execute_steps(*, source_verify: Callable[[], dict[str, Any]], destination_create: Callable[[], dict[str, Any]]) -> dict[str, Any]:
    """Enforce the source-success barrier before the single destination action."""
    source_result = source_verify()
    if not isinstance(source_result, dict) or source_result.get("Accepted") is not True:
        raise RuntimeError("authorized source identity query did not verify")
    destination_result = destination_create()
    return {"SourceVerification": source_result, "DestinationProvisioning": destination_result}


def source_identity_query_command(source: dict[str, Any], environment_file: str, sql: str) -> list[str]:
    """Build the one authorized query, explicitly selecting its pinned database."""
    return ["podman", "exec", "--env-file", environment_file, source["ContainerName"], "mariadb",
            "--protocol=socket", "-uroot", "-D", source["DatabaseName"], "-N", "-B", "-e", sql]


def failure_result(phase: str, error: str, source_runtime_identity: dict[str, Any] | None) -> dict[str, Any]:
    """Build sanitized failure evidence; caller supplies only the provisioner sanitizer output."""
    result = {
        "ReportVersion": "cw-m5-026b-continuation-result-v1",
        "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Classification": "CONTINUATION_FAILED_RESOURCES_RETAINED",
        "Phase": phase,
        "Error": error,
        "DestinationMayBePartiallyCreated": phase in {"DESTINATION_PROVISIONING_STARTED", "DESTINATION_PROVISIONED"},
        "DoNotRetryWithoutInspection": phase in {"DESTINATION_PROVISIONING_STARTED", "DESTINATION_PROVISIONED"},
        "AutomaticCleanup": False,
        "ProductionMigration": "NO_GO",
        "RepositoryActivation": "NO_GO",
    }
    if source_runtime_identity is not None:
        result["SourceRuntimeIdentity"] = source_runtime_identity
    return result


def _runner(command: list[str], **kwargs: Any) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, **kwargs)


def write_report_once(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def reserve_report(path: Path):
    """Exclusively claim evidence path before any host operation."""
    path.parent.mkdir(parents=True, exist_ok=True)
    stream = path.open("x+", encoding="utf-8")
    os.chmod(path, 0o600)
    write_reserved_report(stream, {
        "ReportVersion": "cw-m5-026b-continuation-result-v1",
        "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "Classification": "CONTINUATION_IN_PROGRESS",
        "DoNotRetryWithoutInspection": True,
        "AutomaticCleanup": False,
        "ProductionMigration": "NO_GO",
        "RepositoryActivation": "NO_GO",
    })
    return stream


def write_reserved_report(stream, value: dict[str, Any]) -> None:
    stream.seek(0)
    stream.truncate()
    json.dump(value, stream, indent=2)
    stream.write("\n")
    stream.flush()
    os.fsync(stream.fileno())


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-authorization", required=True)
    parser.add_argument("--destination-authorization", required=True)
    parser.add_argument("--preflight-report", required=True)
    parser.add_argument("--provisioning-evidence", default="secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json")
    parser.add_argument("--original-authorization", default="secure/evidence/cw-m5-026b-provisioning-authorization.json")
    parser.add_argument("--host-evidence", default="artifacts/validation/mediawiki-category-backup-restore-readonly-host-inspection-v1.json")
    parser.add_argument("--report", required=True)
    parser.add_argument("--continue-live", action="store_true")
    parser.add_argument("--confirm-destination-provisioning", action="store_true")
    args = parser.parse_args(argv)
    if not args.continue_live:
        print("CW-M5-026B continuation plan-only: no Podman or database operations will run.")
        return 0
    if not args.confirm_destination_provisioning:
        print("CW-M5-026B continuation: --continue-live requires --confirm-destination-provisioning", file=sys.stderr)
        return 64
    report_path = Path(args.report)
    phase = "AUTHORIZATION_NOT_VALIDATED"
    report_stream = None
    source_runtime_identity: dict[str, Any] | None = None
    try:
        report_stream = reserve_report(report_path)
        source_auth_path, destination_auth_path = Path(args.source_authorization), Path(args.destination_authorization)
        source_auth = json.loads(source_auth_path.read_text(encoding="utf-8"))
        destination_auth = json.loads(destination_auth_path.read_text(encoding="utf-8"))
        preflight = json.loads(Path(args.preflight_report).read_text(encoding="utf-8"))
        evidence_path, original_path, host_path = Path(args.provisioning_evidence), Path(args.original_authorization), Path(args.host_evidence)
        if (sha256(evidence_path) != PREFLIGHT.PINNED_EVIDENCE_HASHES["provisioning"]
                or sha256(original_path) != PREFLIGHT.PINNED_EVIDENCE_HASHES["original-authorization"]
                or sha256(host_path) != PREFLIGHT.PINNED_EVIDENCE_HASHES["host"]):
            raise ValueError("pinned retained-resource or host provenance evidence changed")
        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
        original = json.loads(original_path.read_text(encoding="utf-8"))
        host_evidence = json.loads(host_path.read_text(encoding="utf-8"))
        source, destination = target_context(evidence, original)
        authorization_gate(source_auth, destination_auth, source, destination, source_auth_path, destination_auth_path,
                           preflight, original["ApprovalId"], socket.gethostname(), original, host_evidence)
        phase = "AUTHORIZATIONS_VALIDATED"
        runner = _runner
        source_run_id = evidence["Provisioning"]["RunId"]

        def verify_source() -> dict[str, Any]:
            nonlocal phase, source_runtime_identity
            source_check = inspect_retained_source(source, source_run_id, runner, os.getuid())
            phase = "SOURCE_CONTAINER_AND_CREDENTIALS_VERIFIED"
            destination_is_absent(destination, runner, PROVISIONER)
            source_errors = PREFLIGHT.validate_authorization(source_auth, PREFLIGHT.SOURCE_OPERATION, source)
            if source_errors:
                raise RuntimeError("source verification authorization expired or became invalid before query")
            env_file = str(Path(source["CredentialDirectory"]) / "environment.env")
            write_reserved_report(report_stream, {"ReportVersion": "cw-m5-026b-continuation-result-v1", "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                                                  "Classification": "CONTINUATION_IN_PROGRESS", "Phase": "SOURCE_IDENTITY_QUERY_STARTED",
                                                  "DestinationProvisioningMayHaveStarted": False, "DoNotRetryWithoutInspection": True,
                                                  "AutomaticCleanup": False, "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"})
            query = runner(source_identity_query_command(source, env_file, source_auth["Target"]["Sql"]))
            evaluation = PROVISIONER.classify_runtime_identity_query(query.returncode, query.stdout, source["DatabaseName"])
            source_runtime_identity = PROVISIONER.sanitize_runtime_identity_diagnostic(
                evaluation, exit_code=query.returncode if query.returncode else None)
            if not evaluation["Accepted"]:
                raise RuntimeError("authorized source identity query did not verify")
            phase = "SOURCE_IDENTITY_VERIFIED"
            return {"Accepted": True, "SourceInspection": source_check, "RuntimeIdentity": source_runtime_identity}

        def create_destination() -> dict[str, Any]:
            nonlocal phase
            # Destination approval is independently checked again at the point of creation.
            current_errors = PREFLIGHT.validate_authorization(destination_auth, PREFLIGHT.DESTINATION_OPERATION, destination)
            if (current_errors or sha256(source_auth_path) != preflight["AuthorizationFiles"]["SourceSha256"]
                    or sha256(destination_auth_path) != preflight["AuthorizationFiles"]["DestinationSha256"]):
                raise RuntimeError("destination provisioning authorization expired or became invalid before creation")
            destination_is_absent(destination, runner, PROVISIONER)
            dest_path = PROVISIONER.safe_directory(destination["CredentialDirectory"], must_be_new=True)
            phase = "DESTINATION_PROVISIONING_STARTED"
            destination_run_id = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + f"-{os.getpid()}-{secrets.token_hex(16)}"
            write_reserved_report(report_stream, {"ReportVersion": "cw-m5-026b-continuation-result-v1", "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                                                  "Classification": "CONTINUATION_IN_PROGRESS", "Phase": phase,
                                                  "DestinationMayBePartiallyCreated": True, "DoNotRetryWithoutInspection": True,
                                                  "DestinationContainerName": destination["ContainerName"], "DestinationVolumeName": destination["VolumeName"],
                                                  "AutomaticCleanup": False, "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO"})
            return PROVISIONER.provision_one(destination, dest_path, destination["ImageReference"], destination_run_id)

        step_result = execute_steps(source_verify=verify_source, destination_create=create_destination)
        source_check = step_result["SourceVerification"]["SourceInspection"]
        diagnostic = step_result["SourceVerification"]["RuntimeIdentity"]
        dest_result = step_result["DestinationProvisioning"]
        phase = "DESTINATION_PROVISIONED"
        result = {"ReportVersion": "cw-m5-026b-continuation-result-v1", "GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                  "Classification": "SOURCE_VERIFIED_DESTINATION_PROVISIONED", "SourceVerification": source_check,
                  "SourceRuntimeIdentity": diagnostic, "Destination": dest_result,
                  "Authorization": {"SourceApprovalId": source_auth["ApprovalId"], "DestinationApprovalId": destination_auth["ApprovalId"],
                                    "SourceManifestSha256": sha256(source_auth_path), "DestinationManifestSha256": sha256(destination_auth_path)},
                  "SourceContainerId": source["ContainerId"], "PinnedImageReference": source["ImageReference"],
                  "SourceVolumeName": source["VolumeName"], "DestinationContainerName": destination["ContainerName"],
                  "ProductionMigration": "NO_GO", "RepositoryActivation": "NO_GO", "CleanupPerformed": False}
        write_reserved_report(report_stream, result)
        print(f"CW-M5-026B continuation: classification={result['Classification']}; report={report_path}")
        return 0
    except (OSError, ValueError, RuntimeError, KeyError, TypeError, AttributeError) as error:
        # Do not serialize command output, credentials, exception values from subprocesses, or secrets.
        safe_error = str(error) if not isinstance(error, subprocess.SubprocessError) else "external operation failed"
        if report_stream is not None:
            try:
                write_reserved_report(report_stream, failure_result(phase, safe_error, source_runtime_identity))
            except OSError:
                pass
        print(f"CW-M5-026B continuation: {safe_error}", file=sys.stderr)
        return 64
    finally:
        if report_stream is not None:
            report_stream.close()


if __name__ == "__main__":
    sys.exit(main())
