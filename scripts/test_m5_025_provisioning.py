#!/usr/bin/env python3
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/cw-m5-025/provision-mariadb-m5-025.sh"


class M5ProvisioningSourceTests(unittest.TestCase):
    def run_script(self, *args):
        return subprocess.run(
            [str(SCRIPT), *args],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            env={**os.environ, "CW_M5_025_PORT": ""},
        )

    def test_shell_syntax_is_valid(self):
        result = subprocess.run(
            ["bash", "-n", "scripts/cw-m5-025/common.sh", str(SCRIPT)],
            cwd=ROOT,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        self.assertEqual(0, result.returncode, result.stderr)

    def test_plan_only_is_scoped_and_does_not_write(self):
        with tempfile.TemporaryDirectory() as directory:
            metadata = Path(directory, "metadata.json")
            secrets = Path(directory, "secrets")
            result = self.run_script(
                "--suffix", "source-test",
                "--port", "13327",
                "--metadata", str(metadata),
                "--secrets-dir", str(secrets),
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("CW-M5-025C plan-only provisioning", result.stdout)
            self.assertIn("continuumwiki-m5-025-source-test", result.stdout)
            self.assertIn("cw_m5_025_source_test", result.stdout)
            self.assertIn("continuumwiki_m5_025_app", result.stdout)
            self.assertIn("127.0.0.1:13327", result.stdout)
            self.assertFalse(metadata.exists())
            self.assertFalse(secrets.exists())

    def test_provision_requires_explicit_disposable_confirmation(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.run_script(
                "--provision",
                "--suffix", "source-test",
                "--port", "13327",
                "--metadata", str(Path(directory, "metadata.json")),
                "--secrets-dir", str(Path(directory, "secrets")),
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("--confirm-disposable-target", result.stderr)

    def test_invalid_scope_is_rejected_before_live_mode(self):
        result = self.run_script("--suffix", "operator4", "--port", "13327")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("existing operator4", result.stderr)

        invalid = self.run_script("--suffix", "M5-025-invalid", "--port", "13327")
        self.assertNotEqual(0, invalid.returncode)
        self.assertIn("invalid lowercase suffix", invalid.stderr)


if __name__ == "__main__":
    unittest.main()
