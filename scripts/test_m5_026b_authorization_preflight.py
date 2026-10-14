#!/usr/bin/env python3
import importlib.util
import json
import os
import pwd
import socket
import subprocess
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


PREFLIGHT = load("cw_m5_026b_preflight", ROOT / "scripts/preflight-m5-026b.py")
STAGE = load("cw_m5_026b_stage", ROOT / "scripts/cw-m5-026b/stage.py")
NOW = datetime(2026, 10, 12, 12, 0, tzinfo=timezone.utc)


def valid_manifest():
    operations = {name: False for name in PREFLIGHT.ALL_OPERATIONS}
    for name in PREFLIGHT.CORE_OPERATIONS:
        operations[name] = True
    return {
        "ManifestVersion": PREFLIGHT.MANIFEST_VERSION,
        "ApprovalId": "CW-M5-026B-REHEARSAL-test-approval",
        "ApprovedAtUtc": "2026-10-12T10:00:00Z",
        "ExpiresAtUtc": "2026-10-13T10:00:00Z",
        "Approver": {"Name": "Synthetic approver", "Role": "Project Owner"},
        "Operator": {"Name": "Synthetic operator", "Role": "System Administrator"},
        "TargetOwner": "Synthetic disposable owner",
        "ExecutionHost": {"HostName": socket.gethostname(), "UnixUser": pwd.getpwuid(os.getuid()).pw_name, "Uid": os.getuid(), "RootlessPodman": True},
        "Source": {
            "Disposable": True,
            "ContainerName": "continuumwiki-m5-026b-source-test",
            "VolumeName": "continuumwiki-m5-026b-source-test-data",
            "DatabaseName": "cw_m5_026b_source_test",
            "Endpoint": "127.0.0.1:13341",
            "InstanceIdentity": "milestone=CW-M5-026B;role=source;container=continuumwiki-m5-026b-source-test;volume=continuumwiki-m5-026b-source-test-data;database=cw_m5_026b_source_test;endpoint=127.0.0.1:13341;image=docker.io/library/mariadb@sha256:" + "a" * 64 + ";containerId=" + "1" * 64 + ";runId=source-test-run;",
            "EngineVersion": "MariaDB 11.8.x",
            "DatabaseIdentity": "continuumwiki_m5_026b_source_app",
            "CredentialReference": "/home/operator/.local/state/continuumwiki/cw-m5-026b/source/application.env",
        },
        "Destination": {
            "Disposable": True,
            "ContainerName": "continuumwiki-m5-026b-destination-test",
            "VolumeName": "continuumwiki-m5-026b-destination-test-data",
            "DatabaseName": "cw_m5_026b_destination_test",
            "Endpoint": "127.0.0.1:13342",
            "InstanceIdentity": "milestone=CW-M5-026B;role=destination;container=continuumwiki-m5-026b-destination-test;volume=continuumwiki-m5-026b-destination-test-data;database=cw_m5_026b_destination_test;endpoint=127.0.0.1:13342;image=docker.io/library/mariadb@sha256:" + "a" * 64 + ";containerId=" + "2" * 64 + ";runId=destination-test-run;",
            "EngineVersion": "MariaDB 11.8.x",
            "DatabaseIdentity": "continuumwiki_m5_026b_destination_app",
            "CredentialReference": "/home/operator/.local/state/continuumwiki/cw-m5-026b/destination/application.env",
        },
        "PermittedOperations": operations,
        "Isolation": {
            "Production": False,
            "SharedWithProduction": False,
            "DedicatedCredentials": True,
            "Proof": "Synthetic loopback, rootless, dedicated target proof",
        },
        "SnapshotPlan": {
            "Client": "mariadb-dump",
            "SingleTransaction": True,
            "IncludeRoutines": True,
            "IncludeEvents": True,
            "IncludeTriggers": True,
            "IncludeHexBlob": True,
            "CharacterSet": "utf8mb4",
            "ArtifactDirectory": "/tmp/cw-m5-026b-artifacts",
            "CredentialTransport": "option-file",
            "PostSnapshotWritePolicy": "record-divergence-and-reconcile-explicitly",
            "DdlFreezeConfirmed": True,
        },
        "TestScope": {
            "Suite": ["snapshot", "restore", "table-content-readback"],
            "MaxConcurrentWorkers": 1,
            "CrashBoundaries": list(PREFLIGHT.REQUIRED_CRASH_BOUNDARIES),
            "PostSnapshotWriteCases": False,
        },
    }


class M5026BPreflightTests(unittest.TestCase):
    def test_valid_two_target_manifest_is_accepted(self):
        self.assertEqual([], PREFLIGHT.validate_manifest(valid_manifest(), NOW))

    def test_source_and_destination_must_be_distinct(self):
        manifest = valid_manifest()
        manifest["Destination"] = dict(manifest["Source"])
        self.assertTrue(any("must-be-distinct" in error for error in PREFLIGHT.validate_manifest(manifest, NOW)))

    def test_core_operation_false_is_denied(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["RestoreDestination"] = False
        self.assertIn("PermittedOperations.RestoreDestination:rehearsal-true-required", PREFLIGHT.validate_manifest(manifest, NOW))

    def test_destructive_and_fault_scopes_are_explicit(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["CleanupDestination"] = True
        manifest["PermittedOperations"]["FaultInjection"] = True
        self.assertEqual([], PREFLIGHT.validate_manifest(manifest, NOW))
        report = PREFLIGHT.build_report(manifest, [], "/private/report.json", NOW)
        self.assertTrue(report["Permissions"]["CleanupAuthorized"])
        self.assertTrue(report["Permissions"]["FaultInjectionAuthorized"])

    def test_legacy_or_ambiguous_permission_is_denied(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["Cleanup"] = True
        errors = PREFLIGHT.validate_manifest(manifest, NOW)
        self.assertTrue(any("unsupported-or-ambiguous-operation" in error for error in errors))

    def test_production_shared_and_non_loopback_targets_are_denied(self):
        manifest = valid_manifest()
        manifest["Isolation"]["Production"] = True
        manifest["Isolation"]["SharedWithProduction"] = True
        manifest["Destination"]["Endpoint"] = "192.0.2.10:13342"
        errors = PREFLIGHT.validate_manifest(manifest, NOW)
        self.assertIn("Isolation.Production:must-be-false", errors)
        self.assertIn("Isolation.SharedWithProduction:must-be-false", errors)
        self.assertIn("Destination.Endpoint:loopback-unprivileged-port-required", errors)

    def test_host_runtime_pins_and_ddl_freeze_are_required(self):
        manifest = valid_manifest()
        manifest["ExecutionHost"]["Uid"] = 0
        manifest["SnapshotPlan"]["DdlFreezeConfirmed"] = False
        manifest["Source"]["InstanceIdentity"] = manifest["Source"]["InstanceIdentity"].replace("containerId=" + "1" * 64, "containerId=missing")
        errors = PREFLIGHT.validate_manifest(manifest, NOW)
        self.assertIn("ExecutionHost.Uid:positive-integer-required", errors)
        self.assertIn("SnapshotPlan.DdlFreezeConfirmed:operator-attestation-required", errors)
        self.assertIn("Source.InstanceIdentity:container-id-pin-required", errors)

    def test_authorization_validity_is_bounded_to_twenty_four_hours(self):
        manifest = valid_manifest()
        manifest["ExpiresAtUtc"] = "2026-10-13T10:00:01Z"
        self.assertIn("ExpiresAtUtc:maximum-24-hours-exceeded", PREFLIGHT.validate_manifest(manifest, NOW))

    def test_secret_properties_are_denied_and_report_is_redacted(self):
        manifest = valid_manifest()
        manifest["Password"] = "must-not-appear"
        errors = PREFLIGHT.validate_manifest(manifest, NOW)
        self.assertTrue(any("secret-bearing-property-forbidden" in error for error in errors))
        report = PREFLIGHT.build_report(valid_manifest(), [], "/private/report.json", NOW)
        self.assertNotIn("must-not-appear", json.dumps(report))
        self.assertEqual(0, report["Preflight"]["DatabaseConnections"])
        self.assertFalse(report["Preflight"]["ConnectionEnvironmentVariablesRead"])

    def test_cli_invalid_manifest_is_zero_contact(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory, "invalid.json")
            report = Path(directory, "report.json")
            manifest.write_text("{}", encoding="utf-8")
            result = PREFLIGHT.main(["--manifest", str(manifest), "--report", str(report)])
            self.assertEqual(3, result)
            output = json.loads(report.read_text(encoding="utf-8"))
            self.assertEqual(0, output["Preflight"]["DatabaseConnections"])
            self.assertEqual(0, output["Preflight"]["PodmanCommands"])

    def test_stage_planner_is_separate_and_nonconnecting(self):
        manifest = valid_manifest()
        plan = STAGE.build_stage_plan(manifest, "snapshot", "/tmp/cw-m5-026b/backup.sql")
        self.assertEqual("PLAN_ONLY_SOURCE_TRACK", plan["Execution"])
        self.assertIn("--single-transaction", plan["CommandTemplate"])
        self.assertEqual(0, plan["DatabaseConnections"])
        self.assertEqual(0, plan["PodmanCommands"])
        verify = STAGE.build_stage_plan(manifest, "verify", "/tmp/cw-m5-026b/backup.sql")
        self.assertIn("PageCategoryMembership", verify["VerificationMatrix"]["Tables"])
        self.assertIn("TwCategoryAdministrativeGrantAudit", verify["VerificationMatrix"]["Tables"])

    def test_stage_requires_its_own_permission(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["RestoreDestination"] = False
        with self.assertRaisesRegex(ValueError, "RestoreDestination"):
            STAGE.build_stage_plan(manifest, "restore", "/tmp/cw-m5-026b/backup.sql")

    def test_shell_wrappers_are_syntax_valid_and_do_not_write_live_targets(self):
        wrappers = [
            "scripts/preflight-m5-026b.sh",
            "scripts/cw-m5-026b/snapshot-m5-026b.sh",
            "scripts/cw-m5-026b/restore-m5-026b.sh",
            "scripts/cw-m5-026b/verify-m5-026b.sh",
            "scripts/cw-m5-026b/evidence-m5-026b.sh",
            "scripts/cw-m5-026b/rehearse-m5-026b.sh",
        ]
        result = subprocess.run(["bash", "-n", *wrappers], cwd=ROOT, text=True, capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
