"""Source-only tests for the bounded stopped-source diagnostic plan."""

from __future__ import annotations

import importlib.util
import hashlib
import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "m5_026b_stopped_source_diagnostic_tests",
    ROOT / "scripts/cw-m5-026b/stopped_source_diagnostic.py",
)
assert SPEC and SPEC.loader
DIAGNOSTIC = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(DIAGNOSTIC)


def valid_observations(**overrides):
    target = DIAGNOSTIC.EXPECTED
    container = [
        target["ContainerId"], "exited", "false", "137", target["ImageReference"],
        "sha256:container-image", target["ProvisioningRunId"], target["DatabaseName"],
        f"{target['VolumeName']}=/var/lib/mysql;",
        f"3306/tcp={target['Endpoint']},;",
    ]
    values = {
        "Rootless": {"ReturnCode": 0, "Stdout": "true\n"},
        "Exists": {"ReturnCode": 0, "Stdout": ""},
        "Container": {"ReturnCode": 0, "Stdout": "|".join(container) + "\n"},
        "Port": {"ReturnCode": 0, "Stdout": f"3306/tcp -> {target['Endpoint']}\n"},
        "Image": {"ReturnCode": 0, "Stdout": f"sha256:container-image|{target['ImageReference']}\n"},
        "Volume": {"ReturnCode": 0, "Stdout": f"{target['VolumeName']}|{target['ProvisioningRunId']}|source\n"},
    }
    for name, value in overrides.items():
        values[name] = value
    return values


class StoppedSourceDiagnosticTests(unittest.TestCase):
    def test_restart_contract_is_separate_and_requires_a_successful_diagnostic(self):
        try:
            from jsonschema import Draft202012Validator, FormatChecker
        except ImportError:
            self.skipTest("jsonschema validator not installed")
        schema = json.loads((ROOT / "docs/operations/cw-m5-026b-stopped-source-restart-authorization.schema.json").read_text())
        Draft202012Validator.check_schema(schema)
        target = DIAGNOSTIC.EXPECTED
        manifest = {
            "ManifestVersion": schema["$id"],
            "ApprovalId": "CW-M5-026B-RECOVERY-START-TEST-ONLY",
            "ApprovedAtUtc": "2026-10-13T01:00:00Z",
            "ExpiresAtUtc": "2026-10-13T02:00:00Z",
            "Approver": {"Name": "Test Owner", "Role": "Project Owner"},
            "Operator": {"Name": "Test Operator", "Role": "System Administrator"},
            "TargetOwner": "Test Owner",
            "ExecutionHost": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True},
            "Target": {key: target[key] for key in ("ContainerName", "ContainerId", "ImageReference", "VolumeName", "DatabaseName", "Endpoint", "ProvisioningRunId")},
            "DiagnosticClassification": "SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH",
            "DiagnosticReportSha256": "a" * 64,
            "PermittedOperations": {
                "StartStoppedSourceContainerOnce": True, "ContainerCreation": False,
                "ContainerRecreation": False, "ContainerRestart": False,
                "ImageOrVolumeChanges": False, "DatabaseConnections": False,
                "DatabaseMutations": False, "Migrations": False, "GrantChanges": False,
                "FixtureWrites": False, "Backup": False, "Restore": False, "Cleanup": False,
                "ContainerVolumeDeletion": False, "ProductionAccess": False,
            },
            "ResultReportPath": "/home/cdjensen94/continuumwiki-validation/start-result.json",
        }
        validator = Draft202012Validator(schema, format_checker=FormatChecker())
        validator.validate(manifest)
        manifest["DiagnosticClassification"] = "SOURCE_CONTAINER_RECREATED"
        self.assertTrue(list(validator.iter_errors(manifest)))

    def test_command_plan_is_exact_target_bounded_and_read_only(self):
        commands = DIAGNOSTIC.podman_diagnostic_commands()
        self.assertEqual(6, len(commands))
        self.assertEqual(["info", "container", "container", "port", "image", "volume"],
                         [command[1] for command in commands])
        self.assertIn(DIAGNOSTIC.EXPECTED["ContainerName"], commands[1])
        self.assertEqual(DIAGNOSTIC.EXPECTED["ContainerName"], commands[2][5])
        self.assertIn(".State.ExitCode", commands[2][4])
        self.assertIn(".HostConfig.PortBindings", commands[2][4])
        self.assertIn(DIAGNOSTIC.EXPECTED["ImageReference"], commands[4])
        self.assertIn(DIAGNOSTIC.EXPECTED["VolumeName"], commands[5])
        self.assertFalse(any(word in command for command in commands for word in
                             ("start", "restart", "rm", "exec", "run", "volume rm", "--latest")))
        self.assertFalse(any("mariadb" == word for command in commands for word in command))

    def test_matching_stopped_container_records_exit_code_and_identity(self):
        report = DIAGNOSTIC.classify_observations(valid_observations())
        self.assertEqual("SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH", report["Classification"])
        self.assertEqual(137, report["ObservedExitCode"])
        self.assertEqual("exited", report["ObservedState"])
        self.assertTrue(report["PinnedImageIdentityMatched"])
        self.assertEqual(["ACTIVE_ENDPOINT_INACTIVE_WHILE_STOPPED"], report["Findings"])
        self.assertTrue(report["ConfiguredEndpointMatched"])
        self.assertTrue(report["PodmanPortMappingReported"])
        self.assertEqual("CONTAINER_STOPPED_ENDPOINT_INACTIVE", report["ActiveEndpointObservation"])
        self.assertTrue(report["ContainerRunIdLabelPresent"])
        self.assertTrue(report["VolumeRunIdLabelPresent"])
        self.assertTrue(report["ContainerAndVolumeRunIdLabelsMatch"])
        expected_hash = hashlib.sha256(DIAGNOSTIC.EXPECTED["ProvisioningRunId"].encode()).hexdigest()
        self.assertEqual(expected_hash, report["ContainerRunIdLabelSha256"])
        self.assertEqual(0, report["DatabaseConnections"])
        self.assertEqual(0, report["DatabaseMutations"])

    def test_run_id_observations_distinguish_shared_alternate_from_absent_without_raw_values(self):
        values = valid_observations()
        fields = values["Container"]["Stdout"].strip().split("|")
        fields[6] = "alternate-run"
        values["Container"]["Stdout"] = "|".join(fields) + "\n"
        values["Volume"]["Stdout"] = f"{DIAGNOSTIC.EXPECTED['VolumeName']}|alternate-run|source\n"
        shared = DIAGNOSTIC.classify_observations(values)
        self.assertFalse(shared["RunIdLabelMatched"])
        self.assertFalse(shared["VolumeIdentityMatched"])
        self.assertTrue(shared["ContainerAndVolumeRunIdLabelsMatch"])
        self.assertNotIn("alternate-run", json.dumps(shared))

        absent = valid_observations()
        fields = absent["Container"]["Stdout"].strip().split("|")
        fields[6] = ""
        absent["Container"]["Stdout"] = "|".join(fields) + "\n"
        absent["Volume"]["Stdout"] = f"{DIAGNOSTIC.EXPECTED['VolumeName']}||source\n"
        report = DIAGNOSTIC.classify_observations(absent)
        self.assertFalse(report["ContainerRunIdLabelPresent"])
        self.assertFalse(report["VolumeRunIdLabelPresent"])
        self.assertIsNone(report["ContainerRunIdLabelSha256"])
        self.assertIsNone(report["VolumeRunIdLabelSha256"])
        try:
            from jsonschema import Draft202012Validator
        except ImportError:
            self.skipTest("jsonschema validator not installed")
        schema = json.loads((ROOT / "docs/operations/cw-m5-026b-stopped-source-diagnostic-report.schema.json").read_text())
        Draft202012Validator.check_schema(schema)
        report.update({"PodmanReadOnlyCommands": 6, "AuthorizationApprovalId": "CW-M5-026B-DIAGNOSTIC-TEST",
                       "ManifestSha256": "a" * 64, "PreflightReportSha256": "b" * 64,
                       "GeneratedAtUtc": "2026-10-13T00:00:00Z"})
        Draft202012Validator(schema).validate(report)

    def test_missing_container_is_distinct_from_storage_or_query_failure(self):
        missing = DIAGNOSTIC.classify_observations(valid_observations(
            Exists={"ReturnCode": 1, "Stdout": ""}))
        self.assertEqual("SOURCE_CONTAINER_MISSING", missing["Classification"])
        self.assertIn("CONTAINER_NAME_NOT_FOUND", missing["Findings"])
        inaccessible = DIAGNOSTIC.classify_observations(valid_observations(
            Exists={"ReturnCode": 125, "Stdout": "", "Stderr": "never persisted"}))
        self.assertEqual("SOURCE_CONTAINER_STORAGE_INACCESSIBLE", inaccessible["Classification"])
        self.assertIn("CONTAINER_STORAGE_INACCESSIBLE", inaccessible["Findings"])
        self.assertNotIn("Stderr", inaccessible)

    def test_recreated_container_is_distinct_from_same_id_identity_mismatch(self):
        fields = valid_observations()["Container"]["Stdout"].strip().split("|")
        fields[0] = "a" * 64
        recreated = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)}))
        self.assertEqual("SOURCE_CONTAINER_RECREATED", recreated["Classification"])
        self.assertIn("CONTAINER_ID_MISMATCH", recreated["Findings"])
        fields = valid_observations()["Container"]["Stdout"].strip().split("|")
        fields[6] = "other-run"
        mismatched = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)}))
        self.assertEqual("SOURCE_CONTAINER_IDENTITY_MISMATCH", mismatched["Classification"])
        self.assertIn("PROVISIONING_RUN_ID_LABEL_MISMATCH", mismatched["Findings"])

    def test_all_runtime_identity_dimensions_are_checked(self):
        cases = (
            ("Container", 7, "wrong-database", "DATABASE_LABEL_MISMATCH"),
            ("Container", 8, "other-volume=/var/lib/mysql;", "VOLUME_MOUNT_MISMATCH"),
            ("Container", 9, "3306/tcp=0.0.0.0:13326,;", "CONFIGURED_ENDPOINT_MISMATCH"),
            ("Image", None, "sha256:other|some/image@sha256:" + "0" * 64, "IMMUTABLE_IMAGE_ID_OR_DIGEST_MISMATCH"),
            ("Volume", None, DIAGNOSTIC.EXPECTED["VolumeName"] + "|other-run|source", "VOLUME_IDENTITY_MISMATCH"),
            ("Volume", None, DIAGNOSTIC.EXPECTED["VolumeName"] + "|" + DIAGNOSTIC.EXPECTED["ProvisioningRunId"] + "|destination", "VOLUME_ROLE_MISMATCH"),
        )
        for observation, index, replacement, code in cases:
            with self.subTest(code=code):
                values = valid_observations()
                original = values[observation]["Stdout"]
                if index is None:
                    values[observation]["Stdout"] = replacement
                else:
                    fields = original.strip().split("|")
                    fields[index] = replacement
                    values[observation]["Stdout"] = "|".join(fields)
                report = DIAGNOSTIC.classify_observations(values)
                self.assertEqual("SOURCE_CONTAINER_IDENTITY_MISMATCH", report["Classification"])
                self.assertIn(code, report["Findings"])

    def test_stopped_container_uses_configured_binding_and_missing_binding_fails_closed(self):
        target = DIAGNOSTIC.EXPECTED
        report = DIAGNOSTIC.classify_observations(valid_observations(
            Port={"ReturnCode": 0, "Stdout": ""}))
        self.assertEqual("SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH", report["Classification"])
        self.assertTrue(report["ConfiguredEndpointMatched"])
        self.assertFalse(report["PodmanPortMappingReported"])
        self.assertIn("ACTIVE_ENDPOINT_INACTIVE_WHILE_STOPPED", report["Findings"])

        unavailable = DIAGNOSTIC.classify_observations(valid_observations(
            Port={"ReturnCode": 125, "Stdout": ""}))
        self.assertEqual("SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH", unavailable["Classification"])
        self.assertTrue(unavailable["ConfiguredEndpointMatched"])
        self.assertEqual("PORT_OBSERVATION_UNAVAILABLE_WHILE_STOPPED", unavailable["ActiveEndpointObservation"])
        self.assertIn("PODMAN_PORT_OBSERVATION_UNAVAILABLE", unavailable["Findings"])

        fields = valid_observations()["Container"]["Stdout"].strip().split("|")
        fields[9] = "3306/tcp=;"
        missing = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)},
            Port={"ReturnCode": 0, "Stdout": ""}))
        self.assertEqual("SOURCE_CONTAINER_IDENTITY_MISMATCH", missing["Classification"])
        self.assertIn("CONFIGURED_ENDPOINT_BINDING_MISSING", missing["Findings"])
        self.assertNotIn("LOOPBACK_ENDPOINT_MISMATCH", missing["Findings"])

    def test_running_container_requires_matching_runtime_mapping(self):
        fields = valid_observations()["Container"]["Stdout"].strip().split("|")
        fields[1:3] = ["running", "true"]
        target = DIAGNOSTIC.EXPECTED
        good = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)},
            Port={"ReturnCode": 0, "Stdout": f"3306/tcp -> {target['Endpoint']}\n"}))
        self.assertEqual("SOURCE_CONTAINER_RUNNING_IDENTITY_MATCH", good["Classification"])
        self.assertTrue(good["PodmanPortMappingReported"])
        bad = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)},
            Port={"ReturnCode": 0, "Stdout": "3306/tcp -> 0.0.0.0:13326\n"}))
        self.assertEqual("SOURCE_CONTAINER_IDENTITY_MISMATCH", bad["Classification"])
        self.assertIn("ACTIVE_ENDPOINT_MISMATCH", bad["Findings"])

    def test_nonrunning_state_and_malformed_observations_are_fail_closed(self):
        fields = valid_observations()["Container"]["Stdout"].strip().split("|")
        fields[1:3] = ["created", "false"]
        unexpected = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)}))
        self.assertEqual("SOURCE_CONTAINER_STATE_UNEXPECTED", unexpected["Classification"])
        fields[3] = "unknown"
        malformed = DIAGNOSTIC.classify_observations(valid_observations(
            Container={"ReturnCode": 0, "Stdout": "|".join(fields)}))
        self.assertEqual("DIAGNOSTIC_INCOMPLETE", malformed["Classification"])
        self.assertIn("CONTAINER_EXIT_CODE_MALFORMED", malformed["Findings"])
        malformed = DIAGNOSTIC.classify_observations({"Rootless": {"ReturnCode": 0, "Stdout": "true"},
                                                       "Exists": {"ReturnCode": 0, "Stdout": ""},
                                                       "Container": {"ReturnCode": 0, "Stdout": "raw secret stderr"}})
        self.assertEqual("DIAGNOSTIC_INCOMPLETE", malformed["Classification"])
        self.assertIn("CONTAINER_INSPECT_MALFORMED", malformed["Findings"])
        self.assertNotIn("raw secret", json.dumps(malformed))

    def test_image_name_string_alias_does_not_substitute_for_digest_verification(self):
        fields = valid_observations()["Container"]["Stdout"].strip().split("|")
        fields[4] = "mariadb@sha256:short-name-representation"
        values = valid_observations(Container={"ReturnCode": 0, "Stdout": "|".join(fields)})
        report = DIAGNOSTIC.classify_observations(values)
        self.assertEqual("SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH", report["Classification"])
        self.assertTrue(report["PinnedImageIdentityMatched"])
        self.assertIn("CONFIGURED_IMAGE_REFERENCE_DIFFERS", report["Findings"])
        values["Image"]["Stdout"] = "sha256:other|other@sha256:" + "0" * 64
        report = DIAGNOSTIC.classify_observations(values)
        self.assertEqual("SOURCE_CONTAINER_IDENTITY_MISMATCH", report["Classification"])
        self.assertFalse(report["PinnedImageIdentityMatched"])


if __name__ == "__main__":
    unittest.main()
