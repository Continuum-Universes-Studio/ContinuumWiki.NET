#!/usr/bin/env python3
"""Nonconnecting validation for the two separately authorized M5-026B resume steps."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
VERSION = "cw-m5-026b-continuation-authorization-v1"
SOURCE_OPERATION = "VerifyRetainedSourceIdentity"
DESTINATION_OPERATION = "ProvisionDestinationTarget"
MAX_VALIDITY = timedelta(hours=24)
SOURCE_SQL = "SELECT DATABASE(), VERSION(), @@version_comment;"
PINNED_IMAGE_REFERENCE = "docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15"
PRIOR_APPROVAL_ID = "CW-M5-026B-PROVISION-20261012-001"
PINNED_EVIDENCE_HASHES = {
    "provisioning": "bd25a8ea1c0663287c8ba288d10469cae9951a993becd2486aa39cdb9a591a81",
    "original-authorization": "56c445d08e655070e7e4fd0f97c2af77e0a1a34dbb2a40873c8450aede819d77",
    "host": "55ea82f6868bd6c30e68c986218e2aa91abc84e3a01a6047fad0399262ac1bf8",
}
PINNED_SOURCE_CREDENTIAL_DIRECTORY = "/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/source-ro1"
PINNED_DESTINATION_CREDENTIAL_DIRECTORY = "/home/cdjensen94/.local/state/continuumwiki/cw-m5-026b/destination-ro1"
FORBIDDEN_KEYS = re.compile(r"password|passwd|secret|token|connectionstring|privatekey|credentialmaterial", re.I)
FORBIDDEN_VALUES = re.compile(r"password\s*=|user\s*id\s*=|access[_-]?token|-----BEGIN .*PRIVATE KEY-----", re.I)


def _scan_secrets(value: Any, path: str, errors: list[str]) -> None:
    if isinstance(value, dict):
        for key, child in value.items():
            if FORBIDDEN_KEYS.search(str(key)):
                errors.append(f"{path}.{key}:secret-bearing-property-forbidden")
            _scan_secrets(child, f"{path}.{key}", errors)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            _scan_secrets(child, f"{path}[{index}]", errors)
    elif isinstance(value, str) and FORBIDDEN_VALUES.search(value):
        errors.append(f"{path}:secret-bearing-value-forbidden")


def _utc(value: Any, field: str, errors: list[str]) -> datetime | None:
    if not isinstance(value, str) or not value.endswith("Z"):
        errors.append(f"{field}:utc-z-timestamp-required")
        return None
    try:
        return datetime.fromisoformat(value[:-1] + "+00:00").astimezone(timezone.utc)
    except ValueError:
        errors.append(f"{field}:invalid-timestamp")
        return None


def _identity_prefix(target: dict[str, Any], role: str) -> str:
    return (
        f"milestone=CW-M5-026B;role={role};container={target.get('ContainerName')};"
        f"volume={target.get('VolumeName')};database={target.get('DatabaseName')};"
        f"endpoint={target.get('Endpoint')};"
    )


def validate_authorization(document: Any, expected_operation: str, expected_target: dict[str, Any], now: datetime | None = None) -> list[str]:
    errors: list[str] = []
    if not isinstance(document, dict):
        return ["root:required-object"]
    _scan_secrets(document, "root", errors)
    allowed_root = {"ManifestVersion", "Operation", "ApprovalId", "ApprovedAtUtc", "ExpiresAtUtc", "Approver", "Operator", "TargetOwner", "Host", "Target", "Permissions", "Scope"}
    errors.extend(f"root.{key}:unexpected-property" for key in sorted(set(document) - allowed_root))
    if document.get("ManifestVersion") != VERSION:
        errors.append("ManifestVersion:unsupported")
    if document.get("Operation") != expected_operation:
        errors.append("Operation:unexpected-or-cross-scope")
    approval_id = document.get("ApprovalId")
    if not isinstance(approval_id, str) or not re.fullmatch(r"[A-Za-z0-9._:-]{1,128}", approval_id):
        errors.append("ApprovalId:invalid-token")
    approved = _utc(document.get("ApprovedAtUtc"), "ApprovedAtUtc", errors)
    expires = _utc(document.get("ExpiresAtUtc"), "ExpiresAtUtc", errors)
    current = (now or datetime.now(timezone.utc)).astimezone(timezone.utc)
    if approved and approved > current:
        errors.append("ApprovedAtUtc:not-yet-valid")
    if approved and expires:
        if expires <= approved:
            errors.append("ExpiresAtUtc:not-after-approval")
        elif expires - approved > MAX_VALIDITY:
            errors.append("ExpiresAtUtc:validity-exceeds-24-hours")
    if expires and expires <= current:
        errors.append("ExpiresAtUtc:expired")
    for key in ("Approver", "Operator"):
        person = document.get(key)
        if not isinstance(person, dict) or not isinstance(person.get("Name"), str) or not person["Name"].strip() or not isinstance(person.get("Role"), str) or not person["Role"].strip():
            errors.append(f"{key}:genuine-name-and-role-required")
        elif set(person) != {"Name", "Role"} or len(person["Name"]) > 160 or len(person["Role"]) > 160:
            errors.append(f"{key}:unexpected-or-unbounded-properties")
    if not isinstance(document.get("TargetOwner"), str) or not document["TargetOwner"].strip():
        errors.append("TargetOwner:required")
    host = document.get("Host")
    if (not isinstance(host, dict) or not isinstance(host.get("HostName"), str) or not host["HostName"].strip()
            or not isinstance(host.get("UnixUser"), str) or not host["UnixUser"].strip()
            or not isinstance(host.get("Uid"), int) or isinstance(host.get("Uid"), bool) or host["Uid"] < 1000
            or host.get("RootlessPodmanRequired") is not True):
        errors.append("Host:exact-host-user-uid-and-rootless-required")
    elif set(host) != {"HostName", "UnixUser", "Uid", "RootlessPodmanRequired"}:
        errors.append("Host:unexpected-properties")
    target = document.get("Target")
    if not isinstance(target, dict):
        errors.append("Target:required-object")
        target = {}
    target_allowed = ({"ContainerName", "ContainerId", "VolumeName", "DatabaseName", "Endpoint", "DatabaseIdentity", "ImageReference", "CredentialDirectory", "IdentityPrefix", "Sql"}
                      if expected_operation == SOURCE_OPERATION else
                      {"ContainerName", "VolumeName", "DatabaseName", "Endpoint", "DatabaseIdentity", "ImageReference", "CredentialDirectory", "IdentityPrefix"})
    errors.extend(f"Target.{key}:unexpected-property" for key in sorted(set(target) - target_allowed))
    for key, expected in expected_target.items():
        if target.get(key) != expected:
            errors.append(f"Target.{key}:does-not-match-retained-provisioning-evidence")
    pinned_credential = PINNED_SOURCE_CREDENTIAL_DIRECTORY if expected_operation == SOURCE_OPERATION else PINNED_DESTINATION_CREDENTIAL_DIRECTORY
    if target.get("CredentialDirectory") != pinned_credential:
        errors.append("Target.CredentialDirectory:does-not-match-pinned-retained-path")
    if target.get("ImageReference") != PINNED_IMAGE_REFERENCE or target.get("ImageReference") != expected_target.get("ImageReference"):
        errors.append("Target.ImageReference:authorized-immutable-image-required")
    role = "source" if expected_operation == SOURCE_OPERATION else "destination"
    if target.get("IdentityPrefix") != _identity_prefix(target, role):
        errors.append("Target.IdentityPrefix:exact-target-identity-required")
    permissions = document.get("Permissions")
    expected_permissions = {
        "VerifyRetainedSourceIdentity": expected_operation == SOURCE_OPERATION,
        "ProvisionDestinationTarget": expected_operation == DESTINATION_OPERATION,
        "RetainSanitizedEvidence": True,
    }
    if not isinstance(permissions, dict) or permissions != expected_permissions:
        errors.append("Permissions:must-grant-only-this-operation-and-evidence-retention")
    scope = document.get("Scope")
    expected_scope = {
        "Production": False, "SharedWithProduction": False, "RehearsalRightsGranted": False,
        "BackupAllowed": False, "RestoreAllowed": False, "SchemaMigrationAllowed": False,
        "CertificationAllowed": False, "CleanupAllowed": False, "SchemaDeletionAllowed": False,
        "ContainerVolumeDeletionAllowed": False,
    }
    if not isinstance(scope, dict) or scope != expected_scope:
        errors.append("Scope:prohibited-operations-must-remain-false")
    if expected_operation == SOURCE_OPERATION and target.get("Sql") != SOURCE_SQL:
        errors.append("Target.Sql:only-authorized-runtime-identity-query-allowed")
    return sorted(set(errors))


def _sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def build_preflight_report(source_doc: Any, destination_doc: Any, source_errors: list[str], destination_errors: list[str], source_path: str, destination_path: str, generated_at: datetime | None = None) -> dict[str, Any]:
    errors = [f"Source.{error}" for error in source_errors] + [f"Destination.{error}" for error in destination_errors]
    source_id = source_doc.get("ApprovalId") if isinstance(source_doc, dict) else None
    destination_id = destination_doc.get("ApprovalId") if isinstance(destination_doc, dict) else None
    if source_id == destination_id:
        errors.append("ApprovalId:operation-authorizations-must-be-distinct")
    source_operator = source_doc.get("Operator") if isinstance(source_doc, dict) else None
    destination_operator = destination_doc.get("Operator") if isinstance(destination_doc, dict) else None
    if source_operator != destination_operator:
        errors.append("Operator:both-operation-authorizations-must-name-same-operator")
    source_host = source_doc.get("Host") if isinstance(source_doc, dict) else None
    destination_host = destination_doc.get("Host") if isinstance(destination_doc, dict) else None
    if source_host != destination_host:
        errors.append("Host:both-operation-authorizations-must-name-same-host")
    source_owner = source_doc.get("TargetOwner") if isinstance(source_doc, dict) else None
    destination_owner = destination_doc.get("TargetOwner") if isinstance(destination_doc, dict) else None
    if source_owner != destination_owner:
        errors.append("TargetOwner:both-operation-authorizations-must-name-same-owner")
    generated = (generated_at or datetime.now(timezone.utc)).astimezone(timezone.utc)
    safe_hash = lambda path: _sha256_file(Path(path)) if Path(path).is_file() else None
    return {
        "ReportVersion": "cw-m5-026b-continuation-preflight-v1",
        "GeneratedAtUtc": generated.isoformat().replace("+00:00", "Z"),
        "Classification": "CATEGORY_CONTINUATION_AUTHORIZATIONS_VALID_NONCONNECTING" if not errors else "CATEGORY_CONTINUATION_AUTHORIZATIONS_INVALID",
        "Valid": not errors,
        "Operations": {"Source": SOURCE_OPERATION, "Destination": DESTINATION_OPERATION},
        "AuthorizationFiles": {
            "SourceSha256": safe_hash(source_path),
            "DestinationSha256": safe_hash(destination_path),
            "ApprovalIdsDistinct": source_id != destination_id,
        },
        "PodmanCommands": 0,
        "DatabaseConnections": 0,
        "DatabaseQueries": 0,
        "DatabaseMutations": 0,
        "Errors": sorted(set(errors)),
        "ProductionMigration": "NO_GO",
        "RepositoryActivation": "NO_GO",
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-authorization", required=True)
    parser.add_argument("--destination-authorization", required=True)
    parser.add_argument("--provisioning-evidence", default="secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json")
    parser.add_argument("--original-authorization", default="secure/evidence/cw-m5-026b-provisioning-authorization.json")
    parser.add_argument("--host-evidence", default="artifacts/validation/mediawiki-category-backup-restore-readonly-host-inspection-v1.json")
    parser.add_argument("--report", required=True)
    args = parser.parse_args(argv)
    try:
        source_path, destination_path = Path(args.source_authorization), Path(args.destination_authorization)
        evidence_path, original_path, host_path = Path(args.provisioning_evidence), Path(args.original_authorization), Path(args.host_evidence)
        if (_sha256_file(evidence_path) != PINNED_EVIDENCE_HASHES["provisioning"]
                or _sha256_file(original_path) != PINNED_EVIDENCE_HASHES["original-authorization"]
                or _sha256_file(host_path) != PINNED_EVIDENCE_HASHES["host"]):
            raise ValueError("pinned retained-resource or host provenance evidence changed")
        source_doc = json.loads(source_path.read_text(encoding="utf-8"))
        destination_doc = json.loads(destination_path.read_text(encoding="utf-8"))
        evidence = json.loads(evidence_path.read_text(encoding="utf-8"))
        original = json.loads(original_path.read_text(encoding="utf-8"))
        host_evidence = json.loads(host_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        print("CW-M5-026B continuation preflight: required authorization or sanitized target evidence unavailable", file=sys.stderr)
        return 64
    except ValueError:
        print("CW-M5-026B continuation preflight: pinned authorization or provenance evidence mismatch", file=sys.stderr)
        return 64
    source_evidence = evidence.get("Targets", {}).get("Source", {})
    destination_evidence = evidence.get("Targets", {}).get("Destination", {})
    approved = original.get("TargetPlan", {})
    image_reference = original.get("Image", {}).get("Reference")
    if original.get("ApprovalId") != PRIOR_APPROVAL_ID or image_reference != PINNED_IMAGE_REFERENCE:
        print("CW-M5-026B continuation preflight: original approval or immutable image identity mismatch", file=sys.stderr)
        return 64
    image_digest = image_reference.rsplit("@sha256:", 1)[-1] if isinstance(image_reference, str) and "@sha256:" in image_reference else None
    source_expected = {
        "ContainerName": source_evidence.get("ContainerName"), "ContainerId": source_evidence.get("ContainerId"),
        "VolumeName": source_evidence.get("VolumeName"), "DatabaseName": source_evidence.get("DatabaseName"),
        "Endpoint": source_evidence.get("Endpoint"), "DatabaseIdentity": source_evidence.get("DatabaseIdentity"), "ImageReference": image_reference,
        "CredentialDirectory": source_evidence.get("CredentialDirectory"), "IdentityPrefix": source_evidence.get("InstanceIdentity"),
    }
    destination_expected = {
        "ContainerName": destination_evidence.get("ContainerName"), "VolumeName": destination_evidence.get("VolumeName"),
        "DatabaseName": destination_evidence.get("DatabaseName"), "Endpoint": destination_evidence.get("Endpoint"),
        "DatabaseIdentity": destination_evidence.get("DatabaseIdentity"), "CredentialDirectory": destination_evidence.get("CredentialDirectory"),
        "IdentityPrefix": destination_evidence.get("InstanceIdentity"), "ImageReference": image_reference,
    }
    for role, observed in (("Source", source_evidence), ("Destination", destination_evidence)):
        planned = approved.get(role, {})
        if any(observed.get("InstanceIdentity" if field == "IdentityPrefix" else field) != planned.get(field) for field in ("ContainerName", "VolumeName", "DatabaseName", "Endpoint", "DatabaseIdentity", "CredentialDirectory", "IdentityPrefix")):
            print(f"CW-M5-026B continuation preflight: {role.lower()} provisioning evidence does not match original target plan", file=sys.stderr)
            return 64
    if (evidence.get("Classification") != "PROVISIONING_FAILED_RESOURCES_RETAINED"
            or evidence.get("Provisioning", {}).get("RootlessPodman") is not True
            or evidence.get("Provisioning", {}).get("PinnedImageReference") != image_reference
            or source_evidence.get("ObservedStatus") != "running"
            or destination_evidence.get("ObservedStatus") != "not-created"
            or not isinstance(source_expected["ContainerId"], str)
            or not re.fullmatch(r"[a-f0-9]{64}", source_expected["ContainerId"])
            or not isinstance(image_digest, str)
            or source_evidence.get("ImageDigest") != f"sha256:{image_digest}"
            or source_evidence.get("CredentialDirectoryMode") != "700"
            or source_evidence.get("CredentialFileMode") != "600"
            or source_evidence.get("ObservedPortBinding") != f"{source_evidence.get('Endpoint')}->3306/tcp"
            or evidence.get("CrossTargetIsolation", {}).get("LoopbackOnly") is not True
            or evidence.get("CrossTargetIsolation", {}).get("ProductionContacted") is not False
            or evidence.get("CrossTargetIsolation", {}).get("PreviousTargetReused") is not False):
        print("CW-M5-026B continuation preflight: prior failed-source state is not eligible for continuation", file=sys.stderr)
        return 64
    source_errors = validate_authorization(source_doc, SOURCE_OPERATION, source_expected)
    destination_errors = validate_authorization(destination_doc, DESTINATION_OPERATION, destination_expected)
    observed_host = host_evidence.get("ObservedHost", {})
    expected_host = {"HostName": observed_host.get("Hostname"), "UnixUser": observed_host.get("InvokingUser"), "Uid": observed_host.get("InvokingUid"), "RootlessPodmanRequired": True}
    if (host_evidence.get("Classification") != "APPROVAL_RECORDED_MANIFEST_INCOMPLETE" or observed_host.get("Status") != "VERIFIED_READ_ONLY"
            or host_evidence.get("PodmanInspection", {}).get("Rootless") != "VERIFIED_OPERATOR_EVIDENCE"):
        source_errors.append("Host:verified-rootless-host-evidence-unavailable")
        destination_errors.append("Host:verified-rootless-host-evidence-unavailable")
    for label, document in (("Source", source_doc), ("Destination", destination_doc)):
        if isinstance(document, dict):
            if document.get("Host") != expected_host:
                (source_errors if label == "Source" else destination_errors).append("Host:does-not-match-verified-operator-host-evidence")
            if document.get("TargetOwner") != original.get("TargetOwner"):
                (source_errors if label == "Source" else destination_errors).append("TargetOwner:does-not-match-original-target-owner")
            if document.get("Operator") != original.get("Operator"):
                (source_errors if label == "Source" else destination_errors).append("Operator:does-not-match-verified-provisioning-operator")
            if document.get("Approver") != original.get("Approver"):
                (source_errors if label == "Source" else destination_errors).append("Approver:does-not-match-recorded-project-owner")
    prior_approval_id = original.get("ApprovalId")
    if isinstance(source_doc, dict) and source_doc.get("ApprovalId") == prior_approval_id:
        source_errors.append("ApprovalId:must-not-reuse-prior-provisioning-approval")
    if isinstance(destination_doc, dict) and destination_doc.get("ApprovalId") == prior_approval_id:
        destination_errors.append("ApprovalId:must-not-reuse-prior-provisioning-approval")
    report = build_preflight_report(source_doc, destination_doc, source_errors, destination_errors, args.source_authorization, args.destination_authorization)
    report_path = Path(args.report)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with report_path.open("x", encoding="utf-8") as stream:
            json.dump(report, stream, indent=2)
            stream.write("\n")
    except FileExistsError:
        print("CW-M5-026B continuation preflight: refusing to overwrite an existing report", file=sys.stderr)
        return 64
    print(f"CW-M5-026B continuation preflight: classification={report['Classification']}; podman_commands=0; database_queries=0")
    return 0 if report["Valid"] else 3


if __name__ == "__main__":
    sys.exit(main())
