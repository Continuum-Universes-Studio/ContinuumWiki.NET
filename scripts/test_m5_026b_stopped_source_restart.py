"""Mock-only coverage for the one-shot CW-M5-026B source start gate."""

from __future__ import annotations

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


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


PREFLIGHT = load("m5_026b_restart_preflight_tests", ROOT / "scripts/preflight-m5-026b-stopped-source-restart.py")
RUNNER = load("m5_026b_start_runner_tests", ROOT / "scripts/cw-m5-026b/start_stopped_source.py")
NOW = datetime(2026, 10, 13, 3, 0, tzinfo=timezone.utc)


def write_private(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
    os.chmod(path, 0o600)


class RestartGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.base = Path(self.temp.name)
        os.chmod(self.base, 0o700)
        self.validation = self.base / "continuumwiki-validation"
        self.validation.mkdir(mode=0o700)
        self.manifests = self.base / "manifests"
        self.manifests.mkdir(mode=0o700)
        self.diag_path = self.validation / "diagnostic.json"
        self.preflight_path = self.validation / "restart-preflight.json"
        self.result_path = self.validation / "start-result.json"
        target = PREFLIGHT.AUTHORIZED_TARGET
        self.diagnostic = {
            "ReportVersion": "cw-m5-026b-stopped-source-diagnostic-v1",
            "Classification": "SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH",
            "TargetRole": "Source",
            "ExpectedContainerId": target["ContainerId"],
            "ObservedContainerIdSha256": "a" * 64,
            "ObservedState": "exited",
            "ObservedExitCode": 0,
            "PinnedImageIdentityMatched": True,
            "ContainerIdMatched": True,
            "RunIdLabelMatched": True,
            "DatabaseLabelMatched": True,
            "VolumeMountMatched": True,
            "EndpointMatched": True,
            "VolumeIdentityMatched": True,
            "Findings": ["ACTIVE_ENDPOINT_INACTIVE_WHILE_STOPPED"],
            "RawPodmanOutputPersisted": False,
            "StderrPersisted": False,
            "DatabaseConnections": 0,
            "DatabaseMutations": 0,
            "CredentialsRead": 0,
            "PodmanCommandsAreReadOnly": True,
            "PodmanReadOnlyCommands": 6,
            "AuthorizationApprovalId": "CW-M5-026B-DIAGNOSTIC-20261013-002",
            "ManifestSha256": "b" * 64,
            "PreflightReportSha256": "c" * 64,
            "GeneratedAtUtc": "2026-10-13T02:41:13Z",
        }
        write_private(self.diag_path, self.diagnostic)
        diagnostic_hash = __import__("hashlib").sha256(self.diag_path.read_bytes()).hexdigest()
        self.manifest = {
            "ManifestVersion": PREFLIGHT.CONTRACT,
            "ApprovalId": "CW-M5-026B-RECOVERY-START-TEST-001",
            "ApprovedAtUtc": (NOW - timedelta(minutes=5)).isoformat().replace("+00:00", "Z"),
            "ExpiresAtUtc": (NOW + timedelta(hours=1)).isoformat().replace("+00:00", "Z"),
            "Approver": {"Name": "Test Approver", "Role": "Project Owner"},
            "Operator": {"Name": "Test Operator", "Role": "System Administrator"},
            "TargetOwner": "Test Owner",
            "ExecutionHost": {"HostName": "christian-laptop", "UnixUser": "cdjensen94", "Uid": 1000, "RootlessPodman": True},
            "Target": dict(target),
            "DiagnosticClassification": "SOURCE_CONTAINER_STOPPED_IDENTITY_MATCH",
            "DiagnosticReportSha256": diagnostic_hash,
            "PermittedOperations": dict(PREFLIGHT.OPS),
            "ResultReportPath": str(self.result_path),
        }
        self.manifest_path = self.manifests / "authorization.json"
        write_private(self.manifest_path, self.manifest)
        self.roots = patch.object(PREFLIGHT, "VALIDATION_ROOT", self.validation)
        self.roots.start()
        self.runner_roots = patch.object(RUNNER.PREFLIGHT, "VALIDATION_ROOT", self.validation)
        self.runner_roots.start()
        self.runner_root = patch.object(RUNNER, "REPORT_ROOT", self.validation)
        self.runner_root.start()
        self.host = patch.object(RUNNER, "verify_execution_host", return_value=None)
        self.host.start()

    def tearDown(self):
        self.host.stop()
        self.runner_root.stop()
        self.runner_roots.stop()
        self.roots.stop()
        self.temp.cleanup()

    def preflight(self, result_path: Path | None = None, suffix: str = ""):
        if result_path is not None:
            manifest = dict(self.manifest)
            manifest["ResultReportPath"] = str(result_path)
            manifest_path = self.manifests / f"authorization-{suffix or 'alternate'}.json"
            write_private(manifest_path, manifest)
        else:
            manifest_path = self.manifest_path
        preflight_path = self.validation / f"preflight-{suffix or 'default'}.json"
        ok, report = PREFLIGHT.run(manifest_path, self.diag_path, preflight_path, now=NOW)
        self.assertTrue(ok, report)
        return manifest_path, preflight_path, report

    def test_schema_contracts_are_valid_and_scope_is_start_only(self):
        from jsonschema import Draft202012Validator

        for name in (
            "cw-m5-026b-stopped-source-restart-authorization.schema.json",
            "cw-m5-026b-stopped-source-restart-preflight.schema.json",
            "cw-m5-026b-stopped-source-restart-report.schema.json",
        ):
            schema = json.loads((ROOT / "docs/operations" / name).read_text())
            Draft202012Validator.check_schema(schema)
        self.assertTrue(PREFLIGHT.OPS["StartStoppedSourceContainerOnce"])
        self.assertFalse(PREFLIGHT.OPS["ContainerRestart"])
        self.assertFalse(PREFLIGHT.OPS["DatabaseConnections"])

    def test_preflight_binds_fresh_diagnostic_hash_and_has_zero_operations(self):
        _, _, report = self.preflight()
        self.assertEqual("CATEGORY_STOPPED_SOURCE_RESTART_AUTHORIZATION_VALID_NONCONNECTING", report["Classification"])
        self.assertEqual({"Podman": 0, "DatabaseConnections": 0, "DatabaseMutations": 0, "CredentialReads": 0}, report["ResourceOperationCounts"])
        altered = dict(self.manifest)
        altered["DiagnosticReportSha256"] = "d" * 64
        altered_path = self.manifests / "authorization-altered.json"
        write_private(altered_path, altered)
        with self.assertRaisesRegex(PREFLIGHT.PreflightError, "DIAGNOSTIC_REPORT_HASH_MISMATCH"):
            PREFLIGHT.validate(altered, self.diag_path, NOW)

    def test_runner_executes_only_one_start_and_sanitizes_output(self):
        manifest_path, preflight_path, _ = self.preflight()
        command = [PREFLIGHT.PODMAN, "start", PREFLIGHT.AUTHORIZED_TARGET["ContainerName"]]
        calls = []

        def fake_run(actual, **kwargs):
            calls.append((actual, kwargs))
            self.assertEqual(command, actual)
            self.assertIs(kwargs["shell"], False)
            self.assertNotIn("CONTAINER_HOST", kwargs["env"])
            return subprocess.CompletedProcess(actual, 0, "container-name\n", "private stderr")

        report = RUNNER.execute(manifest_path, self.diag_path, preflight_path, self.result_path, fake_run, NOW)
        self.assertEqual("SOURCE_CONTAINER_START_SUCCEEDED", report["Classification"])
        self.assertEqual(1, report["PodmanStartCommands"])
        self.assertEqual(0, report["DatabaseConnections"])
        self.assertEqual(1, len(calls))
        serialized = self.result_path.read_text()
        self.assertNotIn("private stderr", serialized)
        self.assertNotIn("container-name", serialized)
        self.assertTrue(self.result_path.with_name("start-result.json.started").exists())
        from jsonschema import Draft202012Validator
        schema = json.loads((ROOT / "docs/operations/cw-m5-026b-stopped-source-restart-report.schema.json").read_text())
        Draft202012Validator(schema).validate(report)

    def test_failed_start_is_terminal_and_replay_is_rejected(self):
        manifest_path, preflight_path, _ = self.preflight()
        calls = []

        def fake_run(command, **kwargs):
            calls.append(command)
            return subprocess.CompletedProcess(command, 125, "", "private failure")

        report = RUNNER.execute(manifest_path, self.diag_path, preflight_path, self.result_path, fake_run, NOW)
        self.assertEqual("SOURCE_CONTAINER_START_FAILED", report["Classification"])
        self.assertEqual(125, report["StartExitCode"])
        self.result_path.unlink()
        with self.assertRaisesRegex(RUNNER.StartError, "AUTHORIZATION_ATTEMPT_ALREADY_CLAIMED"):
            RUNNER.execute(manifest_path, self.diag_path, preflight_path, self.result_path, fake_run, NOW)
        self.assertEqual(1, len(calls))

    def test_ineligible_diagnostic_cannot_authorize_start(self):
        altered = dict(self.diagnostic)
        altered["Classification"] = "SOURCE_CONTAINER_IDENTITY_MISMATCH"
        write_private(self.diag_path, altered)
        with self.assertRaisesRegex(PREFLIGHT.PreflightError, "DIAGNOSTIC_REPORT_NOT_START_ELIGIBLE"):
            PREFLIGHT.validate(self.manifest, self.diag_path, NOW)


if __name__ == "__main__":
    unittest.main()
