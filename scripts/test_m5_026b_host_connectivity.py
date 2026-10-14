from __future__ import annotations

import copy
import importlib.util
import json
import os
import socket
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
PREFLIGHT_PATH = ROOT / "scripts/preflight-m5-026b-host-connectivity.py"
spec = importlib.util.spec_from_file_location("host_connectivity_preflight_tests", PREFLIGHT_PATH)
assert spec and spec.loader
PREFLIGHT = importlib.util.module_from_spec(spec)
spec.loader.exec_module(PREFLIGHT)
RUNNER_PATH = ROOT / "scripts/cw-m5-026b/diagnose_host_connectivity.py"
spec = importlib.util.spec_from_file_location("host_connectivity_runner_tests", RUNNER_PATH)
assert spec and spec.loader
RUNNER = importlib.util.module_from_spec(spec)
spec.loader.exec_module(RUNNER)


def target_records():
    return {
        "Source": {"ContainerName": "continuumwiki-m5-026b-source-ro1", "ContainerId": "6a4d4f8707ee4f5b14dddfff183db6d26ce7453eaba377b7c9d081bc1157f914", "VolumeName": "continuumwiki-m5-026b-source-ro1-data", "DatabaseName": "cw_m5_026b_source_ro1", "Endpoint": "127.0.0.1:13326", "ImageReference": "docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15", "ProvisioningRunId": "20261012T093746Z-86380"},
        "Destination": {"ContainerName": "continuumwiki-m5-026b-destination-ro1", "ContainerId": "1c58a2f370d36af443e53283d4212604e997188e9fbf81175655446b7c3ea1f5", "VolumeName": "continuumwiki-m5-026b-destination-ro1-data", "DatabaseName": "cw_m5_026b_destination_ro1", "Endpoint": "127.0.0.1:13327", "ImageReference": "docker.io/library/mariadb@sha256:6422478cb8e159f080fb1d8ccf65101e26fe51385787fde7d16c3b165a331f15", "ProvisioningRunId": "20261012T123438Z-282573-599fff18456ea0cf36ef987019b3efef"},
    }


def sample(now=None, directory=None):
    instant = now or datetime.now(timezone.utc)
    approval = "CW-M5-026B-CONNECTIVITY-OFFLINE-TEST"
    folder = Path(directory) if directory else Path("/home/cdjensen94/continuumwiki-validation/cw-m5-026b-connectivity-" + approval)
    return {"ManifestVersion": PREFLIGHT.CONTRACT, "ApprovalId": approval,
        "ApprovedAtUtc": instant.isoformat().replace("+00:00", "Z"),
        "ExpiresAtUtc": (instant + timedelta(minutes=10)).isoformat().replace("+00:00", "Z"),
        "Approver": {"Name": "Test approver", "Role": "Project owner"},
        "Operator": {"Name": "cdjensen94", "Role": "Operator", "Uid": 1000},
        "ExecutionHost": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True},
        "Targets": {role: {key: target_records()[role][key] for key in ("ContainerName", "ContainerId", "ProvisioningRunId", "Endpoint")} for role in ("Source", "Destination")},
        "PermittedOperations": {"ReadOnlyPodmanInspection": True, "PayloadFreeTcpConnect": True, "NoDatabaseClient": True, "NoSql": True, "NoCredentialAccess": True, "NoLifecycleChanges": True, "NoPodmanExec": True, "NoNetworkConfigurationChanges": True, "RetainSanitizedEvidence": True},
        "Limits": {"TcpConnectTimeoutSeconds": 3, "AttemptsPerEndpoint": 1, "OverallTimeoutSeconds": 600},
        "Evidence": {"ManifestPath": str(folder / "authorization.json"), "PreflightReportPath": str(folder / "preflight.json"), "ResultReportPath": str(folder / "result.json")}}


def completed(output):
    return subprocess.CompletedProcess([], 0, output + "\n", "")


class HostConnectivityTests(unittest.TestCase):
    def test_draft_schema_validates_fixed_manifest_and_rejects_arbitrary_scope(self):
        from jsonschema import Draft202012Validator
        schema = json.loads(PREFLIGHT.SCHEMA_PATH.read_text())
        Draft202012Validator.check_schema(schema)
        validator = Draft202012Validator(schema)
        self.assertFalse(list(validator.iter_errors(sample())))
        approved = sample(); approved["ApprovalId"] = "CW-M5-026B-HOST-CONNECTIVITY-20261014-002"
        self.assertFalse(list(validator.iter_errors(approved)))
        mutations = [
            lambda d: d["Targets"]["Source"].update({"Endpoint": "127.0.0.1:13328"}),
            lambda d: d["Targets"].update({"Other": copy.deepcopy(d["Targets"]["Source"])}),
            lambda d: d.update({"Command": "podman ps"}),
            lambda d: d.update({"ProbeTypes": ["UDP"]}),
            lambda d: d["Limits"].update({"AttemptsPerEndpoint": 2}),
        ]
        for mutate in mutations:
            document = sample(); mutate(document)
            self.assertTrue(list(validator.iter_errors(document)))

    def test_manifest_enforces_exact_targets_distinct_runs_and_expiry(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); root.chmod(0o700)
            directory = root / "cw-m5-026b-connectivity-CW-M5-026B-CONNECTIVITY-OFFLINE-TEST"
            directory.mkdir(mode=0o700)
            document = sample(now, directory)
            manifest_path = directory / "authorization.json"
            manifest_path.write_text(json.dumps(document)); manifest_path.chmod(0o600)
            with patch.object(PREFLIGHT, "_schema_validate"), patch.object(PREFLIGHT, "_expected_targets", return_value=target_records()), patch("socket.gethostname", return_value="christian-laptop"), patch("os.getuid", return_value=1000), patch("pwd.getpwuid", return_value=SimpleNamespace(pw_name="cdjensen94")), patch.object(PREFLIGHT, "VALIDATION_ROOT", root):
                self.assertEqual(document["ApprovalId"], PREFLIGHT.validate_manifest(document, manifest_path, now=now)["ApprovalId"])
                expired = copy.deepcopy(document)
                expired["ApprovedAtUtc"] = (now - timedelta(hours=1)).isoformat().replace("+00:00", "Z")
                expired["ExpiresAtUtc"] = (now - timedelta(minutes=30)).isoformat().replace("+00:00", "Z")
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "AUTHORIZATION_NOT_CURRENT"):
                    PREFLIGHT.validate_manifest(expired, manifest_path, now=now)
                wrong = copy.deepcopy(document); wrong["Targets"]["Destination"]["ProvisioningRunId"] = wrong["Targets"]["Source"]["ProvisioningRunId"]
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "EXACT_TARGET_BINDING_MISMATCH"):
                    PREFLIGHT.validate_manifest(wrong, manifest_path, now=now)
                prior = copy.deepcopy(document); prior["ApprovalId"] = PREFLIGHT.NON_EXECUTABLE_PRIOR_APPROVAL
                prior_dir = root / ("cw-m5-026b-connectivity-" + prior["ApprovalId"])
                prior_dir.mkdir(mode=0o700)
                prior["Evidence"] = {"ManifestPath": str(prior_dir / "authorization.json"), "PreflightReportPath": str(prior_dir / "preflight.json"), "ResultReportPath": str(prior_dir / "result.json")}
                prior_path = prior_dir / "authorization.json"; prior_path.write_text(json.dumps(prior)); prior_path.chmod(0o600)
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "PRIOR_APPROVAL_NOT_EXECUTION_AUTHORITY"):
                    PREFLIGHT.validate_manifest(prior, prior_path, now=now)

    def test_nonconnecting_preflight_calls_no_podman_or_socket(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); root.chmod(0o700)
            directory = root / ("cw-m5-026b-connectivity-" + sample(now)["ApprovalId"])
            directory.mkdir(mode=0o700)
            manifest = sample(now, directory)
            manifest_path = directory / "authorization.json"
            manifest_path.write_text(json.dumps(manifest)); manifest_path.chmod(0o600)
            with patch.object(PREFLIGHT, "_expected_targets", return_value=target_records()), patch("socket.gethostname", return_value="christian-laptop"), patch("os.getuid", return_value=1000), patch("pwd.getpwuid", return_value=SimpleNamespace(pw_name="cdjensen94")), patch.object(PREFLIGHT, "_schema_validate"), patch("subprocess.run", side_effect=AssertionError("Podman call")), patch("socket.create_connection", side_effect=AssertionError("socket call")), patch.object(PREFLIGHT, "VALIDATION_ROOT", root):
                # the injected temp directory is the sole approved evidence root for this test.
                result = PREFLIGHT.preflight(manifest_path, directory / "preflight.json", now=now)
            self.assertEqual("HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING", result["Classification"])
            self.assertEqual(0, result["PodmanCommands"])
            self.assertEqual(0, result["TcpConnections"])

    def test_runner_revalidation_allows_only_private_existing_preflight_report(self):
        now = datetime.now(timezone.utc)
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); root.chmod(0o700)
            document = sample(now, root / ("cw-m5-026b-connectivity-" + sample(now)["ApprovalId"]))
            directory = Path(document["Evidence"]["ManifestPath"]).parent
            directory.mkdir(mode=0o700)
            manifest_path = Path(document["Evidence"]["ManifestPath"])
            manifest_path.write_text(json.dumps(document)); manifest_path.chmod(0o600)
            preflight_path = Path(document["Evidence"]["PreflightReportPath"])
            preflight_path.write_text("{}\n"); preflight_path.chmod(0o600)
            with patch.object(PREFLIGHT, "_schema_validate"), patch.object(PREFLIGHT, "_expected_targets", return_value=target_records()), patch("socket.gethostname", return_value="christian-laptop"), patch("os.getuid", return_value=1000), patch("pwd.getpwuid", return_value=SimpleNamespace(pw_name="cdjensen94")), patch.object(PREFLIGHT, "VALIDATION_ROOT", root):
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "EVIDENCE_PATH_ALREADY_USED"):
                    PREFLIGHT.validate_manifest(document, manifest_path, now=now)
                self.assertEqual(document["ApprovalId"], PREFLIGHT.validate_manifest(document, manifest_path, now=now, allow_existing_preflight=True)["ApprovalId"])
                preflight_path.chmod(0o644)
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "PREFLIGHT_REPORT_PERMISSIONS_INVALID"):
                    PREFLIGHT.validate_manifest(document, manifest_path, now=now, allow_existing_preflight=True)
                preflight_path.chmod(0o600)
                Path(document["Evidence"]["ResultReportPath"]).write_text("{}\n")
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "EVIDENCE_PATH_ALREADY_USED"):
                    PREFLIGHT.validate_manifest(document, manifest_path, now=now, allow_existing_preflight=True)
                Path(document["Evidence"]["ResultReportPath"]).unlink()
                (directory / ".started").write_text("consumed\n")
                with self.assertRaisesRegex(PREFLIGHT.ConnectivityPreflightError, "EVIDENCE_PATH_ALREADY_USED"):
                    PREFLIGHT.validate_manifest(document, manifest_path, now=now, allow_existing_preflight=True)

    def test_runner_rejects_stale_or_mismatched_preflight(self):
        now = datetime.now(timezone.utc); document = sample(now)
        digest = "a" * 64
        stale = {"Classification": "HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING", "GeneratedAtUtc": (now - timedelta(seconds=601)).isoformat().replace("+00:00", "Z"), "ApprovalId": document["ApprovalId"], "ManifestSha256": digest, "Targets": document["Targets"], "PodmanCommands": 0, "TcpConnections": 0, "DatabaseConnections": 0, "CredentialsRead": False}
        with self.assertRaisesRegex(RUNNER.ConnectivityRunError, "PREFLIGHT_NOT_FRESH_OR_NOT_MATCHED"):
            RUNNER._fresh_preflight(stale, document, digest, now)
        stale["GeneratedAtUtc"] = now.isoformat().replace("+00:00", "Z")
        stale["ManifestSha256"] = "b" * 64
        with self.assertRaisesRegex(RUNNER.ConnectivityRunError, "PREFLIGHT_NOT_FRESH_OR_NOT_MATCHED"):
            RUNNER._fresh_preflight(stale, document, digest, now)

    def test_podman_commands_are_fixed_read_only_and_never_exec(self):
        for target in target_records().values():
            commands = RUNNER._commands(target)
            self.assertEqual(4, len(commands))
            self.assertTrue(all(command[0] == "/usr/bin/podman" for command in commands))
            self.assertFalse(any("exec" in command for command in commands))
            self.assertFalse(any("start" in command or "stop" in command or "rm" in command for command in commands))

    def test_live_container_id_mismatch_fails_identity_check(self):
        target = target_records()["Source"]
        image_id = "sha256:" + "a" * 64
        expected_image = target["ImageReference"]
        def fake_run(command, operation_id):
            if command[1:3] == ["container", "inspect"]:
                fields = ["0" * 64, "running", "true", "healthy", expected_image, image_id,
                          target["ProvisioningRunId"], target["DatabaseName"], "bridge",
                          "127.0.0.1:13326,", target["VolumeName"] + "=/var/lib/mysql;"]
                return completed("|".join(fields))
            if command[1] == "port":
                return completed("3306/tcp -> 127.0.0.1:13326")
            if command[1:3] == ["image", "inspect"]:
                return completed(image_id + "|" + expected_image)
            if command[1:3] == ["volume", "inspect"]:
                return completed(target["VolumeName"] + "|" + target["ProvisioningRunId"] + "|source")
            raise AssertionError("unexpected Podman command")
        result = RUNNER._inspect_target(target, "Source", fake_run)
        self.assertFalse(result["IdentityMatched"])
        self.assertFalse(result["IdentityChecks"]["ContainerId"])
        self.assertTrue(all(value for key, value in result["IdentityChecks"].items() if key != "ContainerId"))

    def test_correct_source_and_destination_observations_pass_identity(self):
        for role, target in target_records().items():
            with self.subTest(role=role):
                image_id = "sha256:" + ("a" if role == "Source" else "b") * 64
                def fake_run(command, operation_id):
                    if command[1:3] == ["container", "inspect"]:
                        fields = [target["ContainerId"], "running", "true", "healthy", target["ImageReference"],
                                  image_id, target["ProvisioningRunId"], target["DatabaseName"], "bridge",
                                  target["Endpoint"] + ",", target["VolumeName"] + "=/var/lib/mysql;"]
                        return completed("|".join(fields))
                    if command[1] == "port":
                        return completed("3306/tcp -> " + target["Endpoint"])
                    if command[1:3] == ["image", "inspect"]:
                        return completed(image_id + "|" + target["ImageReference"])
                    if command[1:3] == ["volume", "inspect"]:
                        volume_role = role.casefold()
                        return completed(target["VolumeName"] + "|" + target["ProvisioningRunId"] + "|" + volume_role)
                    raise AssertionError("unexpected fixed Podman operation")
                observed = RUNNER._inspect_target(target, role, fake_run)
                self.assertTrue(observed["IdentityMatched"])
                self.assertIsNone(observed["FailureClassification"])

    def test_wrong_run_id_is_an_identity_mismatch_not_an_execution_failure(self):
        target = target_records()["Source"]
        image_id = "sha256:" + "a" * 64
        def fake_run(command, operation_id):
            if command[1:3] == ["container", "inspect"]:
                values = [target["ContainerId"], "running", "true", "healthy", target["ImageReference"],
                          image_id, "wrong-run-id", target["DatabaseName"], "bridge", target["Endpoint"] + ",",
                          target["VolumeName"] + "=/var/lib/mysql;"]
                return completed("|".join(values))
            if command[1] == "port":
                return completed("3306/tcp -> " + target["Endpoint"])
            if command[1:3] == ["image", "inspect"]:
                return completed(image_id + "|" + target["ImageReference"])
            if command[1:3] == ["volume", "inspect"]:
                return completed(target["VolumeName"] + "|" + target["ProvisioningRunId"] + "|source")
            raise AssertionError("unexpected fixed Podman operation")
        observed = RUNNER._inspect_target(target, "Source", fake_run)
        self.assertEqual("PODMAN_IDENTITY_MISMATCH", observed["FailureClassification"])
        self.assertEqual(["RunIdLabel"], observed["MismatchedIdentityFields"])

    def test_podman_launch_timeout_and_nonzero_exit_are_distinct(self):
        deadline = RUNNER.time.monotonic() + 10
        with patch("subprocess.run", side_effect=FileNotFoundError(2, "missing", "/secret/path/podman")):
            with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
                RUNNER._run_podman(["/usr/bin/podman", "info"], deadline, "PODMAN_ROOTLESS_CONTEXT")
        self.assertEqual("PODMAN_EXECUTION_UNAVAILABLE", raised.exception.classification)
        self.assertEqual("EXECUTABLE_NOT_FOUND", raised.exception.podman_error_category)
        with patch("subprocess.run", side_effect=subprocess.TimeoutExpired(["/usr/bin/podman"], 1)):
            with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
                RUNNER._run_podman(["/usr/bin/podman", "info"], deadline, "PODMAN_ROOTLESS_CONTEXT")
        self.assertEqual("PODMAN_TIMEOUT", raised.exception.classification)
        self.assertTrue(raised.exception.timed_out)
        failed = subprocess.CompletedProcess([], 125, "", "Error: no such container secret-name at /private/path")
        with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
            RUNNER._stdout(failed, "SOURCE_CONTAINER_INSPECT")
        self.assertEqual("PODMAN_COMMAND_FAILED", raised.exception.classification)
        self.assertEqual(125, raised.exception.exit_code)
        self.assertEqual("CONTAINER_NOT_FOUND", raised.exception.podman_error_category)
        self.assertNotIn("secret-name", json.dumps(raised.exception.sanitized_record()))
        self.assertNotIn("/private/path", json.dumps(raised.exception.sanitized_record()))

    def test_empty_invalid_and_schema_mismatched_outputs_are_distinct(self):
        with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
            RUNNER._stdout(completed(""), "SOURCE_CONTAINER_INSPECT")
        self.assertEqual("PODMAN_OUTPUT_EMPTY", raised.exception.classification)
        with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
            RUNNER._one_line("not-json-or-delimited", 11, "SOURCE_CONTAINER_INSPECT")
        self.assertEqual("PODMAN_OUTPUT_INVALID", raised.exception.classification)
        with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
            RUNNER._one_line("id|running", 11, "SOURCE_CONTAINER_INSPECT")
        self.assertEqual("PODMAN_OUTPUT_INVALID", raised.exception.classification)
        with self.assertRaises(RUNNER.ConnectivityRunError) as raised:
            RUNNER._one_line("||||||||||", 11, "SOURCE_CONTAINER_INSPECT", (0, 1, 2, 4, 5, 6, 7, 8))
        self.assertEqual("PODMAN_OUTPUT_SCHEMA_MISMATCH", raised.exception.classification)

    def test_failed_observation_is_sanitized_and_suppresses_probes(self):
        now = datetime.now(timezone.utc); document = sample(now)
        preflight = {"Classification": "HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING", "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z"), "ApprovalId": document["ApprovalId"], "ManifestSha256": "a" * 64, "Targets": document["Targets"], "PodmanCommands": 0, "TcpConnections": 0, "DatabaseConnections": 0, "CredentialsRead": False}
        secretish = "Error: bad Podman state at /private/home/token-value"
        failed = subprocess.CompletedProcess([], 125, "", secretish)
        with patch.object(RUNNER.PREFLIGHT, "validate_manifest", return_value={"ManifestSha256": "a" * 64}), patch.object(RUNNER.PREFLIGHT, "_expected_targets", return_value=target_records()), patch.object(RUNNER, "_claim_once"), patch.object(RUNNER, "_run_podman", side_effect=[completed("true"), failed]), patch.object(RUNNER, "_probe") as probe:
            report = RUNNER.run_diagnostic(document, preflight, now=now)
        self.assertEqual("PODMAN_COMMAND_FAILED", report["Classification"])
        self.assertEqual("SOURCE_CONTAINER_INSPECT", report["ObservationFailures"][0]["OperationId"])
        self.assertEqual(125, report["ObservationFailures"][0]["ExitCode"])
        serialized = json.dumps(report)
        self.assertNotIn(secretish, serialized)
        self.assertNotIn("token-value", serialized)
        self.assertEqual(2, report["PodmanCommands"])
        self.assertEqual(0, report["TcpAttempts"])
        self.assertEqual(0, report["Retries"])
        probe.assert_not_called()
        with tempfile.TemporaryDirectory() as temp:
            result_path = Path(temp) / "result.json"
            RUNNER.PREFLIGHT.BASE._write_once(result_path, report)
            evidence = result_path.read_text()
            self.assertNotIn(secretish, evidence)
            self.assertNotIn("token-value", evidence)
            self.assertEqual(0o600, os.stat(result_path).st_mode & 0o777)
            with self.assertRaises(FileExistsError):
                RUNNER.PREFLIGHT.BASE._write_once(result_path, report)

    def test_identity_mismatch_prevents_all_tcp_probes(self):
        now = datetime.now(timezone.utc); document = sample(now)
        preflight = {"Classification": "HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING", "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z"), "ApprovalId": document["ApprovalId"], "ManifestSha256": "a" * 64, "Targets": document["Targets"], "PodmanCommands": 0, "TcpConnections": 0, "DatabaseConnections": 0, "CredentialsRead": False}
        obs = [{"Role": role, "IdentityMatched": False, "IdentityChecks": {"ContainerId": False}, "FailureClassification": "PODMAN_IDENTITY_MISMATCH", "MismatchedIdentityFields": ["ContainerId"], "Running": None, "State": "unknown", "Health": "unavailable", "ConfiguredEndpoint": document["Targets"][role]["Endpoint"], "ConfiguredPortBindingMatched": False, "PublishedPortMappingMatched": False} for role in ("Source", "Destination")]
        with patch.object(RUNNER.PREFLIGHT, "validate_manifest", return_value={"ManifestSha256": "a" * 64}), patch.object(RUNNER, "_claim_once"), patch.object(RUNNER, "_run_podman", return_value=completed("true")), patch.object(RUNNER, "_inspect_target", side_effect=obs), patch.object(RUNNER, "_probe") as probe:
            report = RUNNER.run_diagnostic(document, preflight, now=now)
        self.assertEqual("PODMAN_IDENTITY_MISMATCH", report["Classification"])
        self.assertEqual(2, len(report["ObservationFailures"]))
        probe.assert_not_called()

    def test_probe_classifications_are_single_connects_and_payload_free(self):
        class FakeSocket:
            def __init__(self): self.closed = False
            def close(self): self.closed = True
            def send(self, _): raise AssertionError("payload send")
            sendall = send
        cases = [(FakeSocket(), "TCP_ACCEPTED"), (ConnectionRefusedError(), "CONNECTION_REFUSED"), (socket.timeout(), "CONNECTION_TIMEOUT"), (OSError(), "NETWORK_ERROR")]
        for response, expected in cases:
            with self.subTest(expected=expected), patch("socket.create_connection") as connect:
                if isinstance(response, BaseException):
                    connect.side_effect = response
                else:
                    connect.return_value = response
                outcome = RUNNER._probe("127.0.0.1:13326", 3)
                self.assertEqual(expected, outcome)
                connect.assert_called_once_with(("127.0.0.1", 13326), timeout=3)
                if expected == "TCP_ACCEPTED":
                    self.assertTrue(response.closed)

    def test_exactly_one_probe_per_endpoint_after_all_identity_checks(self):
        now = datetime.now(timezone.utc); document = sample(now)
        preflight = {"Classification": "HOST_CONNECTIVITY_PREFLIGHT_VALID_NONCONNECTING", "GeneratedAtUtc": now.isoformat().replace("+00:00", "Z"), "ApprovalId": document["ApprovalId"], "ManifestSha256": "a" * 64, "Targets": document["Targets"], "PodmanCommands": 0, "TcpConnections": 0, "DatabaseConnections": 0, "CredentialsRead": False}
        obs = [{"Role": role, "IdentityMatched": True, "IdentityChecks": {}, "Running": True, "State": "running", "Health": "healthy", "ConfiguredEndpoint": document["Targets"][role]["Endpoint"], "ConfiguredPortBindingMatched": True, "PublishedPortMappingMatched": True} for role in ("Source", "Destination")]
        with patch.object(RUNNER.PREFLIGHT, "validate_manifest", return_value={"ManifestSha256": "a" * 64}), patch.object(RUNNER, "_claim_once"), patch.object(RUNNER, "_run_podman", return_value=completed("true")), patch.object(RUNNER, "_inspect_target", side_effect=obs), patch.object(RUNNER, "_probe", side_effect=["TCP_ACCEPTED", "CONNECTION_REFUSED"]) as probe:
            report = RUNNER.run_diagnostic(document, preflight, now=now)
        self.assertEqual("HOST_CONNECTIVITY_DIAGNOSTIC_COMPLETED", report["Classification"])
        self.assertEqual(["TCP_ACCEPTED", "CONNECTION_REFUSED"], [item["Outcome"] for item in report["TcpProbes"]])
        self.assertEqual(2, probe.call_count)
        self.assertEqual(0, report["PayloadBytesSent"])
        self.assertEqual(0, report["Retries"])


if __name__ == "__main__":
    unittest.main()
