#!/usr/bin/env python3
"""Source-safe regressions for the explicitly gated rehearsal runner."""

import importlib.util
import json
import os
import subprocess
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec and spec.loader
    spec.loader.exec_module(module)
    return module


OLD_TESTS = load("m5_026b_existing_preflight_tests", ROOT / "scripts/test_m5_026b_authorization_preflight.py")
RUNNER = load("cw_m5_026b_rehearse", ROOT / "scripts/cw-m5-026b/rehearse.py")
NOW = datetime(2026, 10, 12, 12, 0, tzinfo=timezone.utc)
IMAGE = "docker.io/library/mariadb@sha256:" + "a" * 64
RUN_IDS = {"source": "source-run-20261012", "destination": "destination-run-20261012"}
IDS = {"source": "1" * 64, "destination": "2" * 64}
IMAGE_ID = "sha256:" + "b" * 64
TABLES = [
    "Page", "PageRevision", "PageTag", "PageReference", "PageCategoryIndexState",
    "PageCategoryMembership", "TwCategoryRecoveryOperation", "TwCategoryRecoveryAuditEvent",
    "TwCategoryAdministrativeGrant", "TwCategoryAdministrativeGrantAudit",
]


def bound_manifest(source_credential, destination_credential, artifact_directory):
    document = OLD_TESTS.valid_manifest()
    document["SnapshotPlan"]["ArtifactDirectory"] = str(artifact_directory)
    for role, key in (("Source", "source"), ("Destination", "destination")):
        target = document[role]
        target["InstanceIdentity"] = (f"milestone=CW-M5-026B;role={key};container={target['ContainerName']};"
                                      f"volume={target['VolumeName']};database={target['DatabaseName']};endpoint={target['Endpoint']};"
                                      f"image={IMAGE};containerId={IDS[key]};runId={RUN_IDS[key]};")
        target["CredentialReference"] = str(source_credential if key == "source" else destination_credential)
    return document


def command_result(stdout="", returncode=0):
    return SimpleNamespace(stdout=stdout, stderr="", returncode=returncode)


class FakeHost:
    def __init__(self, document):
        self.document = document
        self.calls = []
        self.restored = False
        self.global_grants = False

    def __call__(self, command, **kwargs):
        self.calls.append(list(command))
        if command[:2] == ["podman", "info"]:
            return command_result("true\n")
        if command[:3] == ["podman", "container", "inspect"]:
            name = command[-1]
            target, role = next((self.document[r], r.lower()) for r in ("Source", "Destination") if self.document[r]["ContainerName"] == name)
            tokens = RUNNER._identity_tokens(target)
            value = {"Id": tokens["containerId"], "Image": IMAGE_ID,
                     "Config": {"Image": tokens["image"], "Labels": {
                         "io.continuumwiki.m5-026b-run-id": tokens["runId"],
                         "io.continuumwiki.m5-026b-database": target["DatabaseName"]}},
                     "State": {"Running": True}, "Mounts": [{"Name": target["VolumeName"], "Destination": "/var/lib/mysql"}]}
            return command_result(json.dumps(value))
        if command[:2] == ["podman", "port"]:
            target = next(self.document[r] for r in ("Source", "Destination") if self.document[r]["ContainerName"] == command[2])
            return command_result("127.0.0.1:" + target["Endpoint"].rsplit(":", 1)[1] + "\n")
        if command[:3] == ["podman", "image", "inspect"]:
            return command_result(json.dumps({"Id": IMAGE_ID, "RepoDigests": [IMAGE]}))
        if command[:3] == ["podman", "volume", "inspect"]:
            target = next(self.document[r] for r in ("Source", "Destination") if self.document[r]["VolumeName"] == command[-1])
            tokens = RUNNER._identity_tokens(target)
            return command_result(json.dumps({"Name": target["VolumeName"], "Labels": {"io.continuumwiki.m5-026b-run-id": tokens["runId"]}}))
        if command[0] == "mariadb" and "--execute" in command:
            # Resolve target from the selected database argument, not any credential contents.
            database = command[command.index("--database") + 1]
            target = next(self.document[r] for r in ("Source", "Destination") if self.document[r]["DatabaseName"] == database)
            query = command[command.index("--execute") + 1]
            if query.startswith("SELECT DATABASE()"):
                return command_result(f"{database}\t11.8.2-MariaDB\tMariaDB Server\t{target['DatabaseIdentity']}@localhost\n")
            if query == "SHOW GRANTS;":
                if self.global_grants:
                    return command_result(f"GRANT ALL PRIVILEGES ON *.* TO `{target['DatabaseIdentity']}`@`localhost`\n")
                return command_result(f"GRANT USAGE ON *.* TO `{target['DatabaseIdentity']}`@`localhost`\nGRANT ALL PRIVILEGES ON `{database}`.* TO `{target['DatabaseIdentity']}`@`localhost`\n")
            if query.startswith("SELECT TABLE_NAME"):
                if target is self.document["Source"] or self.restored:
                    return command_result("".join(f"{table}\tInnoDB\n" for table in TABLES))
                return command_result("")
        if command[0] == "mariadb-dump" and "--single-transaction" in command:
            kwargs["output_stream"].write(b"-- mock dump artifact\n")
            kwargs["output_stream"].flush()
            return command_result()
        if command[0] == "mariadb-dump" and "--no-create-info" in command:
            kwargs["output_stream"].write(b"INSERT DATA\n")
            kwargs["output_stream"].flush()
            return command_result()
        if command[0] == "mariadb" and "--binary-mode" in command:
            self.restored = True
            return command_result()
        raise AssertionError("unexpected mocked command: " + " ".join(command))


class M5026BRehearsalTests(unittest.TestCase):
    def test_live_binding_requires_container_image_and_run_provenance(self):
        manifest = OLD_TESTS.valid_manifest()
        manifest["Source"]["InstanceIdentity"] = manifest["Source"]["InstanceIdentity"].replace(IDS["source"], "missing")
        with self.assertRaisesRegex(ValueError, "container-id-pin-required"):
            RUNNER.validate_execution_manifest(manifest, now=NOW)

    def test_destructive_permissions_are_rejected_even_when_preflight_accepts_them(self):
        with tempfile.TemporaryDirectory() as temp:
            manifest = bound_manifest(Path(temp, "continuumwiki-s.cnf"), Path(temp, "continuumwiki-d.cnf"), Path(temp))
            manifest["PermittedOperations"]["ContainerVolumeDeletionSource"] = True
            with self.assertRaisesRegex(ValueError, "refuses fault injection, cleanup, and deletion"):
                RUNNER.validate_execution_manifest(manifest, now=NOW)

    def test_client_arguments_reference_option_file_without_secret_values(self):
        target = OLD_TESTS.valid_manifest()["Source"]
        command = RUNNER.client_command("mariadb", target, Path("/private/source.cnf"))
        self.assertIn("--defaults-extra-file=/private/source.cnf", command)
        self.assertFalse(any("password" in item.casefold() for item in command))

    def test_successful_mocked_rehearsal_preserves_evidence_and_never_calls_real_host(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            artifacts, reports = base / "artifacts", base / "reports"
            artifacts.mkdir(mode=0o700)
            reports.mkdir(mode=0o700)
            credentials = base / "continuumwiki"
            credentials.mkdir(mode=0o700)
            source_credential, destination_credential = credentials / "source.cnf", credentials / "destination.cnf"
            source_credential.write_text("placeholder; never passed to a real client\n", encoding="utf-8")
            destination_credential.write_text("placeholder; never passed to a real client\n", encoding="utf-8")
            source_credential.chmod(0o600)
            destination_credential.chmod(0o600)
            manifest = bound_manifest(source_credential, destination_credential, artifacts)
            fake = FakeHost(manifest)
            artifact, report = artifacts / "snapshot.sql", reports / "result.json"
            result = RUNNER.run_rehearsal(manifest, artifact, report, runner=fake, uid=os.getuid(), now=lambda: NOW)
            self.assertEqual("RESTORE_CONTENT_READBACK_MATCHED_NOT_FULL_CERTIFIED", result["Classification"])
            self.assertTrue(artifact.is_file())
            self.assertTrue(Path(str(artifact) + ".sha256").is_file())
            self.assertEqual(0o600, artifact.stat().st_mode & 0o777)
            persisted = json.loads(report.read_text(encoding="utf-8"))
            self.assertEqual("NOT_PERFORMED", persisted["Readback"]["FullSchemaLedgerForeignKeyAndRecoveryCertification"])
            self.assertNotIn("CredentialReference", report.read_text(encoding="utf-8"))
            self.assertGreater(len(fake.calls), 0)

    def test_nonempty_destination_refuses_restore_and_retains_report_without_cleanup(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            artifacts, reports = base / "artifacts", base / "reports"
            artifacts.mkdir(mode=0o700)
            reports.mkdir(mode=0o700)
            credential_directory = base / "continuumwiki"
            credential_directory.mkdir(mode=0o700)
            credentials = [credential_directory / "s.cnf", credential_directory / "d.cnf"]
            for path in credentials:
                path.write_text("not consumed by this test\n", encoding="utf-8")
                path.chmod(0o600)
            manifest = bound_manifest(*credentials, artifacts)
            fake = FakeHost(manifest)
            fake.restored = True  # simulate a nonempty destination before the first inventory
            with self.assertRaisesRegex(RuntimeError, "destination is not empty"):
                RUNNER.run_rehearsal(manifest, artifacts / "backup.sql", reports / "failed.json", runner=fake, uid=os.getuid(), now=lambda: NOW)
            evidence = json.loads((reports / "failed.json").read_text(encoding="utf-8"))
            self.assertEqual("REHEARSAL_FAILED_RESOURCES_RETAINED", evidence["Classification"])
            self.assertFalse(any("--binary-mode" in call for call in fake.calls))
            self.assertFalse(evidence["AutomaticCleanup"])

    def test_global_database_grant_refuses_snapshot_and_retains_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            artifacts, reports, credential_directory = base / "artifacts", base / "reports", base / "continuumwiki"
            artifacts.mkdir(mode=0o700)
            reports.mkdir(mode=0o700)
            credential_directory.mkdir(mode=0o700)
            credentials = [credential_directory / "s.cnf", credential_directory / "d.cnf"]
            for path in credentials:
                path.write_text("placeholder only\n", encoding="utf-8")
                path.chmod(0o600)
            manifest = bound_manifest(*credentials, artifacts)
            fake = FakeHost(manifest)
            fake.global_grants = True
            with self.assertRaisesRegex(RuntimeError, "global privileges"):
                RUNNER.run_rehearsal(manifest, artifacts / "backup.sql", reports / "failed.json", runner=fake, uid=os.getuid(), now=lambda: NOW)
            evidence = json.loads((reports / "failed.json").read_text(encoding="utf-8"))
            self.assertEqual("REHEARSAL_FAILED_RESOURCES_RETAINED", evidence["Classification"])
            self.assertFalse(any("--single-transaction" in call for call in fake.calls))

    def test_modified_checksum_sidecar_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            artifact = Path(temp, "snapshot.sql")
            artifact.write_bytes(b"snapshot\n")
            digest = RUNNER._sha256_file(artifact)
            sidecar = Path(str(artifact) + ".sha256")
            sidecar.write_text("0" * 64 + "  snapshot.sql\n", encoding="ascii")
            sidecar.chmod(0o600)
            with self.assertRaisesRegex(RuntimeError, "checksum verification failed"):
                RUNNER._verify_checksum_sidecar(artifact, sidecar, digest)

    def test_existing_report_is_create_only_and_stops_before_external_calls(self):
        with tempfile.TemporaryDirectory() as temp:
            base = Path(temp)
            artifacts, reports = base / "artifacts", base / "reports"
            artifacts.mkdir(mode=0o700)
            reports.mkdir(mode=0o700)
            credential_directory = base / "continuumwiki"
            credential_directory.mkdir(mode=0o700)
            credentials = [credential_directory / "s.cnf", credential_directory / "d.cnf"]
            for path in credentials:
                path.write_text("placeholder\n", encoding="utf-8")
                path.chmod(0o600)
            manifest = bound_manifest(*credentials, artifacts)
            report = reports / "existing.json"
            report.write_text("preserve me", encoding="utf-8")
            fake = FakeHost(manifest)
            with self.assertRaises(FileExistsError):
                RUNNER.run_rehearsal(manifest, artifacts / "backup.sql", report, runner=fake, uid=os.getuid(), now=lambda: NOW)
            self.assertEqual("preserve me", report.read_text(encoding="utf-8"))
            self.assertEqual([], fake.calls)

    def test_schema_endpoint_pattern_accepts_ipv4_loopback_not_near_match(self):
        schema = json.loads((ROOT / "docs/operations/cw-m5-026b-authorization-manifest.schema.json").read_text(encoding="utf-8"))
        pattern = schema["$defs"]["target"]["properties"]["Endpoint"]["pattern"]
        self.assertRegex("127.0.0.1:13326", pattern)
        self.assertNotRegex("127.0.1:13326", pattern)

    def test_restore_rejects_database_routing_statements(self):
        with tempfile.TemporaryDirectory() as temp:
            artifact = Path(temp, "snapshot.sql")
            artifact.write_text("USE `cw_m5_026b_source_ro1`;\n", encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "database-routing"):
                RUNNER._reject_database_routing_statements(artifact)

    def test_shell_entrypoint_syntax_and_plan_mode_do_not_start_external_tools(self):
        wrapper = ROOT / "scripts/cw-m5-026b/rehearse-m5-026b.sh"
        syntax = subprocess.run(["bash", "-n", str(wrapper)], capture_output=True, text=True)
        self.assertEqual(0, syntax.returncode, syntax.stderr)
        result = subprocess.run(["python3", str(ROOT / "scripts/cw-m5-026b/rehearse.py"), "--help"], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
