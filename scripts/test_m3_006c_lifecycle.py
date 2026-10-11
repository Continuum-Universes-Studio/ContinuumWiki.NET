import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
VERIFY = (ROOT / "scripts/verify-m3-006c.sh").read_text()
GENERATE = (ROOT / "scripts/generate-m3-006c4-comparisons.sh").read_text()
DIAGNOSTIC = (ROOT / "scripts/verify-m3-006c-diagnostic.sh").read_text()


class LifecycleTests(unittest.TestCase):
    def test_import_validation_precedes_comparison_and_cleanup_is_trap_based(self):
        self.assertLess(VERIFY.index("--reconciliation-output \"$real_report\""), VERIFY.index("comparison_status=0"))
        self.assertIn('trap cleanup EXIT INT TERM', VERIFY)
        self.assertIn('podman rm -f "$container_name"', VERIFY)

    def test_report_verdict_batch_and_database_guards_exist(self):
        self.assertIn("import_verdict=", VERIFY)
        self.assertIn('[[ "$import_verdict" != VERIFIED ]]', VERIFY)
        self.assertIn('[[ ! "$import_batch_id" =~ ^[0-9]+$', VERIFY)
        self.assertIn("SELECT DATABASE()", VERIFY)
        self.assertIn('Database=${database_name};', VERIFY)

    def test_generation_receives_private_connection_only_as_subprocess_environment(self):
        self.assertIn('CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION="$connection"', VERIFY)
        self.assertIn('CONTINUUMWIKI_M3_006C_IMPORT_BATCH_ID="$import_batch_id"', VERIFY)
        self.assertNotIn('export CONTINUUMWIKI_M3_006C_OUTPUT_CONNECTION', VERIFY)
        self.assertIn('set +e', GENERATE)
        self.assertIn('generation_status=$?', GENERATE)

    def test_missing_references_and_failures_are_non_success(self):
        self.assertIn("-eq 8", GENERATE)
        self.assertIn('if [[ "$status" -ne 0 ]]; then comparison_status=1; fi', GENERATE)
        self.assertIn('generation_status" -ne 0 || "$comparison_status" -ne 0', GENERATE)

    def test_diagnostic_entry_point_probes_transport_before_import(self):
        self.assertIn('export CW_M3_006C_DIAGNOSTIC=1', DIAGNOSTIC)
        self.assertIn('connected-worker-protocol-ok', DIAGNOSTIC)
        self.assertIn('filesystem/permission-denied', DIAGNOSTIC)
        self.assertIn('connection-failure:', DIAGNOSTIC)
        self.assertIn('worker-activation-failure:', DIAGNOSTIC)
        self.assertIn('protocol-rejection:', DIAGNOSTIC)
        self.assertIn('exec "$repo_root/scripts/verify-m3-006c.sh"', DIAGNOSTIC)


if __name__ == "__main__":
    unittest.main()
