#!/usr/bin/env python3
import importlib.util
import json
import os
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("preflight-m5-019.py")
ROOT = Path(__file__).resolve().parents[1]
CANDIDATE_PATH = ROOT / "artifacts/validation/mediawiki-category-authorization-candidate-operator4-v1.json"
SPEC = importlib.util.spec_from_file_location("cw_m5_019_preflight", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


NOW = datetime(2026, 10, 11, 12, 0, tzinfo=timezone.utc)


def valid_manifest():
    return {
        "ManifestVersion": MODULE.MANIFEST_VERSION,
        "ApprovalId": "approval:cw-m5-019:test",
        "ApprovedAtUtc": "2026-10-11T10:00:00Z",
        "ExpiresAtUtc": "2026-10-12T10:00:00Z",
        "Approver": {"Name": "Synthetic Approver", "Role": "Owner"},
        "Operator": {"Name": "Synthetic Operator", "Role": "DBA"},
        "TargetOwner": "Synthetic disposable owner",
        "Target": {
            "Disposable": True,
            "ContainerName": "continuumwiki-m5-019-test",
            "VolumeName": "continuumwiki-m5-019-test-data",
            "DatabaseName": "cw_m5_019_test",
            "Endpoint": "127.0.0.1:13318",
            "InstanceIdentity": "container=continuumwiki-m5-019-test;database=cw_m5_019_test;endpoint=127.0.0.1:13318;volume=continuumwiki-m5-019-test-data;image=digest-only",
            "EngineVersion": "MariaDB 11.8.x",
            "DedicatedApplicationIdentity": "continuumwiki_m5_019_app",
        },
        "PermittedOperations": {
            **{name: True for name in MODULE.REQUIRED_OPERATIONS},
            "CreateDisposableSchema": False,
            "DeleteDisposableSchema": False,
            "DatabaseCleanup": False,
            "ContainerVolumeDeletion": False,
        },
        "Isolation": {"Production": False, "SharedWithProduction": False, "DedicatedCredentials": True, "Proof": "Synthetic isolated target proof"},
        "Provisioning": {"Procedure": "Synthetic provision", "CleanupProcedure": "Synthetic cleanup", "CleanupOwner": "Synthetic operator", "CleanupAttestationPath": "evidence/cleanup.json"},
        "TestScope": {"Suite": ["category-v1", "recovery-v2"], "MaxConcurrentWorkers": 2, "MaxPages": 100, "CrashBoundaries": list(MODULE.REQUIRED_CRASH_BOUNDARIES)},
    }


class AuthorizationPreflightTests(unittest.TestCase):
    def test_valid_manifest_is_accepted_at_source_only(self):
        self.assertEqual([], MODULE.validate_manifest(valid_manifest(), NOW))

    def test_missing_operation_is_denied(self):
        manifest = valid_manifest()
        del manifest["PermittedOperations"]["ApplyV2Migration"]
        self.assertIn("PermittedOperations.ApplyV2Migration:explicit-boolean-required", MODULE.validate_manifest(manifest, NOW))

    def test_certification_permission_false_is_denied(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["ApplyV2Migration"] = False
        self.assertIn("PermittedOperations.ApplyV2Migration:certification-true-required", MODULE.validate_manifest(manifest, NOW))

    def test_expired_or_production_target_is_denied(self):
        manifest = valid_manifest()
        manifest["ExpiresAtUtc"] = "2026-10-11T11:59:59Z"
        manifest["Isolation"]["Production"] = True
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("ExpiresAtUtc:expired", errors)
        self.assertIn("Isolation.Production:must-be-false", errors)

    def test_secret_properties_and_non_loopback_target_are_denied(self):
        manifest = valid_manifest()
        manifest["Password"] = "must-not-be-accepted"
        manifest["Target"]["Endpoint"] = "192.0.2.10:13318"
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("root.Password:secret-bearing-property-forbidden", errors)
        self.assertIn("Target.Endpoint:loopback-unprivileged-port-required", errors)

    def test_missing_approver_and_crash_scope_are_denied(self):
        manifest = valid_manifest()
        del manifest["Approver"]
        manifest["TestScope"]["CrashBoundaries"] = []
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("Approver:required-object", errors)
        self.assertIn("TestScope.CrashBoundaries:complete-required-set", errors)

    def test_certification_can_explicitly_refuse_all_destructive_operations(self):
        manifest = valid_manifest()
        self.assertEqual([], MODULE.validate_manifest(manifest, NOW))

    def test_legacy_version_and_ambiguous_cleanup_keys_are_denied(self):
        manifest = valid_manifest()
        manifest["ManifestVersion"] = "cw-m5-019-authorization-v1"
        manifest["PermittedOperations"]["Cleanup"] = True
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("ManifestVersion:unsupported", errors)
        self.assertIn("PermittedOperations.Cleanup:unsupported-or-legacy-operation", errors)

    def test_target_identity_mismatch_is_denied(self):
        manifest = valid_manifest()
        manifest["Target"]["DatabaseName"] = "cw_m5_019_other"
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("Target.InstanceIdentity:isolated-database-identity-required", errors)

    def test_invalid_approval_id_and_expiry_window_are_denied(self):
        manifest = valid_manifest()
        manifest["ApprovalId"] = "operator approval with spaces"
        manifest["ApprovedAtUtc"] = "2026-10-12T10:00:00Z"
        manifest["ExpiresAtUtc"] = "2026-10-12T09:00:00Z"
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("ApprovalId:invalid-token", errors)
        self.assertIn("ExpiresAtUtc:not-after-approval", errors)

    def test_report_is_redacted_and_never_reads_connection_environment(self):
        os.environ["CONTINUUMWIKI_MYSQL_TEST_CONNECTION"] = "Password=must-never-appear"
        try:
            report = MODULE.build_report(valid_manifest(), [], "/private/approval.json", NOW)
        finally:
            del os.environ["CONTINUUMWIKI_MYSQL_TEST_CONNECTION"]
        text = json.dumps(report)
        self.assertNotIn("Password=", text)
        self.assertNotIn("127.0.0.1:13318", text)
        self.assertEqual(0, report["Preflight"]["DatabaseConnections"])
        self.assertEqual(0, report["Preflight"]["DatabaseMutations"])
        self.assertFalse(report["Preflight"]["ConnectionEnvironmentVariablesRead"])

    def test_cli_invalid_manifest_writes_denied_zero_contact_report(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory, "invalid.json")
            report = Path(directory, "report.json")
            manifest.write_text("{}", encoding="utf-8")
            self.assertEqual(3, MODULE.main(["--manifest", str(manifest), "--report", str(report)]))
            output = json.loads(report.read_text(encoding="utf-8"))
            self.assertEqual("CATEGORY_CERTIFICATION_READINESS_SOURCE_PARTIAL", output["Classification"])
            self.assertEqual(0, output["Preflight"]["DatabaseConnections"])
            self.assertEqual(0, output["Preflight"]["DatabaseMutations"])

    def test_cli_v2_certification_manifest_with_destructive_scopes_false_passes(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory, "authorized-v2.json")
            report = Path(directory, "report.json")
            manifest.write_text(json.dumps(valid_manifest()), encoding="utf-8")
            self.assertEqual(0, MODULE.main(["--manifest", str(manifest), "--report", str(report)]))
            output = json.loads(report.read_text(encoding="utf-8"))
            self.assertTrue(output["Manifest"]["Validated"])
            self.assertEqual("cw-m5-019-authorization-v2", output["Manifest"]["AuthorizationManifestVersion"])
            self.assertEqual(0, output["Preflight"]["DatabaseConnections"])
            self.assertEqual(0, output["Preflight"]["DatabaseMutations"])

    def test_operator4_candidate_is_not_an_authorization_manifest(self):
        candidate = json.loads(CANDIDATE_PATH.read_text(encoding="utf-8"))
        errors = MODULE.validate_manifest(candidate, NOW)
        self.assertTrue(errors)
        self.assertTrue(candidate["DoNotPassToPreflight"])
        self.assertFalse(candidate["AuthoritativeManifestWritten"])
        self.assertEqual("cw_m5_019_operator4", candidate["TargetEvidence"]["Target"]["DatabaseName"])
        self.assertEqual("127.0.0.1:13321", candidate["TargetEvidence"]["Target"]["Endpoint"])
        self.assertFalse(candidate["CredentialsIncluded"])


if __name__ == "__main__":
    unittest.main()
