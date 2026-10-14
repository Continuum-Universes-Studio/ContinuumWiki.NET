from __future__ import annotations

import copy
import importlib.util
import json
import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-restricted-query-diagnostic.py"
SPEC = importlib.util.spec_from_file_location("restricted_query_preflight_tests", PREFLIGHT_PATH)
assert SPEC and SPEC.loader
PREFLIGHT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PREFLIGHT)
RUNNER_PATH = ROOT / "scripts/cw-m5-026b/run_restricted_query_diagnostic.py"
RUN_SPEC = importlib.util.spec_from_file_location("restricted_query_runner_tests", RUNNER_PATH)
assert RUN_SPEC and RUN_SPEC.loader
RUNNER = importlib.util.module_from_spec(RUN_SPEC)
RUN_SPEC.loader.exec_module(RUNNER)

def sample(now=None):
    instant = now or datetime.now(timezone.utc)
    targets = PREFLIGHT._targets()
    approval = "CW-M5-026B-RESTRICTED-QUERY-TEST-ONLY"
    folder = f"/home/cdjensen94/continuumwiki-validation/cw-m5-026b-query-diagnostic-{approval}"
    return {"ManifestVersion": PREFLIGHT.CONTRACT, "ApprovalId": approval,
        "ApprovedAtUtc": instant.isoformat().replace("+00:00", "Z"),
        "ExpiresAtUtc": (instant + timedelta(minutes=10)).isoformat().replace("+00:00", "Z"),
        "Approver": {"Name": "Test approver", "Role": "Project Owner"},
        "Operator": {"Name": "Test operator", "Role": "System Administrator"},
        "ExecutionHost": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True},
        "Targets": targets, "AuthorizedQueries": copy.deepcopy(PREFLIGHT.QUERY_BINDINGS),
        "Limits": {"PerQueryTimeoutSeconds": 60, "OverallTimeoutSeconds": 600},
        "Evidence": {"ManifestPath": folder + "/authorization.json", "PreflightReportPath": folder + "/preflight.json", "ResultReportPath": folder + "/result.json"}}

class RestrictedQueryDiagnosticTests(unittest.TestCase):
    def test_schema_is_draft_2020_12_and_sample_is_valid(self):
        from jsonschema import Draft202012Validator
        schema = json.loads(PREFLIGHT.SCHEMA_PATH.read_text())
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema).validate(sample())

    def test_only_the_two_fixed_query_and_target_pairs_are_allowed(self):
        manifest = sample()
        altered = copy.deepcopy(manifest)
        altered["AuthorizedQueries"][0]["TargetRole"] = "Destination"
        with patch.object(PREFLIGHT, "_schema_validate", return_value=None), patch("socket.gethostname", return_value="christian-laptop"), patch("os.getuid", return_value=1000):
            with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "QUERY_ALLOWLIST_BINDING_MISMATCH"):
                PREFLIGHT.validate_manifest(altered, Path(altered["Evidence"]["ManifestPath"]))

    def test_extra_fields_arbitrary_sql_and_unknown_query_ids_rejected_by_schema(self):
        from jsonschema import Draft202012Validator
        validator = Draft202012Validator(json.loads(PREFLIGHT.SCHEMA_PATH.read_text()))
        for mutate in (lambda d: d.update({"Sql": "SELECT 1"}),
                       lambda d: d.update({"AuthorizedQueries": [{"QueryId": "FREEFORM", "TargetRole": "Source"}]}),
                       lambda d: d["Targets"]["Source"].update({"Sql": "SELECT 1"})):
            document = sample(); mutate(document)
            self.assertTrue(list(validator.iter_errors(document)))

    def test_wrong_credential_option_paths_fail_exact_binding(self):
        document = sample()
        document["Targets"]["Source"]["CredentialOptionFile"] += ".copy"
        with patch.object(PREFLIGHT, "_schema_validate", return_value=None):
            with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "EXACT_TARGET_BINDING_MISMATCH"):
                PREFLIGHT.validate_manifest(document, Path(document["Evidence"]["ManifestPath"]))

    def test_credential_metadata_is_checked_without_opening_file_contents(self):
        document = sample()
        with patch.object(PREFLIGHT.INSPECTOR, "_check_credential_option_file") as metadata_check:
            PREFLIGHT.check_credentials(document)
        self.assertEqual(2, metadata_check.call_count)
        self.assertEqual(1000, metadata_check.call_args_list[0].args[1])

    def test_stale_or_wrong_hash_preflight_is_rejected_by_runner(self):
        document = sample()
        report = {"GeneratedAtUtc": (datetime.now(timezone.utc) - timedelta(seconds=601)).isoformat().replace("+00:00", "Z"),
                  "Classification": "RESTRICTED_QUERY_PREFLIGHT_VALID_NONCONNECTING", "ApprovalId": document["ApprovalId"], "ManifestSha256": "b" * 64,
                  "Queries": PREFLIGHT.QUERY_BINDINGS, "CredentialContentsRead": False, "PodmanCommands": 0, "DatabaseConnections": 0, "DatabaseQueries": 0}
        with self.assertRaisesRegex(RUNNER.DiagnosticRunError, "PREFLIGHT_NOT_FRESH_OR_NOT_MATCHED"):
            RUNNER._validate_fresh_preflight(report, document, "a" * 64, datetime.now(timezone.utc))

    def test_consumed_marker_and_existing_report_are_create_only_blockers(self):
        document = sample()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            directory.chmod(0o700)
            document["Evidence"] = {"ManifestPath": str(directory / "authorization.json"), "PreflightReportPath": str(directory / "preflight.json"), "ResultReportPath": str(directory / "result.json")}
            (directory / ".started").write_text("consumed")
            with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "AUTHORIZATION_ALREADY_CONSUMED"):
                PREFLIGHT.check_unique_and_create_only(document)
            (directory / ".started").unlink()
            (directory / "result.json").write_text("prior")
            with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "EVIDENCE_PATH_ALREADY_EXISTS"):
                PREFLIGHT.check_unique_and_create_only(document)

    def test_duplicate_approval_id_in_retained_manifest_is_rejected(self):
        document = sample()
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            directory.chmod(0o700)
            current_directory = directory / ("cw-m5-026b-query-diagnostic-" + document["ApprovalId"])
            current_directory.mkdir(mode=0o700)
            document["Evidence"] = {"ManifestPath": str(current_directory / "authorization.json"), "PreflightReportPath": str(current_directory / "preflight.json"), "ResultReportPath": str(current_directory / "result.json")}
            current_manifest = Path(document["Evidence"]["ManifestPath"])
            current_manifest.write_text(json.dumps(document))
            prior_directory = directory / "cw-m5-026b-query-diagnostic-prior"
            prior_directory.mkdir(mode=0o700)
            prior_path = prior_directory / "authorization.json"
            prior_path.write_text(json.dumps({"ApprovalId": document["ApprovalId"]}))
            with patch.object(PREFLIGHT, "VALIDATION_ROOT", directory):
                with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "APPROVAL_ID_ALREADY_RECORDED"):
                    PREFLIGHT.check_unique_and_create_only(document)

    def test_expired_approval_is_rejected(self):
        now = datetime.now(timezone.utc)
        document = sample(now - timedelta(hours=1))
        with patch.object(PREFLIGHT, "_schema_validate", return_value=None), patch("socket.gethostname", return_value="christian-laptop"), patch("os.getuid", return_value=1000):
            with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "AUTHORIZATION_NOT_CURRENT"):
                PREFLIGHT.validate_manifest(document, Path(document["Evidence"]["ManifestPath"]), now=now)

    def test_previously_unused_pasted_approval_cannot_be_used_for_execution(self):
        document = sample()
        document["ApprovalId"] = "CW-M5-026B-QUERY-DIAG-20261014-001"
        with patch.object(PREFLIGHT, "_schema_validate", return_value=None), patch("socket.gethostname", return_value="christian-laptop"), patch("os.getuid", return_value=1000):
            with self.assertRaisesRegex(PREFLIGHT.DiagnosticPreflightError, "KNOWN_UNUSED_DIAGNOSTIC_APPROVAL"):
                PREFLIGHT.validate_manifest(document, Path(document["Evidence"]["ManifestPath"]))

    def test_identity_failure_is_fail_closed_before_queries(self):
        document = sample()
        fresh = {"GeneratedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"), "Classification": "RESTRICTED_QUERY_PREFLIGHT_VALID_NONCONNECTING", "ApprovalId": document["ApprovalId"], "ManifestSha256": "a" * 64, "Queries": PREFLIGHT.QUERY_BINDINGS, "CredentialContentsRead": False, "PodmanCommands": 0, "DatabaseConnections": 0, "DatabaseQueries": 0}
        completed = subprocess.CompletedProcess([], 0, "true\n", "")
        with patch.object(RUNNER.PREFLIGHT, "validate_manifest", return_value={"ManifestSha256": "a" * 64}), patch.object(RUNNER, "_claim_once"), patch("subprocess.run", return_value=completed), patch.object(RUNNER.INSPECTOR, "_inspect_target_host", side_effect=RUNNER.INSPECTOR.InspectionError("CONTAINER_ID_MISMATCH")), patch.object(RUNNER, "_execute_query") as query:
            result = RUNNER.run_diagnostic(document, fresh)
        self.assertEqual("NOT_VERIFIED", result["ContainerIdentity"])
        query.assert_not_called()

    def test_timeout_is_sanitized_and_does_not_retry(self):
        target = dict(PREFLIGHT._targets()["Source"])
        target["CredentialDirectory"] = str(Path(target["CredentialOptionFile"]).parent)
        with patch.object(RUNNER.INSPECTOR, "_db_command", return_value=["mariadb", "--execute", "fixed"]), patch("subprocess.run", side_effect=subprocess.TimeoutExpired(["mariadb"], 1)) as call:
            result = RUNNER._execute_query("SOURCE_DATABASE_IDENTITY", target, Path(target["CredentialOptionFile"]), 1)
        self.assertEqual("TIMEOUT", result["Outcome"])
        self.assertEqual("UNKNOWN", result["ReachedMariaDB"])
        self.assertEqual(1, call.call_count)

    def test_error_report_keeps_only_errno_state_category_not_raw_stderr(self):
        details = RUNNER._error_details("ERROR 1045 (28000): Access denied for user 'secret-name'", 1)
        self.assertEqual("SERVER_REJECTED_QUERY", details["Outcome"])
        self.assertEqual("YES", details["ReachedMariaDB"])
        self.assertNotIn("secret-name", json.dumps(details))

    def test_error_2002_hy000_is_classified_as_unreached_connectivity_failure(self):
        details = RUNNER._error_details("ERROR 2002 (HY000): simulated connection refusal", 1)
        self.assertEqual("CONNECTION_FAILURE", details["Outcome"])
        self.assertEqual("NO", details["ReachedMariaDB"])
        self.assertEqual(2002, details["MariaDbErrorNumber"])
        self.assertEqual("HY000", details["SqlState"])

    def test_empty_unrecognized_and_initialization_stderr_remain_unclassified(self):
        target = dict(PREFLIGHT._targets()["Source"])
        target["CredentialDirectory"] = str(Path(target["CredentialOptionFile"]).parent)
        cases = ("", "some opaque client diagnostic", "mariadb: unknown option '--simulated'")
        with patch.object(RUNNER.INSPECTOR, "_db_command", return_value=["mariadb", "--execute", "fixed"]):
            for stderr in cases:
                completed = subprocess.CompletedProcess([], 1, "", stderr)
                with self.subTest(stderr=bool(stderr), initialization="unknown option" in stderr), patch("subprocess.run", return_value=completed):
                    result = RUNNER._execute_query("SOURCE_DATABASE_IDENTITY", target, Path(target["CredentialOptionFile"]), 5)
                self.assertEqual("CLIENT_FAILURE_UNCLASSIFIED", result["Outcome"])
                self.assertEqual("UNKNOWN", result["ReachedMariaDB"])
                self.assertEqual(1, result["ClientExitCode"])
                if stderr:
                    self.assertNotIn(stderr, json.dumps(result))

    def test_failed_client_initialization_is_not_mistaken_for_missing_executable(self):
        target = dict(PREFLIGHT._targets()["Source"])
        target["CredentialDirectory"] = str(Path(target["CredentialOptionFile"]).parent)
        completed = subprocess.CompletedProcess([], 1, "", "mariadb: unknown option '--simulated'")
        with patch.object(RUNNER.INSPECTOR, "_db_command", return_value=["mariadb", "--execute", "fixed"]), patch("subprocess.run", return_value=completed):
            result = RUNNER._execute_query("SOURCE_DATABASE_IDENTITY", target, Path(target["CredentialOptionFile"]), 5)
        self.assertEqual("CLIENT_FAILURE_UNCLASSIFIED", result["Outcome"])
        self.assertEqual(1, result["ClientExitCode"])
        self.assertIsNone(result["MariaDbErrorNumber"])
        self.assertNotEqual("CLIENT_UNAVAILABLE", result["Outcome"])

    def test_create_once_marker_rejects_reuse(self):
        document = sample()
        with tempfile.TemporaryDirectory() as temp:
            document["Evidence"]["ManifestPath"] = str(Path(temp) / "authorization.json")
            marker = Path(temp) / ".started"
            marker.write_text("claimed")
            with self.assertRaises(FileExistsError):
                RUNNER._claim_once(document, "a" * 64)

if __name__ == "__main__":
    unittest.main()
