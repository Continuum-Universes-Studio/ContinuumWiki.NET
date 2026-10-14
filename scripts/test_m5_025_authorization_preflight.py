#!/usr/bin/env python3
import importlib.util
import json
import os
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("preflight-m5-025.py")
SPEC = importlib.util.spec_from_file_location("cw_m5_025_preflight", MODULE_PATH)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)
NOW = datetime(2026, 10, 12, 12, 0, tzinfo=timezone.utc)


def valid_manifest():
    return {
        "ManifestVersion": MODULE.MANIFEST_VERSION,
        "ApprovalId": "CW-M5-025-test-approval",
        "ApprovedAtUtc": "2026-10-12T10:00:00Z",
        "ExpiresAtUtc": "2026-10-13T10:00:00Z",
        "Approver": {"Name": "Synthetic approver", "Role": "Project Owner"},
        "Operator": {"Name": "Synthetic operator", "Role": "System Administrator"},
        "TargetOwner": "Synthetic disposable owner",
        "Target": {
            "Disposable": True,
            "ContainerName": "continuumwiki-m5-025-test",
            "VolumeName": "continuumwiki-m5-025-test-data",
            "DatabaseName": "cw_m5_025_test",
            "Endpoint": "127.0.0.1:13325",
            "InstanceIdentity": "container=continuumwiki-m5-025-test;database=cw_m5_025_test;endpoint=127.0.0.1:13325;volume=continuumwiki-m5-025-test-data;image=digest-only",
            "EngineVersion": "MariaDB 11.8.x",
            "DatabaseIdentity": "continuumwiki_m5_025_app",
        },
        "PermittedOperations": {**{name: True for name in MODULE.REQUIRED_OPERATIONS}, "DatabaseCleanup": False, "SchemaDeletion": False, "ContainerVolumeDeletion": False},
        "Isolation": {"Production": False, "SharedWithProduction": False, "DedicatedCredentials": True, "Proof": "Synthetic isolated target proof"},
        "TestScope": {"Suite": ["grant-v3", "grant-restart"], "MaxConcurrentWorkers": 2, "CrashBoundaries": list(MODULE.REQUIRED_CRASH_BOUNDARIES)},
    }


class AuthorizationPreflightTests(unittest.TestCase):
    def test_valid_manifest_is_accepted_without_contact(self):
        self.assertEqual([], MODULE.validate_manifest(valid_manifest(), NOW))

    def test_old_manifest_version_is_denied(self):
        manifest = valid_manifest()
        manifest["ManifestVersion"] = "cw-m5-019-authorization-v2"
        self.assertIn("ManifestVersion:unsupported", MODULE.validate_manifest(manifest, NOW))

    def test_missing_grant_operation_is_denied(self):
        manifest = valid_manifest()
        del manifest["PermittedOperations"]["GrantConsumeMutations"]
        self.assertIn("PermittedOperations.GrantConsumeMutations:explicit-boolean-required", MODULE.validate_manifest(manifest, NOW))

    def test_false_certification_operation_is_denied(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["ApplyV3Migration"] = False
        self.assertIn("PermittedOperations.ApplyV3Migration:certification-true-required", MODULE.validate_manifest(manifest, NOW))

    def test_cleanup_and_schema_deletion_must_be_false(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["SchemaDeletion"] = True
        self.assertIn("PermittedOperations.SchemaDeletion:must-remain-false", MODULE.validate_manifest(manifest, NOW))

    def test_expired_or_wrong_target_is_denied(self):
        manifest = valid_manifest()
        manifest["ExpiresAtUtc"] = "2026-10-12T11:59:59Z"
        manifest["Target"]["DatabaseName"] = "cw_m5_019_operator4"
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("ExpiresAtUtc:expired", errors)
        self.assertIn("Target.DatabaseName:m5-025-prefix-required", errors)

    def test_not_yet_valid_approval_is_denied(self):
        manifest = valid_manifest()
        manifest["ApprovedAtUtc"] = "2026-10-12T13:00:00Z"
        self.assertIn("ApprovedAtUtc:not-yet-valid", MODULE.validate_manifest(manifest, NOW))

    def test_secret_properties_and_redacted_report(self):
        manifest = valid_manifest()
        manifest["Password"] = "must-not-be-accepted"
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("root.Password:secret-bearing-property-forbidden", errors)
        report = MODULE.build_report(valid_manifest(), [], "/private/approval.json", NOW)
        text = json.dumps(report)
        self.assertNotIn("Password", text)
        self.assertEqual(0, report["Preflight"]["DatabaseConnections"])
        self.assertFalse(report["Preflight"]["ConnectionEnvironmentVariablesRead"])

    def test_cli_invalid_manifest_is_denied_without_contact(self):
        with tempfile.TemporaryDirectory() as directory:
            manifest = Path(directory, "invalid.json")
            report = Path(directory, "report.json")
            manifest.write_text("{}", encoding="utf-8")
            self.assertEqual(3, MODULE.main(["--manifest", str(manifest), "--report", str(report)]))
            result = json.loads(report.read_text(encoding="utf-8"))
            self.assertEqual("CATEGORY_ADMIN_GRANT_MARIADB_AUTHORIZATION_MANIFEST_INVALID", result["Classification"])
            self.assertEqual(0, result["Preflight"]["DatabaseConnections"])
            self.assertEqual(0, result["Preflight"]["DatabaseMutations"])


if __name__ == "__main__":
    unittest.main()
