"""Mock-only coverage for post-start source identity verification."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]


def load(name: str, path: Path):
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


VERIFY = load("m5_026b_started_source_verification_tests", ROOT / "scripts/cw-m5-026b/verify_started_source.py")


def write_private(path: Path, value: dict) -> None:
    path.write_text(json.dumps(value, sort_keys=True), encoding="utf-8")
    os.chmod(path, 0o600)


class StartedSourceVerificationTests(unittest.TestCase):
    def test_fixed_read_only_checks_produce_running_identity_report(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            os.chmod(base, 0o700)
            validation = base / "continuumwiki-validation"
            validation.mkdir(mode=0o700)
            manifest_dir = base / "manifests"
            manifest_dir.mkdir(mode=0o700)
            target = VERIFY.PLAN.EXPECTED
            manifest = {
                "ManifestVersion": "cw-m5-026b-stopped-source-restart-authorization-v1",
                "ApprovalId": "CW-M5-026B-RECOVERY-START-TEST-001",
                "Target": dict(target),
                "ResultReportPath": str(validation / "start-result.json"),
            }
            manifest_path = manifest_dir / "authorization.json"
            write_private(manifest_path, manifest)
            manifest_hash = hashlib.sha256(json.dumps(manifest, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
            start_report = {
                "ReportVersion": "cw-m5-026b-stopped-source-start-v1",
                "Classification": "SOURCE_CONTAINER_START_SUCCEEDED",
                "AuthorizationApprovalId": manifest["ApprovalId"],
                "ManifestSha256": manifest_hash,
                "ExpectedContainerId": target["ContainerId"],
                "TargetContainerName": target["ContainerName"],
                "PodmanStartCommands": 1,
                "DatabaseConnections": 0,
                "DatabaseMutations": 0,
                "CredentialsRead": 0,
            }
            start_path = validation / "start-result.json"
            write_private(start_path, start_report)
            result_path = validation / "verification.json"
            fields = [target["ContainerId"], "running", "true", "0", target["ImageReference"], "sha256:image", target["ProvisioningRunId"], target["DatabaseName"], f"{target['VolumeName']}=/var/lib/mysql;", f"3306/tcp={target['Endpoint']},;"]
            observations = {
                "Rootless": {"ReturnCode": 0, "Stdout": "true\n"},
                "Exists": {"ReturnCode": 0, "Stdout": ""},
                "Container": {"ReturnCode": 0, "Stdout": "|".join(fields) + "\n"},
                "Port": {"ReturnCode": 0, "Stdout": f"3306/tcp -> {target['Endpoint']}\n"},
                "Image": {"ReturnCode": 0, "Stdout": f"sha256:image|{target['ImageReference']}\n"},
                "Volume": {"ReturnCode": 0, "Stdout": f"{target['VolumeName']}|{target['ProvisioningRunId']}|source\n"},
            }
            commands = VERIFY.PLAN.podman_diagnostic_commands()
            calls = []

            def fake_run(command, **kwargs):
                calls.append(command)
                self.assertIn(command, commands)
                self.assertIs(kwargs["shell"], False)
                self.assertNotIn("CONTAINER_HOST", kwargs["env"])
                name = ("Rootless", "Exists", "Container", "Port", "Image", "Volume")[commands.index(command)]
                item = observations[name]
                return subprocess.CompletedProcess(command, item["ReturnCode"], item["Stdout"], "private stderr")

            with patch.object(VERIFY, "REPORT_ROOT", validation), patch.object(VERIFY, "_verify_host", return_value=None):
                report = VERIFY.execute(manifest_path, start_path, result_path, fake_run)
            self.assertEqual("SOURCE_CONTAINER_RUNNING_IDENTITY_MATCH", report["Classification"])
            self.assertEqual(6, report["PodmanReadOnlyCommands"])
            self.assertEqual([], report["Findings"])
            self.assertEqual(6, len(calls))
            self.assertNotIn("private stderr", result_path.read_text())


if __name__ == "__main__":
    unittest.main()
