#!/usr/bin/env python3
import importlib.util
import json
import subprocess
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_provisioning", ROOT / "scripts/preflight-m5-026b-provisioning.py")
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)
IMPLEMENTATION_SPEC = importlib.util.spec_from_file_location("cw_m5_026b_provisioning_impl", ROOT / "scripts/cw-m5-026b/provision_targets.py")
assert IMPLEMENTATION_SPEC and IMPLEMENTATION_SPEC.loader
IMPLEMENTATION = importlib.util.module_from_spec(IMPLEMENTATION_SPEC)
IMPLEMENTATION_SPEC.loader.exec_module(IMPLEMENTATION)
NOW = datetime(2026, 10, 12, 12, 0, tzinfo=timezone.utc)


def target(role: str, endpoint: str) -> dict[str, str]:
    suffix = "source-a" if role == "source" else "destination-b"
    return {
        "ContainerName": f"continuumwiki-m5-026b-{role}-{suffix.split('-')[-1]}",
        "VolumeName": f"continuumwiki-m5-026b-{role}-{suffix.split('-')[-1]}-data",
        "DatabaseName": f"cw_m5_026b_{role}_{suffix.replace('-', '_')}",
        "Endpoint": endpoint,
        "DatabaseIdentity": f"continuumwiki_m5_026b_{role}_app",
        "CredentialDirectory": f"/tmp/cw-m5-026b-credentials/{role}",
        "IdentityPrefix": f"milestone=CW-M5-026B;role={role};container=continuumwiki-m5-026b-{role}-{suffix.split('-')[-1]};volume=continuumwiki-m5-026b-{role}-{suffix.split('-')[-1]}-data;database=cw_m5_026b_{role}_{suffix.replace('-', '_')};endpoint={endpoint};",
    }


def valid_manifest() -> dict:
    return {
        "ManifestVersion": MODULE.MANIFEST_VERSION,
        "ApprovalId": "CW-M5-026B-provision-test",
        "ApprovedAtUtc": "2026-10-11T10:00:00Z",
        "ExpiresAtUtc": "2026-10-13T10:00:00Z",
        "Approver": {"Name": "Synthetic approver", "Role": "Project Owner"},
        "Operator": {"Name": "Synthetic operator", "Role": "System Administrator"},
        "TargetOwner": "Synthetic disposable owner",
        "Host": {"HostName": "test-host", "AllowedEndpointHost": "127.0.0.1", "RootlessPodmanRequired": True, "PortRange": {"Minimum": 13341, "Maximum": 13380}},
        "Image": {"Reference": "docker.io/library/mariadb@sha256:" + "a" * 64, "EngineVersion": "MariaDB 11.8.x", "DigestRequired": True},
        "TargetPlan": {"Source": target("source", "127.0.0.1:13341"), "Destination": target("destination", "127.0.0.1:13342")},
        "ExcludedResources": {
            "ContainerPrefixes": ["continuumwiki-m5-019-", "continuumwiki-m5-025-", "production"],
            "VolumePrefixes": ["continuumwiki-m5-019-", "continuumwiki-m5-025-", "production"],
            "DatabasePrefixes": ["cw_m5_019_", "cw_m5_025_", "production"],
            "ExactNames": ["operator4", "production"],
        },
        "PermittedOperations": {
            "ProvisionSourceTarget": True,
            "ProvisionDestinationTarget": True,
            "VerifyTargetIsolation": True,
            "RetainSanitizedEvidence": True,
            "CleanupSource": False,
            "CleanupDestination": False,
            "SchemaDeletionSource": False,
            "SchemaDeletionDestination": False,
            "ContainerVolumeDeletionSource": False,
            "ContainerVolumeDeletionDestination": False,
        },
        "Scope": {
            "FreshTargetsRequired": True,
            "ReuseExistingResources": False,
            "Production": False,
            "SharedWithProduction": False,
            "DedicatedCredentials": True,
            "RehearsalRightsGranted": False,
            "BackupAllowed": False,
            "RestoreAllowed": False,
            "SchemaMigrationAllowed": False,
            "CertificationAllowed": False,
            "CleanupAllowed": False,
        },
    }


class M5026BProvisioningTests(unittest.TestCase):
    def test_runtime_identity_valid_mariadb_11_8_tuple_is_accepted(self):
        result = IMPLEMENTATION.classify_runtime_identity_query(
            0, "cw_m5_026b_source_ro1\t11.8.5-MariaDB-ubu2404\tMariaDB Server\n", "cw_m5_026b_source_ro1"
        )
        self.assertTrue(result["Accepted"])
        self.assertEqual([], result["Codes"])

    def test_runtime_identity_reports_independent_predicate_failures(self):
        result = IMPLEMENTATION.classify_runtime_identity_query(0, "wrong\t10.11.8\tOther Server\n", "cw_m5_026b_source_ro1")
        self.assertFalse(result["Accepted"])
        self.assertEqual(
            ["DATABASE_NAME_MISMATCH", "VERSION_FAMILY_MISMATCH", "VERSION_COMMENT_MISMATCH"],
            result["Codes"],
        )

    def test_runtime_identity_accepts_mixed_case_mariadb_comment(self):
        result = IMPLEMENTATION.classify_runtime_identity_query(0, "cw_m5_026b_source_ro1\t11.8.5\tmArIaDb.org binary distribution\n", "cw_m5_026b_source_ro1")
        self.assertTrue(result["Accepted"])
        self.assertEqual([], result["Codes"])
        diagnostic = IMPLEMENTATION.sanitize_runtime_identity_diagnostic(result)
        self.assertTrue(diagnostic["FieldShape"]["VersionComment"]["ContainsCaseInsensitiveMariaDB"])
        self.assertFalse(diagnostic["FieldShape"]["VersionComment"]["ContainsCaseSensitiveMariaDB"])

    def test_runtime_identity_rejects_non_mariadb_comment(self):
        result = IMPLEMENTATION.classify_runtime_identity_query(0, "cw_m5_026b_source_ro1\t11.8.5\tPostgreSQL Server\n", "cw_m5_026b_source_ro1")
        self.assertFalse(result["Accepted"])
        self.assertEqual(["VERSION_COMMENT_MISMATCH"], result["Codes"])

    def test_runtime_identity_rejects_unsupported_version_family(self):
        result = IMPLEMENTATION.classify_runtime_identity_query(0, "cw_m5_026b_source_ro1\t10.11.8-MariaDB\tMariaDB Server\n", "cw_m5_026b_source_ro1")
        self.assertFalse(result["Accepted"])
        self.assertEqual(["VERSION_FAMILY_MISMATCH"], result["Codes"])

    def test_runtime_identity_rejects_shape_null_empty_and_whitespace(self):
        for output, expected in (
            ("cw_m5_026b_source_ro1\t11.8.5\n", "ROW_SHAPE_INVALID"),
            ("cw_m5_026b_source_ro1\t11.8.5\tMariaDB\textra\n", "ROW_SHAPE_INVALID"),
            ("cw_m5_026b_source_ro1\t\tMariaDB\n", "FIELD_EMPTY"),
            ("cw_m5_026b_source_ro1\t11.8.5\tNULL\n", "FIELD_NULL"),
            (" cw_m5_026b_source_ro1\t11.8.5\tMariaDB\n", "WHITESPACE_MISMATCH"),
            ("cw_m5_026b_source_ro1\t11.8.5\tMariaDB\n\n", "OUTPUT_FORMAT_INVALID"),
        ):
            result = IMPLEMENTATION.classify_runtime_identity_query(0, output, "cw_m5_026b_source_ro1")
            self.assertIn(expected, result["Codes"], output)

    def test_runtime_identity_command_failure_is_separate_and_redacted(self):
        result = IMPLEMENTATION.classify_runtime_identity_query(125, "secret-looking\toutput\n", "cw_m5_026b_source_ro1")
        self.assertEqual(["RUNTIME_QUERY_COMMAND_FAILED"], result["Codes"])
        self.assertEqual(125, result["ExitCode"])
        diagnostic = IMPLEMENTATION.sanitize_runtime_identity_diagnostic(result)
        self.assertNotIn("secret-looking", json.dumps(diagnostic))

    def test_runtime_identity_diagnostic_is_deterministic_and_omits_values(self):
        output = "wrong\t10.10.1\tOther Server\n"
        first = IMPLEMENTATION.sanitize_runtime_identity_diagnostic(
            IMPLEMENTATION.classify_runtime_identity_query(0, output, "cw_m5_026b_source_ro1")
        )
        second = IMPLEMENTATION.sanitize_runtime_identity_diagnostic(
            IMPLEMENTATION.classify_runtime_identity_query(0, output, "cw_m5_026b_source_ro1")
        )
        self.assertEqual(first, second)
        serialized = json.dumps(first)
        self.assertNotIn("cw_m5_026b_source_ro1", serialized)
        self.assertNotIn("Other Server", serialized)

    def test_runtime_identity_failure_is_safe_to_persist_in_metadata(self):
        source = target("source", "127.0.0.1:13341")
        destination = target("destination", "127.0.0.1:13342")
        failure = IMPLEMENTATION.sanitize_runtime_identity_diagnostic(
            IMPLEMENTATION.classify_runtime_identity_query(
                0,
                "wrong\t10.10.1\tOther Server\n",
                source["DatabaseName"],
            )
        )
        report = IMPLEMENTATION.metadata(
            valid_manifest(),
            "PROVISIONING_FAILED_RESOURCES_RETAINED",
            "synthetic-run",
            {
                "Source": IMPLEMENTATION.target_evidence(source, Path("/tmp/source")),
                "Destination": IMPLEMENTATION.target_evidence(destination, Path("/tmp/destination")),
            },
            Path("/tmp/source"),
            Path("/tmp/destination"),
            failure,
        )
        self.assertEqual(failure, report["Failure"])
        serialized = json.dumps(report)
        self.assertNotIn("Other Server", serialized)
        self.assertNotIn("wrong", serialized)

    def test_source_and_destination_provisioning_run_ids_are_retained_independently(self):
        source = target("source", "127.0.0.1:13341")
        destination = target("destination", "127.0.0.1:13342")
        source_evidence = IMPLEMENTATION.target_evidence(
            source, Path("/tmp/source"), provisioning_run_id="20261013T010000Z-source"
        )
        destination_evidence = IMPLEMENTATION.target_evidence(
            destination, Path("/tmp/destination"), provisioning_run_id="20261013T010001Z-destination"
        )
        self.assertEqual("20261013T010000Z-source", source_evidence["ProvisioningRunId"])
        self.assertEqual("20261013T010001Z-destination", destination_evidence["ProvisioningRunId"])
        self.assertNotEqual(source_evidence["ProvisioningRunId"], destination_evidence["ProvisioningRunId"])

    def test_valid_provisioning_manifest_is_accepted(self):
        self.assertEqual([], MODULE.validate_manifest(valid_manifest(), NOW))

    def test_digest_is_required(self):
        manifest = valid_manifest()
        manifest["Image"]["Reference"] = "docker.io/library/mariadb:11.8"
        self.assertIn("Image.Reference:immutable-mariadb-11-8-digest-required", MODULE.validate_manifest(manifest, NOW))

    def test_source_and_destination_must_be_distinct(self):
        manifest = valid_manifest()
        manifest["TargetPlan"]["Destination"] = dict(manifest["TargetPlan"]["Source"])
        self.assertTrue(any("must-be-distinct" in error for error in MODULE.validate_manifest(manifest, NOW)))

    def test_wrong_host_scope_and_rehearsal_rights_are_denied(self):
        manifest = valid_manifest()
        manifest["Host"]["AllowedEndpointHost"] = "0.0.0.0"
        manifest["Scope"]["RehearsalRightsGranted"] = True
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("Host.AllowedEndpointHost:loopback-only-required", errors)
        self.assertIn("Scope.RehearsalRightsGranted:required-value-mismatch", errors)

    def test_cleanup_and_legacy_permissions_are_denied(self):
        manifest = valid_manifest()
        manifest["PermittedOperations"]["CleanupSource"] = True
        manifest["PermittedOperations"]["ConnectSource"] = True
        errors = MODULE.validate_manifest(manifest, NOW)
        self.assertIn("PermittedOperations.CleanupSource:must-remain-false", errors)
        self.assertTrue(any("unsupported-or-ambiguous-operation" in error for error in errors))

    def test_plan_only_wrapper_writes_no_credentials_or_podman_target(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "provisioning.json"
            metadata = root / "metadata.json"
            manifest.write_text(json.dumps(valid_manifest()), encoding="utf-8")
            result = subprocess.run([
                str(ROOT / "scripts/cw-m5-026b/provision-targets-m5-026b.sh"),
                "--manifest", str(manifest), "--metadata", str(metadata),
            ], cwd=ROOT, text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("plan-only", result.stdout)
            self.assertFalse((root / "credentials").exists())
            report = json.loads(metadata.read_text(encoding="utf-8"))
            self.assertEqual("PLAN_ONLY", report["Classification"])
            self.assertFalse(report["Provisioning"]["Executed"])

    def test_live_mode_requires_confirmation_before_podman(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest = root / "provisioning.json"
            metadata = root / "metadata.json"
            document = valid_manifest()
            document["TargetPlan"]["Source"]["CredentialDirectory"] = str(root / "source")
            document["TargetPlan"]["Destination"]["CredentialDirectory"] = str(root / "destination")
            manifest.write_text(json.dumps(document), encoding="utf-8")
            result = subprocess.run([
                str(ROOT / "scripts/cw-m5-026b/provision-targets-m5-026b.sh"),
                "--manifest", str(manifest), "--metadata", str(metadata), "--provision",
            ], cwd=ROOT, text=True, capture_output=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("confirm-provisioning", result.stderr)
            self.assertFalse((root / "source").exists())
            self.assertFalse((root / "destination").exists())

    def test_shell_syntax_is_valid(self):
        result = subprocess.run([
            "bash", "-n",
            "scripts/preflight-m5-026b-provisioning.sh",
            "scripts/cw-m5-026b/provision-targets-m5-026b.sh",
        ], cwd=ROOT, text=True, capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
