#!/usr/bin/env python3
"""Source-only regression tests for the two-gate M5-026B continuation."""

import importlib.util
import io
import json
import os
import pwd
import tempfile
import unittest
import subprocess
from contextlib import redirect_stderr
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("cw_m5_026b_continuation", ROOT / "scripts/cw-m5-026b/continue_targets.py")
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC and SPEC.loader
SPEC.loader.exec_module(MODULE)
NOW = datetime(2026, 10, 12, 12, 0, tzinfo=timezone.utc)
EVIDENCE = json.loads((ROOT / "secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json").read_text())
ORIGINAL = json.loads((ROOT / "secure/evidence/cw-m5-026b-provisioning-authorization.json").read_text())
SOURCE_TARGET, DESTINATION_TARGET = MODULE.target_context(EVIDENCE, ORIGINAL)


def auth(operation, target, approval):
    source = operation == MODULE.PREFLIGHT.SOURCE_OPERATION
    return {
        "ManifestVersion": MODULE.PREFLIGHT.VERSION,
        "Operation": operation,
        "ApprovalId": approval,
        "ApprovedAtUtc": "2026-10-12T11:00:00Z",
        "ExpiresAtUtc": "2026-10-13T11:00:00Z",
        "Approver": {"Name": "Test approver", "Role": "Project Owner"},
        "Operator": {"Name": "Test operator", "Role": "System Administrator"},
        "TargetOwner": "Test target owner",
        "Host": {"HostName": "test-host", "UnixUser": pwd.getpwuid(os.getuid()).pw_name, "Uid": os.getuid(), "RootlessPodmanRequired": True},
        "Target": {**target, **({"Sql": MODULE.PREFLIGHT.SOURCE_SQL} if source else {})},
        "Permissions": {
            "VerifyRetainedSourceIdentity": source,
            "ProvisionDestinationTarget": not source,
            "RetainSanitizedEvidence": True,
        },
        "Scope": {
            "Production": False, "SharedWithProduction": False, "RehearsalRightsGranted": False,
            "BackupAllowed": False, "RestoreAllowed": False, "SchemaMigrationAllowed": False,
            "CertificationAllowed": False, "CleanupAllowed": False, "SchemaDeletionAllowed": False,
            "ContainerVolumeDeletionAllowed": False,
        },
    }


def preflight_report(source_path, destination_path):
    return {
        "ReportVersion": "cw-m5-026b-continuation-preflight-v1",
        "Classification": "CATEGORY_CONTINUATION_AUTHORIZATIONS_VALID_NONCONNECTING",
        "Valid": True,
        "PodmanCommands": 0, "DatabaseQueries": 0, "DatabaseMutations": 0, "Errors": [],
        "AuthorizationFiles": {"SourceSha256": MODULE.sha256(source_path), "DestinationSha256": MODULE.sha256(destination_path)},
    }


class M5026BContinuationTests(unittest.TestCase):
    def setUp(self):
        self.source = auth(MODULE.PREFLIGHT.SOURCE_OPERATION, SOURCE_TARGET, "approval-source-new")
        self.destination = auth(MODULE.PREFLIGHT.DESTINATION_OPERATION, DESTINATION_TARGET, "approval-destination-new")

    def test_historic_evidence_binds_exact_retained_source_and_absent_destination(self):
        source, destination = MODULE.target_context(EVIDENCE, ORIGINAL)
        self.assertEqual("6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914", source["ContainerId"])
        self.assertEqual("docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15", source["ImageReference"])
        self.assertEqual("not-created", EVIDENCE["Targets"]["Destination"]["ObservedStatus"])
        self.assertEqual("continuumwiki-m5-026b-source-ro1-data", source["VolumeName"])
        self.assertEqual("continuumwiki-m5-026b-destination-ro1-data", destination["VolumeName"])

    def test_each_operation_requires_its_own_exact_scope(self):
        self.assertEqual([], MODULE.PREFLIGHT.validate_authorization(self.source, MODULE.PREFLIGHT.SOURCE_OPERATION, SOURCE_TARGET, NOW))
        self.assertEqual([], MODULE.PREFLIGHT.validate_authorization(self.destination, MODULE.PREFLIGHT.DESTINATION_OPERATION, DESTINATION_TARGET, NOW))
        wrong = dict(self.source, Operation=MODULE.PREFLIGHT.DESTINATION_OPERATION)
        self.assertTrue(MODULE.PREFLIGHT.validate_authorization(wrong, MODULE.PREFLIGHT.SOURCE_OPERATION, SOURCE_TARGET, NOW))

    def test_expired_or_excessive_authorization_fails_closed(self):
        expired = dict(self.source, ExpiresAtUtc="2026-10-11T11:00:00Z")
        excessive = dict(self.source, ExpiresAtUtc="2026-10-14T11:00:00Z")
        self.assertIn("ExpiresAtUtc:expired", MODULE.PREFLIGHT.validate_authorization(expired, MODULE.PREFLIGHT.SOURCE_OPERATION, SOURCE_TARGET, NOW))
        self.assertIn("ExpiresAtUtc:validity-exceeds-24-hours", MODULE.PREFLIGHT.validate_authorization(excessive, MODULE.PREFLIGHT.SOURCE_OPERATION, SOURCE_TARGET, NOW))

    def test_wrong_source_container_or_image_fails_closed(self):
        for field, value in (("ContainerId", "f" * 64), ("ImageReference", "docker.io/library/mariadb:11.8"), ("DatabaseName", "other")):
            altered = dict(SOURCE_TARGET, **{field: value})
            self.assertTrue(MODULE.PREFLIGHT.validate_authorization(self.source, MODULE.PREFLIGHT.SOURCE_OPERATION, altered, NOW), field)

    def test_prior_approval_and_same_approval_id_are_rejected(self):
        self.source["ApprovalId"] = ORIGINAL["ApprovalId"]
        errors = MODULE.PREFLIGHT.validate_authorization(self.source, MODULE.PREFLIGHT.SOURCE_OPERATION, SOURCE_TARGET, NOW)
        self.assertFalse(errors)  # Historical ID is rejected by the cross-record gate, not the single-manifest schema.
        self.destination["ApprovalId"] = self.source["ApprovalId"]
        with tempfile.TemporaryDirectory() as directory:
            s, d = Path(directory) / "s.json", Path(directory) / "d.json"
            s.write_text(json.dumps(self.source)); d.write_text(json.dumps(self.destination))
            report = preflight_report(s, d)
            host_evidence = json.loads((ROOT / "artifacts/validation/mediawiki-category-backup-restore-readonly-host-inspection-v1.json").read_text())
            with self.assertRaisesRegex(ValueError, "reuse the initial provisioning approval"):
                MODULE.authorization_gate(self.source, self.destination, SOURCE_TARGET, DESTINATION_TARGET, s, d, report, ORIGINAL["ApprovalId"], "test-host", ORIGINAL, host_evidence, NOW)

    def test_crossed_and_prohibited_permissions_are_rejected(self):
        self.destination["Permissions"]["VerifyRetainedSourceIdentity"] = True
        errors = MODULE.PREFLIGHT.validate_authorization(self.destination, MODULE.PREFLIGHT.DESTINATION_OPERATION, DESTINATION_TARGET, NOW)
        self.assertIn("Permissions:must-grant-only-this-operation-and-evidence-retention", errors)
        self.destination = auth(MODULE.PREFLIGHT.DESTINATION_OPERATION, DESTINATION_TARGET, "new-destination")
        self.destination["Scope"]["ContainerVolumeDeletionAllowed"] = True
        errors = MODULE.PREFLIGHT.validate_authorization(self.destination, MODULE.PREFLIGHT.DESTINATION_OPERATION, DESTINATION_TARGET, NOW)
        self.assertIn("Scope:prohibited-operations-must-remain-false", errors)

    def test_preflight_hash_binding_and_host_identity_are_required(self):
        with tempfile.TemporaryDirectory() as directory:
            source_path, destination_path = Path(directory) / "source.json", Path(directory) / "destination.json"
            source_path.write_text(json.dumps(self.source)); destination_path.write_text(json.dumps(self.destination))
            report = preflight_report(source_path, destination_path)
            host_evidence = json.loads((ROOT / "artifacts/validation/mediawiki-category-backup-restore-readonly-host-inspection-v1.json").read_text())
            with patch.dict(self.source, {"Host": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodmanRequired": True}}), patch.dict(self.destination, {"Host": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodmanRequired": True}}):
                self.source["Approver"] = ORIGINAL["Approver"]; self.source["Operator"] = ORIGINAL["Operator"]; self.source["TargetOwner"] = ORIGINAL["TargetOwner"]
                self.destination["Approver"] = ORIGINAL["Approver"]; self.destination["Operator"] = ORIGINAL["Operator"]; self.destination["TargetOwner"] = ORIGINAL["TargetOwner"]
                source_path.write_text(json.dumps(self.source)); destination_path.write_text(json.dumps(self.destination))
                report = preflight_report(source_path, destination_path)
                MODULE.authorization_gate(self.source, self.destination, SOURCE_TARGET, DESTINATION_TARGET, source_path, destination_path, report, ORIGINAL["ApprovalId"], "christian-laptop", ORIGINAL, host_evidence, NOW)
            with self.assertRaisesRegex(ValueError, "preflight"):
                MODULE.authorization_gate(self.source, self.destination, SOURCE_TARGET, DESTINATION_TARGET, source_path, destination_path, {"Valid": True, "AuthorizationFiles": {}}, ORIGINAL["ApprovalId"], "christian-laptop", ORIGINAL, host_evidence, NOW)
            with self.assertRaisesRegex(ValueError, "does not match recorded operator/owner evidence"):
                MODULE.authorization_gate(self.source, self.destination, SOURCE_TARGET, DESTINATION_TARGET, source_path, destination_path, report, ORIGINAL["ApprovalId"], "different-host", ORIGINAL, host_evidence, NOW)

    def test_destination_never_runs_when_source_verification_fails(self):
        calls = []
        def verify():
            calls.append("source")
            raise RuntimeError("source identity mismatch")
        def create():
            calls.append("destination")
            return {}
        with self.assertRaisesRegex(RuntimeError, "source identity"):
            MODULE.execute_steps(source_verify=verify, destination_create=create)
        self.assertEqual(["source"], calls)

    def test_destination_never_runs_when_source_predicates_are_rejected(self):
        calls = []
        with self.assertRaisesRegex(RuntimeError, "source identity"):
            MODULE.execute_steps(
                source_verify=lambda: calls.append("source") or {"Accepted": False, "Codes": ["DATABASE_NAME_MISMATCH"]},
                destination_create=lambda: calls.append("destination") or {},
            )
        self.assertEqual(["source"], calls)

    def test_destination_runs_only_after_source_verification(self):
        calls = []
        result = MODULE.execute_steps(source_verify=lambda: calls.append("source") or {"Accepted": True},
                                      destination_create=lambda: calls.append("destination") or {"Created": True})
        self.assertEqual(["source", "destination"], calls)
        self.assertTrue(result["DestinationProvisioning"]["Created"])

    def test_source_identity_query_selects_authorized_database_and_exact_sql(self):
        sql = "SELECT DATABASE(), VERSION(), @@version_comment;"
        command = MODULE.source_identity_query_command(SOURCE_TARGET, "/safe/test/environment.env", sql)
        self.assertEqual("-D", command[command.index("-D")])
        self.assertEqual(SOURCE_TARGET["DatabaseName"], command[command.index("-D") + 1])
        self.assertEqual(sql, command[command.index("-e") + 1])
        self.assertEqual(MODULE.PREFLIGHT.SOURCE_SQL, sql)

    def test_failed_source_predicates_are_retained_only_as_sanitized_diagnostic(self):
        raw_output = "wrong-database\t10.7.9-MariaDB\tDefinitelyNotMySQL\n"
        evaluation = MODULE.PROVISIONER.classify_runtime_identity_query(0, raw_output, SOURCE_TARGET["DatabaseName"])
        diagnostic = MODULE.PROVISIONER.sanitize_runtime_identity_diagnostic(evaluation)
        result = MODULE.failure_result("SOURCE_IDENTITY_QUERY_STARTED", "authorized source identity query did not verify", diagnostic)
        serialized = json.dumps(result)
        self.assertEqual(
            ["DATABASE_NAME_MISMATCH", "VERSION_FAMILY_MISMATCH", "VERSION_COMMENT_MISMATCH"],
            result["SourceRuntimeIdentity"]["Codes"],
        )
        self.assertNotIn("wrong-database", serialized)
        self.assertNotIn("10.7.9-MariaDB", serialized)
        self.assertNotIn("DefinitelyNotMySQL", serialized)
        self.assertNotIn(raw_output, serialized)
        self.assertNotIn("stderr", serialized.lower())
        self.assertNotIn("credential", serialized.lower())

    def test_existing_destination_collision_is_not_overwritten(self):
        class Result:
            returncode = 0
            stdout = ""
            stderr = ""
        with patch.object(MODULE.PROVISIONER, "port_available", return_value=True):
            with self.assertRaisesRegex(RuntimeError, "refusing existing destination container"):
                MODULE.destination_is_absent(DESTINATION_TARGET, lambda command: Result(), MODULE.PROVISIONER)

    def test_retained_source_inspection_is_read_only_and_pins_all_resource_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            cred = Path(directory) / "source"
            cred.mkdir(mode=0o700)
            (cred / "environment.env").write_text("not-read-by-test\n")
            (cred / "application.env").write_text("not-read-by-test\n")
            (cred / "environment.env").chmod(0o600); (cred / "application.env").chmod(0o600)
            source = dict(SOURCE_TARGET, CredentialDirectory=str(cred))
            run_id = EVIDENCE["Provisioning"]["RunId"]
            image_id = "sha256:" + "b" * 64
            container = {"Id": source["ContainerId"], "Image": image_id, "State": {"Running": True},
                         "Config": {"Image": source["ImageReference"], "Labels": {
                             "io.continuumwiki.m5-026b-run-id": run_id,
                             "io.continuumwiki.m5-026b-database": source["DatabaseName"]}},
                         "Mounts": [{"Name": source["VolumeName"], "Destination": "/var/lib/mysql"}]}
            volume = {"Name": source["VolumeName"], "Labels": {
                "io.continuumwiki.m5-026b-run-id": run_id, "io.continuumwiki.m5-026b-role": "source"}}
            image = {"Id": image_id, "RepoDigests": [source["ImageReference"]]}
            def runner(command):
                if command[:3] == ["podman", "info", "--format"]:
                    return subprocess.CompletedProcess(command, 0, "true\n", "")
                if command[:3] == ["podman", "container", "inspect"]:
                    return subprocess.CompletedProcess(command, 0, json.dumps(container), "")
                if command[:3] == ["podman", "image", "inspect"]:
                    return subprocess.CompletedProcess(command, 0, json.dumps(image), "")
                if command[:3] == ["podman", "volume", "inspect"]:
                    return subprocess.CompletedProcess(command, 0, json.dumps(volume), "")
                if command[:2] == ["podman", "port"]:
                    return subprocess.CompletedProcess(command, 0, "127.0.0.1:13326\n", "")
                self.fail("unexpected (potentially mutating) command in source inspection: " + " ".join(command))
            result = MODULE.inspect_retained_source(source, run_id, runner, os.getuid())
            self.assertTrue(all(result.values()))

    def test_retained_source_container_id_mismatch_stops_without_exec_or_mutation(self):
        source = dict(SOURCE_TARGET)
        commands = []
        def runner(command):
            commands.append(command)
            if command[:3] == ["podman", "info", "--format"]:
                return subprocess.CompletedProcess(command, 0, "true\n", "")
            if command[:3] == ["podman", "container", "inspect"]:
                return subprocess.CompletedProcess(command, 1, "", "")
            self.fail("unexpected source inspection command")
        with self.assertRaisesRegex(RuntimeError, "container inspection failed"):
            MODULE.inspect_retained_source(source, EVIDENCE["Provisioning"]["RunId"], runner, os.getuid())
        self.assertEqual(2, len(commands))

    def test_existing_volume_with_old_labels_is_never_attached_or_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            target = dict(DESTINATION_TARGET, CredentialDirectory=str(Path(directory) / "credentials"))
            calls = []
            def fake_run(command, **kwargs):
                calls.append(command)
                if command[1:3] == ["pull", "--quiet"]:
                    return ""
                if command[1:3] == ["image", "inspect"]:
                    return target["ImageReference"]
                if command[1:3] == ["volume", "create"]:
                    return target["VolumeName"]
                if command[1:3] == ["volume", "inspect"]:
                    return json.dumps({"Name": target["VolumeName"], "Labels": {"io.continuumwiki.m5-026b-run-id": "old-run", "io.continuumwiki.m5-026b-role": "destination"}})
                self.fail("unexpected provisioning command after volume ownership mismatch")
            with patch.object(MODULE.PROVISIONER, "run", side_effect=fake_run):
                with self.assertRaisesRegex(RuntimeError, "not owned by this provisioning run"):
                    MODULE.PROVISIONER.provision_one(target, Path(target["CredentialDirectory"]), target["ImageReference"], "new-run")
            self.assertFalse(any(command[1:2] == ["run"] for command in calls))

    def test_reports_are_create_only(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.json"
            MODULE.write_report_once(path, {"safe": True})
            with self.assertRaises(FileExistsError):
                MODULE.write_report_once(path, {"unsafe": True})
            self.assertEqual({"safe": True}, json.loads(path.read_text()))

    def test_live_report_path_is_reserved_private_and_never_overwritten(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "reserved.json"
            stream = MODULE.reserve_report(path)
            stream.close()
            self.assertEqual(0o600, path.stat().st_mode & 0o777)
            self.assertEqual("CONTINUATION_IN_PROGRESS", json.loads(path.read_text())["Classification"])
            with self.assertRaises(FileExistsError):
                MODULE.reserve_report(path)

    def test_live_runner_refuses_existing_result_file_before_any_host_operation(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "existing-result.json"
            original = '{"preserve":"existing evidence"}\n'
            path.write_text(original)
            stderr = io.StringIO()
            with patch.object(MODULE, "_runner") as runner, redirect_stderr(stderr):
                result = MODULE.main([
                    "--source-authorization", str(Path(directory) / "source.json"),
                    "--destination-authorization", str(Path(directory) / "destination.json"),
                    "--preflight-report", str(Path(directory) / "preflight.json"),
                    "--report", str(path), "--continue-live", "--confirm-destination-provisioning",
                ])
            self.assertEqual(64, result)
            runner.assert_not_called()
            self.assertEqual(original, path.read_text())

    def test_nonconnecting_preflight_binds_both_distinct_manifests(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source_path, destination_path, report_path = root / "source.json", root / "destination.json", root / "preflight.json"
            source_path.write_text(json.dumps(self.source)); destination_path.write_text(json.dumps(self.destination))
            for document in (self.source, self.destination):
                document["Approver"] = ORIGINAL["Approver"]; document["Operator"] = ORIGINAL["Operator"]; document["TargetOwner"] = ORIGINAL["TargetOwner"]
                document["Host"] = {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodmanRequired": True}
            source_path.write_text(json.dumps(self.source)); destination_path.write_text(json.dumps(self.destination))
            result = MODULE.PREFLIGHT.main([
                "--source-authorization", str(source_path), "--destination-authorization", str(destination_path),
                "--provisioning-evidence", str(ROOT / "secure/evidence/mediawiki-category-backup-restore-provisioning-v1.json"),
                "--original-authorization", str(ROOT / "secure/evidence/cw-m5-026b-provisioning-authorization.json"),
                "--report", str(report_path),
            ])
            self.assertEqual(0, result)
            report = json.loads(report_path.read_text())
            self.assertTrue(report["Valid"])
            self.assertEqual(0, report["PodmanCommands"])
            self.assertEqual(0, report["DatabaseQueries"])
            self.assertEqual(0, report["DatabaseMutations"])


if __name__ == "__main__":
    unittest.main()
