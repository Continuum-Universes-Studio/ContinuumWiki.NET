#!/usr/bin/env python3
"""Source-only CW-M5-019A provisioning safety tests."""

from __future__ import annotations

import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest


ROOT = pathlib.Path(__file__).resolve().parents[1]
PROVISION = ROOT / "scripts/cw-m5-019/provision-mariadb-m5-019.sh"
INSPECT = ROOT / "scripts/cw-m5-019/inspect-mariadb-m5-019.sh"
CLEANUP = ROOT / "scripts/cw-m5-019/cleanup-mariadb-m5-019.sh"


def run_script(path: pathlib.Path, *args: str, env: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    merged = os.environ.copy()
    if env:
        merged.update(env)
    return subprocess.run(
        ["/bin/bash", str(path), *args],
        cwd=ROOT,
        env=merged,
        text=True,
        capture_output=True,
        check=False,
    )


class ProvisioningSafetyTests(unittest.TestCase):
    def test_default_is_plan_only_and_does_not_invoke_podman(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            marker = pathlib.Path(temporary) / "podman-called"
            fake_bin = pathlib.Path(temporary) / "bin"
            fake_bin.mkdir()
            fake_podman = fake_bin / "podman"
            fake_podman.write_text(f"#!/bin/sh\ntouch {marker}\nexit 99\n", encoding="utf-8")
            fake_podman.chmod(0o755)
            result = run_script(
                PROVISION,
                "--suffix",
                "source-test",
                "--port",
                "13319",
                env={"PATH": f"{fake_bin}:{os.environ['PATH']}"},
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("plan-only", result.stdout)
            self.assertFalse(marker.exists())

    def test_unsupported_image_is_rejected_before_execution(self) -> None:
        result = run_script(PROVISION, "--image", "docker.io/library/mariadb:10.11")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("image must be", result.stderr)

    def test_invalid_suffix_and_port_are_rejected(self) -> None:
        invalid_suffix = run_script(PROVISION, "--suffix", "Bad_Name")
        invalid_port = run_script(PROVISION, "--port", "1024")
        self.assertNotEqual(invalid_suffix.returncode, 0)
        self.assertNotEqual(invalid_port.returncode, 0)

    def test_secret_directory_inside_repository_is_rejected(self) -> None:
        result = run_script(
            PROVISION,
            "--suffix",
            "source-test",
            "--secrets-dir",
            str(ROOT / "secure" / "evidence"),
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("outside the repository", result.stderr)

    def test_missing_podman_fails_provisioning_before_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            only_bin = pathlib.Path(temporary) / "bin"
            only_bin.mkdir()
            for tool in ("dirname", "pwd"):
                resolved = shutil.which(tool)
                self.assertIsNotNone(resolved)
                (only_bin / tool).symlink_to(resolved)
            result = run_script(
                PROVISION,
                "--provision",
                "--suffix",
                "source-test",
                env={"PATH": str(only_bin), "HOME": temporary},
            )
            self.assertEqual(result.returncode, 127)
            self.assertIn("podman", result.stderr)
            self.assertFalse((pathlib.Path(temporary) / ".local").exists())

    def test_cleanup_requires_execute_and_exact_metadata_identity(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            secret_dir = root / "secrets"
            secret_dir.mkdir(mode=0o700)
            secret_file = secret_dir / "environment.env"
            secret_file.write_text("MYSQL_PWD=not-for-output\n", encoding="utf-8")
            secret_file.chmod(0o600)
            metadata = root / "metadata.json"
            digest = "docker.io/library/mariadb@sha256:" + "a" * 64
            identity = "container=continuumwiki-m5-019-test;database=cw_m5_019_test;endpoint=127.0.0.1:13319;volume=continuumwiki-m5-019-test-data;image=" + digest
            metadata.write_text(
                json.dumps(
                    {
                        "Provisioning": {"RunId": "20261011T000000Z-1234"},
                        "Target": {
                            "ContainerName": "continuumwiki-m5-019-test",
                            "VolumeName": "continuumwiki-m5-019-test-data",
                            "DatabaseName": "cw_m5_019_test",
                            "Port": 13319,
                            "InstanceIdentity": identity,
                            "ImageResolvedDigest": digest,
                        },
                        "Credentials": {"EnvironmentFile": str(secret_file)},
                    }
                ),
                encoding="utf-8",
            )
            manifest = root / "authorization.json"
            manifest.write_text(
                json.dumps(
                    {
                        "ManifestVersion": "cw-m5-019-authorization-v2",
                        "ApprovalId": "approval:cw-m5-019:test",
                        "ApprovedAtUtc": "2099-01-01T00:00:00Z",
                        "ExpiresAtUtc": "2099-01-02T00:00:00Z",
                        "Approver": {"Name": "Test Approver", "Role": "Owner"},
                        "Operator": {"Name": "Test Operator", "Role": "DBA"},
                        "TargetOwner": "Disposable test",
                        "Target": {
                            "Disposable": True,
                            "ContainerName": "continuumwiki-m5-019-test",
                            "VolumeName": "continuumwiki-m5-019-test-data",
                            "DatabaseName": "cw_m5_019_test",
                            "Endpoint": "127.0.0.1:13319",
                            "InstanceIdentity": identity,
                            "EngineVersion": "MariaDB 11.8.x",
                            "DedicatedApplicationIdentity": "continuumwiki_m5_019_app",
                        },
                        "PermittedOperations": {
                            name: True
                            for name in (
                                "Connect", "CreateDisposableSchema",
                                "ApplyV1Migration", "ApplyV2Migration", "CategoryMutations",
                                "ConcurrencyAndCrashTests", "RetainSanitizedEvidence",
                                "DeleteDisposableSchema", "DatabaseCleanup", "ContainerVolumeDeletion",
                            )
                        },
                        "Isolation": {
                            "Production": False,
                            "SharedWithProduction": False,
                            "DedicatedCredentials": True,
                            "Proof": "isolated source-only test",
                        },
                        "Provisioning": {
                            "Procedure": "source-only",
                            "CleanupProcedure": "source-only",
                            "CleanupOwner": "test",
                            "CleanupAttestationPath": "evidence.json",
                        },
                        "TestScope": {
                            "Suite": ["category-v1", "recovery-v2"],
                            "MaxConcurrentWorkers": 1,
                            "MaxPages": 100,
                            "CrashBoundaries": [
                                "before-mutation", "after-mutation-before-audit",
                                "after-audit-before-checkpoint", "during-checkpoint-cas",
                                "after-checkpoint",
                            ],
                        },
                    }
                ),
                encoding="utf-8",
            )
            report = root / "cleanup.json"
            result = run_script(
                CLEANUP,
                "--metadata", str(metadata),
                "--manifest", str(manifest),
                "--attestation", str(report),
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("plan-only", result.stdout)
            self.assertFalse(report.exists())

    def test_source_retains_required_boundaries(self) -> None:
        provision_source = PROVISION.read_text(encoding="utf-8")
        inspect_source = INSPECT.read_text(encoding="utf-8")
        cleanup_source = CLEANUP.read_text(encoding="utf-8")
        common_source = ROOT.joinpath("scripts/cw-m5-019/common.sh").read_text(encoding="utf-8")
        self.assertIn('--publish "$endpoint:3306"', provision_source)
        self.assertIn("--volume", provision_source)
        self.assertIn('-D "$database_name"', provision_source)
        self.assertIn("MARIADB_ROOT_HOST=localhost", provision_source)
        self.assertIn("information_schema.USER_PRIVILEGES", provision_source)
        self.assertIn("information_schema.SCHEMA_PRIVILEGES", provision_source)
        self.assertIn("PRIVILEGE_TYPE <> 'USAGE'", provision_source)
        self.assertIn("ALTER,CREATE,DELETE,INDEX,INSERT,REFERENCES,SELECT,UPDATE", provision_source)
        self.assertIn('-D "$database_name"', inspect_source)
        self.assertIn("MigrationsExecuted", common_source)
        self.assertIn("PROVISIONING_FAILED", provision_source)
        self.assertIn("container exists", provision_source)
        self.assertIn("volume exists", provision_source)
        self.assertIn("127.0.0.1", provision_source)
        self.assertNotIn("podman rm", provision_source)
        self.assertIn("DatabaseMutations", inspect_source)
        self.assertIn("--execute", cleanup_source)
        self.assertIn("validate_manifest", cleanup_source)
        self.assertIn("refusing cleanup", cleanup_source)
        self.assertIn("ContainerVolumeDeletion", cleanup_source)
        self.assertNotIn('get("Cleanup")', cleanup_source)


if __name__ == "__main__":
    unittest.main()
