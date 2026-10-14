#!/usr/bin/env python3
"""Plan one CW-M5-026B stage without contacting a target.

The four shell entry points deliberately invoke this planner separately for
snapshot, restore, verification, and evidence retention.  This source-only
implementation emits an auditable plan and never launches Podman, mariadb,
mariadb-dump, or a subprocess.  A future live implementation must retain the
same manifest and stage gates rather than turning this into one pipeline.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import sys
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
PREFLIGHT_PATH = ROOT / "scripts" / "preflight-m5-026b.py"
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_preflight", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)

STAGES = ("snapshot", "restore", "verify", "evidence")
STAGE_PERMISSIONS = {
    "snapshot": ("ConnectSource", "VerifySourceIsolation", "BackupSource", "VerifyBackupArtifact", "RetainSanitizedEvidence"),
    "restore": ("ConnectDestination", "VerifyDestinationIsolation", "VerifyBackupArtifact", "RestoreDestination", "RetainSanitizedEvidence"),
    "verify": ("ConnectSource", "ConnectDestination", "VerifySourceIsolation", "VerifyDestinationIsolation", "VerifyRestore", "RetainSanitizedEvidence"),
    "evidence": ("RetainSanitizedEvidence",),
}


def _target(document: dict[str, Any], role: str) -> dict[str, Any]:
    return document[role.capitalize()]


def _safe_artifact(path: str) -> str:
    candidate = Path(path).expanduser()
    if not candidate.is_absolute():
        raise ValueError("artifact path must be absolute")
    if any(part.lower() in {"password", "secret", "token", "credential"} for part in candidate.parts):
        raise ValueError("artifact path contains a secret-bearing path component")
    return str(candidate)


def build_stage_plan(document: dict[str, Any], stage: str, artifact: str, report: str | None = None) -> dict[str, Any]:
    if stage not in STAGES:
        raise ValueError(f"unsupported stage: {stage}")
    errors = PREFLIGHT.validate_manifest(document)
    if errors:
        raise ValueError("authorization manifest rejected: " + ", ".join(errors))
    operations = document["PermittedOperations"]
    missing = [name for name in STAGE_PERMISSIONS[stage] if operations.get(name) is not True]
    if missing:
        raise ValueError(f"stage {stage} is not authorized: {', '.join(missing)}")
    artifact_path = _safe_artifact(artifact)
    report_path = _safe_artifact(report) if report else None
    source = _target(document, "source")
    destination = _target(document, "destination")
    plan: dict[str, Any] = {
        "PlanVersion": "cw-m5-026b-stage-plan-v1",
        "Milestone": "CW-M5-026B",
        "Stage": stage,
        "Execution": "PLAN_ONLY_SOURCE_TRACK",
        "AuthorizationManifestVersion": PREFLIGHT.MANIFEST_VERSION,
        "ApprovalIdRecorded": document["ApprovalId"],
        "DatabaseConnections": 0,
        "DatabaseMutations": 0,
        "PodmanCommands": 0,
        "CredentialsRead": False,
        "Source": {"DatabaseName": source["DatabaseName"], "Endpoint": source["Endpoint"], "InstanceIdentitySha256": PREFLIGHT._digest(source["InstanceIdentity"])},
        "Destination": {"DatabaseName": destination["DatabaseName"], "Endpoint": destination["Endpoint"], "InstanceIdentitySha256": PREFLIGHT._digest(destination["InstanceIdentity"])},
        "Artifact": {"Path": artifact_path, "ChecksumRequired": True, "ChecksumRecorded": False},
        "RequiredPermissions": list(STAGE_PERMISSIONS[stage]),
        "Production": {"Migration": "NO_GO", "RepositoryActivation": "NO_GO"},
    }
    if report_path:
        plan["EvidenceReportPath"] = report_path
    if stage == "snapshot":
        plan["CommandTemplate"] = [
            "mariadb-dump", "--defaults-extra-file=$CW_M5_026B_SOURCE_CNF",
            "--single-transaction", "--routines", "--events", "--triggers", "--hex-blob",
            "--default-character-set=utf8mb4", "--skip-comments", "--databases",
            source["DatabaseName"], ">", artifact_path,
        ]
        plan["ConsistencyAssumptions"] = [
            "All included tables must be InnoDB for transactional snapshot semantics.",
            "Concurrent DDL is not made transactional by --single-transaction and aborts the rehearsal if it invalidates the artifact.",
            "A nonzero dump exit or missing artifact checksum is not a backup.",
        ]
    elif stage == "restore":
        plan["Preconditions"] = [
            "Destination is a distinct, empty, loopback-bound, authorized target.",
            "Backup checksum has been independently verified.",
            "No drop, truncate, schema deletion, or source restore is permitted.",
        ]
        plan["CommandTemplate"] = [
            "mariadb", "--defaults-extra-file=$CW_M5_026B_DESTINATION_CNF",
            "--database", destination["DatabaseName"], "<", artifact_path,
        ]
    elif stage == "verify":
        plan["VerificationMatrix"] = {
            "SchemaAndLedgers": ["MySqlPageSchemaVersion", "MySqlCategorySchemaVersion"],
            "Tables": [
                "Page", "PageRevision", "PageTag", "PageReference", "PageCategoryIndexState",
                "PageCategoryMembership", "TwCategoryRecoveryOperation", "TwCategoryRecoveryAuditEvent",
                "TwCategoryAdministrativeGrant", "TwCategoryAdministrativeGrantAudit",
            ],
            "Relationships": "foreign-key and orphan checks for every listed relationship",
            "StateSemantics": ["unindexed", "indexed-empty", "populated", "incomplete", "failed", "unknown"],
            "Provenance": ["page/revision identity", "source page/revision identity", "wiki identity", "index generation"],
            "Recovery": ["checkpoint CAS/read-back", "audit sequence/event-key uniqueness", "idempotency"],
            "Grants": ["issued", "consumed", "revoked", "expired", "event-key uniqueness"],
            "Privacy": "aggregate counts, hashes, and redacted diagnostics only; no private row export",
        }
        plan["PostSnapshotWrites"] = "compare source and restored aggregate identity; record divergence and explicit reconciliation, never lossless rollback"
    else:
        plan["EvidenceRequirements"] = [
            "approval ID and target identity fingerprints, not credentials",
            "backup artifact size and SHA-256",
            "restore status and verification matrix results",
            "source/destination distinction and post-snapshot divergence",
            "connections, mutations, skipped checks, cleanup status, and limitations",
        ]
    return plan


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=STAGES, required=True)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--artifact", required=True)
    parser.add_argument("--report")
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        document = json.loads(Path(args.manifest).read_text(encoding="utf-8"))
        plan = build_stage_plan(document, args.stage, args.artifact, args.report)
    except (OSError, json.JSONDecodeError, KeyError, TypeError, ValueError) as error:
        print(f"CW-M5-026B {args.stage} planner refused: {error}", file=sys.stderr)
        return 3
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(plan, indent=2) + "\n", encoding="utf-8")
    print(f"CW-M5-026B {args.stage} plan written: {output}; database_connections=0; database_mutations=0; podman_commands=0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
