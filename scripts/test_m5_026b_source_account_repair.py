from __future__ import annotations

import importlib.util
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

MODULE_PATH = Path(__file__).parent / "cw-m5-026b/repair_source_application_account.py"
SPEC = importlib.util.spec_from_file_location("m5_026b_source_account_repair", MODULE_PATH)
assert SPEC and SPEC.loader
repair = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(repair)


class SourceAccountRepairContractTests(unittest.TestCase):
    def setUp(self):
        self.now = datetime.now(timezone.utc)
        self.manifest = repair._manifest_template(self.now)

    def test_exact_approval_and_target_validate(self):
        digest = repair.validate_manifest(self.manifest, repair.MANIFEST_PATH, self.now)
        self.assertRegex(digest, r"^[0-9a-f]{64}$")

    def test_wrong_container_is_rejected(self):
        self.manifest["Target"]["ContainerId"] = "0" * 64
        with self.assertRaises(repair.RepairError):
            repair.validate_manifest(self.manifest, repair.MANIFEST_PATH, self.now)

    def test_destination_or_extra_operation_is_rejected(self):
        self.manifest["Operations"]["DestinationAccess"] = True
        with self.assertRaises(repair.RepairError):
            repair.validate_manifest(self.manifest, repair.MANIFEST_PATH, self.now)

    def test_expired_approval_is_rejected(self):
        expired = self.now - timedelta(seconds=1)
        self.manifest["ApprovedAtUtc"] = repair._utc(expired - timedelta(hours=1))
        self.manifest["ExpiresAtUtc"] = repair._utc(expired)
        with self.assertRaises(repair.RepairError):
            repair.validate_manifest(self.manifest, repair.MANIFEST_PATH, self.now)

    def test_evidence_path_substitution_is_rejected(self):
        self.manifest["Evidence"]["ResultPath"] = str(repair.EVIDENCE_DIR / "other.json")
        with self.assertRaises(repair.RepairError):
            repair.validate_manifest(self.manifest, repair.MANIFEST_PATH, self.now)

    def test_grant_verification_accepts_only_exact_schema_privileges(self):
        expected = "cw_m5_026b_source_ro1\tALTER,CREATE,DELETE,INDEX,INSERT,REFERENCES,SELECT,UPDATE\t\t0"
        self.assertTrue(repair._grant_output_matches(expected))
        self.assertFalse(repair._grant_output_matches(expected.replace("ALTER,", "ALTER,DROP,")))
        self.assertFalse(repair._grant_output_matches(expected.replace("\t\t0", "\tSUPER\t0")))
        self.assertFalse(repair._grant_output_matches(expected.replace("\t0", "\t1")))

    def test_repair_sql_is_exact_and_secret_is_not_embedded_in_invocation(self):
        marker = "a" * 64
        sql = repair._build_create_grant_sql(marker)
        self.assertIn("CREATE USER 'continuumwiki_m5_026b_source_app'@'%'", sql)
        self.assertIn("ON `cw_m5_026b_source_ro1`.*", sql)
        self.assertIn("SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, REFERENCES", sql)
        self.assertNotIn("GRANT OPTION", sql.upper())
        self.assertNotIn("GLOBAL", sql.upper())
        self.assertEqual(sql.count(";"), 2)


if __name__ == "__main__":
    unittest.main()
