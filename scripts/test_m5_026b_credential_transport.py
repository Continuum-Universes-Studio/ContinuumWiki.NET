"""Source-safe tests for the separately authorized inspection credential transport."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
import stat
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr
from datetime import datetime, timedelta, timezone
from io import StringIO
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name: str, relative: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / relative)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PREFLIGHT = load("m5_026b_credential_preflight_tests", "scripts/preflight-m5-026b-inspection-credential-preparation.py")
PREPARER = load("m5_026b_credential_preparer_tests", "scripts/cw-m5-026b/prepare_inspection_credentials.py")
FAKE_PASSWORD = "a" * 64


def target_for(role: str, directory: Path) -> dict[str, str]:
    base = PREFLIGHT.INSPECTION_PREFLIGHT.expected_targets()[role]
    return {key: base[key] for key in (
        "ContainerName", "ContainerId", "VolumeName", "DatabaseName", "Endpoint",
        "ImageReference", "ProvisioningRunId", "DatabaseIdentity", "InstanceIdentity",
    )} | {
        "CredentialDirectory": str(directory),
        "ApplicationEnvironmentFile": str(directory / "application.env"),
        "CredentialOptionFile": str(directory / "inspection-client.cnf"),
    }


def manifest_for(role: str, target: dict[str, str], *, now: datetime | None = None) -> dict:
    now = now or datetime.now(timezone.utc)
    return {
        "ManifestVersion": PREFLIGHT.CONTRACT,
        "ApprovalId": "CW-M5-026B-CREDPREP-TEST-ONLY",
        "ApprovedAtUtc": now.isoformat().replace("+00:00", "Z"),
        "ExpiresAtUtc": (now + timedelta(hours=1)).isoformat().replace("+00:00", "Z"),
        "Approver": {"Name": "Test Approver", "Role": "Project Owner"},
        "Operator": {"Name": "Test Operator", "Role": "System Administrator"},
        "TargetOwner": "Test Owner",
        "ExecutionHost": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True},
        "TargetRole": role,
        "Target": copy.deepcopy(target),
        "PermittedOperations": copy.deepcopy(PREFLIGHT.REQUIRED_PERMISSIONS),
        "PreflightReportPath": "/home/cdjensen94/continuumwiki-validation/credprep-preflight-test.json",
    }


def valid_preflight(manifest: dict, now: datetime) -> dict:
    validated = PREFLIGHT.validate_manifest(manifest, now=now)
    return {
        "Classification": "CREDENTIAL_PREPARATION_AUTHORIZATION_VALID_NONCONNECTING",
        "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z"),
        "ManifestSha256": validated["ManifestSha256"],
        "ApprovalId": manifest["ApprovalId"],
        "TargetRole": manifest["TargetRole"],
        "ReportPath": manifest["PreflightReportPath"],
        "OperationCounts": {"CredentialValueReads": 0, "CredentialWrites": 0,
                             "DatabaseConnections": 0, "PodmanCommands": 0},
    }


def write_env(directory: Path, text: str, mode: int = 0o600) -> Path:
    path = directory / "application.env"
    path.write_text(text, encoding="ascii")
    path.chmod(mode)
    return path


class CredentialAuthorizationTests(unittest.TestCase):
    def test_draft_2020_schema_and_separate_scope(self):
        from jsonschema import Draft202012Validator
        schema_path = ROOT / "docs/operations/cw-m5-026b-inspection-credential-preparation-authorization.schema.json"
        schema = json.loads(schema_path.read_text())
        Draft202012Validator.check_schema(schema)
        sample = manifest_for("Source", PREFLIGHT.expected_target("Source"))
        Draft202012Validator(schema).validate(sample)
        sample["PermittedOperations"]["DatabaseConnections"] = True
        with self.assertRaises(Exception):
            Draft202012Validator(schema).validate(sample)
        inspection_schema = json.loads((ROOT / "docs/operations/cw-m5-026b-readonly-inspection-authorization.schema.json").read_text())
        self.assertNotEqual(schema["$id"], inspection_schema["$id"])

    def test_manifest_rejects_expiry_target_mismatch_and_overbroad_scope(self):
        now = datetime.now(timezone.utc)
        target = PREFLIGHT.expected_target("Source")
        document = manifest_for("Source", target, now=now)
        PREFLIGHT.validate_manifest(document, now=now)
        expired = manifest_for("Source", target, now=now - timedelta(hours=2))
        with self.assertRaisesRegex(PREFLIGHT.CredentialPreflightError, "AUTHORIZATION_NOT_CURRENT"):
            PREFLIGHT.validate_manifest(expired, now=now)
        mismatch = manifest_for("Source", target, now=now)
        mismatch["Target"]["ContainerId"] = "0" * 64
        with self.assertRaisesRegex(PREFLIGHT.CredentialPreflightError, "TARGET_BINDING_MISMATCH"):
            PREFLIGHT.validate_manifest(mismatch, now=now)
        overbroad = manifest_for("Source", target, now=now)
        overbroad["PermittedOperations"]["DatabaseConnections"] = True
        with self.assertRaisesRegex(PREFLIGHT.CredentialPreflightError, "OPERATION_SCOPE_NOT_EXACT"):
            PREFLIGHT.validate_manifest(overbroad, now=now)

    def test_preflight_is_nonconnecting_and_create_only(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            private = Path(temp, "private")
            private.mkdir(mode=0o700)
            manifest_path = private / "manifest.json"
            report_path = private / "preflight.json"
            doc = manifest_for("Source", PREFLIGHT.expected_target("Source"), now=now)
            manifest_path.write_text(json.dumps(doc))
            manifest_path.chmod(0o600)
            valid, result = PREFLIGHT.preflight(manifest_path, now=now)
            self.assertTrue(valid)
            self.assertEqual({"CredentialValueReads": 0, "CredentialWrites": 0,
                              "DatabaseConnections": 0, "PodmanCommands": 0}, result["OperationCounts"])
            PREFLIGHT._write_once(report_path, result)
            self.assertEqual(0o600, stat.S_IMODE(report_path.stat().st_mode))
            self.assertEqual(os.getuid(), report_path.stat().st_uid)
            original = report_path.read_bytes()
            with self.assertRaisesRegex(PREFLIGHT.CredentialPreflightError, "PREFLIGHT_REPORT_ALREADY_EXISTS"):
                PREFLIGHT._write_once(report_path, result)
            self.assertEqual(original, report_path.read_bytes())

    def test_cli_renewals_make_unique_create_only_sibling_reports(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            private = Path(temp, "private")
            private.mkdir(mode=0o700)
            manifest_path = private / "manifest.json"
            document = manifest_for("Source", PREFLIGHT.expected_target("Source"), now=now)
            document["PreflightReportPath"] = "/home/cdjensen94/continuumwiki-validation/source-preflight.json"
            manifest_path.write_text(json.dumps(document))
            manifest_path.chmod(0o600)
            created = []
            output = StringIO()
            with patch.object(PREFLIGHT.secrets, "token_hex", side_effect=("a" * 32, "b" * 32)), \
                 patch.object(PREFLIGHT, "_write_once", side_effect=lambda path, report: created.append((path, report))), \
                 redirect_stdout(output):
                self.assertEqual(0, PREFLIGHT.main(["--manifest", str(manifest_path), "--renew"]))
                self.assertEqual(0, PREFLIGHT.main(["--manifest", str(manifest_path), "--renew"]))
            self.assertEqual(2, len(created))
            self.assertNotEqual(created[0][0], created[1][0])
            original_hash = PREFLIGHT.validate_manifest(document, now=now)["ManifestSha256"]
            for path, report in created:
                self.assertEqual(path, Path(report["ReportPath"]))
                self.assertEqual(Path(document["PreflightReportPath"]).parent, path.parent)
                self.assertTrue(PREFLIGHT.report_path_is_bound(Path(document["PreflightReportPath"]), path))
                self.assertEqual(original_hash, report["ManifestSha256"])
                self.assertEqual({"CredentialValueReads": 0, "CredentialWrites": 0,
                                  "DatabaseConnections": 0, "PodmanCommands": 0}, report["OperationCounts"])

    def test_renewal_path_is_sibling_derived_from_manifest_bound_path(self):
        bound = Path("/home/cdjensen94/continuumwiki-validation/source-check.json")
        now = datetime(2026, 10, 12, 23, 10, tzinfo=timezone.utc)
        with patch.object(PREFLIGHT.secrets, "token_hex", return_value="c" * 32):
            renewed = PREFLIGHT._renewal_report_path(bound, now=now)
        self.assertEqual(bound.parent, renewed.parent)
        self.assertTrue(PREFLIGHT.report_path_is_bound(bound, renewed))
        self.assertFalse(PREFLIGHT.report_path_is_bound(bound, bound.with_name("other.json")))
        self.assertFalse(PREFLIGHT.report_path_is_bound(bound, bound.parent.parent / renewed.name))


class CredentialFileTests(unittest.TestCase):
    def test_synthetic_environment_file_converts_to_owner_only_mariadb_option_file(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            target = target_for("Source", directory)
            write_env(directory, f"CW_M5_APP_USER={target['DatabaseIdentity']}\nCW_M5_APP_PASSWORD={FAKE_PASSWORD}\n")
            directory_fd = PREPARER._private_directory_fd(directory, os.getuid())
            try:
                secret = PREPARER._read_application_env(directory_fd, target["DatabaseIdentity"])
                PREPARER._write_option_file(directory_fd, target["DatabaseIdentity"], secret, os.getuid())
                path = directory / "inspection-client.cnf"
                self.assertEqual(f"[client]\nuser={target['DatabaseIdentity']}\npassword={FAKE_PASSWORD}\n", path.read_text())
                self.assertEqual(0o600, path.stat().st_mode & 0o777)
                self.assertEqual(os.getuid(), path.stat().st_uid)
                self.assertEqual(0, os.stat(directory).st_mode & 0o077)
                self.assertEqual(bytearray(64), secret)
            finally:
                os.close(directory_fd)

    def test_malformed_or_wrong_identity_data_is_rejected_without_secret_diagnostics(self):
        cases = (
            f"CW_M5_APP_USER=unexpected_user\nCW_M5_APP_PASSWORD={FAKE_PASSWORD}\n",
            f"CW_M5_APP_USER=continuumwiki_m5_026b_source_app\nCW_M5_APP_PASSWORD=not-a-valid-password\n",
            f"CW_M5_APP_USER=continuumwiki_m5_026b_source_app\nCW_M5_APP_PASSWORD={FAKE_PASSWORD}\nEXTRA={FAKE_PASSWORD}\n",
            f"CW_M5_APP_USER=continuumwiki_m5_026b_source_app\r\nCW_M5_APP_PASSWORD={FAKE_PASSWORD}\r\n",
        )
        for contents in cases:
            with self.subTest(case="malformed"), tempfile.TemporaryDirectory() as temp:
                directory = Path(temp, "credentials")
                directory.mkdir(mode=0o700)
                write_env(directory, contents)
                fd = PREPARER._private_directory_fd(directory, os.getuid())
                try:
                    with self.assertRaises(PREPARER.CredentialPreparationError) as raised:
                        PREPARER._read_application_env(fd, "continuumwiki_m5_026b_source_app")
                    self.assertNotIn(FAKE_PASSWORD, str(raised.exception))
                    self.assertIn(str(raised.exception), {"APPLICATION_CREDENTIAL_FILE_MALFORMED", "APPLICATION_CREDENTIAL_IDENTITY_MISMATCH"})
                finally:
                    os.close(fd)

    def test_rejects_directory_and_input_file_permissions_and_symlinks(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            loose_dir = root / "loose"
            loose_dir.mkdir(mode=0o700)
            os.chmod(loose_dir, 0o755)
            with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "CREDENTIAL_DIRECTORY_PERMISSIONS_INVALID"):
                PREPARER._private_directory_fd(loose_dir, os.getuid())
            directory = root / "credentials"
            directory.mkdir(mode=0o700)
            path = write_env(directory, f"CW_M5_APP_USER=continuumwiki_m5_026b_source_app\nCW_M5_APP_PASSWORD={FAKE_PASSWORD}\n", 0o644)
            fd = PREPARER._private_directory_fd(directory, os.getuid())
            try:
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "APPLICATION_CREDENTIAL_FILE_PERMISSIONS_INVALID"):
                    PREPARER._read_application_env(fd, "continuumwiki_m5_026b_source_app")
            finally:
                os.close(fd)
            path.unlink()
            outside = root / "outside.env"
            outside.write_text(f"CW_M5_APP_USER=continuumwiki_m5_026b_source_app\nCW_M5_APP_PASSWORD={FAKE_PASSWORD}\n")
            outside.chmod(0o600)
            path.symlink_to(outside)
            fd = PREPARER._private_directory_fd(directory, os.getuid())
            try:
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "APPLICATION_CREDENTIAL_FILE_UNAVAILABLE"):
                    PREPARER._read_application_env(fd, "continuumwiki_m5_026b_source_app")
            finally:
                os.close(fd)

    def test_existing_option_file_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            path = directory / "inspection-client.cnf"
            path.write_text("operator-owned existing content\n")
            path.chmod(0o600)
            fd = PREPARER._private_directory_fd(directory, os.getuid())
            try:
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "OPTION_FILE_ALREADY_EXISTS"):
                    PREPARER._write_option_file(fd, "continuumwiki_m5_026b_source_app", bytearray(FAKE_PASSWORD.encode()), os.getuid())
            finally:
                os.close(fd)
            self.assertEqual("operator-owned existing content\n", path.read_text())

    def test_partial_write_failure_removes_only_its_new_incomplete_file_and_redacts_error(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            fd = PREPARER._private_directory_fd(directory, os.getuid())
            original_write = os.write
            calls = 0

            def fail_after_partial(descriptor, data):
                nonlocal calls
                calls += 1
                if calls == 1:
                    return original_write(descriptor, data[:5])
                raise OSError("synthetic failure including " + FAKE_PASSWORD)

            try:
                with patch.object(PREPARER.os, "write", side_effect=fail_after_partial):
                    with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "OPTION_FILE_WRITE_FAILED") as raised:
                        PREPARER._write_option_file(fd, "continuumwiki_m5_026b_source_app", bytearray(FAKE_PASSWORD.encode()), os.getuid())
                self.assertNotIn(FAKE_PASSWORD, str(raised.exception))
                self.assertFalse((directory / "inspection-client.cnf").exists())
            finally:
                os.close(fd)

    def test_interruption_after_partial_write_cleans_the_new_file_and_zeroes_buffer(self):
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            fd = PREPARER._private_directory_fd(directory, os.getuid())
            original_write = os.write
            calls = 0
            password = bytearray(FAKE_PASSWORD.encode())

            def interrupt_after_partial(descriptor, data):
                nonlocal calls
                calls += 1
                if calls == 1:
                    return original_write(descriptor, data[:5])
                raise KeyboardInterrupt()

            try:
                with patch.object(PREPARER.os, "write", side_effect=interrupt_after_partial):
                    with self.assertRaises(KeyboardInterrupt):
                        PREPARER._write_option_file(fd, "continuumwiki_m5_026b_source_app", password, os.getuid())
                self.assertFalse((directory / "inspection-client.cnf").exists())
                self.assertEqual(bytearray(64), password)
            finally:
                os.close(fd)

    def test_authorized_gate_requires_matching_recent_preflight_before_opening_env(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            target = target_for("Source", directory)
            manifest = manifest_for("Source", target, now=now)
            expected = {"Source": target_for("Source", directory), "Destination": PREFLIGHT.expected_target("Destination")}
            with patch.object(PREFLIGHT, "expected_target", side_effect=lambda role: expected[role]), \
                 patch.object(PREPARER.PREFLIGHT, "expected_target", side_effect=lambda role: expected[role]), \
                 patch.object(PREPARER.socket, "gethostname", return_value="christian-laptop"), \
                 patch.object(PREPARER.pwd, "getpwuid", return_value=SimpleNamespace(pw_name="cdjensen94")):
                preflight = valid_preflight(manifest, now)
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
                    PREPARER.prepare(manifest, dict(preflight, ManifestSha256="0" * 64), manifest["ApprovalId"],
                                     Path(manifest["PreflightReportPath"]), now=now)
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
                    PREPARER.prepare(manifest, dict(preflight, GeneratedAtUtc=(now - timedelta(minutes=11)).isoformat().replace("+00:00", "Z")), manifest["ApprovalId"],
                                     Path(manifest["PreflightReportPath"]), now=now)
                self.assertFalse((directory / "application.env").exists())
                self.assertFalse((directory / "inspection-client.cnf").exists())

    def test_converter_rejects_report_path_substitution_and_unbound_renewal(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            manifest = manifest_for("Source", target_for("Source", directory), now=now)
            expected = {"Source": target_for("Source", directory),
                        "Destination": PREFLIGHT.expected_target("Destination")}
            with patch.object(PREFLIGHT, "expected_target", side_effect=lambda role: expected[role]), \
                 patch.object(PREPARER.PREFLIGHT, "expected_target", side_effect=lambda role: expected[role]):
                bound = Path(manifest["PreflightReportPath"])
                allowed = bound.with_name(bound.stem + ".renewal-20261012T231000Z-" + "d" * 32 + ".json")
                another_allowed = bound.with_name(bound.stem + ".renewal-20261012T231001Z-" + "e" * 32 + ".json")
                report = valid_preflight(manifest, now)
                report["ReportPath"] = str(allowed)
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
                    PREPARER._validate_gate(manifest, report, manifest["ApprovalId"],
                                            another_allowed, now=now)
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
                    PREPARER._validate_gate(manifest, report, manifest["ApprovalId"],
                                            allowed.with_name("substituted.json"), now=now)
                report["ReportPath"] = str(bound.parent / "unbound-renewal.json")
                with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
                    PREPARER._validate_gate(manifest, report, manifest["ApprovalId"],
                                            Path(report["ReportPath"]), now=now)

    def test_converter_rejects_expired_approval_even_with_fresh_matching_report(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp, "credentials")
            directory.mkdir(mode=0o700)
            target = target_for("Source", directory)
            manifest = manifest_for("Source", target, now=now - timedelta(hours=2))
            expected = {"Source": target, "Destination": PREFLIGHT.expected_target("Destination")}
            with patch.object(PREFLIGHT, "expected_target", side_effect=lambda role: expected[role]), \
                 patch.object(PREPARER.PREFLIGHT, "expected_target", side_effect=lambda role: expected[role]):
                fresh_report = valid_preflight(manifest_for("Source", target, now=now), now)
                fresh_report["ApprovalId"] = manifest["ApprovalId"]
                fresh_report["ManifestSha256"] = PREFLIGHT.validate_manifest(manifest_for("Source", target, now=now), now=now)["ManifestSha256"]
                with self.assertRaisesRegex(PREPARER.PREFLIGHT.CredentialPreflightError, "AUTHORIZATION_NOT_CURRENT"):
                    PREPARER._validate_gate(manifest, fresh_report, manifest["ApprovalId"],
                                            Path(manifest["PreflightReportPath"]), now=now)

    def test_preflight_reader_rejects_symlink_and_hardlink_reports(self):
        with tempfile.TemporaryDirectory() as temp:
            private = Path(temp, "private")
            private.mkdir(mode=0o700)
            source = private / "report.json"
            source.write_text("{}")
            source.chmod(0o600)
            symlink = private / "symlink.json"
            symlink.symlink_to(source)
            with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "PREFLIGHT_PATH_INVALID"):
                PREPARER._read_preflight(symlink)
            hardlink = private / "hardlink.json"
            os.link(source, hardlink)
            with self.assertRaisesRegex(PREPARER.CredentialPreparationError, "PREFLIGHT_PERMISSIONS_INVALID"):
                PREPARER._read_preflight(source)

    def test_command_output_and_preflight_json_never_include_credential_values(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            private = Path(temp, "private")
            private.mkdir(mode=0o700)
            report_path = private / "preflight.json"
            document = manifest_for("Source", PREFLIGHT.expected_target("Source"), now=now)
            manifest_path = private / "manifest.json"
            manifest_path.write_text(json.dumps(document))
            manifest_path.chmod(0o600)
            valid, report = PREFLIGHT.preflight(manifest_path, now=now)
            self.assertTrue(valid)
            report_text = json.dumps(report)
            self.assertNotIn(FAKE_PASSWORD, report_text)
            with patch.object(PREPARER.PREFLIGHT, "_read_private", return_value=document), \
                 patch.object(PREPARER, "_read_preflight", return_value={}), \
                 patch.object(PREPARER, "prepare", return_value={"Classification": "INSPECTION_CLIENT_OPTION_FILE_CREATED", "TargetRole": "Source"}) as prepare_mock, \
                 redirect_stdout(StringIO()) as stdout, redirect_stderr(StringIO()) as stderr:
                status = PREPARER.main(["--manifest", str(manifest_path), "--preflight-report", str(report_path),
                                        "--approval-id", document["ApprovalId"], "--prepare"])
            self.assertEqual(0, status)
            prepare_mock.assert_called_once_with(document, {}, document["ApprovalId"], report_path)
            self.assertNotIn(FAKE_PASSWORD, stdout.getvalue())
            self.assertNotIn(FAKE_PASSWORD, stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
