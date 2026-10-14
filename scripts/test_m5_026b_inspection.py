"""Source-safe tests for the isolated CW-M5-026B read-only inspector."""

from __future__ import annotations

import copy
import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name: str, path: str):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PREFLIGHT = load("m5_026b_inspection_preflight_tests", "scripts/preflight-m5-026b-inspection.py")
INSPECTOR = load("m5_026b_inspection_tests", "scripts/cw-m5-026b/inspect_readiness.py")


def sample_manifest(now: datetime | None = None) -> dict:
    now = now or datetime.now(timezone.utc)
    targets = PREFLIGHT.expected_targets()
    manifest = {
        "ManifestVersion": PREFLIGHT.CONTRACT,
        "ApprovalId": "CW-M5-026B-INSPECT-TEST-ONLY",
        "ApprovedAtUtc": now.isoformat().replace("+00:00", "Z"),
        "ExpiresAtUtc": (now + timedelta(hours=1)).isoformat().replace("+00:00", "Z"),
        "Approver": {"Name": "Test Approver", "Role": "Project Owner"},
        "Operator": {"Name": "Test Operator", "Role": "System Administrator"},
        "TargetOwner": "Test Owner",
        "ExecutionHost": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True},
        "Isolation": {"Disposable": True, "Production": False, "SharedWithProduction": False},
        "PermittedOperations": copy.deepcopy(PREFLIGHT.REQUIRED_PERMISSIONS),
        "ReportPath": "/home/cdjensen94/continuumwiki-validation/inspection-test-result.json",
    }
    for role, values in targets.items():
        values = dict(values)
        values.update({"Disposable": True, "CredentialOptionFile": values["CredentialDirectory"] + "/inspection-client.cnf"})
        manifest[role] = values
    return manifest


def schema_results(database: str) -> dict[str, str]:
    expected = INSPECTOR._expected_schema()
    tables = [f"{name}\tBASE TABLE\tInnoDB" for name in sorted(expected["Tables"])]
    columns = [f"{table}\t{column}\t{meta['ColumnType']}\t{meta['IsNullable']}"
               for (table, column), meta in sorted(expected["Columns"].items())]
    indexes = []
    for (table, name), (non_unique, cols) in sorted(expected["Indexes"].items()):
        indexes.extend(f"{table}\t{name}\t{1 if non_unique else 0}\t{i}\t{col}" for i, col in enumerate(cols, 1))
    constraints = [f"{table}\t{name}\t{kind}" for (table, name), kind in sorted(expected["Constraints"].items())]
    fks = []
    for (table, name), meta in sorted(expected["ForeignKeys"].items()):
        for i, (col, ref_col) in enumerate(zip(meta["Columns"], meta["ReferencedColumns"]), 1):
            fks.append(f"{table}\t{name}\t{col}\t{database}\t{meta['ReferencedTable']}\t{ref_col}\t{i}\t{meta.get('OnDelete','RESTRICT')}")
    ledgers = [f"{domain}\t{version}" for domain, version in sorted(expected["Ledgers"].items())]
    return {"Tables": "\n".join(tables), "Columns": "\n".join(columns), "Indexes": "\n".join(indexes),
            "Constraints": "\n".join(constraints), "ForeignKeys": "\n".join(fks), "Ledgers": "\n".join(ledgers)}


class InspectionAuthorizationTests(unittest.TestCase):
    def test_destination_run_id_uses_continuation_specific_provenance(self):
        original_read = PREFLIGHT._read_json

        def read_with_destination_run_id(path, code):
            value = original_read(path, code)
            if path == PREFLIGHT.CONTINUATION_PATH:
                value = copy.deepcopy(value)
                value["Destination"]["ProvisioningRunId"] = "20261013T010001Z-destination"
            return value

        with patch.object(PREFLIGHT, "_read_json", side_effect=read_with_destination_run_id):
            targets = PREFLIGHT.expected_targets()
        self.assertEqual("20261012T093746Z-86380", targets["Source"]["ProvisioningRunId"])
        self.assertEqual("20261013T010001Z-destination", targets["Destination"]["ProvisioningRunId"])
        self.assertNotEqual(targets["Source"]["ProvisioningRunId"], targets["Destination"]["ProvisioningRunId"])

    def test_contract_is_valid_draft_2020_12_and_separate_from_rehearsal(self):
        try:
            from jsonschema import Draft202012Validator
        except ImportError:
            self.skipTest("jsonschema validator not installed")
        schema = json.loads((ROOT / "docs/operations/cw-m5-026b-readonly-inspection-authorization.schema.json").read_text())
        Draft202012Validator.check_schema(schema)
        Draft202012Validator(schema).validate(sample_manifest())
        rehearsal = json.loads((ROOT / "docs/operations/cw-m5-026b-authorization-manifest.schema.json").read_text())
        self.assertNotEqual(rehearsal["$id"], schema["$id"])
        self.assertEqual(False, schema["properties"]["PermittedOperations"]["properties"]["Backup"]["const"])

    def test_exact_retained_source_and_destination_bindings_are_required(self):
        manifest = sample_manifest()
        self.assertEqual(PREFLIGHT.expected_targets()["Source"]["ContainerId"], manifest["Source"]["ContainerId"])
        manifest["Destination"]["ContainerId"] = "0" * 64
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "TARGET_BINDING_MISMATCH"):
            PREFLIGHT.validate_manifest(manifest)
        manifest = sample_manifest()
        manifest["Source"]["CredentialOptionFile"] = manifest["Source"]["CredentialDirectory"] + "/../destination-ro1/client.cnf"
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "CREDENTIAL_OPTION_FILE_REFERENCE_INVALID"):
            PREFLIGHT.validate_manifest(manifest)
        manifest = sample_manifest()
        manifest["Source"]["CredentialOptionFile"] = manifest["Source"]["CredentialDirectory"] + "/client.cnf"
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "CREDENTIAL_OPTION_FILE_REFERENCE_INVALID"):
            PREFLIGHT.validate_manifest(manifest)

    def test_expired_future_or_overlong_approval_rejected(self):
        now = datetime.now(timezone.utc)
        expired = sample_manifest(now - timedelta(hours=2))
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "AUTHORIZATION_NOT_CURRENT"):
            PREFLIGHT.validate_manifest(expired, now=now)
        future = sample_manifest(now + timedelta(minutes=1))
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "AUTHORIZATION_NOT_CURRENT"):
            PREFLIGHT.validate_manifest(future, now=now)
        too_long = sample_manifest(now)
        too_long["ExpiresAtUtc"] = (now + timedelta(hours=24, seconds=1)).isoformat().replace("+00:00", "Z")
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "AUTHORIZATION_WINDOW_INVALID"):
            PREFLIGHT.validate_manifest(too_long, now=now)

    def test_overprivileged_and_unknown_permission_rejected(self):
        manifest = sample_manifest()
        manifest["PermittedOperations"]["Backup"] = True
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "OPERATION_SCOPE_NOT_READONLY_EXACT"):
            PREFLIGHT.validate_manifest(manifest)
        manifest = sample_manifest()
        manifest["PermittedOperations"]["ArbitrarySql"] = True
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "OPERATION_SCOPE_NOT_READONLY_EXACT"):
            PREFLIGHT.validate_manifest(manifest)

    def test_manifest_secret_fields_invalid_host_and_nonisolated_targets_rejected(self):
        manifest = sample_manifest()
        manifest["Source"]["password"] = "never-a-secret"
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "SECRET_BEARING_PROPERTY_REJECTED"):
            PREFLIGHT.validate_manifest(manifest)
        manifest = sample_manifest()
        manifest["ExecutionHost"]["Uid"] = 0
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "AUTHORIZED_HOST_IDENTITY_MISMATCH"):
            PREFLIGHT.validate_manifest(manifest)
        manifest = sample_manifest()
        manifest["Isolation"]["Production"] = True
        with self.assertRaisesRegex(PREFLIGHT.InspectionPreflightError, "TARGET_ISOLATION_NOT_AUTHORIZED"):
            PREFLIGHT.validate_manifest(manifest)

    def test_nonconnecting_preflight_zero_operations_and_create_only_report(self):
        manifest = sample_manifest()
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp, "manifest.json")
            path.write_text(json.dumps(manifest))
            path.chmod(0o600)
            report = Path(temp, "preflight.json")
            ok, result = PREFLIGHT.preflight(path, report)
            self.assertTrue(ok)
            self.assertEqual(0, result["ResourceOperationCounts"]["Podman"])
            original = report.read_text()
            with self.assertRaises(FileExistsError):
                PREFLIGHT._write_once(report, result)
            self.assertEqual(original, report.read_text())


class InspectionParsingTests(unittest.TestCase):
    def test_grant_classifier_never_returns_raw_grant_lines(self):
        scoped = INSPECTOR.classify_grants("GRANT USAGE ON *.* TO 'app'@'%'\nGRANT SELECT ON `cw_m5_026b_source_ro1`.* TO 'app'@'%'", "cw_m5_026b_source_ro1")
        self.assertEqual("VERIFIED_SCHEMA_SCOPED", scoped["Classification"])
        self.assertNotIn("GRANT", json.dumps(scoped))
        for grants, code in (("GRANT SELECT ON *.* TO 'x'@'%'", "GLOBAL_PRIVILEGES_PRESENT"),
                             ("GRANT SELECT ON otherdb.* TO 'x'@'%'", "UNRELATED_OR_UNPARSEABLE_SCOPE_PRESENT"),
                             ("GRANT SELECT ON `cw_m5_026b_source_ro1`.* TO 'x'@'%' WITH GRANT OPTION", "GRANT_OPTION_PRESENT"),
                             ("GRANT role_x TO 'x'@'%'", "UNSCOPED_ROLE_OR_GRANT_PRESENT")):
            result = INSPECTOR.classify_grants(grants, "cw_m5_026b_source_ro1")
            self.assertIn(code, result["Codes"])
            self.assertNotIn(grants, json.dumps(result))
        self.assertEqual("INSPECTION_FAILED", INSPECTOR.classify_grants("", "db")["Classification"])

    def test_identity_and_metadata_parsers_fail_closed_on_malformed_data(self):
        target = sample_manifest()["Source"]
        self.assertEqual("MATCH", INSPECTOR._identity_status(f"{target['DatabaseName']}\t11.8.9-MariaDB\tmariadb\t{target['DatabaseIdentity']}@localhost", target))
        self.assertEqual("IDENTITY_RESPONSE_MALFORMED", INSPECTOR._identity_status("raw-secret", target))
        with self.assertRaisesRegex(INSPECTOR.InspectionError, "COLUMNS_RESPONSE_MALFORMED"):
            INSPECTOR._rows("private", 4, "COLUMNS_RESPONSE_MALFORMED")

    def test_schema_comparison_identifies_exact_schema_and_differences(self):
        database = sample_manifest()["Source"]["DatabaseName"]
        data = schema_results(database)
        result = INSPECTOR.compare_schema({"DatabaseName": database}, data)
        self.assertEqual("SCHEMA_MATCH", result["Classification"])
        self.assertEqual(21, result["ExpectedTableCount"])
        self.assertEqual(19, result["ExpectedForeignKeyCount"])
        broken = dict(data)
        broken["Indexes"] = ""
        result = INSPECTOR.compare_schema({"DatabaseName": database}, broken)
        self.assertEqual("SCHEMA_MISMATCH", result["Classification"])
        self.assertTrue(result["Differences"]["MissingIndexes"])

    def test_cross_schema_foreign_key_is_not_mistaken_for_expected_local_reference(self):
        database = sample_manifest()["Source"]["DatabaseName"]
        data = schema_results(database)
        first = data["ForeignKeys"].splitlines()
        fields = first[0].split("\t")
        fields[3] = "unrelated_schema"
        first[0] = "\t".join(fields)
        data["ForeignKeys"] = "\n".join(first)
        result = INSPECTOR.compare_schema({"DatabaseName": database}, data)
        self.assertEqual("SCHEMA_MISMATCH", result["Classification"])
        self.assertTrue(result["Differences"]["IncompatibleForeignKeys"])

    def test_fixture_and_destination_assessments_are_independent_and_aggregate_only(self):
        fixture = INSPECTOR._fixture_summary("Page\t2\nPageRevision\t3\nIndexState:Verified\t2\nMembership\t1\nRecoveryOperation\t1\nRecoveryAudit\t2\nAdministrativeGrant\t3\nAdministrativeGrantAudit\t6",
                                              "1\t1\t1\t2\t1")
        self.assertEqual("AGGREGATE_CASES_PRESENT_SEMANTICS_UNVERIFIED", fixture["Classification"])
        self.assertFalse(fixture["Coverage"]["ExplicitSyntheticFixtureIdentityProven"])
        self.assertEqual("EMPTY", INSPECTOR._destination_empty("TABLES_OR_VIEWS\t0\nROUTINES\t0\nEVENTS\t0\nTRIGGERS\t0")["Classification"])
        self.assertEqual("NOT_EMPTY", INSPECTOR._destination_empty("TABLES_OR_VIEWS\t1\nROUTINES\t0\nEVENTS\t0\nTRIGGERS\t0")["Classification"])

    def test_podman_command_and_sql_surfaces_are_fixed_read_only_allowlists(self):
        target = sample_manifest()["Source"]
        commands = INSPECTOR.podman_commands(target)
        self.assertEqual(4, len(commands))
        self.assertIn("{{.ImageName}}", commands[0][4])
        self.assertNotIn("{{.Config.Image}}", commands[0][4])
        self.assertTrue(all(command[0] == "/usr/bin/podman" and not any(word in command for word in ("rm", "exec", "run", "volume rm")) for command in commands))
        self.assertTrue(all(sql.strip().upper().startswith(("SELECT ", "SHOW GRANTS")) for sql in INSPECTOR.SQL.values()))
        with self.assertRaisesRegex(INSPECTOR.InspectionError, "SQL_NOT_ALLOWLISTED"):
            INSPECTOR._db_command(target, Path("/tmp/no-read"), "DROP TABLE Page")

    def test_container_identity_fields_have_independent_stable_diagnostics(self):
        target = sample_manifest()["Source"]
        expected = [target["ContainerId"], "true", target["ImageReference"], "sha256:image-id",
                    target["ProvisioningRunId"], target["DatabaseName"], f"{target['VolumeName']}=/var/lib/mysql;"]
        cases = (
            (0, "0" * 64, "CONTAINER_ID_MISMATCH"),
            (1, "false", "CONTAINER_NOT_RUNNING"),
            (1, "unknown", "CONTAINER_RUNNING_STATE_MALFORMED"),
            (2, "mariadb@sha256:different-reference", "CONFIGURED_IMAGE_REFERENCE_MISMATCH"),
            (4, "different-run", "PROVISIONING_RUN_ID_LABEL_MISMATCH"),
            (5, "different-database", "DATABASE_LABEL_MISMATCH"),
        )
        for index, value, code in cases:
            with self.subTest(code=code):
                fields = list(expected)
                fields[index] = value
                calls = []
                def run(command):
                    calls.append(command)
                    return subprocess.CompletedProcess(command, 0, "|".join(fields) + "\n", "")
                with self.assertRaisesRegex(INSPECTOR.InspectionError, code):
                    INSPECTOR._inspect_target_host(target, run)
                self.assertEqual(1, len(calls))

    def test_container_and_image_inspect_output_fail_closed_independently(self):
        target = sample_manifest()["Source"]
        container_cmd, port_cmd, image_cmd, volume_cmd = INSPECTOR.podman_commands(target)
        valid = [target["ContainerId"], "true", target["ImageReference"], "sha256:container-image",
                 target["ProvisioningRunId"], target["DatabaseName"], f"{target['VolumeName']}=/var/lib/mysql;"]
        for result, code in (
            (subprocess.CompletedProcess(container_cmd, 1, "", "private stderr"), "CONTAINER_INSPECT_FAILED"),
            (subprocess.CompletedProcess(container_cmd, 0, "only|six|fields|here|now|", ""), "CONTAINER_INSPECT_MALFORMED"),
            (subprocess.CompletedProcess(container_cmd, 0, "|".join(valid) + "\nextra\n", ""), "CONTAINER_INSPECT_MALFORMED"),
        ):
            with self.subTest(code=code, returncode=result.returncode, stdout=result.stdout):
                with self.assertRaisesRegex(INSPECTOR.InspectionError, code):
                    INSPECTOR._inspect_target_host(target, lambda _command, result=result: result)

        def wrong_image_id(command):
            if command == container_cmd:
                return subprocess.CompletedProcess(command, 0, "|".join(valid) + "\n", "")
            if command == image_cmd:
                return subprocess.CompletedProcess(command, 0, "sha256:different-image|" + target["ImageReference"] + "\n", "")
            if command == port_cmd:
                return subprocess.CompletedProcess(command, 0, f"3306/tcp -> {target['Endpoint']}\n", "")
            if command == volume_cmd:
                return subprocess.CompletedProcess(command, 0, f"{target['VolumeName']}|{target['ProvisioningRunId']}\n", "")
            raise AssertionError("unexpected command in fixed Podman inspection sequence")
        with self.assertRaisesRegex(INSPECTOR.InspectionError, "IMMUTABLE_IMAGE_DIGEST_MISMATCH"):
            INSPECTOR._inspect_target_host(target, wrong_image_id)

        for result, code in (
            (subprocess.CompletedProcess(image_cmd, 1, "", "private stderr"), "IMAGE_INSPECT_FAILED"),
            (subprocess.CompletedProcess(image_cmd, 0, "only-one-field\n", ""), "IMAGE_INSPECT_MALFORMED"),
        ):
            with self.subTest(image_code=code):
                def malformed_image(command, result=result):
                    if command == container_cmd:
                        return subprocess.CompletedProcess(command, 0, "|".join(valid) + "\n", "")
                    if command == port_cmd:
                        return subprocess.CompletedProcess(command, 0, f"3306/tcp -> {target['Endpoint']}\n", "")
                    if command == image_cmd:
                        return result
                    raise AssertionError("volume inspection must follow successful image verification")
                with self.assertRaisesRegex(INSPECTOR.InspectionError, code):
                    INSPECTOR._inspect_target_host(target, malformed_image)

    def test_first_target_identity_failure_is_two_podman_commands_and_no_database_access(self):
        manifest = sample_manifest()
        now = datetime.now(timezone.utc)
        pf = PREFLIGHT.validate_manifest(manifest, now=now)
        pf.update({"Classification": "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING",
                   "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z")})
        calls = []
        source = manifest["Source"]
        def fake_run(command):
            calls.append(command)
            if command[1] == "info":
                return subprocess.CompletedProcess(command, 0, "true\n", "")
            fields = ["0" * 64, "true", source["ImageReference"], "sha256:image-id",
                      source["ProvisioningRunId"], source["DatabaseName"], f"{source['VolumeName']}=/var/lib/mysql;"]
            return subprocess.CompletedProcess(command, 0, "|".join(fields) + "\n", "")
        with patch.object(INSPECTOR, "_check_credential_option_file", side_effect=AssertionError("must not inspect credentials")):
            result = INSPECTOR.run_inspection(manifest, pf, approval_id=manifest["ApprovalId"], run=fake_run,
                                               host=("christian-laptop", "cdjensen94", 1000))
        self.assertEqual("CATEGORY_READONLY_INSPECTION_INCOMPLETE", result["Classification"])
        self.assertEqual(["CONTAINER_ID_MISMATCH"], result["Errors"])
        self.assertEqual(2, len(calls))
        self.assertEqual("info", calls[0][1])
        self.assertEqual("container", calls[1][1])
        self.assertEqual(2, result["ResourceOperationCounts"]["PodmanReadOnlyCommands"])
        self.assertEqual(0, result["ResourceOperationCounts"]["DatabaseConnections"])
        self.assertFalse(any(command[0] == INSPECTOR.MARIADB for command in calls))

    def test_inspector_verifies_both_hosts_before_any_database_connection(self):
        manifest = sample_manifest()
        now = datetime.now(timezone.utc)
        preflight = PREFLIGHT.validate_manifest(manifest, now=now)
        preflight.update({"Classification": "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING",
                          "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z")})
        calls = []

        def fake_run(command):
            calls.append(command)
            if command[0] == INSPECTOR.PODMAN and command[1] == "info":
                return subprocess.CompletedProcess(command, 0, "true\n", "")
            if command[0] == INSPECTOR.PODMAN:
                target = next((x for x in (manifest["Source"], manifest["Destination"])
                               if x["ContainerName"] in command or x["VolumeName"] in command), manifest["Source"])
                if command[1] == "container":
                    value = "|".join((target["ContainerId"], "true", target["ImageReference"], "sha256:image-id",
                                      target["ProvisioningRunId"], target["DatabaseName"], f"{target['VolumeName']}=/var/lib/mysql;"))
                elif command[1] == "port":
                    value = f"3306/tcp -> {target['Endpoint']}"
                elif command[1] == "image":
                    value = f"sha256:image-id|{target['ImageReference']}"
                else:
                    value = f"{target['VolumeName']}|{target['ProvisioningRunId']}"
                return subprocess.CompletedProcess(command, 0, value + "\n", "")
            target = next(x for x in (manifest["Source"], manifest["Destination"]) if x["DatabaseName"] == command[command.index("--database") + 1])
            sql = command[-1]
            metadata = schema_results(target["DatabaseName"])
            if sql == INSPECTOR.SQL["Identity"]:
                out = f"{target['DatabaseName']}\t11.8.9-MariaDB\tmariadb.org\t{target['DatabaseIdentity']}@localhost"
            elif sql == INSPECTOR.SQL["Grants"]:
                out = f"GRANT USAGE ON *.* TO '{target['DatabaseIdentity']}'@'localhost'\nGRANT SELECT ON `{target['DatabaseName']}`.* TO '{target['DatabaseIdentity']}'@'localhost'"
            elif sql == INSPECTOR.SQL["Tables"]: out = metadata["Tables"]
            elif sql == INSPECTOR.SQL["Columns"]: out = metadata["Columns"]
            elif sql == INSPECTOR.SQL["Indexes"]: out = metadata["Indexes"]
            elif sql == INSPECTOR.SQL["Constraints"]: out = metadata["Constraints"]
            elif sql == INSPECTOR.SQL["ForeignKeys"]: out = metadata["ForeignKeys"]
            elif sql == INSPECTOR.SQL["Ledgers"]: out = metadata["Ledgers"]
            elif sql == INSPECTOR.SQL["FixtureAggregate"]: out = "Page\t2\nPageRevision\t2\nIndexState:Verified\t2\nMembership\t1\nRecoveryOperation\t1\nRecoveryAudit\t1\nAdministrativeGrant\t1\nAdministrativeGrantAudit\t1"
            elif sql == INSPECTOR.SQL["FixtureCases"]: out = "1\t1\t1\t2\t1"
            elif sql == INSPECTOR.SQL["DestinationObjects"]: out = "TABLES_OR_VIEWS\t0\nROUTINES\t0\nEVENTS\t0\nTRIGGERS\t0"
            else: raise AssertionError("unexpected SQL escaped allowlist")
            return subprocess.CompletedProcess(command, 0, out + "\n", "")

        with patch.object(INSPECTOR, "_check_credential_option_file", side_effect=lambda target, uid: Path(target["CredentialOptionFile"])):
            result = INSPECTOR.run_inspection(manifest, preflight, approval_id=manifest["ApprovalId"], run=fake_run,
                                               host=("christian-laptop", "cdjensen94", 1000))
        self.assertEqual("CATEGORY_READONLY_INSPECTION_COMPLETED", result["Classification"], result)
        self.assertEqual("SCHEMA_MATCH", result["Targets"]["Source"]["Schema"]["Classification"])
        self.assertEqual("EMPTY", result["Targets"]["Destination"]["DestinationEmptiness"]["Classification"])
        self.assertEqual(19, result["ResourceOperationCounts"]["DatabaseConnections"])
        self.assertEqual(0, result["ResourceOperationCounts"]["DatabaseMutations"])
        serialized = json.dumps(result)
        self.assertNotIn("GRANT SELECT", serialized)
        self.assertNotIn("continuumwiki_m5_026b_source_app@localhost", serialized)
        self.assertEqual(9, sum(command[0] == INSPECTOR.PODMAN for command in calls))

    def test_unexpected_container_stops_before_any_database_access(self):
        manifest = sample_manifest()
        now = datetime.now(timezone.utc)
        pf = PREFLIGHT.validate_manifest(manifest, now=now)
        pf.update({"Classification": "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING",
                   "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z")})
        calls = []
        def fake_run(command):
            calls.append(command)
            if command[1] == "info": return subprocess.CompletedProcess(command, 0, "true", "")
            return subprocess.CompletedProcess(command, 0, f"{'0' * 64}|true|bad-image|id|bad-run|bad-db|bad-volume=/tmp;", "")
        with patch.object(INSPECTOR, "_check_credential_option_file", side_effect=lambda target, uid: Path(target["CredentialOptionFile"])):
            result = INSPECTOR.run_inspection(manifest, pf, approval_id=manifest["ApprovalId"], run=fake_run,
                                               host=("christian-laptop", "cdjensen94", 1000))
        self.assertEqual("CATEGORY_READONLY_INSPECTION_INCOMPLETE", result["Classification"])
        self.assertIn("CONTAINER_ID_MISMATCH", result["Errors"])
        self.assertFalse(any(command[0] == INSPECTOR.MARIADB for command in calls))

    def test_wrong_approval_stale_preflight_and_wrong_host_fail_before_podman(self):
        manifest = sample_manifest()
        now = datetime.now(timezone.utc)
        pf = PREFLIGHT.validate_manifest(manifest, now=now)
        pf.update({"Classification": "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING",
                   "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z")})
        calls = []
        run = lambda command: calls.append(command) or subprocess.CompletedProcess(command, 0, "true", "")
        with self.assertRaisesRegex(INSPECTOR.InspectionError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
            INSPECTOR.run_inspection(manifest, pf, approval_id="wrong", run=run, host=("christian-laptop", "cdjensen94", 1000))
        stale = dict(pf, GeneratedAtUtc=(now - timedelta(minutes=11)).isoformat().replace("+00:00", "Z"))
        with self.assertRaisesRegex(INSPECTOR.InspectionError, "FRESH_PREFLIGHT_OR_APPROVAL_MISMATCH"):
            INSPECTOR.run_inspection(manifest, stale, approval_id=manifest["ApprovalId"], run=run, host=("christian-laptop", "cdjensen94", 1000))
        result = INSPECTOR.run_inspection(manifest, pf, approval_id=manifest["ApprovalId"], run=run, host=("other", "other", 1000))
        self.assertIn("EXECUTION_HOST_IDENTITY_MISMATCH", result["Errors"])
        self.assertEqual([], calls)

    def test_incomplete_database_queries_are_reported_without_raw_diagnostics(self):
        manifest = sample_manifest()
        now = datetime.now(timezone.utc)
        pf = PREFLIGHT.validate_manifest(manifest, now=now)
        pf.update({"Classification": "CATEGORY_READONLY_INSPECTION_AUTHORIZATION_VALID_NONCONNECTING",
                   "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z")})
        def fake_run(command):
            if command[0] == INSPECTOR.PODMAN:
                if command[1] == "info": return subprocess.CompletedProcess(command, 0, "true", "")
                target = next((x for x in (manifest["Source"], manifest["Destination"])
                               if x["ContainerName"] in command or x["VolumeName"] in command), manifest["Source"])
                if command[1] == "container": out = "|".join((target["ContainerId"], "true", target["ImageReference"], "sha256:image-id", target["ProvisioningRunId"], target["DatabaseName"], f"{target['VolumeName']}=/var/lib/mysql;"))
                elif command[1] == "port": out = f"3306/tcp -> {target['Endpoint']}"
                elif command[1] == "image": out = f"sha256:image-id|{target['ImageReference']}"
                else: out = f"{target['VolumeName']}|{target['ProvisioningRunId']}"
                return subprocess.CompletedProcess(command, 0, out, "")
            if command[-1] == INSPECTOR.SQL["Identity"]:
                t = next(x for x in (manifest["Source"], manifest["Destination"]) if x["DatabaseName"] == command[command.index("--database") + 1])
                return subprocess.CompletedProcess(command, 0, f"{t['DatabaseName']}\t11.8.9-MariaDB\tmariadb\t{t['DatabaseIdentity']}@localhost", "")
            return subprocess.CompletedProcess(command, 1, "", "raw sensitive error")
        with patch.object(INSPECTOR, "_check_credential_option_file", side_effect=lambda target, uid: Path(target["CredentialOptionFile"])):
            result = INSPECTOR.run_inspection(manifest, pf, approval_id=manifest["ApprovalId"], run=fake_run,
                                               host=("christian-laptop", "cdjensen94", 1000))
        self.assertEqual("CATEGORY_READONLY_INSPECTION_INCOMPLETE", result["Classification"])
        self.assertNotIn("raw sensitive error", json.dumps(result))
        self.assertEqual("INSPECTION_INCOMPLETE", result["Targets"]["Source"]["Schema"]["Classification"])

    def test_report_destination_is_private_and_create_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            report_dir = root / "reports"
            report_dir.mkdir(mode=0o700)
            report_path = report_dir / "inspection.json"
            with patch.object(INSPECTOR, "REPORT_ROOT", root):
                INSPECTOR._validate_report_target(report_path, os.getuid())
                INSPECTOR._write_once(report_path, {"Classification": "SAFE"})
                with self.assertRaisesRegex(INSPECTOR.InspectionError, "REPORT_PATH_ALREADY_EXISTS"):
                    INSPECTOR._validate_report_target(report_path, os.getuid())
                original = report_path.read_bytes()
                with self.assertRaises(FileExistsError):
                    INSPECTOR._write_once(report_path, {"Classification": "OVERWRITE"})
                self.assertEqual(original, report_path.read_bytes())


if __name__ == "__main__":
    unittest.main()
